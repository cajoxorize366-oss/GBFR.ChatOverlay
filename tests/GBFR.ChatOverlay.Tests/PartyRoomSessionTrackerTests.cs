using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyRoomSessionTrackerTests
{
    private static readonly nint Network = (nint)0x1000;
    private static readonly nint LocalUser = (nint)0x2000;
    private static readonly nint Endpoint = (nint)0x3000;
    private static readonly nint StateManager = (nint)0x4000;

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

    [Fact]
    public void FirstActiveBatchTreatsExistingRemoteEndpointsAsBaseline()
    {
        var existingEndpoint = (nint)0x5000;
        var joiningEndpoint = (nint)0x6000;
        var api = new FakeEndpointApi();
        api.EntityIds[existingEndpoint] = "existing-player";
        api.EntityIds[joiningEndpoint] = "joining-player";
        var identitySnapshot = new RelinkPartyMemberIdentitySnapshot(
            ["existing-player", "", "local-player", ""],
            LocalMemberSlot: 2);
        var tracker = new PartyRoomSessionTracker();
        tracker.ConfigureMemberTracking(api, () => identitySnapshot);

        tracker.BeginStateChangeBatch(StateManager);
        tracker.Observe(Authentication());
        tracker.Observe(EndpointCreation());
        tracker.Observe(RemoteEndpointCreated(existingEndpoint));
        tracker.OnBatchFinished(StateManager);

        Assert.True(tracker.IsActive);
        Assert.True(tracker.TryReadMemberTransition(out var baseline));
        Assert.Equal(PartyMemberTransitionKind.Baseline, baseline.Kind);
        Assert.Equal(1, baseline.RemotePlayerOrdinal);
        Assert.Equal("existing-player", baseline.EntityId);
        Assert.False(tracker.TryReadMemberTransition(out _));

        identitySnapshot = new RelinkPartyMemberIdentitySnapshot(
            ["existing-player", "joining-player", "local-player", ""],
            LocalMemberSlot: 2);
        tracker.BeginStateChangeBatch(StateManager);
        tracker.Observe(RemoteEndpointCreated(joiningEndpoint));
        tracker.OnBatchFinished(StateManager);

        Assert.True(tracker.TryReadMemberTransition(out var joined));
        Assert.Equal(PartyMemberTransitionKind.Joined, joined.Kind);
        Assert.Equal(2, joined.RemotePlayerOrdinal);
        Assert.Equal("joining-player", joined.EntityId);
        Assert.False(tracker.TryReadMemberTransition(out _));
    }

    [Fact]
    public void ResetMemberTransitions_RearmsActiveRoomOnNextFinishedBatch()
    {
        var api = new FakeEndpointApi();
        var identitySnapshot = new RelinkPartyMemberIdentitySnapshot(
            ["existing-player", "", "local-player", ""],
            LocalMemberSlot: 2);
        var tracker = new PartyRoomSessionTracker();
        tracker.ConfigureMemberTracking(api, () => identitySnapshot);

        tracker.BeginStateChangeBatch(StateManager);
        tracker.Observe(Authentication());
        tracker.Observe(EndpointCreation());
        tracker.OnBatchFinished(StateManager);

        Assert.Equal(
            PartyMemberTransitionKind.Baseline,
            ReadSingleMemberTransition(tracker).Kind);

        tracker.ResetMemberTransitions();
        Assert.False(tracker.TryReadMemberTransition(out _));

        tracker.BeginStateChangeBatch(StateManager);
        tracker.OnBatchFinished(StateManager);

        var baseline = ReadSingleMemberTransition(tracker);
        Assert.Equal(PartyMemberTransitionKind.Baseline, baseline.Kind);
        Assert.Equal(1, baseline.RemotePlayerOrdinal);
        Assert.Equal("existing-player", baseline.EntityId);
    }

    [Fact]
    public void CancelStateChangeBatch_RearmsActiveRoomOnNextFinishedBatch()
    {
        var api = new FakeEndpointApi();
        var identitySnapshot = new RelinkPartyMemberIdentitySnapshot(
            ["existing-player", "", "local-player", ""],
            LocalMemberSlot: 2);
        var tracker = new PartyRoomSessionTracker();
        tracker.ConfigureMemberTracking(api, () => identitySnapshot);

        tracker.BeginStateChangeBatch(StateManager);
        tracker.Observe(Authentication());
        tracker.Observe(EndpointCreation());
        tracker.OnBatchFinished(StateManager);
        ReadSingleMemberTransition(tracker);

        tracker.BeginStateChangeBatch(StateManager);
        tracker.CancelStateChangeBatch(StateManager);
        Assert.False(tracker.TryReadMemberTransition(out _));

        tracker.BeginStateChangeBatch(StateManager);
        tracker.OnBatchFinished(StateManager);

        var baseline = ReadSingleMemberTransition(tracker);
        Assert.Equal(PartyMemberTransitionKind.Baseline, baseline.Kind);
        Assert.Equal("existing-player", baseline.EntityId);
    }

    [Fact]
    public void CancelStateChangeBatch_WhileInactive_DiscardsObservedEndpointBeforeActivation()
    {
        var staleEndpoint = (nint)0x5000;
        var api = new FakeEndpointApi();
        api.EntityIds[staleEndpoint] = "stale-player";
        var identitySnapshot = new RelinkPartyMemberIdentitySnapshot(
            ["stale-player", "", "local-player", ""],
            LocalMemberSlot: 2);
        var tracker = new PartyRoomSessionTracker();
        tracker.ConfigureMemberTracking(api, () => identitySnapshot);

        tracker.BeginStateChangeBatch(StateManager);
        tracker.Observe(Authentication());
        tracker.Observe(RemoteEndpointCreated(staleEndpoint));
        tracker.CancelStateChangeBatch(StateManager);

        identitySnapshot = new RelinkPartyMemberIdentitySnapshot(
            ["", "", "local-player", ""],
            LocalMemberSlot: 2);
        tracker.BeginStateChangeBatch(StateManager);
        tracker.Observe(EndpointCreation());
        tracker.OnBatchFinished(StateManager);

        identitySnapshot = new RelinkPartyMemberIdentitySnapshot(
            ["stale-player", "", "local-player", ""],
            LocalMemberSlot: 2);

        Assert.False(tracker.TryReadMemberTransition(out _));
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuccessfulGracefulLeave_WithUnknownOrMissingIdentity_ReportsSelfLeft(
        bool useExplicitUnknownIdentity)
    {
        var tracker = CreateActiveTracker();
        ConsumeEntered(tracker);

        var identity = useExplicitUnknownIdentity
            ? new PartyRoomIdentitySnapshot("Quest Room", PartyRoomHostState.Unknown)
            : default(PartyRoomIdentitySnapshot?);
        tracker.MarkNetworkLeaveQueued(Network, identity);
        tracker.Observe(LeaveCompleted());

        Assert.True(tracker.TryReadTransition(out var exited));
        Assert.Equal(PartyRoomExitReason.SelfLeft, exited.ExitReason);
        Assert.Equal(useExplicitUnknownIdentity ? "Quest Room" : null, exited.RoomName);
        Assert.False(tracker.TryReadTransition(out _));
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

    [Fact]
    public void CreateCompletionThenAuthenticationThenEndpoint_ExposesCreated()
    {
        var tracker = new PartyRoomSessionTracker();
        tracker.Observe(CreateNewCompleted());
        tracker.Observe(Authentication());
        tracker.Observe(EndpointCreation());

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Created, tracker.LocalNetworkRole);
    }

    [Fact]
    public void ConnectCompletionThenAuthenticationThenEndpoint_ExposesConnected()
    {
        var tracker = new PartyRoomSessionTracker();
        tracker.Observe(ConnectCompleted());
        tracker.Observe(Authentication());
        tracker.Observe(EndpointCreation());

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Connected, tracker.LocalNetworkRole);
    }

    [Fact]
    public void AuthenticationWithoutRoleCompletion_ExposesUnknown()
    {
        var tracker = CreateActiveTracker();

        Assert.Equal(PartyNetworkLocalRole.Unknown, tracker.LocalNetworkRole);
    }

    [Fact]
    public void DuplicateAuthentication_KeepsBoundCreatedRole()
    {
        var tracker = CreateCreatedHostTracker();

        tracker.Observe(Authentication());

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Created, tracker.LocalNetworkRole);
    }

    [Fact]
    public void DuplicateAuthentication_KeepsBoundConnectedRole()
    {
        var tracker = CreateConnectedGuestTracker();

        tracker.Observe(Authentication());

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Connected, tracker.LocalNetworkRole);
    }

    [Fact]
    public void DuplicateCreateCompletion_DoesNotLeakCreatedRoleIntoNextNetwork()
    {
        var tracker = CreateCreatedHostTracker();
        tracker.Observe(CreateNewCompleted());

        var replacementNetwork = (nint)0x4000;
        tracker.Observe(Authentication() with { Network = replacementNetwork });
        tracker.Observe(EndpointCreation() with { Network = replacementNetwork });

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Unknown, tracker.LocalNetworkRole);
    }

    [Fact]
    public void UnmatchedPendingConnect_IsDiscardedWhenAnotherSessionAuthenticates()
    {
        var tracker = new PartyRoomSessionTracker();
        var unrelatedNetwork = (nint)0x4000;
        tracker.Observe(ConnectCompleted() with { Network = unrelatedNetwork });
        tracker.Observe(CreateNewCompleted());
        tracker.Observe(Authentication());
        tracker.Observe(EndpointCreation());
        Assert.Equal(PartyNetworkLocalRole.Created, tracker.LocalNetworkRole);

        var replacementUser = (nint)0x5000;
        tracker.Observe(Authentication() with
        {
            Network = unrelatedNetwork,
            LocalUser = replacementUser,
        });
        tracker.Observe(EndpointCreation() with
        {
            Network = unrelatedNetwork,
            LocalUser = replacementUser,
        });

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Unknown, tracker.LocalNetworkRole);
    }

    [Fact]
    public void MismatchedAuthentication_ClearsCreatedRoleAndClosesRoom()
    {
        var tracker = CreateCreatedHostTracker();

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.AuthenticateLocalUserCompleted)
        {
            Result = 0,
            Network = (nint)0x4000,
            LocalUser = (nint)0x5000,
        });

        Assert.False(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Unknown, tracker.LocalNetworkRole);
    }

    [Fact]
    public void FailedCreateCompletion_AfterRoleBound_ClearsOnlyTheHostRole()
    {
        var tracker = CreateCreatedHostTracker();

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateNewNetworkCompleted)
        {
            Result = 1,
            LocalUser = LocalUser,
        });

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Unknown, tracker.LocalNetworkRole);
        Assert.True(tracker.TryReadTransition(out var entered));
        Assert.Equal(PartyRoomTransitionKind.Entered, entered.Kind);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void FailedConnectCompletion_AfterRoleBound_ClearsOnlyTheHostRole()
    {
        var tracker = CreateConnectedGuestTracker();

        tracker.Observe(new PartyStateChangeSnapshot((uint)PartyStateChangeType.ConnectToNetworkCompleted)
        {
            Result = 1,
            Network = Network,
        });

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Unknown, tracker.LocalNetworkRole);
        Assert.True(tracker.TryReadTransition(out var entered));
        Assert.Equal(PartyRoomTransitionKind.Entered, entered.Kind);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void ConnectCompletion_AfterCreatedRole_KeepsCreatedRole()
    {
        var tracker = CreateCreatedHostTracker();

        tracker.Observe(ConnectCompleted());

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Created, tracker.LocalNetworkRole);
    }

    [Fact]
    public void CreateCompletion_AfterConnectedRole_UpgradesToCreatedWithoutClosingRoom()
    {
        var tracker = CreateConnectedGuestTracker();

        tracker.Observe(CreateNewCompleted());

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Created, tracker.LocalNetworkRole);
        Assert.True(tracker.TryReadTransition(out var entered));
        Assert.Equal(PartyRoomTransitionKind.Entered, entered.Kind);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void CreateThenConnectBeforeAuthentication_ExposesCreated()
    {
        var tracker = new PartyRoomSessionTracker();
        tracker.Observe(CreateNewCompleted());
        tracker.Observe(ConnectCompleted());
        tracker.Observe(Authentication());
        tracker.Observe(EndpointCreation());

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Created, tracker.LocalNetworkRole);
    }

    [Fact]
    public void ConnectThenCreateBeforeAuthentication_ExposesCreated()
    {
        var tracker = new PartyRoomSessionTracker();
        tracker.Observe(ConnectCompleted());
        tracker.Observe(CreateNewCompleted());
        tracker.Observe(Authentication());
        tracker.Observe(EndpointCreation());

        Assert.True(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Created, tracker.LocalNetworkRole);
    }

    [Fact]
    public void Reset_ClearsLocalNetworkRole()
    {
        var tracker = CreateCreatedHostTracker();

        tracker.Reset();

        Assert.Equal(PartyNetworkLocalRole.Unknown, tracker.LocalNetworkRole);
    }

    [Fact]
    public void MarkNetworkLeaveQueued_ClearsLocalNetworkRole()
    {
        var tracker = CreateCreatedHostTracker();

        tracker.MarkNetworkLeaveQueued(Network);

        Assert.Equal(PartyNetworkLocalRole.Unknown, tracker.LocalNetworkRole);
    }

    [Fact]
    public void CleanupReset_ClearsLocalNetworkRole()
    {
        var tracker = CreateCreatedHostTracker();

        tracker.ResetPreservingTransitions();

        Assert.False(tracker.IsActive);
        Assert.Equal(PartyNetworkLocalRole.Unknown, tracker.LocalNetworkRole);
    }

    private static void ConsumeEntered(PartyRoomSessionTracker tracker)
    {
        Assert.True(tracker.TryReadTransition(out var entered));
        Assert.Equal(PartyRoomTransitionKind.Entered, entered.Kind);
    }

    private static PartyMemberTransition ReadSingleMemberTransition(PartyRoomSessionTracker tracker)
    {
        Assert.True(tracker.TryReadMemberTransition(out var transition));
        Assert.False(tracker.TryReadMemberTransition(out _));
        return transition;
    }

    private static PartyRoomSessionTracker CreateCreatedHostTracker()
    {
        var tracker = new PartyRoomSessionTracker();
        tracker.Observe(CreateNewCompleted());
        tracker.Observe(Authentication());
        tracker.Observe(EndpointCreation());
        Assert.True(tracker.IsActive);
        return tracker;
    }

    private static PartyRoomSessionTracker CreateConnectedGuestTracker()
    {
        var tracker = new PartyRoomSessionTracker();
        tracker.Observe(ConnectCompleted());
        tracker.Observe(Authentication());
        tracker.Observe(EndpointCreation());
        Assert.True(tracker.IsActive);
        return tracker;
    }

    private static PartyRoomSessionTracker CreateActiveTracker()
    {
        var tracker = new PartyRoomSessionTracker();
        tracker.Observe(Authentication());
        tracker.Observe(EndpointCreation());
        Assert.True(tracker.IsActive);
        return tracker;
    }

    private static PartyStateChangeSnapshot CreateNewCompleted() =>
        new((uint)PartyStateChangeType.CreateNewNetworkCompleted)
        {
            Result = 0,
            LocalUser = LocalUser,
        };

    private static PartyStateChangeSnapshot ConnectCompleted() =>
        new((uint)PartyStateChangeType.ConnectToNetworkCompleted)
        {
            Result = 0,
            Network = Network,
        };

    private static PartyStateChangeSnapshot EndpointCreation() =>
        new((uint)PartyStateChangeType.CreateEndpointCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
            Endpoint = Endpoint,
        };

    private static PartyStateChangeSnapshot Authentication() =>
        new((uint)PartyStateChangeType.AuthenticateLocalUserCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
        };

    private static PartyStateChangeSnapshot RemoteEndpointCreated(nint endpoint) =>
        new((uint)PartyStateChangeType.EndpointCreated)
        {
            Network = Network,
            Endpoint = endpoint,
        };

    private static PartyStateChangeSnapshot LeaveCompleted() =>
        new((uint)PartyStateChangeType.LeaveNetworkCompleted)
        {
            Result = 0,
            Network = Network,
        };

    private sealed class FakeEndpointApi : IPartyEndpointApi
    {
        internal Dictionary<nint, string> EntityIds { get; } = [];

        public uint IsEndpointLocal(nint endpoint, out bool isLocal)
        {
            isLocal = false;
            return 0;
        }

        public uint GetEndpointEntityId(nint endpoint, out string? entityId)
        {
            if (EntityIds.TryGetValue(endpoint, out var value))
            {
                entityId = value;
                return 0;
            }

            entityId = null;
            return 0x80000003;
        }
    }

    private static PartyStateChangeSnapshot Teardown(PartyStateChangeType type) =>
        new((uint)type)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
            Endpoint = Endpoint,
        };
}
