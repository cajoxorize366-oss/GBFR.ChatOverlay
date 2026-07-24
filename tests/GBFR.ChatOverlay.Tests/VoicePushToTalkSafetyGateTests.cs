using System.Diagnostics;
using GBFR.ChatOverlay.Input;

namespace GBFR.ChatOverlay.Tests;

public sealed class VoicePushToTalkSafetyGateTests
{
    [Fact]
    public void Report_NotifiesOnlyEdges_AndForceMuteReleases()
    {
        var reports = new List<bool>();
        long now = 0;
        using var gate = new VoicePushToTalkSafetyGate(
            reports.Add,
            log: null,
            TimeSpan.FromSeconds(1),
            () => now,
            startWatchdog: false);

        gate.Report(true);
        gate.Report(true);
        gate.ForceMute();
        gate.ForceMute();

        Assert.Equal(new[] { true, false }, reports);
    }

    [Fact]
    public void Watchdog_ForcesMuteWhenDirectInputHeartbeatStops()
    {
        var reports = new List<bool>();
        var logs = new List<string>();
        long now = 0;
        using var gate = new VoicePushToTalkSafetyGate(
            reports.Add,
            logs.Add,
            TimeSpan.FromSeconds(1),
            () => now,
            startWatchdog: false);

        gate.Report(true);
        now = Stopwatch.Frequency - 1;
        gate.CheckForTimeout();
        Assert.Equal(new[] { true }, reports);

        now = Stopwatch.Frequency;
        gate.CheckForTimeout();

        Assert.Equal(new[] { true, false }, reports);
        Assert.Contains(logs, line => line.Contains("heartbeat timed out", StringComparison.Ordinal));
    }

    [Fact]
    public void Dispose_ReleasesAnActivePushToTalkState()
    {
        var reports = new List<bool>();
        long now = 0;
        var gate = new VoicePushToTalkSafetyGate(
            reports.Add,
            log: null,
            TimeSpan.FromSeconds(1),
            () => now,
            startWatchdog: false);

        gate.Report(true);
        gate.Dispose();

        Assert.Equal(new[] { true, false }, reports);
    }

    [Fact]
    public void Suspend_RejectsLateHeartbeatsUntilResume()
    {
        var reports = new List<bool>();
        long now = 0;
        using var gate = new VoicePushToTalkSafetyGate(
            reports.Add,
            log: null,
            TimeSpan.FromSeconds(1),
            () => now,
            startWatchdog: false);

        gate.Report(true);
        gate.Suspend();
        gate.Report(true);

        Assert.Equal(new[] { true, false }, reports);

        gate.Resume();
        gate.Report(true);

        Assert.Equal(new[] { true, false, true }, reports);
    }

    [Fact]
    public async Task Suspend_CannotBeOvertakenByAnInFlightPressedCallback()
    {
        var reports = new List<bool>();
        var reportsSync = new object();
        var callbackTimedOut = 0;
        using var pressedCallbackEntered = new ManualResetEventSlim();
        using var releasePressedCallback = new ManualResetEventSlim();
        using var gate = new VoicePushToTalkSafetyGate(
            pressed =>
            {
                if (pressed)
                {
                    pressedCallbackEntered.Set();
                    if (!releasePressedCallback.Wait(TimeSpan.FromSeconds(5)))
                        Interlocked.Exchange(ref callbackTimedOut, 1);
                }

                lock (reportsSync)
                    reports.Add(pressed);
            },
            log: null,
            TimeSpan.FromSeconds(1),
            Stopwatch.GetTimestamp,
            startWatchdog: false);

        var reportTask = Task.Run(() => gate.Report(true));
        Task? suspendTask = null;
        try
        {
            Assert.True(pressedCallbackEntered.Wait(TimeSpan.FromSeconds(5)));
            suspendTask = Task.Run(gate.Suspend);
        }
        finally
        {
            releasePressedCallback.Set();
        }

        Assert.NotNull(suspendTask);
        await Task.WhenAll(reportTask, suspendTask);

        Assert.Equal(0, Volatile.Read(ref callbackTimedOut));
        lock (reportsSync)
            Assert.Equal(new[] { true, false }, reports);
    }
}
