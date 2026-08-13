using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

internal sealed class PartyRoomMemberTracker
{
    private const uint Success = 0;

    private readonly IPartyEndpointApi _api;
    private readonly Func<RelinkPartyMemberIdentitySnapshot>? _identitySnapshotReader;
    private readonly object _sync = new();
    private readonly Queue<PartyMemberTransition> _transitions = new();
    private readonly Queue<PartyMemberTransition> _pendingTransitions = new();
    private readonly Dictionary<nint, string> _entityIdByEndpoint = new();
    private readonly Dictionary<string, HashSet<nint>> _endpointsByEntityId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openEntityIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publishedMemberEntityIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingMemberEntityIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _remoteOrdinalByEntityId = new(StringComparer.Ordinal);
    private readonly Queue<LeaveCandidate> _leaveCandidates = new();

    private readonly record struct LeaveCandidate(
        string EntityId,
        PartyMemberLeaveReason LeaveReason,
        uint NativeReason,
        uint ErrorDetail);

    private nint _network;
    private nint _stateBatchManager;
    private bool _stateBatchActive;
    private bool _roomActive;

    internal PartyRoomMemberTracker(
        IPartyEndpointApi api,
        Func<RelinkPartyMemberIdentitySnapshot>? identitySnapshotReader = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _identitySnapshotReader = identitySnapshotReader;
    }

    internal bool TryReadTransition(out PartyMemberTransition transition)
    {
        lock (_sync)
        {
            if (!_stateBatchActive && _roomActive)
            {
                TryPublishReadyTransitionsLocked();
                TryConfirmLeaveCandidatesLocked();
            }

            if (_transitions.Count == 0)
            {
                transition = default;
                return false;
            }

            transition = _transitions.Dequeue();
            return true;
        }
    }

    internal void BeginStateChangeBatch(nint manager)
    {
        lock (_sync)
        {
            if (manager == nint.Zero)
                return;

            if (_stateBatchActive && _stateBatchManager != manager)
            {
                _pendingTransitions.Clear();
                _pendingMemberEntityIds.Clear();
                _leaveCandidates.Clear();
            }

            _stateBatchActive = true;
            _stateBatchManager = manager;
        }
    }

    internal void CancelStateChangeBatch(nint manager)
    {
        lock (_sync)
        {
            if (!_stateBatchActive || _stateBatchManager != manager)
                return;

            _stateBatchActive = false;
            _stateBatchManager = nint.Zero;
            _pendingTransitions.Clear();
            _pendingMemberEntityIds.Clear();
            _leaveCandidates.Clear();
        }
    }

    internal void OnBatchFinished(nint manager)
    {
        lock (_sync)
        {
            if (!_stateBatchActive || _stateBatchManager != manager)
                return;

            _stateBatchActive = false;
            _stateBatchManager = nint.Zero;
            if (!_roomActive)
            {
                _pendingTransitions.Clear();
                _pendingMemberEntityIds.Clear();
                _leaveCandidates.Clear();
                return;
            }

            TryPublishReadyTransitionsLocked();
            TryConfirmLeaveCandidatesLocked();
        }
    }

    internal void ActivateRoom()
    {
        lock (_sync)
        {
            if (_roomActive)
                return;

            _roomActive = true;
            QueueSnapshotBaselineTransitionsLocked();
            foreach (var entityId in _openEntityIds.Order(StringComparer.Ordinal))
                QueuePendingMemberTransitionLocked(PartyMemberTransitionKind.Baseline, entityId);
            if (!_stateBatchActive)
                TryPublishReadyTransitionsLocked();
        }
    }

    internal void DeactivateRoom()
    {
        lock (_sync)
        {
            _roomActive = false;
            _pendingTransitions.Clear();
            _pendingMemberEntityIds.Clear();
            _leaveCandidates.Clear();
        }
    }

    internal void Reset()
    {
        lock (_sync)
            ResetLocked();
    }

