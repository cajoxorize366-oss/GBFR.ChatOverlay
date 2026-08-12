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

    [Fact]
    public void EnteredTransition_IsQueuedOnceOnActivation()
    {
        var tracker = CreateActiveTracker();

        Assert.True(tracker.TryReadTransition(out var entered));
        Assert.Equal(PartyRoomTransitionKind.Entered, entered.Kind);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void DuplicateMatchingEndpointCreation_DoesNotQueueAnotherEntered()
    {
        var tracker = CreateActiveTracker();

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateEndpointCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
            Endpoint = Endpoint,
        });

        Assert.True(tracker.TryReadTransition(out var entered));
        Assert.Equal(PartyRoomTransitionKind.Entered, entered.Kind);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuccessfulLeave_WithKnownPresentHost_ReportsSelfLeft(bool localHost)
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);
        var hostState = localHost
            ? PartyRoomHostState.LocalHost
            : PartyRoomHostState.RemoteHostPresent;

        tracker.MarkNetworkLeaveQueued(
            Network,
            new PartyRoomIdentitySnapshot("Quest Room", hostState));
        tracker.Observe(LeaveCompleted());

        Assert.False(tracker.IsActive);
        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomTransitionKind.Exited, exited.Kind);
        Assert.Equal(PartyRoomExitReason.SelfLeft, exited.ExitReason);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void SuccessfulLeave_WhenRemoteHostMissing_ReportsHostDisconnected()
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);

        tracker.MarkNetworkLeaveQueued(
            Network,
            new PartyRoomIdentitySnapshot("Quest Room", PartyRoomHostState.RemoteHostMissing));
        tracker.Observe(LeaveCompleted());

        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomExitReason.HostDisconnected, exited.ExitReason);
        Assert.Equal("Quest Room", exited.RoomName);
    }

    [Fact]
    public void SuccessfulLeave_DoesNotPublishUntilCompletion()
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);

        tracker.MarkNetworkLeaveQueued(
            Network,
            new PartyRoomIdentitySnapshot("Quest Room", PartyRoomHostState.RemoteHostPresent));

        Assert.False(tracker.IsActive);
        Assert.False(tracker.TryReadTransition(out _));

        tracker.Observe(LeaveCompleted());

        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomExitReason.SelfLeft, exited.ExitReason);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void SuccessfulLeave_UnknownHostState_ReportsNetworkInterrupted()
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);

        tracker.MarkNetworkLeaveQueued(Network, default);
        tracker.Observe(LeaveCompleted());

        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomExitReason.NetworkInterrupted, exited.ExitReason);
    }

    [Fact]
    public void QueuedLeave_ThenMatchingKick_ReportsOnlyKicked()
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);

        tracker.MarkNetworkLeaveQueued(Network);
        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalUserKicked)
        {
            Network = Network,
            LocalUser = LocalUser,
        });

        Assert.False(tracker.IsActive);
        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomExitReason.Kicked, exited.ExitReason);

        tracker.Observe(LeaveCompleted());

        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void FailedAuthentication_WhileLeaveQueued_FinalizesPendingLeave()
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);
        tracker.MarkNetworkLeaveQueued(
            Network,
            new PartyRoomIdentitySnapshot("Quest Room", PartyRoomHostState.RemoteHostPresent));

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.AuthenticateLocalUserCompleted)
        {
            Result = 1,
            Network = Network,
            LocalUser = LocalUser,
        });

        Assert.False(tracker.IsActive);
        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomExitReason.SelfLeft, exited.ExitReason);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void ReplacementAuthentication_WhileLeaveQueued_FinalizesPendingLeave()
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);
        tracker.MarkNetworkLeaveQueued(
            Network,
            new PartyRoomIdentitySnapshot("Quest Room", PartyRoomHostState.RemoteHostPresent));

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.AuthenticateLocalUserCompleted)
        {
            Result = 0,
            Network = (nint)0x4000,
            LocalUser = (nint)0x5000,
        });

        Assert.False(tracker.IsActive);
        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomExitReason.SelfLeft, exited.ExitReason);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void DuplicateSuccessfulAuthentication_WhileLeaveQueued_DoesNotFinalizePendingLeave()
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);
        tracker.MarkNetworkLeaveQueued(
            Network,
            new PartyRoomIdentitySnapshot("Quest Room", PartyRoomHostState.RemoteHostPresent));

        tracker.Observe(Authentication());

        Assert.False(tracker.IsActive);
        Assert.False(tracker.TryReadTransition(out _));

        tracker.Observe(LeaveCompleted());

        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomExitReason.SelfLeft, exited.ExitReason);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void CleanupReset_PreservesQueuedTransitionAndFinalizesPendingLeave()
    {
        var tracker = CreateActiveTracker();
        tracker.MarkNetworkLeaveQueued(
            Network,
            new PartyRoomIdentitySnapshot("Quest Room", PartyRoomHostState.RemoteHostPresent));

        tracker.ResetPreservingTransitions();

        Assert.False(tracker.IsActive);
        Assert.True(tracker.TryReadTransition(out var entered));
        Assert.Equal(PartyRoomTransitionKind.Entered, entered.Kind);
        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomExitReason.SelfLeft, exited.ExitReason);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void CleanupReset_ActiveRoomWithoutPriorTeardownReportsNetworkInterrupted()
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);

        tracker.ResetPreservingTransitions(
            new PartyRoomIdentitySnapshot("Quest Room", PartyRoomHostState.RemoteHostPresent));

        Assert.False(tracker.IsActive);
        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomTransitionKind.Exited, exited.Kind);
        Assert.Equal(PartyRoomExitReason.NetworkInterrupted, exited.ExitReason);
        Assert.Equal("Quest Room", exited.RoomName);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void LocalUserKicked_ReportsKicked()
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalUserKicked)
        {
            Network = Network,
            LocalUser = LocalUser,
        });

        Assert.False(tracker.IsActive);
        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomExitReason.Kicked, exited.ExitReason);
    }

    [Theory]
    [InlineData(PartyStateChangeType.EndpointDestroyed)]
    [InlineData(PartyStateChangeType.NetworkDestroyed)]
    public void DisconnectedReasonOne_ReportsNetworkInterrupted(PartyStateChangeType type)
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);

        tracker.Observe(new PartyStateChangeSnapshot((uint)type)
        {
            Reason = 1,
            Network = Network,
            Endpoint = Endpoint,
        });

        Assert.False(tracker.IsActive);
        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomExitReason.NetworkInterrupted, exited.ExitReason);
        Assert.Equal(1u, exited.NativeReason);
    }

    [Fact]
    public void FollowOnTeardownAfterLeave_DoesNotQueueDuplicateExit()
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);

        tracker.MarkNetworkLeaveQueued(Network);
        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.LeaveNetworkCompleted)
        {
            Result = 0,
            Network = Network,
        });
        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.NetworkDestroyed)
        {
            Network = Network,
        });

        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomTransitionKind.Exited, exited.Kind);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void MismatchedTeardown_DoesNotQueueTransition()
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);

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
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void Reset_ClearsTransitionsWithoutExitPrompt()
    {
        var tracker = CreateActiveTracker();

        tracker.Reset();

        Assert.False(tracker.IsActive);
        Assert.False(tracker.TryReadTransition(out _));
    }

    private static void ConsumeEntered(PartyRoomSessionTracker tracker)
    {
        Assert.True(tracker.TryReadTransition(out var entered));
        Assert.Equal(PartyRoomTransitionKind.Entered, entered.Kind);
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

    private static PartyStateChangeSnapshot LeaveCompleted() =>
        new((uint)PartyStateChangeType.LeaveNetworkCompleted)
        {
            Result = 0,
            Network = Network,
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
