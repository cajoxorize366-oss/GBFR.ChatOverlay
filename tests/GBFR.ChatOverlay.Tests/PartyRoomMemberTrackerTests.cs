using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyRoomMemberTrackerTests
{
    private static readonly nint Network = (nint)0x1000;
    private static readonly nint Manager = (nint)0x2000;
    private static readonly nint RemoteEndpoint = (nint)0x3000;
    private static readonly nint SecondRemoteEndpoint = (nint)0x3100;
    private static readonly nint LocalEndpoint = (nint)0x4000;

    [Fact]
    public void BaselineEndpointBeforeActivation_DoesNotEmitJoinedOrLeft()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "baseline-player";
        api.EntityIds[SecondRemoteEndpoint] = "baseline-player";
        var tracker = new PartyRoomMemberTracker(api);

        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointCreated(Created(RemoteEndpoint));
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));

        tracker.ActivateRoom();
        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointCreated(Created(SecondRemoteEndpoint));
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));

        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(SecondRemoteEndpoint));
        tracker.OnBatchFinished(Manager);
        Assert.False(tracker.TryReadTransition(out _));

        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint));
        tracker.OnBatchFinished(Manager);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void AfterActivation_NewRemoteEndpoint_EmitsOneJoined()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "joined-player";
        api.EntityIds[SecondRemoteEndpoint] = "joined-player";
        var snapshot = Present("joined-player");
        var tracker = CreateActiveTracker(api, () => snapshot);

        var joined = ReadSingle(tracker);
        Assert.Equal(PartyMemberTransitionKind.Joined, joined.Kind);
        Assert.Equal("joined-player", joined.EntityId);
        Assert.Equal(1, joined.RemotePlayerOrdinal);

        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointCreated(Created(RemoteEndpoint));
        tracker.ObserveEndpointCreated(Created(SecondRemoteEndpoint));
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void DuplicateAndMultiEndpoint_DeduplicateAndEmitLeftOnlyAfterLastDestroyed()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "multi-endpoint";
        api.EntityIds[SecondRemoteEndpoint] = "multi-endpoint";
        var snapshot = Present("multi-endpoint");
        var tracker = CreateActiveTracker(api, () => snapshot);

        var joined = ReadSingle(tracker);
        Assert.Equal(PartyMemberTransitionKind.Joined, joined.Kind);

        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointCreated(Created(SecondRemoteEndpoint));
        tracker.OnBatchFinished(Manager);
        Assert.False(tracker.TryReadTransition(out _));

        snapshot = Empty();
        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint));
        tracker.OnBatchFinished(Manager);
        Assert.False(tracker.TryReadTransition(out _));

        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(SecondRemoteEndpoint, reason: 1, errorDetail: 0x22));
        tracker.OnBatchFinished(Manager);

        var left = ReadSingle(tracker);
        Assert.Equal(PartyMemberTransitionKind.Left, left.Kind);
        Assert.Equal("multi-endpoint", left.EntityId);
        Assert.Equal(PartyMemberLeaveReason.Disconnected, left.LeaveReason);
        Assert.Equal(1u, left.NativeReason);
        Assert.Equal(0x22u, left.ErrorDetail);
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 2u)]
    [InlineData(2u, 3u)]
    [InlineData(3u, 4u)]
    [InlineData(4u, 5u)]
    [InlineData(99u, 0u)]
    public void EndpointDestroyedReason_MapsOfficialValuesAndWaitsForSnapshotConfirmation(
        uint nativeReason,
        uint expectedReason)
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "reason-player";
        var snapshot = Present("reason-player");
        var tracker = CreateActiveTracker(api, () => snapshot);
        ReadSingle(tracker);

        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint, nativeReason, 0x1234));
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));

        snapshot = Empty();
        tracker.BeginStateChangeBatch(Manager);
        tracker.OnBatchFinished(Manager);

        var left = ReadSingle(tracker);
        Assert.Equal((PartyMemberLeaveReason)expectedReason, left.LeaveReason);
        Assert.Equal(nativeReason, left.NativeReason);
        Assert.Equal(0x1234u, left.ErrorDetail);
    }

    [Fact]
    public void EndpointDestroyed_SameBatchSnapshotStillHasMember_WaitsForCoherentAbsence()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "still-present";
        var snapshot = Present("still-present");
        var tracker = CreateActiveTracker(api, () => snapshot);
        ReadSingle(tracker);

        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint, reason: 1));
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));

        snapshot = Empty();
        tracker.BeginStateChangeBatch(Manager);
        tracker.OnBatchFinished(Manager);

        var left = ReadSingle(tracker);
        Assert.Equal(PartyMemberTransitionKind.Left, left.Kind);
        Assert.Equal(PartyMemberLeaveReason.Disconnected, left.LeaveReason);
    }

    [Fact]
    public void LocalEndpoint_IsIgnored()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[LocalEndpoint] = "local-player";
        api.Local.Add(LocalEndpoint);
        var tracker = new PartyRoomMemberTracker(api);
        tracker.BeginStateChangeBatch(Manager);
        tracker.ActivateRoom();
        tracker.ObserveEndpointCreated(Created(LocalEndpoint));
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void EndpointApiErrorOrEmptyEntityId_FailsClosed()
    {
        var api = new FakeEndpointApi();
        api.FailEntityId.Add(RemoteEndpoint);
        var tracker = new PartyRoomMemberTracker(api);
        tracker.BeginStateChangeBatch(Manager);
        tracker.ActivateRoom();
        tracker.ObserveEndpointCreated(Created(RemoteEndpoint));
        tracker.OnBatchFinished(Manager);
        Assert.False(tracker.TryReadTransition(out _));

        var emptyApi = new FakeEndpointApi();
        emptyApi.EntityIds[SecondRemoteEndpoint] = string.Empty;
        var emptyTracker = new PartyRoomMemberTracker(emptyApi);
        emptyTracker.BeginStateChangeBatch(Manager);
        emptyTracker.ActivateRoom();
        emptyTracker.ObserveEndpointCreated(Created(SecondRemoteEndpoint));
        emptyTracker.OnBatchFinished(Manager);
        Assert.False(emptyTracker.TryReadTransition(out _));
    }

    [Fact]
    public void DestroyedGetterFailure_UsesCachedEntityAndEmitsLeft()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "cached-player";
        var snapshot = Present("cached-player");
        var tracker = CreateActiveTracker(api, () => snapshot);
        ReadSingle(tracker);

        api.FailEntityId.Add(RemoteEndpoint);
        snapshot = Empty();
        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint, reason: 2));
        tracker.OnBatchFinished(Manager);

        var left = ReadSingle(tracker);
        Assert.Equal(PartyMemberTransitionKind.Left, left.Kind);
        Assert.Equal("cached-player", left.EntityId);
        Assert.Equal(PartyMemberLeaveReason.Kicked, left.LeaveReason);
    }

    [Fact]
    public void DestroyedGetterReportsDifferentEntity_FailsClosedWithoutLeft()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "cached-player";
        var snapshot = Present("cached-player");
        var tracker = CreateActiveTracker(api, () => snapshot);
        ReadSingle(tracker);

        api.EntityIds[RemoteEndpoint] = "different-player";
        snapshot = Empty();
        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint));
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void InactiveRoomAtBatchEnd_DiscardsPendingTeardownEvents()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "leaving-player";
        var snapshot = Present("leaving-player");
        var tracker = CreateActiveTracker(api, () => snapshot);
        ReadSingle(tracker);

        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint));
        tracker.DeactivateRoom();
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void JoinedWithoutResolvedOrdinal_IsNotReadable()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "remote-slot-zero";
        var snapshot = default(RelinkPartyMemberIdentitySnapshot);
        var tracker = new PartyRoomMemberTracker(api, () => snapshot);

        tracker.BeginStateChangeBatch(Manager);
        tracker.ActivateRoom();
        tracker.ObserveEndpointCreated(Created(RemoteEndpoint));
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));

        snapshot = Present("remote-slot-zero");

        var joined = ReadSingle(tracker);
        Assert.Equal(PartyMemberTransitionKind.Joined, joined.Kind);
        Assert.Equal(1, joined.RemotePlayerOrdinal);
    }

    [Fact]
    public void DelayedCoherentSnapshot_ResolvesJoinedThenLeftUsingCachedOrdinal()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "remote-slot-zero";
        var snapshot = default(RelinkPartyMemberIdentitySnapshot);
        var tracker = new PartyRoomMemberTracker(api, () => snapshot);
        tracker.BeginStateChangeBatch(Manager);
        tracker.ActivateRoom();
        tracker.ObserveEndpointCreated(Created(RemoteEndpoint));
        tracker.OnBatchFinished(Manager);

        snapshot = Present("remote-slot-zero");

        var joined = ReadSingle(tracker);
        Assert.Equal(PartyMemberTransitionKind.Joined, joined.Kind);
        Assert.Equal(1, joined.RemotePlayerOrdinal);

        snapshot = Empty();
        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint, reason: 1));
        tracker.OnBatchFinished(Manager);

        var left = ReadSingle(tracker);
        Assert.Equal(PartyMemberTransitionKind.Left, left.Kind);
        Assert.Equal(1, left.RemotePlayerOrdinal);
    }

    [Fact]
    public void SnapshotUnavailable_SuppressesLeftUntilAbsentSnapshotAvailable()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "snapshot-gap-player";
        var snapshot = Present("snapshot-gap-player");
        var tracker = CreateActiveTracker(api, () => snapshot);
        ReadSingle(tracker);

        snapshot = default;
        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint));
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));

        snapshot = Empty();
        tracker.BeginStateChangeBatch(Manager);
        tracker.OnBatchFinished(Manager);

        var left = ReadSingle(tracker);
        Assert.Equal(PartyMemberTransitionKind.Left, left.Kind);
    }

    [Fact]
    public void EndpointRecreatedAfterDestroy_CancelsLeaveCandidate()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "recreated-player";
        api.EntityIds[SecondRemoteEndpoint] = "recreated-player";
        var snapshot = Present("recreated-player");
        var tracker = CreateActiveTracker(api, () => snapshot);
        ReadSingle(tracker);

        snapshot = Empty();
        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint));
        tracker.ObserveEndpointCreated(Created(SecondRemoteEndpoint));
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));

        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(SecondRemoteEndpoint, reason: 2));
        tracker.OnBatchFinished(Manager);

        var left = ReadSingle(tracker);
        Assert.Equal(PartyMemberTransitionKind.Left, left.Kind);
        Assert.Equal("recreated-player", left.EntityId);
        Assert.Equal(PartyMemberLeaveReason.Kicked, left.LeaveReason);
    }

    [Fact]
    public void DuplicateDestroyedState_DoesNotEmitSecondLeft()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "repeat-left";
        var snapshot = Present("repeat-left");
        var tracker = CreateActiveTracker(api, () => snapshot);
        ReadSingle(tracker);

        snapshot = Empty();
        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint));
        tracker.OnBatchFinished(Manager);
        Assert.Equal(PartyMemberTransitionKind.Left, ReadSingle(tracker).Kind);

        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint));
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void Reset_AndNewNetwork_ClearPublishedAndPendingEvents()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "old-network-player";
        var snapshot = Present("old-network-player");
        var tracker = CreateActiveTracker(api, () => snapshot);
        var oldJoined = ReadSingle(tracker);
        Assert.Equal("old-network-player", oldJoined.EntityId);

        tracker.Reset();

        api.EntityIds[SecondRemoteEndpoint] = "new-network-player";
        snapshot = Present("new-network-player");
        tracker.BeginStateChangeBatch(Manager);
        tracker.ActivateRoom();
        tracker.ObserveEndpointCreated(Created(SecondRemoteEndpoint));
        tracker.OnBatchFinished(Manager);

        var joined = ReadSingle(tracker);
        Assert.Equal("new-network-player", joined.EntityId);
        Assert.False(tracker.TryReadTransition(out _));
    }

    [Fact]
    public void CancelStateChangeBatch_ClearsUnpublishedLeaveCandidate()
    {
        var api = new FakeEndpointApi();
        api.EntityIds[RemoteEndpoint] = "cancel-left";
        var snapshot = Present("cancel-left");
        var tracker = CreateActiveTracker(api, () => snapshot);
        ReadSingle(tracker);

        snapshot = Empty();
        tracker.BeginStateChangeBatch(Manager);
        tracker.ObserveEndpointDestroyed(Destroyed(RemoteEndpoint));
        tracker.CancelStateChangeBatch(Manager);

        tracker.BeginStateChangeBatch(Manager);
        tracker.OnBatchFinished(Manager);

        Assert.False(tracker.TryReadTransition(out _));
    }

    private static PartyRoomMemberTracker CreateActiveTracker(
        FakeEndpointApi api,
        Func<RelinkPartyMemberIdentitySnapshot>? identitySnapshotReader = null)
    {
        var tracker = new PartyRoomMemberTracker(api, identitySnapshotReader);
        tracker.BeginStateChangeBatch(Manager);
        tracker.ActivateRoom();
        tracker.ObserveEndpointCreated(Created(RemoteEndpoint));
        tracker.OnBatchFinished(Manager);
        return tracker;
    }

    private static PartyMemberTransition ReadSingle(PartyRoomMemberTracker tracker)
    {
        Assert.True(tracker.TryReadTransition(out var transition));
        Assert.False(tracker.TryReadTransition(out _));
        return transition;
    }

    private static RelinkPartyMemberIdentitySnapshot Present(string entityId) =>
        new([entityId, "", "local-player", ""], LocalMemberSlot: 2);

    private static RelinkPartyMemberIdentitySnapshot Empty() =>
        new(["", "", "local-player", ""], LocalMemberSlot: 2);

    private static PartyStateChangeSnapshot Created(nint endpoint) =>
        new((uint)PartyStateChangeType.EndpointCreated)
        {
            Network = Network,
            Endpoint = endpoint,
        };

    private static PartyStateChangeSnapshot Destroyed(nint endpoint, uint reason = 0, uint errorDetail = 0) =>
        new((uint)PartyStateChangeType.EndpointDestroyed)
        {
            Network = Network,
            Endpoint = endpoint,
            Reason = reason,
            ErrorDetail = errorDetail,
        };

    private sealed class FakeEndpointApi : IPartyEndpointApi
    {
        internal Dictionary<nint, string> EntityIds { get; } = [];

        internal HashSet<nint> Local { get; } = [];

        internal HashSet<nint> FailEntityId { get; } = [];

        public uint IsEndpointLocal(nint endpoint, out bool isLocal)
        {
            isLocal = Local.Contains(endpoint);
            return 0;
        }

        public uint GetEndpointEntityId(nint endpoint, out string? entityId)
        {
            entityId = null;
            if (FailEntityId.Contains(endpoint))
                return 0x80000002;
            if (!EntityIds.TryGetValue(endpoint, out var value))
                return 0x80000003;

            entityId = value;
            return 0;
        }
    }
}