    internal void ObserveEndpointCreated(in PartyStateChangeSnapshot state)
    {
        lock (_sync)
        {
            if (!_stateBatchActive || state.Network == nint.Zero || state.Endpoint == nint.Zero)
                return;

            if (_network == nint.Zero)
                _network = state.Network;
            else if (_network != state.Network)
                return;

            if (!TryInspectEndpointLocked(state.Endpoint, out var isLocal, out var entityId))
                return;
            if (isLocal || string.IsNullOrWhiteSpace(entityId))
            {
                RemoveEndpointLocked(state.Endpoint);
                return;
            }

            if (_entityIdByEndpoint.ContainsKey(state.Endpoint))
                return;

            var alreadyOpen = _openEntityIds.Contains(entityId);
            _entityIdByEndpoint[state.Endpoint] = entityId;
            if (!_endpointsByEntityId.TryGetValue(entityId, out var endpoints))
            {
                endpoints = [];
                _endpointsByEntityId[entityId] = endpoints;
            }

            endpoints.Add(state.Endpoint);
            CancelLeaveCandidateLocked(entityId);
            if (alreadyOpen)
                return;

            _openEntityIds.Add(entityId);
            if (_roomActive)
                QueuePendingMemberTransitionLocked(PartyMemberTransitionKind.Joined, entityId);
        }
    }

    internal void ObserveEndpointDestroyed(in PartyStateChangeSnapshot state)
    {
        lock (_sync)
        {
            if (!_stateBatchActive || state.Network == nint.Zero || state.Endpoint == nint.Zero)
                return;

            if (_network == nint.Zero)
                _network = state.Network;
            else if (_network != state.Network)
                return;

            var hadCachedEntity = _entityIdByEndpoint.TryGetValue(state.Endpoint, out var cachedEntityId);
            if (!hadCachedEntity)
            {
                if (TryInspectEndpointLocked(state.Endpoint, out var lateEndpointIsLocal, out var lateEntityId) &&
                    !lateEndpointIsLocal &&
                    !string.IsNullOrWhiteSpace(lateEntityId) &&
                    _roomActive &&
                    _publishedMemberEntityIds.Contains(lateEntityId))
                {
                    QueueLeaveCandidateLocked(lateEntityId, state);
                }
                return;
            }

            if (TryInspectEndpointLocked(state.Endpoint, out var isLocal, out var destroyedEntityId))
            {
                if (isLocal ||
                    (!string.IsNullOrWhiteSpace(destroyedEntityId) &&
                     !string.Equals(cachedEntityId, destroyedEntityId, StringComparison.Ordinal)))
                {
                    RemoveEndpointLocked(state.Endpoint);
                    return;
                }
            }

            var cachedEntity = cachedEntityId!;
            RemoveEndpointLocked(state.Endpoint);
            if (!_roomActive || _openEntityIds.Contains(cachedEntity))
            {
                return;
            }

            if (!_publishedMemberEntityIds.Contains(cachedEntity))
            {
                RemovePendingMemberTransitionLocked(cachedEntity);
                return;
            }

            QueueLeaveCandidateLocked(cachedEntity, state);
        }
    }

    internal static PartyMemberLeaveReason MapLeaveReason(uint nativeReason) =>
        nativeReason switch
        {
            0 => PartyMemberLeaveReason.Requested,
            1 => PartyMemberLeaveReason.Disconnected,
            2 => PartyMemberLeaveReason.Kicked,
            3 => PartyMemberLeaveReason.DeviceLostAuthentication,
            4 => PartyMemberLeaveReason.CreationFailed,
            _ => PartyMemberLeaveReason.Unknown,
        };

    private bool TryInspectEndpointLocked(nint endpoint, out bool isLocal, out string? entityId)
    {
        isLocal = false;
        entityId = null;
        if (_api.IsEndpointLocal(endpoint, out isLocal) != Success)
            return false;
        if (_api.GetEndpointEntityId(endpoint, out entityId) != Success ||
            string.IsNullOrWhiteSpace(entityId))
        {
            entityId = null;
            return false;
        }

        return true;
    }

    private void RemoveEndpointLocked(nint endpoint)
    {
        if (!_entityIdByEndpoint.Remove(endpoint, out var entityId))
            return;

        if (!_endpointsByEntityId.TryGetValue(entityId, out var endpoints))
            return;

        endpoints.Remove(endpoint);
        if (endpoints.Count != 0)
            return;

        _endpointsByEntityId.Remove(entityId);
        _openEntityIds.Remove(entityId);
    }

