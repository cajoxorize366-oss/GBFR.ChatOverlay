using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

internal sealed class PartyLobbyOwnerBinding
{
    internal const int MaximumEntityIdBytes = 512;

    private readonly Dictionary<nint, string> _ownerCandidates = [];
    private string? _boundOwnerEntityId;
    private bool _hasObservedRemoteHostPresent;
    private string? _roomName;
    private int _hostPlayerNumber;
    private PartyNetworkLocalRole _activeRole;

    internal void ObserveOwner(nint lobby, string? ownerEntityId)
    {
        if (lobby == nint.Zero)
            return;

        if (!IsValidOwner(ownerEntityId))
        {
            _ownerCandidates.Remove(lobby);
            return;
        }

        _ownerCandidates[lobby] = ownerEntityId!;
    }

    internal PartyRoomIdentitySnapshot ResolveSnapshot(
        RelinkPartyMemberIdentitySnapshot snapshot,
        PartyNetworkLocalRole role)
    {
        var hostState = ResolveHostState(snapshot, role);
        return new PartyRoomIdentitySnapshot(
            hostState == PartyRoomHostState.Unknown ? null : _roomName,
            hostState);
    }

    internal PartyRoomHostState ResolveHostState(
        RelinkPartyMemberIdentitySnapshot snapshot,
        PartyNetworkLocalRole role)
    {
        EnsureRole(role);
        if (!PartyRoomIdentitySnapshotResolver.TryNormalizeSnapshot(
                snapshot.EntityIds,
                snapshot.LocalMemberSlot,
                out var entityIdSlots))
        {
            _hostPlayerNumber = 0;
            return PartyRoomHostState.Unknown;
        }

        return role switch
        {
            PartyNetworkLocalRole.Unknown => PartyRoomHostState.Unknown,
            PartyNetworkLocalRole.Created => ResolveCreatedHostState(),
            PartyNetworkLocalRole.Connected => ResolveConnectedHostState(snapshot, entityIdSlots),
            _ => PartyRoomHostState.Unknown,
        };
    }

    internal bool TryResolveHostPlayerNumber(
        RelinkPartyMemberIdentitySnapshot snapshot,
        PartyNetworkLocalRole role,
        out int playerNumber)
    {
        var hostState = ResolveHostState(snapshot, role);
        playerNumber = hostState is PartyRoomHostState.LocalHost or PartyRoomHostState.RemoteHostPresent
            ? _hostPlayerNumber
            : 0;
        return playerNumber is >= 1 and <= 4;
    }

    internal bool TryGetCachedHostPlayerNumber(out int playerNumber)
    {
        playerNumber = _hostPlayerNumber;
        return playerNumber is >= 1 and <= 4;
    }

    internal void CacheRoomName(string? roomName)
    {
        if (_activeRole == PartyNetworkLocalRole.Unknown ||
            string.IsNullOrWhiteSpace(roomName) ||
            (_activeRole == PartyNetworkLocalRole.Connected && _boundOwnerEntityId is null))
            return;

        _roomName = roomName.Trim();
    }

    internal void Reset()
    {
        _ownerCandidates.Clear();
        _boundOwnerEntityId = null;
        _hasObservedRemoteHostPresent = false;
        _roomName = null;
        _hostPlayerNumber = 0;
        _activeRole = PartyNetworkLocalRole.Unknown;
    }

    private void EnsureRole(PartyNetworkLocalRole role)
    {
        if (_activeRole == role)
            return;

        _boundOwnerEntityId = null;
        _hasObservedRemoteHostPresent = false;
        _roomName = null;
        _hostPlayerNumber = 0;
        _activeRole = role;
    }

    private PartyRoomHostState ResolveCreatedHostState()
    {
        _boundOwnerEntityId = null;
        _hasObservedRemoteHostPresent = false;
        _hostPlayerNumber = 1;
        return PartyRoomHostState.LocalHost;
    }

    private PartyRoomHostState ResolveConnectedHostState(
        RelinkPartyMemberIdentitySnapshot snapshot,
        IReadOnlyDictionary<string, int> entityIdSlots)
    {
        if (_boundOwnerEntityId is not null)
        {
            var hostState = PartyRoomIdentitySnapshotResolver.ResolveHostState(
                _boundOwnerEntityId,
                snapshot,
                _hasObservedRemoteHostPresent,
                out var currentOwnerSlot);

            if (hostState == PartyRoomHostState.LocalHost)
            {
                _hostPlayerNumber = 0;
                return PartyRoomHostState.Unknown;
            }

            if (hostState == PartyRoomHostState.RemoteHostPresent)
            {
                _hasObservedRemoteHostPresent = true;
                _hostPlayerNumber = ResolveHostPlayerNumber(snapshot, currentOwnerSlot);
                return PartyRoomHostState.RemoteHostPresent;
            }

            _hostPlayerNumber = 0;
            return hostState;
        }

        if (!TryBindUniqueRemoteCandidate(
                entityIdSlots,
                snapshot,
                out var owner,
                out var ownerSlot))
        {
            _hostPlayerNumber = 0;
            return PartyRoomHostState.Unknown;
        }

        _boundOwnerEntityId = owner;
        _hasObservedRemoteHostPresent = true;
        _hostPlayerNumber = ResolveHostPlayerNumber(snapshot, ownerSlot);
        _roomName = null;
        return PartyRoomHostState.RemoteHostPresent;
    }

    private bool TryBindUniqueRemoteCandidate(
        IReadOnlyDictionary<string, int> entityIdSlots,
        RelinkPartyMemberIdentitySnapshot snapshot,
        out string? owner,
        out int ownerSlot)
    {
        owner = null;
        ownerSlot = -1;
        var localEntityId = snapshot.EntityIds[snapshot.LocalMemberSlot];
        var seenCandidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in _ownerCandidates.Values)
        {
            if (string.Equals(candidate, localEntityId, StringComparison.Ordinal))
                continue;

            if (!seenCandidates.Add(candidate))
                continue;

            if (!entityIdSlots.TryGetValue(candidate, out var candidateSlot))
                continue;

            if (owner is not null)
            {
                owner = null;
                ownerSlot = -1;
                return false;
            }

            owner = candidate;
            ownerSlot = candidateSlot;
        }

        return owner is not null;
    }

    private static bool IsValidOwner(string? ownerEntityId)
    {
        if (string.IsNullOrWhiteSpace(ownerEntityId) ||
            ownerEntityId!.Length > MaximumEntityIdBytes ||
            ownerEntityId.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            return false;
        }

        return true;
    }

    private static int ResolveHostPlayerNumber(
        RelinkPartyMemberIdentitySnapshot snapshot,
        int ownerSlot)
    {
        if (ownerSlot == snapshot.LocalMemberSlot)
            return 1;

        return PartyMemberSlotMap.TryGetPlayerNumber(
            snapshot.LocalMemberSlot,
            ownerSlot,
            out var playerNumber)
            ? playerNumber
            : 0;
    }
}
