namespace GBFR.ChatOverlay.Native;

/// <summary>
/// Tracks Relink's existing online Party room without creating or mutating Party objects.
/// A room becomes active only after the same local user authenticates and creates its
/// gameplay endpoint. It closes on the first matching leave/destroy/removal signal.
/// </summary>
internal sealed class PartyRoomSessionTracker
{
    private readonly object _sync = new();
    private readonly Queue<PartyRoomTransition> _transitions = new();
    private Func<PartyRoomIdentitySnapshot>? _roomIdentityReader;
    private Func<int>? _voiceParticipantCountReader;
    private PartyRoomMemberTracker? _memberTracker;
    private nint _network;
    private nint _localUser;
    private nint _localEndpoint;
    private bool _authenticated;
    private bool _leaveQueued;
    private PartyRoomExitReason? _pendingExitReason;
    private string? _pendingExitRoomName;
    private int _active;
    private readonly HashSet<nint> _createdLocalUsers = new();
    private readonly HashSet<nint> _connectedNetworks = new();
    private PartyNetworkLocalRole _networkRole;

    internal bool IsActive => Volatile.Read(ref _active) != 0;

    internal PartyNetworkLocalRole LocalNetworkRole
    {
        get
        {
            lock (_sync)
            {
                return Volatile.Read(ref _active) != 0
                    ? _networkRole
                    : PartyNetworkLocalRole.Unknown;
            }
        }
    }

    internal void ConfigureSnapshotReaders(
        Func<PartyRoomIdentitySnapshot>? roomIdentityReader,
        Func<int>? voiceParticipantCountReader)
    {
        lock (_sync)
        {
            _roomIdentityReader = roomIdentityReader;
            _voiceParticipantCountReader = voiceParticipantCountReader;
        }
    }

    internal void ConfigureMemberTracking(
        IPartyEndpointApi? endpointApi,
        Func<RelinkPartyMemberIdentitySnapshot>? identitySnapshotReader = null)
    {
        lock (_sync)
        {
            _memberTracker?.Reset();
            _memberTracker = endpointApi is null
                ? null
                : new PartyRoomMemberTracker(endpointApi, identitySnapshotReader);
        }
    }

    internal bool TryReadMemberTransition(out PartyMemberTransition transition)
    {
        lock (_sync)
        {
            transition = default;
            return _memberTracker?.TryReadTransition(out transition) ?? false;
        }
    }

    internal void BeginStateChangeBatch(nint manager)
    {
        lock (_sync)
            _memberTracker?.BeginStateChangeBatch(manager);
    }

    internal void CancelStateChangeBatch(nint manager)
    {
        lock (_sync)
            _memberTracker?.CancelStateChangeBatch(manager);
    }

    internal void OnBatchFinished(nint manager)
    {
        lock (_sync)
            _memberTracker?.OnBatchFinished(manager);
    }

    internal void ResetMemberTransitions()
    {
        lock (_sync)
            _memberTracker?.Reset();
    }

    internal bool TryReadTransition(out PartyRoomTransition transition)
    {
        lock (_sync)
        {
            if (_transitions.Count == 0)
            {
                transition = default;
                return false;
            }

            transition = _transitions.Dequeue();
            return true;
        }
    }

