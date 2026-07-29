using GBFR.ChatOverlay.Input;

namespace GBFR.ChatOverlay.Tests;

public sealed class XInputControllerPollerTests
{
    [Fact]
    public void Poll_AggregatesConnectedControllersAndTracksEdges()
    {
        var backend = new FakeBackend
        {
            IsAvailable = true,
            Users =
            {
                [0] = ControllerButtons.A,
                [2] = ControllerButtons.RightBumper,
            },
        };
        var poller = new XInputControllerPoller(backend);

        var first = poller.Poll();
        var unchanged = poller.Poll();
        backend.Users[0] = ControllerButtons.None;
        var released = poller.Poll();

        Assert.True(first.ApiAvailable);
        Assert.True(first.IsConnected);
        Assert.Equal(ControllerButtons.A | ControllerButtons.RightBumper, first.Buttons);
        Assert.Equal(first.Sequence, unchanged.Sequence);
        Assert.True(released.Sequence > unchanged.Sequence);
        Assert.Equal(ControllerButtons.RightBumper, released.Buttons);
    }

    [Fact]
    public void Poll_DistinguishesAvailableApiFromConnectedController()
    {
        var poller = new XInputControllerPoller(new FakeBackend { IsAvailable = true });

        var snapshot = poller.Poll();

        Assert.True(snapshot.ApiAvailable);
        Assert.False(snapshot.IsConnected);
        Assert.Equal(ControllerButtons.None, snapshot.Buttons);
    }

    private sealed class FakeBackend : IXInputControllerBackend
    {
        internal Dictionary<uint, ControllerButtons> Users { get; } = new();

        public bool IsAvailable { get; init; }

        public bool TryGetButtons(uint userIndex, out ControllerButtons buttons) =>
            Users.TryGetValue(userIndex, out buttons);
    }
}
