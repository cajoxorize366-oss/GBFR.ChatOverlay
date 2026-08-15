using System.Collections.Concurrent;
using System.Threading;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GBFR.ChatOverlay.Runtime.Diagnostics;

namespace GBFR.ChatOverlay.Tests;

public sealed class DebugFileLogTests
{
    private const string ModId = "gbfr.qol.chatoverlay";
    private static readonly Regex LogLinePattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2} \[\d+\] \[[^\]]+\] ",
        RegexOptions.Compiled);

    [Fact]
    public void Disabled_DoesNotCreateFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, DebugFileLog.FileName);
        var failures = new ConcurrentQueue<string>();

        using var log = new DebugFileLog(path, ModId, failures.Enqueue);
        log.ApplyEnabled(false);
        log.Write("should-not-appear");
        log.Dispose();

        Assert.False(File.Exists(path));
        Assert.Empty(failures);
    }

    [Fact]
    public void FirstEnable_TruncatesStaleFileAndWritesFormattedHeaderAndMessage()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, DebugFileLog.FileName);
        File.WriteAllText(path, "stale-line\n");
        var failures = new ConcurrentQueue<string>();

        using (var log = new DebugFileLog(path, ModId, failures.Enqueue))
        {
            log.ApplyEnabled(true);
            log.Write("native chat bridge connected");
        }

        var content = File.ReadAllText(path);
        var lines = content.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.DoesNotContain("stale-line", content, StringComparison.Ordinal);
        Assert.Contains("debug file logging enabled.", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "[gbfr.qol.chatoverlay] native chat bridge connected",
            content,
            StringComparison.Ordinal);
        Assert.Contains("debug file logging session ended.", content, StringComparison.OrdinalIgnoreCase);
        Assert.All(lines, line => Assert.Matches(LogLinePattern, line));
        Assert.Empty(failures);
    }

    [Fact]
    public void DisableThenReenable_StopsWritesAndAppendsSameSession()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, DebugFileLog.FileName);
        var failures = new ConcurrentQueue<string>();

        using var log = new DebugFileLog(path, ModId, failures.Enqueue);
        log.ApplyEnabled(true);
        log.Write("first-session-message");
        log.ApplyEnabled(false);
        log.Write("must-be-dropped");
        log.ApplyEnabled(true);
        log.Write("second-session-message");
        log.Dispose();

        var content = File.ReadAllText(path);
        Assert.Contains("first-session-message", content, StringComparison.Ordinal);
        Assert.Contains("second-session-message", content, StringComparison.Ordinal);
        Assert.DoesNotContain("must-be-dropped", content, StringComparison.Ordinal);

        var firstIndex = content.IndexOf("first-session-message", StringComparison.Ordinal);
        var secondIndex = content.IndexOf("second-session-message", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0);
        Assert.True(secondIndex > firstIndex);
        Assert.Empty(failures);
    }

    [Fact]
    public void ConcurrentWrites_AreNotLostOrInterleaved()
    {
        const int MessageCount = 400;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, DebugFileLog.FileName);

        using var log = new DebugFileLog(path, ModId, _ => { });
        log.ApplyEnabled(true);
        Parallel.For(0, MessageCount, index => log.Write($"message-{index:D4}"));
        log.Dispose();

        var lines = File.ReadAllLines(path);
        for (var index = 0; index < MessageCount; index++)
        {
            var expected = $"message-{index:D4}";
            Assert.Equal(
                1,
                lines.Count(line => line.Contains(expected, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Overflow_DropsNewWritesAndWritesBoundedSummary()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, DebugFileLog.FileName);
        var failures = new ConcurrentQueue<string>();
        var enteredBlock = new ManualResetEventSlim(false);
        var releaseBlock = new ManualResetEventSlim(false);
        var blockingStream = new BlockingWriteStream(path, enteredBlock, releaseBlock);

        using var log = new DebugFileLog(
            path,
            ModId,
            failures.Enqueue,
            (_, _) => new StreamWriter(blockingStream, new UTF8Encoding(false))
            {
                AutoFlush = true
            },
            queueCapacity: 8);
        log.ApplyEnabled(true);

        blockingStream.BlockNextWrite();
        log.Write("blocking-write");
        Assert.True(enteredBlock.Wait(TimeSpan.FromSeconds(5)));
        for (var index = 0; index < 100; index++)
            log.Write($"overflow-{index}");

        releaseBlock.Set();
        log.Dispose();

        var content = File.ReadAllText(path);
        Assert.Contains("were dropped", content, StringComparison.Ordinal);
        Assert.Empty(failures);
    }

    [Fact]
    public void Disable_ReleasesHandleBeforeReturning()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, DebugFileLog.FileName);
        using var log = new DebugFileLog(path, ModId, _ => { });
        log.ApplyEnabled(true);
        log.ApplyEnabled(false);

        File.Delete(path);
        Assert.False(File.Exists(path));
        log.Dispose();
    }

    [Fact]
    public void FailureCallback_CanRequestDisposeWithoutDeadlock()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, DebugFileLog.FileName);
        var failures = new ConcurrentQueue<string>();
        DebugFileLog? reentrantLog = null;
        var log = new DebugFileLog(
            path,
            ModId,
            _ =>
            {
                failures.Enqueue("fault");
                reentrantLog?.Dispose();
            },
            (_, _) => new StreamWriter(new FaultingWriteStream(), new UTF8Encoding(false))
            {
                AutoFlush = true
            });
        reentrantLog = log;

        using (log)
        {
            log.ApplyEnabled(true);
            log.Write("message");
            log.Dispose();
        }

        Assert.Single(failures);
    }

    [Fact]
    public void UnwritablePath_DoesNotThrowAndReportsOnce()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "missing-directory", DebugFileLog.FileName);
        var failures = new ConcurrentQueue<string>();

        using var log = new DebugFileLog(path, ModId, failures.Enqueue);
        log.ApplyEnabled(true);
        log.Write("not-written");
        log.Dispose();

        Assert.Single(failures);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SessionSizeLimit_ClosesSinkAndReportsOnce()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, DebugFileLog.FileName);
        var failures = new ConcurrentQueue<string>();

        using var log = new DebugFileLog(
            path,
            ModId,
            failures.Enqueue,
            maxSessionBytes: 512);
        log.ApplyEnabled(true);
        log.Write(new string('x', 2000));
        log.Dispose();

        Assert.Contains(
            failures,
            message => message.Contains("session size limit", StringComparison.Ordinal));
        Assert.Single(failures);
    }

    [Fact]
    public void WriteFault_DoesNotThrowAndReportsOnce()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, DebugFileLog.FileName);
        var failures = new ConcurrentQueue<string>();

        using var log = new DebugFileLog(
            path,
            ModId,
            failures.Enqueue,
            (_, _) => new StreamWriter(new FaultingWriteStream(), new UTF8Encoding(false))
            {
                AutoFlush = true
            });
        log.ApplyEnabled(true);
        log.Write("message");
        log.Write("another-message");
        log.Dispose();

        Assert.Single(failures);
    }

    [Fact]
    public void EnabledFile_CanBeReadWhileSinkIsOpen()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, DebugFileLog.FileName);

        using var log = new DebugFileLog(path, ModId, _ => { });
        log.ApplyEnabled(true);
        log.Write("visible-while-open");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        var content = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            content = reader.ReadToEnd();
            if (content.Contains("visible-while-open", StringComparison.Ordinal))
                break;

            Thread.Sleep(10);
        }

        Assert.Contains("visible-while-open", content, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), $"gbfr-debug-log-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly FileStream _fileStream;
        private readonly ManualResetEventSlim _enteredBlock;
        private readonly ManualResetEventSlim _releaseBlock;
        private volatile bool _blockNextWrite;

        public BlockingWriteStream(
            string path,
            ManualResetEventSlim enteredBlock,
            ManualResetEventSlim releaseBlock)
            : this(
                new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete),
                enteredBlock,
                releaseBlock)
        {
        }

        private BlockingWriteStream(
            FileStream fileStream,
            ManualResetEventSlim enteredBlock,
            ManualResetEventSlim releaseBlock)
        {
            _fileStream = fileStream;
            _enteredBlock = enteredBlock;
            _releaseBlock = releaseBlock;
        }

        public void BlockNextWrite() => _blockNextWrite = true;

        public override bool CanRead => _fileStream.CanRead;
        public override bool CanSeek => _fileStream.CanSeek;
        public override bool CanWrite => _fileStream.CanWrite;
        public override long Length => _fileStream.Length;
        public override long Position
        {
            get => _fileStream.Position;
            set => _fileStream.Position = value;
        }

        public override void Flush()
        {
            if (_blockNextWrite)
            {
                _blockNextWrite = false;
                _enteredBlock.Set();
                _releaseBlock.Wait();
            }

            _fileStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _fileStream.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            _fileStream.Seek(offset, origin);

        public override void SetLength(long value) => _fileStream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_blockNextWrite)
            {
                _blockNextWrite = false;
                _enteredBlock.Set();
                _releaseBlock.Wait();
            }

            _fileStream.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _fileStream.Dispose();

            base.Dispose(disposing);
        }
    }

    private sealed class FaultingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new IOException("flush failed");

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("write failed");
    }
}