    internal void Observe(in PartyStateChangeSnapshot snapshot)
    {
        lock (_sync)
        {
            switch ((PartyStateChangeType)snapshot.Type)
            {
                case PartyStateChangeType.CreateNewNetworkCompleted:
                    ObserveCreateNewNetworkLocked(snapshot);
                    break;
                case PartyStateChangeType.ConnectToNetworkCompleted:
                    ObserveConnectToNetworkLocked(snapshot);
                    break;
                case PartyStateChangeType.AuthenticateLocalUserCompleted:
                    ObserveAuthenticationLocked(snapshot);
                    break;
                case PartyStateChangeType.CreateEndpointCompleted:
                    ObserveEndpointCreationLocked(snapshot);
                    if (Volatile.Read(ref _active) != 0)
                        _memberTracker?.ActivateRoom();
                    break;
                case PartyStateChangeType.DestroyEndpointCompleted:
                    if (!_leaveQueued &&
                        snapshot.Result == 0 &&
                        MatchesEndpointLocked(snapshot.Network, snapshot.Endpoint))
                    {
                        ResetLocked(
                            emitExited: true,
                            exitReason: PartyRoomExitReason.NetworkInterrupted,
                            nativeReason: snapshot.Reason,
                            errorDetail: snapshot.ErrorDetail);
                    }
                    break;
                case PartyStateChangeType.EndpointCreated:
                    if (MatchesNetworkLocked(snapshot.Network))
                        _memberTracker?.ObserveEndpointCreated(snapshot);
                    break;
                case PartyStateChangeType.EndpointDestroyed:
                    if (MatchesNetworkLocked(snapshot.Network))
                        _memberTracker?.ObserveEndpointDestroyed(snapshot);
                    if (!_leaveQueued && MatchesEndpointLocked(snapshot.Network, snapshot.Endpoint))
                    {
                        ResetLocked(
                            emitExited: true,
                            exitReason: PartyRoomExitReason.NetworkInterrupted,
                            nativeReason: snapshot.Reason,
                            errorDetail: snapshot.ErrorDetail);
                    }
                    break;
                case PartyStateChangeType.LocalUserRemoved:
                    if (!_leaveQueued && MatchesLocalUserLocked(snapshot.Network, snapshot.LocalUser))
                    {
                        ResetLocked(
                            emitExited: true,
                            exitReason: PartyRoomExitReason.NetworkInterrupted,
                            nativeReason: snapshot.Reason,
                            errorDetail: snapshot.ErrorDetail);
                    }
                    break;
                case PartyStateChangeType.LocalUserKicked:
                    if (MatchesLocalUserLocked(snapshot.Network, snapshot.LocalUser))
                    {
                        if (_leaveQueued)
                        {
                            FinalizePendingLeaveLocked(
                                overrideReason: PartyRoomExitReason.Kicked,
                                nativeReason: snapshot.Reason,
                                errorDetail: snapshot.ErrorDetail);
                            break;
                        }

                        ResetLocked(
                            emitExited: true,
                            exitReason: PartyRoomExitReason.Kicked,
                            nativeReason: snapshot.Reason,
                            errorDetail: snapshot.ErrorDetail);
                    }
                    break;
                case PartyStateChangeType.RemoveLocalUserCompleted:
                    if (!_leaveQueued &&
                        snapshot.Result == 0 &&
                        MatchesLocalUserLocked(snapshot.Network, snapshot.LocalUser))
                    {
                        ResetLocked(
                            emitExited: true,
                            exitReason: PartyRoomExitReason.NetworkInterrupted,
                            nativeReason: snapshot.Reason,
                            errorDetail: snapshot.ErrorDetail);
                    }
                    break;
                case PartyStateChangeType.DestroyLocalUserCompleted:
                    if (!_leaveQueued &&
                        snapshot.Result == 0 &&
                        _localUser != nint.Zero &&
                        snapshot.LocalUser == _localUser)
                    {
                        ResetLocked(
                            emitExited: true,
                            exitReason: PartyRoomExitReason.NetworkInterrupted,
                            nativeReason: snapshot.Reason,
                            errorDetail: snapshot.ErrorDetail);
                    }
                    break;
                case PartyStateChangeType.LeaveNetworkCompleted:
                    if (snapshot.Result == 0 && MatchesNetworkLocked(snapshot.Network))
                    {
                        if (_leaveQueued)
                        {
                            FinalizePendingLeaveLocked();
                            break;
                        }

                        ResetLocked(
                            emitExited: true,
                            exitReason: PartyRoomExitReason.NetworkInterrupted,
                            nativeReason: snapshot.Reason,
                            errorDetail: snapshot.ErrorDetail);
                    }
                    break;
                case PartyStateChangeType.NetworkDestroyed:
                    if (!_leaveQueued && MatchesNetworkLocked(snapshot.Network))
                    {
                        ResetLocked(
                            emitExited: true,
                            exitReason: PartyRoomExitReason.NetworkInterrupted,
                            nativeReason: snapshot.Reason,
                            errorDetail: snapshot.ErrorDetail);
                    }
                    break;
            }
        }
    }

    internal void MarkNetworkLeaveQueued(nint network, PartyRoomIdentitySnapshot? identity = null)
    {
        lock (_sync)
        {
            if (!MatchesNetworkLocked(network))
                return;
            if (Volatile.Read(ref _active) == 0)
                return;

            var reason = identity?.HostState switch
            {
                PartyRoomHostState.RemoteHostMissing => PartyRoomExitReason.HostDisconnected,
                PartyRoomHostState.LocalHost or PartyRoomHostState.RemoteHostPresent =>
                    PartyRoomExitReason.SelfLeft,
                _ => PartyRoomExitReason.NetworkInterrupted,
            };
            _memberTracker?.Reset();
            _leaveQueued = true;
            _pendingExitReason = reason;
            _pendingExitRoomName = identity?.RoomName;
            Volatile.Write(ref _active, 0);
            _networkRole = PartyNetworkLocalRole.Unknown;
            _createdLocalUsers.Clear();
            _connectedNetworks.Clear();
        }
    }

    internal void Reset()
    {
        lock (_sync)
        {
            _transitions.Clear();
            ResetLocked();
        }
    }

