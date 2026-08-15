using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace GBFR.ChatOverlay.Runtime.Diagnostics;

internal sealed class DebugFileLog : IDisposable
{
    internal const string FileName = "GBFR.ChatOverlay.debug.log";
    internal const int DefaultQueueCapacity = 1024;
    internal const long DefaultMaxSessionBytes = 16L * 1024 * 1024;

    private readonly object _controlSync = new();
    private readonly string? _logFilePath;
    private readonly string _modId;
    private readonly Action<string> _failureCallback;
    private readonly Func<string, bool, StreamWriter> _writerFactory;
    private readonly int _queueCapacity;
    private readonly long _maxSessionBytes;
    private readonly AsyncLocal<bool> _consumerContext = new();

    private volatile Channel<DebugLogCommand>? _queue;
    private volatile bool _acceptingWrites;
    private volatile bool _sessionStarted;
    private volatile bool _writerFailed;
    private volatile bool _disableRequested;
    private volatile bool _disposeRequested;
    private bool _disposed;
    private Task? _consumerTask;
    private StreamWriter? _writer;
    private long _sessionBytes;
    private long _droppedCount;
    private int _failureReported;
    private int _pathFailureReported;

    internal DebugFileLog(
        string? logFilePath,
        string modId,
        Action<string> failureCallback,
        Func<string, bool, StreamWriter>? writerFactory = null,
        int queueCapacity = DefaultQueueCapacity,
        long maxSessionBytes = DefaultMaxSessionBytes)
    {
        _logFilePath = logFilePath;
        _modId = modId ?? string.Empty;
        _failureCallback = failureCallback ?? (_ => { });
        _writerFactory = writerFactory ?? CreateWriter;
        _queueCapacity = Math.Max(1, queueCapacity);
        _maxSessionBytes = Math.Max(0, maxSessionBytes);
    }

    internal void ApplyEnabled(bool enabled)
    {
        if (enabled)
            Enable();
        else
            Disable();
    }

    internal void Write(string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        var queue = _queue;
        if (!_acceptingWrites || queue is null)
            return;

        var command = new WriteCommand(
            DateTimeOffset.Now,
            Environment.CurrentManagedThreadId,
            message);
        if (!queue.Writer.TryWrite(command))
            Interlocked.Increment(ref _droppedCount);
    }

    public void Dispose()
    {
        Channel<DebugLogCommand>? queue = null;
        Task? taskToWait = null;

        lock (_controlSync)
        {
            if (_disposed)
                return;

            if (_queue is null)
            {
                _disposed = true;
                return;
            }

            if (IsInConsumerContext())
            {
                _acceptingWrites = false;
                _disposeRequested = true;
                _disposed = true;
                return;
            }

            _disposed = true;
            _acceptingWrites = false;
            queue = _queue;
            taskToWait = _consumerTask;
        }

        if (queue is not null)
        {
            try
            {
                queue.Writer.WriteAsync(new DisposeCommand()).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // The consumer may already have completed after a file fault.
            }
        }

        taskToWait?.Wait();

        lock (_controlSync)
        {
            CleanupAfterControl();
        }
    }

    private void Enable()
    {
        TaskCompletionSource<bool>? completion = null;
        string? pendingPathFailure = null;

        lock (_controlSync)
        {
            if (_disposed || _queue is not null || IsInConsumerContext())
                return;

            if (string.IsNullOrWhiteSpace(_logFilePath))
            {
                if (Interlocked.Exchange(ref _pathFailureReported, 1) == 0)
                    pendingPathFailure = "Debug file log path is unavailable.";
            }
            else
            {
                _acceptingWrites = true;
                _disableRequested = false;
                _disposeRequested = false;
                _writerFailed = false;
                Volatile.Write(ref _failureReported, 0);

                var queue = CreateQueue(_queueCapacity);
                _queue = queue;
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var enableCommand = new EnableCommand(_sessionStarted, completion);
                _consumerTask = Task.Run(() => ConsumerLoop(queue, enableCommand));
            }
        }

        if (pendingPathFailure is not null)
            ReportFailure(pendingPathFailure);

        if (completion is null)
            return;

        completion.Task.Wait();

        lock (_controlSync)
        {
            if (!completion.Task.Result)
            {
                _acceptingWrites = false;
                _queue = null;
                _consumerTask = null;
            }
        }
    }

    private void Disable()
    {
        Channel<DebugLogCommand>? queue = null;
        Task? taskToWait = null;

        lock (_controlSync)
        {
            if (_disposed || _queue is null)
                return;

            if (IsInConsumerContext())
            {
                _acceptingWrites = false;
                _disableRequested = true;
                return;
            }

            _acceptingWrites = false;
            queue = _queue;
            taskToWait = _consumerTask;
        }

        if (queue is not null)
        {
            try
            {
                queue.Writer.WriteAsync(new DisableCommand()).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // The consumer may already have completed after a file fault.
            }
        }

        taskToWait?.Wait();

        lock (_controlSync)
        {
            CleanupAfterControl();
        }
    }

