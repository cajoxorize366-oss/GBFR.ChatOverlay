using GBFR.ChatOverlay.Input;

namespace GBFR.ChatOverlay.Tests;

public sealed class VoiceInputModeCoordinatorTests
{
    [Fact]
    public void LocalMonitor_OpensAndClosesIndependently()
    {
        var events = new List<string>();
        var coordinator = CreateCoordinator(events);

        coordinator.ReportLocalMonitor(true);
        coordinator.ReportLocalMonitor(false);

        Assert.Equal(new[] { "local:True", "local:False" }, events);
    }

    [Fact]
    public void RemotePushToTalk_ClosesLocalPathBeforeOpeningPartyPath()
    {
        var events = new List<string>();
        var coordinator = CreateCoordinator(events);

        coordinator.ReportLocalMonitor(true);
        coordinator.ReportRemotePushToTalk(true);
        coordinator.ReportRemotePushToTalk(false);

        Assert.Equal(
            new[] { "local:True", "local:False", "remote:True", "remote:False" },
            events);
    }

    [Fact]
    public void InterruptedHeldI_RequiresPhysicalReleaseBeforeItCanRestart()
    {
        var events = new List<string>();
        var coordinator = CreateCoordinator(events);

        coordinator.ReportLocalMonitor(true);
        coordinator.ReportRemotePushToTalk(true);
        coordinator.ReportRemotePushToTalk(false);
        coordinator.ReportLocalMonitor(true);

        Assert.Equal(
            new[] { "local:True", "local:False", "remote:True", "remote:False" },
            events);

        coordinator.ReportLocalMonitor(false);
        coordinator.ReportLocalMonitor(true);

        Assert.Equal("local:True", events[^1]);
    }

    [Fact]
    public void PressingIWhileUIsHeld_DoesNotAutoStartAfterURelease()
    {
        var events = new List<string>();
        var coordinator = CreateCoordinator(events);

        coordinator.ReportRemotePushToTalk(true);
        coordinator.ReportLocalMonitor(true);
        coordinator.ReportRemotePushToTalk(false);

        Assert.Equal(new[] { "remote:True", "remote:False" }, events);

        coordinator.ReportLocalMonitor(false);
        coordinator.ReportLocalMonitor(true);

        Assert.Equal("local:True", events[^1]);
    }

    [Fact]
    public async Task ConcurrentIAndUReports_NeverLeaveBothPathsOpen()
    {
        var remoteOpen = false;
        var localOpen = false;
        var bothOpenObserved = false;
        var stateSync = new object();
        var coordinator = new VoiceInputModeCoordinator(
            pressed =>
            {
                lock (stateSync)
                {
                    remoteOpen = pressed;
                    bothOpenObserved |= remoteOpen && localOpen;
                }
            },
            pressed =>
            {
                lock (stateSync)
                {
                    localOpen = pressed;
                    bothOpenObserved |= remoteOpen && localOpen;
                }
            });

        using var start = new ManualResetEventSlim();
        var localTask = Task.Run(() =>
        {
            start.Wait();
            coordinator.ReportLocalMonitor(true);
        });
        var remoteTask = Task.Run(() =>
        {
            start.Wait();
            coordinator.ReportRemotePushToTalk(true);
        });

        start.Set();
        await Task.WhenAll(localTask, remoteTask);

        lock (stateSync)
        {
            Assert.False(bothOpenObserved);
            Assert.True(remoteOpen);
            Assert.False(localOpen);
        }
    }

    private static VoiceInputModeCoordinator CreateCoordinator(List<string> events) =>
        new(
            pressed => events.Add($"remote:{pressed}"),
            pressed => events.Add($"local:{pressed}"));
}