    internal void ResetPreservingTransitions(PartyRoomIdentitySnapshot? identity = null)
    {
        lock (_sync)
        {
            if (_leaveQueued)
            {
                FinalizePendingLeaveLocked();
                return;
            }

            if (Volatile.Read(ref _active) != 0)
            {
                EnqueueExitedLocked(
                    PartyRoomExitReason.NetworkInterrupted,
                    identity?.RoomName,
                    nativeReason: 0,
                    errorDetail: 0);
            }

            ClearSessionLocked();
        }
    }

    private void ObserveAuthenticationLocked(in PartyStateChangeSnapshot snapshot)
    {
        if (snapshot.Result != 0 || snapshot.Network == nint.Zero || snapshot.LocalUser == nint.Zero)
        {
            if (MatchesNetworkLocked(snapshot.Network) ||
                (_localUser != nint.Zero && snapshot.LocalUser == _localUser))
            {
                if (_leaveQueued)
                    FinalizePendingLeaveLocked();
                else
                    ResetLocked(
                        emitExited: true,
                        exitReason: PartyRoomExitReason.NetworkInterrupted,
                        nativeReason: snapshot.Reason,
                        errorDetail: snapshot.ErrorDetail);
            }
            return;
        }

        if (_network != snapshot.Network || _localUser != snapshot.LocalUser)
        {
            var pendingCreated = snapshot.LocalUser != nint.Zero &&
                                 _createdLocalUsers.Contains(snapshot.LocalUser);
            var pendingConnected = snapshot.Network != nint.Zero &&
                                   _connectedNetworks.Contains(snapshot.Network);

            if (_leaveQueued)
                FinalizePendingLeaveLocked();
            else if (_network != nint.Zero || _localUser != nint.Zero)
                ResetLocked(
                    emitExited: true,
                    exitReason: PartyRoomExitReason.NetworkInterrupted);

            if (pendingCreated)
                _createdLocalUsers.Add(snapshot.LocalUser);
            if (pendingConnected)
                _connectedNetworks.Add(snapshot.Network);
        }

        if (_network != snapshot.Network ||
            _localUser != snapshot.LocalUser ||
            _networkRole == PartyNetworkLocalRole.Unknown)
        {
            BindNetworkRoleLocked(snapshot.Network, snapshot.LocalUser);
        }

        _network = snapshot.Network;
        _localUser = snapshot.LocalUser;
        _authenticated = true;
    }

    private void ObserveCreateNewNetworkLocked(in PartyStateChangeSnapshot snapshot)
    {
        if (snapshot.Result != 0 || snapshot.LocalUser == nint.Zero)
        {
            if (snapshot.LocalUser != nint.Zero &&
                (_createdLocalUsers.Remove(snapshot.LocalUser) ||
                 (_localUser != nint.Zero &&
                  snapshot.LocalUser == _localUser &&
                  _networkRole == PartyNetworkLocalRole.Created)))
            {
                ClearNetworkRoleLocked();
            }

            return;
        }

        if (_authenticated && _localUser == snapshot.LocalUser)
        {
            if (_networkRole == PartyNetworkLocalRole.Created)
                return;
            if (_networkRole == PartyNetworkLocalRole.Connected)
            {
                // The creator may queue ConnectToNetwork immediately, before the asynchronous
                // CreateNewNetwork completion is delivered. A later matching create completion
                // is stronger evidence and upgrades the current local role without closing it.
                _networkRole = PartyNetworkLocalRole.Created;
                return;
            }
        }

        if (_createdLocalUsers.Add(snapshot.LocalUser))
        {
            if (_authenticated &&
                _networkRole == PartyNetworkLocalRole.Unknown &&
                _localUser == snapshot.LocalUser)
            {
                BindNetworkRoleLocked(_network, _localUser);
            }
        }
    }

    private void ObserveConnectToNetworkLocked(in PartyStateChangeSnapshot snapshot)
    {
        if (snapshot.Result != 0 || snapshot.Network == nint.Zero)
        {
            if (snapshot.Network != nint.Zero &&
                (_connectedNetworks.Remove(snapshot.Network) ||
                 (_network != nint.Zero &&
                  snapshot.Network == _network &&
                  _networkRole == PartyNetworkLocalRole.Connected)))
            {
                ClearNetworkRoleLocked();
            }

            return;
        }

        if (_authenticated && _network == snapshot.Network)
        {
            if (_networkRole is PartyNetworkLocalRole.Connected or PartyNetworkLocalRole.Created)
                return;
        }

        if (_connectedNetworks.Add(snapshot.Network))
        {
            if (_authenticated &&
                _networkRole == PartyNetworkLocalRole.Unknown &&
                _network == snapshot.Network)
            {
                BindNetworkRoleLocked(_network, _localUser);
            }
        }
    }