    private async Task ConsumerLoop(
        Channel<DebugLogCommand> queue,
        EnableCommand enableCommand)
    {
        _consumerContext.Value = true;
        try
        {
            var opened = OpenWriter(enableCommand.Append);
            enableCommand.Completion.TrySetResult(opened);
            if (!opened)
            {
                queue.Writer.TryComplete();
                return;
            }

            while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out var command))
                {
                    if (command is WriteCommand write)
                    {
                        TryWriteLine(write.Timestamp, write.ManagedThreadId, write.Message);
                    }
                    else if (command is DisableCommand)
                    {
                        WriteDropSummaryIfNeeded();
                        TryWriteLine("Debug file logging disabled.");
                        CloseWriter();
                        return;
                    }
                    else if (command is DisposeCommand)
                    {
                        WriteDropSummaryIfNeeded();
                        TryWriteLine("Debug file logging session ended.");
                        CloseWriter();
                        return;
                    }

                    if (ShouldStopConsumer())
                    {
                        CloseWriter();
                        return;
                    }
                }
            }

            CloseWriter();
        }
        catch (Exception exception)
        {
            TryReportFailureOnce(exception.Message);
            CloseWriter();
            enableCommand.Completion.TrySetResult(false);
        }
        finally
        {
            _consumerContext.Value = false;
            queue.Writer.TryComplete();
        }
    }

    private bool OpenWriter(bool append)
    {
        try
        {
            var writer = _writerFactory(_logFilePath!, append);
            _writer = writer;
            _sessionStarted = true;
            return TryWriteLine("Debug file logging enabled.");
        }
        catch (Exception exception)
        {
            _writerFailed = true;
            CloseWriter();
            TryReportFailureOnce(exception.Message);
            return false;
        }
    }

    private bool TryWriteLine(string message) =>
        TryWriteLine(DateTimeOffset.Now, Environment.CurrentManagedThreadId, message);

    private bool TryWriteLine(DateTimeOffset timestamp, int managedThreadId, string message)
    {
        if (_writer is null || _writerFailed)
            return false;

        var line = FormatLine(timestamp, managedThreadId, message);
        var lineBytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
        if (_maxSessionBytes > 0 && _sessionBytes + lineBytes > _maxSessionBytes)
        {
            _writerFailed = true;
            TryReportFailureOnce("Debug file log session size limit reached; file logging disabled.");
            CloseWriter();
            return false;
        }

        try
        {
            _writer.WriteLine(line);
            _sessionBytes += lineBytes;
            return true;
        }
        catch (Exception exception)
        {
            _writerFailed = true;
            CloseWriter();
            TryReportFailureOnce(exception.Message);
            return false;
        }
    }

    private void CloseWriter()
    {
        var writer = _writer;
        _writer = null;
        if (writer is null)
            return;

        try
        {
            writer.Dispose();
        }
        catch (Exception exception)
        {
            _writerFailed = true;
            TryReportFailureOnce(exception.Message);
        }
    }

    private void WriteDropSummaryIfNeeded()
    {
        var dropped = Interlocked.Read(ref _droppedCount);
        if (dropped <= 0)
            return;

        Interlocked.Exchange(ref _droppedCount, 0);
        TryWriteLine(
            $"{dropped} log lines were dropped because the bounded debug queue was full.");
    }

    private bool ShouldStopConsumer() =>
        _writerFailed ||
        _disableRequested ||
        _disposeRequested;

    private bool IsInConsumerContext() => _consumerContext.Value;

    private void CleanupAfterControl()
    {
        _queue = null;
        _consumerTask = null;
        _acceptingWrites = false;
        _disableRequested = false;
        _disposeRequested = false;
        _writerFailed = false;
        Interlocked.Exchange(ref _droppedCount, 0);
        Volatile.Write(ref _failureReported, 0);
    }

    private void ReportFailure(string failure)
    {
        try
        {
            _failureCallback(failure);
        }
        catch
        {
            // A broken diagnostic callback must never escape to the game thread.
        }
    }

    private void TryReportFailureOnce(string failure)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) != 0)
            return;

        try
        {
            _failureCallback(failure);
        }
        catch
        {
            // A broken diagnostic callback must never escape to the game thread.
        }
    }

    private string FormatLine(DateTimeOffset timestamp, int managedThreadId, string message) =>
        $"{timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)} " +
        $"[{managedThreadId}] [{_modId}] {message}";

    private static Channel<DebugLogCommand> CreateQueue(int capacity) =>
        Channel.CreateBounded<DebugLogCommand>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private static StreamWriter CreateWriter(string logFilePath, bool append)
    {
        var stream = new FileStream(
            logFilePath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    private abstract record DebugLogCommand;

    private sealed record WriteCommand(
        DateTimeOffset Timestamp,
        int ManagedThreadId,
        string Message) : DebugLogCommand;

    private sealed record EnableCommand(bool Append, TaskCompletionSource<bool> Completion) : DebugLogCommand;

    private sealed record DisableCommand : DebugLogCommand;

    private sealed record DisposeCommand : DebugLogCommand;
}
