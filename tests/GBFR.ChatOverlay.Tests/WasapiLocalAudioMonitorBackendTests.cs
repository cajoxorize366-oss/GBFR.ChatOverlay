using System.Collections.Concurrent;
using System.Diagnostics;
using GBFR.ChatOverlay.Audio;

namespace GBFR.ChatOverlay.Tests;

public sealed class WasapiLocalAudioMonitorBackendTests
{
    [Fact]
    public void StopBeforeStart_IsNonBlockingIdempotentAndStillCleansUp()
    {
        var logs = new ConcurrentQueue<string>();
        var backend = new WasapiLocalAudioMonitorBackend(
            ResolvedAudioEndpointSelection.SystemDefault(),
            ResolvedAudioEndpointSelection.SystemDefault(),
            1.0f,
            0.35f,
            logs.Enqueue);
        var stopwatch = Stopwatch.StartNew();

        backend.Stop();
        backend.Dispose();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(SpinWait.SpinUntil(
            () => logs.Any(line => line.Contains("cleanup complete", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3)));
        Assert.Contains(logs, line =>
            line.Contains("playback gate is already closed", StringComparison.Ordinal));
    }
}
