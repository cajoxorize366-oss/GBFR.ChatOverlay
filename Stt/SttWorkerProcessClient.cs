using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace GBFR.ChatOverlay.Stt;

/// <summary>
/// Owns the isolated recorder process. Commands never write to a pipe on the game thread;
/// a bounded channel feeds one background writer while stdout is parsed into a concurrent queue.
/// </summary>
public sealed class SttWorkerProcessClient : ISttWorkerClient
{
    private const int MaximumDiagnosticCharacters = 2_000;

    private readonly Process _process;
    private readonly Channel<SttCommand> _commands;
    private readonly ConcurrentQueue<SttEvent> _events = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Action<string> _log;
    private readonly Task _writerTask;
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private int _available = 1;
    private int _disposed;
    private string? _unavailableReason;

    private SttWorkerProcessClient(SttWorkerLaunchOptions options, Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _commands = Channel.CreateBounded<SttCommand>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var startInfo = new ProcessStartInfo
        {
            FileName = options.WorkerExecutable,
            WorkingDirectory = options.RuntimeRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        AddArgument(startInfo, "--whisper", options.WhisperExecutable);
        AddArgument(startInfo, "--model", options.ModelFile);
        AddArgument(startInfo, "--model-sha256", options.ModelSha256);
        AddArgument(startInfo, "--language", options.Language);
        AddArgument(startInfo, "--threads", options.ThreadCount.ToString());
        AddArgument(startInfo, "--max-seconds", options.MaximumCaptureSeconds.ToString());
        AddArgument(startInfo, "--work-directory", Path.Combine(options.RuntimeRoot, "work"));

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += ProcessExited;
        if (!_process.Start())
            throw new InvalidOperationException("The STT worker process did not start.");

        try
        {
            _process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception)
        {
            _log($"Could not lower STT worker priority: {exception.Message}");
        }

        _writerTask = Task.Run(WriteCommandsAsync);
        _stdoutTask = Task.Run(ReadEventsAsync);
        _stderrTask = Task.Run(ReadDiagnosticsAsync);
    }

    public bool IsAvailable => Volatile.Read(ref _available) != 0;
    public string? UnavailableReason => Volatile.Read(ref _unavailableReason);

    public static ISttWorkerClient Create(
        string modDirectory,
        string language,
        int threadCount,
        int maximumCaptureSeconds,
        Action<string> log)
    {
        if (!SttRuntimeManifest.TryResolve(
                modDirectory,
                language,
                threadCount,
                maximumCaptureSeconds,
                out var options,
                out var error))
        {
            return new UnavailableSttWorkerClient(error!);
        }

        try
        {
            return new SttWorkerProcessClient(options!, log);
        }
        catch (Exception exception)
        {
            log($"STT worker startup failed: {exception}");
            return new UnavailableSttWorkerClient($"STT worker startup failed: {exception.Message}");
        }
    }

    public bool TrySend(SttCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsAvailable || Volatile.Read(ref _disposed) != 0)
            return false;

        return _commands.Writer.TryWrite(command);
    }

    public bool TryRead(out SttEvent message) => _events.TryDequeue(out message!);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (IsAvailable)
            _commands.Writer.TryWrite(new SttCommand(SttMessageTypes.Shutdown));
        _commands.Writer.TryComplete();

        try
        {
            if (!_process.HasExited && !_process.WaitForExit(1_500))
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            _log($"STT worker shutdown warning: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _available, 0);
            _shutdown.Cancel();
            try
            {
                Task.WaitAll(new[] { _writerTask, _stdoutTask, _stderrTask }, 1_000);
            }
            catch
            {
            }
            _shutdown.Dispose();
            _process.Dispose();
        }
    }

    private async Task WriteCommandsAsync()
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                var line = SttProtocol.Serialize(command);
                await _process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(_shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MarkUnavailable($"STT command pipe failed: {exception.Message}");
        }
    }

    private async Task ReadEventsAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (line is null)
                    break;

                if (SttProtocol.TryParseEvent(line, out var message, out var error))
                    _events.Enqueue(message!);
                else
                    _log($"Ignored invalid STT worker event: {Truncate(error ?? line)}");
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MarkUnavailable($"STT event pipe failed: {exception.Message}");
        }
    }

    private async Task ReadDiagnosticsAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (line is null)
                    break;
                if (!string.IsNullOrWhiteSpace(line))
                    _log($"STT worker: {Truncate(line)}");
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log($"STT diagnostic pipe ended: {exception.Message}");
        }
    }

    private void ProcessExited(object? sender, EventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        string reason;
        try
        {
            reason = $"STT worker exited with code {_process.ExitCode}.";
        }
        catch
        {
            reason = "STT worker exited unexpectedly.";
        }
        MarkUnavailable(reason);
    }

    private void MarkUnavailable(string reason)
    {
        if (Interlocked.Exchange(ref _available, 0) == 0)
            return;

        Volatile.Write(ref _unavailableReason, reason);
        _commands.Writer.TryComplete();
        _events.Enqueue(new SttEvent(SttMessageTypes.Error, Error: reason));
        _log(reason);
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static string Truncate(string value) =>
        value.Length <= MaximumDiagnosticCharacters
            ? value
            : value[..MaximumDiagnosticCharacters] + "...";
}
