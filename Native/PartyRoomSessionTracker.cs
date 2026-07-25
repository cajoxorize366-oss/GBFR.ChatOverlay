namespace GBFR.ChatOverlay.Native;

/// <summary>
/// Tracks Relink's existing online Party room without creating or mutating Party objects.
/// A room becomes active only after the same local user authenticates and creates its
/// gameplay endpoint. It closes on the first matching leave/destroy/removal signal.
/// </summary>
internal sealed class PartyRoomSessionTracker
{
    private readonly object _sync = new();
    private nint _network;
    private nint _localUser;
    private nint _localEndpoint;
    private bool _authenticated;
    private int _active;

    internal bool IsActive => Volatile.Read(ref _active) != 0;

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
                    if (snapshot.Result == 0 && MatchesEndpointLocked(snapshot.Network, snapshot.Endpoint))
                        ResetLocked();
                    break;
                case PartyStateChangeType.EndpointDestroyed:
                    if (MatchesEndpointLocked(snapshot.Network, snapshot.Endpoint))
                        ResetLocked();
                    break;
                case PartyStateChangeType.LocalUserRemoved:
                case PartyStateChangeType.LocalUserKicked:
                    if (MatchesLocalUserLocked(snapshot.Network, snapshot.LocalUser))
                        ResetLocked();
                    break;
                case PartyStateChangeType.RemoveLocalUserCompleted:
                    if (snapshot.Result == 0 && MatchesLocalUserLocked(snapshot.Network, snapshot.LocalUser))
                        ResetLocked();
                    break;
                case PartyStateChangeType.DestroyLocalUserCompleted:
                    if (snapshot.Result == 0 && _localUser != nint.Zero && snapshot.LocalUser == _localUser)
                        ResetLocked();
                    break;
                case PartyStateChangeType.LeaveNetworkCompleted:
                    if (snapshot.Result == 0 && MatchesNetworkLocked(snapshot.Network))
                        ResetLocked();
                    break;
                case PartyStateChangeType.NetworkDestroyed:
                    if (MatchesNetworkLocked(snapshot.Network))
                        ResetLocked();
                    break;
            }
        }
    }

    internal void MarkNetworkLeaveQueued(nint network)
    {
        lock (_sync)
        {
            if (MatchesNetworkLocked(network))
                ResetLocked();
        }
    }

    internal void Reset()
    {
        lock (_sync)
            ResetLocked();
    }

    private void ObserveAuthenticationLocked(in PartyStateChangeSnapshot snapshot)
    {
        if (snapshot.Result != 0 || snapshot.Network == nint.Zero || snapshot.LocalUser == nint.Zero)
        {
            if (MatchesNetworkLocked(snapshot.Network) ||
                (_localUser != nint.Zero && snapshot.LocalUser == _localUser))
            {
                ResetLocked();
            }
            return;
        }

        if (_network != snapshot.Network || _localUser != snapshot.LocalUser)
            ResetLocked();

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

        _localEndpoint = snapshot.Endpoint;
        Volatile.Write(ref _active, 1);
    }

    private bool MatchesNetworkLocked(nint network) =>
        _network != nint.Zero && network == _network;

    private bool MatchesLocalUserLocked(nint network, nint localUser) =>
        MatchesNetworkLocked(network) && _localUser != nint.Zero && localUser == _localUser;

    private bool MatchesEndpointLocked(nint network, nint endpoint) =>
        MatchesNetworkLocked(network) && _localEndpoint != nint.Zero && endpoint == _localEndpoint;

    private void ResetLocked()
    {
        Volatile.Write(ref _active, 0);
        _network = nint.Zero;
        _localUser = nint.Zero;
        _localEndpoint = nint.Zero;
        _authenticated = false;
    }
}
