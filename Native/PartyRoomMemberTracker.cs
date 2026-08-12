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
    private readonly HashSet<string> _publishedJoinedEntityIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _remoteOrdinalByEntityId = new(StringComparer.Ordinal);

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
            ResolveOrdinalsLocked();
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
                _pendingTransitions.Clear();

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
                return;
            }

            while (_pendingTransitions.Count != 0)
            {
                var transition = _pendingTransitions.Dequeue();
                _transitions.Enqueue(transition);
                if (transition.Kind == PartyMemberTransitionKind.Joined &&
                    !string.IsNullOrWhiteSpace(transition.EntityId))
                {
                    _publishedJoinedEntityIds.Add(transition.EntityId);
                }
            }

            ResolveOrdinalsLocked();
        }
    }

    internal void ActivateRoom()
    {
        lock (_sync)
            _roomActive = true;
    }

    internal void DeactivateRoom()
    {
        lock (_sync)
            _roomActive = false;
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
            CacheRemoteOrdinalLocked(entityId);
            if (alreadyOpen)
                return;

            _openEntityIds.Add(entityId);
            if (_roomActive)
            {
                _pendingTransitions.Enqueue(new PartyMemberTransition(
                    PartyMemberTransitionKind.Joined,
                    RemotePlayerOrdinal: 0,
                    entityId));
            }
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
                // A late endpoint we never classified cannot become a member Left without a prior
                // Joined/baseline membership record.
                TryInspectEndpointLocked(state.Endpoint, out _, out _);
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
            if (!_roomActive ||
                _openEntityIds.Contains(cachedEntity) ||
                !_publishedJoinedEntityIds.Contains(cachedEntity))
            {
                return;
            }

            var leaveReason = MapLeaveReason(state.Reason);
            if ((leaveReason == PartyMemberLeaveReason.DeviceLostAuthentication ||
                 leaveReason == PartyMemberLeaveReason.CreationFailed) &&
                TryConfirmRemoteMemberPresent(cachedEntity))
            {
                return;
            }

            var remoteOrdinal = TryGetCachedOrdinalLocked(cachedEntity, out var cachedOrdinal)
                ? cachedOrdinal
                : 0;
            _pendingTransitions.Enqueue(new PartyMemberTransition(
                PartyMemberTransitionKind.Left,
                remoteOrdinal,
                cachedEntity,
                leaveReason,
                state.Reason,
                state.ErrorDetail));
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

    private void ResolveOrdinalsLocked()
    {
        if (_transitions.Count == 0)
            return;

        var resolved = new Queue<PartyMemberTransition>(_transitions.Count);
        while (_transitions.Count != 0)
        {
            var transition = _transitions.Dequeue();
            if (transition.RemotePlayerOrdinal == 0)
            {
                if (TryGetCachedOrdinalLocked(transition.EntityId, out var remoteOrdinal) ||
                    TryMapAndCacheRemoteOrdinalLocked(transition.EntityId, out remoteOrdinal))
                {
                    transition = transition with { RemotePlayerOrdinal = remoteOrdinal };
                }
            }

            resolved.Enqueue(transition);
        }

        while (resolved.Count != 0)
            _transitions.Enqueue(resolved.Dequeue());
    }

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

    private void CacheRemoteOrdinalLocked(string entityId)
    {
        if (_remoteOrdinalByEntityId.ContainsKey(entityId))
            return;

        if (TryMapRemoteOrdinal(entityId, out var remoteOrdinal))
            _remoteOrdinalByEntityId[entityId] = remoteOrdinal;
    }

    private bool TryMapRemoteOrdinal(string entityId, out int remoteOrdinal)
    {
        remoteOrdinal = 0;
        if (_identitySnapshotReader is null)
            return false;

        try
        {
            var snapshot = _identitySnapshotReader();
            if (!PartyRoomIdentitySnapshotResolver.TryNormalizeSnapshot(
                    snapshot.EntityIds,
                    snapshot.LocalMemberSlot,
                    out var entityIdSlots) ||
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
        catch
        {
            return false;
        }
    }

    private bool TryConfirmRemoteMemberPresent(string entityId)
    {
        if (_identitySnapshotReader is null)
            return false;

        try
        {
            var snapshot = _identitySnapshotReader();
            if (!PartyRoomIdentitySnapshotResolver.TryNormalizeSnapshot(
                    snapshot.EntityIds,
                    snapshot.LocalMemberSlot,
                    out var entityIdSlots) ||
                !entityIdSlots.TryGetValue(entityId, out var actualSlot) ||
                actualSlot == snapshot.LocalMemberSlot)
            {
                return false;
            }

            return true;
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
        _entityIdByEndpoint.Clear();
        _endpointsByEntityId.Clear();
        _openEntityIds.Clear();
        _publishedJoinedEntityIds.Clear();
        _remoteOrdinalByEntityId.Clear();
        _network = nint.Zero;
        _stateBatchManager = nint.Zero;
        _stateBatchActive = false;
        _roomActive = false;
    }
}