    private void TryPublishReadyTransitionsLocked()
    {
        if (_pendingTransitions.Count == 0)
            return;

        var remaining = new Queue<PartyMemberTransition>(_pendingTransitions.Count);
        while (_pendingTransitions.Count != 0)
        {
            var transition = _pendingTransitions.Dequeue();
            var remoteOrdinal = 0;
            if (IsMembershipStart(transition.Kind) &&
                transition.RemotePlayerOrdinal == 0)
            {
                if (!TryGetCachedOrdinalLocked(transition.EntityId, out remoteOrdinal) &&
                    !TryMapAndCacheRemoteOrdinalLocked(transition.EntityId, out remoteOrdinal))
                {
                    remaining.Enqueue(transition);
                    continue;
                }

                transition = transition with { RemotePlayerOrdinal = remoteOrdinal };
            }

            if (IsMembershipStart(transition.Kind) &&
                !string.IsNullOrWhiteSpace(transition.EntityId))
            {
                _publishedMemberEntityIds.Add(transition.EntityId);
                _pendingMemberEntityIds.Remove(transition.EntityId);
            }

            _transitions.Enqueue(transition);
        }

        while (remaining.Count != 0)
            _pendingTransitions.Enqueue(remaining.Dequeue());
    }

    private void TryConfirmLeaveCandidatesLocked()
    {
        if (_leaveCandidates.Count == 0)
            return;

        if (!TryReadCoherentSnapshot(out _, out var entityIdSlots))
            return;

        var remaining = new Queue<LeaveCandidate>(_leaveCandidates.Count);
        while (_leaveCandidates.Count != 0)
        {
            var candidate = _leaveCandidates.Dequeue();
            if (!_publishedMemberEntityIds.Contains(candidate.EntityId))
                continue;
            if (_openEntityIds.Contains(candidate.EntityId) ||
                entityIdSlots.ContainsKey(candidate.EntityId))
            {
                remaining.Enqueue(candidate);
                continue;
            }

            var remoteOrdinal = TryGetCachedOrdinalLocked(candidate.EntityId, out var cachedOrdinal)
                ? cachedOrdinal
                : 0;
            _transitions.Enqueue(new PartyMemberTransition(
                PartyMemberTransitionKind.Left,
                remoteOrdinal,
                candidate.EntityId,
                candidate.LeaveReason,
                candidate.NativeReason,
                candidate.ErrorDetail));
            _publishedMemberEntityIds.Remove(candidate.EntityId);
            _remoteOrdinalByEntityId.Remove(candidate.EntityId);
        }

        while (remaining.Count != 0)
            _leaveCandidates.Enqueue(remaining.Dequeue());
    }

    private void CancelLeaveCandidateLocked(string entityId)
    {
        if (_leaveCandidates.Count == 0)
            return;

        var remaining = new Queue<LeaveCandidate>(_leaveCandidates.Count);
        while (_leaveCandidates.Count != 0)
        {
            var candidate = _leaveCandidates.Dequeue();
            if (!string.Equals(candidate.EntityId, entityId, StringComparison.Ordinal))
                remaining.Enqueue(candidate);
        }

        while (remaining.Count != 0)
            _leaveCandidates.Enqueue(remaining.Dequeue());
    }

    private void QueueLeaveCandidateLocked(
        string entityId,
        in PartyStateChangeSnapshot state)
    {
        CancelLeaveCandidateLocked(entityId);
        _leaveCandidates.Enqueue(new LeaveCandidate(
            entityId,
            MapLeaveReason(state.Reason),
            state.Reason,
            state.ErrorDetail));
    }

    private void QueueSnapshotBaselineTransitionsLocked()
    {
        if (!TryReadCoherentSnapshot(out var snapshot, out _))
            return;

        for (var actualSlot = 0; actualSlot < snapshot.EntityIds.Length; actualSlot++)
        {
            if (actualSlot == snapshot.LocalMemberSlot)
                continue;

            var entityId = snapshot.EntityIds[actualSlot];
            if (string.IsNullOrEmpty(entityId) ||
                !PartyMemberSlotMap.TryGetRemoteOrdinal(
                    snapshot.LocalMemberSlot,
                    actualSlot,
                    out var remoteOrdinal))
            {
                continue;
            }

            _remoteOrdinalByEntityId[entityId] = remoteOrdinal;
            QueuePendingMemberTransitionLocked(
                PartyMemberTransitionKind.Baseline,
                entityId,
                remoteOrdinal);
        }
    }

