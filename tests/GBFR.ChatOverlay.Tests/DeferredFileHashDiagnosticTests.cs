using System.IO;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class DeferredFileHashDiagnosticTests
{
    [Fact]
    public async Task StartCore_DefersHashAndLogsKnownMatch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new List<string>();

        var diagnostic = DeferredFileHashDiagnostic.StartCore(
            async _ =>
            {
                started.SetResult();
                await release.Task;
                return "AABB";
            },
            "test-file",
            "C:\\test.bin",
            "aabb",
            logs.Add);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(diagnostic.IsCompleted);
        release.SetResult();
        await diagnostic.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, message =>
            message.Contains("phase=test-file-sha256 state=begin", StringComparison.Ordinal));
        Assert.Contains(logs, message =>
            message.Contains("state=complete", StringComparison.Ordinal) &&
            message.Contains("expected_hash_match=true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartCore_ContainsHashAndLoggerFailures()
    {
        var diagnostic = DeferredFileHashDiagnostic.StartCore(
            _ => Task.FromException<string>(new IOException("disk failed")),
            "test-file",
            null,
            "AABB",
            _ => throw new InvalidOperationException("logger failed"));

        await diagnostic.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StartCore_ReportsCancellationWithoutThrowing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var logs = new List<string>();

        var diagnostic = DeferredFileHashDiagnostic.StartCore(
            token => Task.FromCanceled<string>(token),
            "test-file",
            null,
            "AABB",
            logs.Add,
            cancellation.Token);

        await diagnostic.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(logs, message =>
            message.Contains("reason=cancelled", StringComparison.Ordinal));
    }
}
