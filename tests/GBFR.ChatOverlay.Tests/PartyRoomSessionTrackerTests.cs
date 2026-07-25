using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyRoomSessionTrackerTests
{
    private static readonly nint Network = (nint)0x1000;
    private static readonly nint LocalUser = (nint)0x2000;
    private static readonly nint Endpoint = (nint)0x3000;

    [Fact]
    public void AuthenticationAloneDoesNotOpenTheRoomGate()
    {
        var tracker = new PartyRoomSessionTracker();

        tracker.Observe(Authentication());

        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void MatchingAuthenticatedGameplayEndpointOpensWithoutARemotePlayer()
    {
        var tracker = CreateActiveTracker();

        Assert.True(tracker.IsActive);
    }

    [Theory]
    [InlineData(PartyStateChangeType.DestroyEndpointCompleted)]
    [InlineData(PartyStateChangeType.EndpointDestroyed)]
    [InlineData(PartyStateChangeType.LocalUserRemoved)]
    [InlineData(PartyStateChangeType.LocalUserKicked)]
    [InlineData(PartyStateChangeType.RemoveLocalUserCompleted)]
    [InlineData(PartyStateChangeType.DestroyLocalUserCompleted)]
    [InlineData(PartyStateChangeType.LeaveNetworkCompleted)]
    [InlineData(PartyStateChangeType.NetworkDestroyed)]
    public void MatchingRoomTeardownEventClosesTheGate(PartyStateChangeType type)
    {
        var tracker = CreateActiveTracker();

        tracker.Observe(Teardown(type));

        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void SuccessfulLeaveCallClosesBeforeCompletionEventsArrive()
    {
        var tracker = CreateActiveTracker();

        tracker.MarkNetworkLeaveQueued(Network);

        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void UnrelatedRemoteOrStaleNetworkEventsDoNotCloseTheLocalRoom()
    {
        var tracker = CreateActiveTracker();

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.EndpointDestroyed)
        {
            Network = Network,
            Endpoint = (nint)0x9999,
        });
        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.NetworkDestroyed)
        {
            Network = (nint)0x8888,
        });

        Assert.True(tracker.IsActive);
    }

    [Fact]
    public void FailedLeaveCompletionDoesNotCloseAnOtherwiseActiveRoom()
    {
        var tracker = CreateActiveTracker();

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.LeaveNetworkCompleted)
        {
            Result = 1,
            Network = Network,
        });

        Assert.True(tracker.IsActive);
    }

    [Theory]
    [InlineData(PartyStateChangeType.DestroyEndpointCompleted)]
    [InlineData(PartyStateChangeType.RemoveLocalUserCompleted)]
    [InlineData(PartyStateChangeType.DestroyLocalUserCompleted)]
    public void FailedAsyncTeardownDoesNotCloseAnOtherwiseActiveRoom(PartyStateChangeType type)
    {
        var tracker = CreateActiveTracker();

        var failed = Teardown(type) with { Result = 1 };
        tracker.Observe(failed);

        Assert.True(tracker.IsActive);
    }

    [Fact]
    public void MismatchedLocalUserAndNetworkTeardownDoNotCloseTheRoom()
    {
        var tracker = CreateActiveTracker();

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalUserRemoved)
        {
            Network = Network,
            LocalUser = (nint)0x9999,
        });
        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.RemoveLocalUserCompleted)
        {
            Result = 0,
            Network = (nint)0x8888,
            LocalUser = LocalUser,
        });

        Assert.True(tracker.IsActive);
    }

    [Fact]
    public void FailedOrMismatchedEndpointCreationDoesNotOpenTheRoom()
    {
        var tracker = new PartyRoomSessionTracker();
        tracker.Observe(Authentication());

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateEndpointCompleted)
        {
            Result = 1,
            Network = Network,
            LocalUser = LocalUser,
            Endpoint = Endpoint,
        });
        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateEndpointCompleted)
        {
            Result = 0,
            Network = (nint)0x7777,
            LocalUser = LocalUser,
            Endpoint = Endpoint,
        });

        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void FailedAuthenticationForTheCurrentLocalUserClosesTheRoom()
    {
        var tracker = CreateActiveTracker();

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.AuthenticateLocalUserCompleted)
        {
            Result = 1,
            Network = Network,
            LocalUser = LocalUser,
        });

        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void ManagerCleanupResetClosesTheRoom()
    {
        var tracker = CreateActiveTracker();

        tracker.Reset();

        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void NewAuthenticationReplacesAndClosesThePreviousRoomUntilItsEndpointExists()
    {
        var tracker = CreateActiveTracker();

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.AuthenticateLocalUserCompleted)
        {
            Result = 0,
            Network = (nint)0x4000,
            LocalUser = (nint)0x5000,
        });

        Assert.False(tracker.IsActive);
    }

    private static PartyRoomSessionTracker CreateActiveTracker()
    {
        var tracker = new PartyRoomSessionTracker();
        tracker.Observe(Authentication());
        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateEndpointCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
            Endpoint = Endpoint,
        });
        Assert.True(tracker.IsActive);
        return tracker;
    }

    private static PartyStateChangeSnapshot Authentication() =>
        new((uint)PartyStateChangeType.AuthenticateLocalUserCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
        };

    private static PartyStateChangeSnapshot Teardown(PartyStateChangeType type) =>
        new((uint)type)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
            Endpoint = Endpoint,
        };
}