    private void QueuePendingMemberTransitionLocked(
        PartyMemberTransitionKind kind,
        string entityId,
        int remoteOrdinal = 0)
    {
        if (_publishedMemberEntityIds.Contains(entityId) ||
            _pendingMemberEntityIds.Contains(entityId))
        {
            return;
        }

        _pendingMemberEntityIds.Add(entityId);
        _pendingTransitions.Enqueue(new PartyMemberTransition(
            kind,
            remoteOrdinal,
            entityId));
    }

    private void RemovePendingMemberTransitionLocked(string entityId)
    {
        if (!_pendingMemberEntityIds.Remove(entityId))
            return;

        var remaining = new Queue<PartyMemberTransition>(_pendingTransitions.Count);
        while (_pendingTransitions.Count != 0)
        {
            var transition = _pendingTransitions.Dequeue();
            if (!string.Equals(transition.EntityId, entityId, StringComparison.Ordinal))
                remaining.Enqueue(transition);
        }

        while (remaining.Count != 0)
            _pendingTransitions.Enqueue(remaining.Dequeue());
    }

    private static bool IsMembershipStart(PartyMemberTransitionKind kind) =>
        kind is PartyMemberTransitionKind.Baseline or PartyMemberTransitionKind.Joined;

    private bool TryGetCachedOrdinalLocked(string? entityId, out int remoteOrdinal)
    {
        remoteOrdinal = 0;
        return !string.IsNullOrWhiteSpace(entityId) &&
               _remoteOrdinalByEntityId.TryGetValue(entityId, out remoteOrdinal) &&
               remoteOrdinal is >= 1 and <= 3;
    }

    private bool TryMapAndCacheRemoteOrdinalLocked(string? entityId, out int remoteOrdinal)
    {
        remoteOrdinal = 0;
        if (string.IsNullOrWhiteSpace(entityId) ||
            !TryMapRemoteOrdinal(entityId, out remoteOrdinal))
        {
            return false;
        }

        _remoteOrdinalByEntityId[entityId] = remoteOrdinal;
        return true;
    }

    private bool TryMapRemoteOrdinal(string entityId, out int remoteOrdinal)
    {
        remoteOrdinal = 0;
        if (!TryReadCoherentSnapshot(out var snapshot, out var entityIdSlots) ||
            !entityIdSlots.TryGetValue(entityId, out var actualSlot) ||
            !PartyMemberSlotMap.TryGetRemoteOrdinal(
                snapshot.LocalMemberSlot,
                actualSlot,
                out remoteOrdinal))
        {
            return false;
        }

        return true;
    }

    private bool TryReadCoherentSnapshot(
        out RelinkPartyMemberIdentitySnapshot snapshot,
        out IReadOnlyDictionary<string, int> entityIdSlots)
    {
        snapshot = default;
        entityIdSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        if (_identitySnapshotReader is null)
            return false;

        try
        {
            snapshot = _identitySnapshotReader();
            return PartyRoomIdentitySnapshotResolver.TryNormalizeSnapshot(
                snapshot.EntityIds,
                snapshot.LocalMemberSlot,
                out entityIdSlots);
        }
        catch
        {
            return false;
        }
    }

    private void ResetLocked()
    {
        _transitions.Clear();
        _pendingTransitions.Clear();
        _pendingMemberEntityIds.Clear();
        _leaveCandidates.Clear();
        _entityIdByEndpoint.Clear();
        _endpointsByEntityId.Clear();
        _openEntityIds.Clear();
        _publishedMemberEntityIds.Clear();
        _remoteOrdinalByEntityId.Clear();
        _network = nint.Zero;
        _stateBatchManager = nint.Zero;
        _stateBatchActive = false;
        _roomActive = false;
    }
}