    private void BindNetworkRoleLocked(nint network, nint localUser)
    {
        var created = localUser != nint.Zero && _createdLocalUsers.Contains(localUser);
        var connected = network != nint.Zero && _connectedNetworks.Contains(network);
        _createdLocalUsers.Clear();
        _connectedNetworks.Clear();
        // PartyCreateNewNetwork allocates the relay but does not connect the local device.
        // The creator therefore also completes ConnectToNetwork before authentication.
        // Creation is the stronger role signal and must win when both completions match.
        if (created)
        {
            _networkRole = PartyNetworkLocalRole.Created;
            return;
        }

        if (connected)
        {
            _networkRole = PartyNetworkLocalRole.Connected;
            return;
        }

        _networkRole = PartyNetworkLocalRole.Unknown;
    }

    private void ClearNetworkRoleLocked()
    {
        _networkRole = PartyNetworkLocalRole.Unknown;
        _createdLocalUsers.Clear();
        _connectedNetworks.Clear();
    }

    private void ObserveEndpointCreationLocked(in PartyStateChangeSnapshot snapshot)
    {
        if (snapshot.Result != 0 ||
            !_authenticated ||
            snapshot.Network != _network ||
            snapshot.LocalUser != _localUser ||
            snapshot.Endpoint == nint.Zero)
        {
            return;
        }
        if (_leaveQueued || Volatile.Read(ref _active) != 0)
            return;

        _localEndpoint = snapshot.Endpoint;
        Volatile.Write(ref _active, 1);
        EnqueueEnteredLocked();
    }

    private bool MatchesNetworkLocked(nint network) =>
        _network != nint.Zero && network == _network;

    private bool MatchesLocalUserLocked(nint network, nint localUser) =>
        MatchesNetworkLocked(network) && _localUser != nint.Zero && localUser == _localUser;

    private bool MatchesEndpointLocked(nint network, nint endpoint) =>
        MatchesNetworkLocked(network) && _localEndpoint != nint.Zero && endpoint == _localEndpoint;

    private void ResetLocked(
        bool emitExited = false,
        PartyRoomExitReason exitReason = PartyRoomExitReason.NetworkInterrupted,
        string? roomName = null,
        uint nativeReason = 0,
        uint errorDetail = 0)
    {
        var wasActive = Volatile.Read(ref _active) != 0;
        if (wasActive && emitExited)
            EnqueueExitedLocked(exitReason, roomName, nativeReason, errorDetail);

        ClearSessionLocked();
    }

    private void ClearSessionLocked()
    {
        _memberTracker?.Reset();
        Volatile.Write(ref _active, 0);
        _network = nint.Zero;
        _localUser = nint.Zero;
        _localEndpoint = nint.Zero;
        _authenticated = false;
        _networkRole = PartyNetworkLocalRole.Unknown;
        _createdLocalUsers.Clear();
        _connectedNetworks.Clear();
        _leaveQueued = false;
        _pendingExitReason = null;
        _pendingExitRoomName = null;
    }

    private void FinalizePendingLeaveLocked(
        PartyRoomExitReason? overrideReason = null,
        string? overrideRoomName = null,
        uint nativeReason = 0,
        uint errorDetail = 0)
    {
        if (!_leaveQueued)
            return;

        var reason = overrideReason ?? _pendingExitReason ?? PartyRoomExitReason.SelfLeft;
        var roomName = overrideRoomName ?? _pendingExitRoomName;
        EnqueueExitedLocked(reason, roomName, nativeReason, errorDetail);
        ClearSessionLocked();
    }

    private void EnqueueEnteredLocked()
    {
        var identity = ReadRoomIdentitySnapshotSafely();
        _transitions.Enqueue(new PartyRoomTransition(
            PartyRoomTransitionKind.Entered,
            RoomName: identity.RoomName,
            VoiceParticipantCount: ReadVoiceParticipantCountSafely()));
    }

    private void EnqueueExitedLocked(
        PartyRoomExitReason exitReason,
        string? roomName,
        uint nativeReason,
        uint errorDetail)
    {
        var identity = ReadRoomIdentitySnapshotSafely();
        _transitions.Enqueue(new PartyRoomTransition(
            PartyRoomTransitionKind.Exited,
            exitReason,
            roomName ?? identity.RoomName,
            ReadVoiceParticipantCountSafely(),
            nativeReason,
            errorDetail));
    }

    private PartyRoomIdentitySnapshot ReadRoomIdentitySnapshotSafely()
    {
        try
        {
            return _roomIdentityReader?.Invoke() ?? default;
        }
        catch
        {
            return default;
        }
    }

    private int ReadVoiceParticipantCountSafely()
    {
        try
        {
            var count = _voiceParticipantCountReader?.Invoke() ?? 0;
            return count >= 0 ? count : 0;
        }
        catch
        {
            return 0;
        }
    }
}
