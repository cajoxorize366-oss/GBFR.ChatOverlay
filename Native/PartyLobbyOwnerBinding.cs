namespace GBFR.ChatOverlay.Native;

internal sealed class PartyLobbyOwnerBinding
{
    internal const int MaximumEntityIdBytes = 512;

    private readonly Dictionary<nint, string> _ownerCandidates = [];
    private string? _boundOwnerEntityId;
    private bool _hasObservedRemoteHostPresent;
    private string? _roomName;
    private int _hostPlayerNumber;

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

    internal PartyRoomIdentitySnapshot ResolveSnapshot(RelinkPartyMemberIdentitySnapshot snapshot)
    {
        var hostState = ResolveHostState(snapshot);
        return new PartyRoomIdentitySnapshot(
            hostState == PartyRoomHostState.Unknown ? null : _roomName,
            hostState);
    }

    internal PartyRoomHostState ResolveHostState(RelinkPartyMemberIdentitySnapshot snapshot)
    {
        if (!PartyRoomIdentitySnapshotResolver.TryNormalizeSnapshot(
                snapshot.EntityIds,
                snapshot.LocalMemberSlot,
                out var entityIdSlots))
        {
            _hostPlayerNumber = 0;
            return PartyRoomHostState.Unknown;
        }

        if (_boundOwnerEntityId is null)
        {
            if (!TryBindUniqueCandidate(entityIdSlots, out var owner, out var ownerSlot))
            {
                _hostPlayerNumber = 0;
                return PartyRoomHostState.Unknown;
            }

            _boundOwnerEntityId = owner;
            _hasObservedRemoteHostPresent = ownerSlot != snapshot.LocalMemberSlot;
            _hostPlayerNumber = ownerSlot + 1;
            _roomName = null;
        }

        var hostState = PartyRoomIdentitySnapshotResolver.ResolveHostState(
            _boundOwnerEntityId,
            snapshot,
            _hasObservedRemoteHostPresent,
            out var currentOwnerSlot);

        if (hostState == PartyRoomHostState.RemoteHostPresent)
            _hasObservedRemoteHostPresent = true;
        if (hostState is PartyRoomHostState.LocalHost or PartyRoomHostState.RemoteHostPresent)
            _hostPlayerNumber = currentOwnerSlot + 1;
        else
            _hostPlayerNumber = 0;

        return hostState;
    }

    internal bool TryResolveHostPlayerNumber(RelinkPartyMemberIdentitySnapshot snapshot, out int playerNumber)
    {
        var hostState = ResolveHostState(snapshot);
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
        if (_boundOwnerEntityId is null || string.IsNullOrWhiteSpace(roomName))
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
    }

    private bool TryBindUniqueCandidate(
        IReadOnlyDictionary<string, int> entityIdSlots,
        out string? owner,
        out int ownerSlot)
    {
        owner = null;
        ownerSlot = -1;
        var seenCandidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in _ownerCandidates.Values)
        {
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
}
