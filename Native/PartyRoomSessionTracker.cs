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
    private nint _network;
    private nint _localUser;
    private nint _localEndpoint;
    private bool _authenticated;
    private bool _leaveQueued;
    private PartyRoomExitReason? _pendingExitReason;
    private string? _pendingExitRoomName;
    private int _active;

    internal bool IsActive => Volatile.Read(ref _active) != 0;

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
                case PartyStateChangeType.AuthenticateLocalUserCompleted:
                    ObserveAuthenticationLocked(snapshot);
                    break;
                case PartyStateChangeType.CreateEndpointCompleted:
                    ObserveEndpointCreationLocked(snapshot);
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
                case PartyStateChangeType.EndpointDestroyed:
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
            _leaveQueued = true;
            _pendingExitReason = reason;
            _pendingExitRoomName = identity?.RoomName;
            Volatile.Write(ref _active, 0);
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
            if (_leaveQueued)
                FinalizePendingLeaveLocked();
            else
                ResetLocked(
                    emitExited: true,
                    exitReason: PartyRoomExitReason.NetworkInterrupted);
        }

        _network = snapshot.Network;
        _localUser = snapshot.LocalUser;
        _authenticated = true;
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
        Volatile.Write(ref _active, 0);
        _network = nint.Zero;
        _localUser = nint.Zero;
        _localEndpoint = nint.Zero;
        _authenticated = false;
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
