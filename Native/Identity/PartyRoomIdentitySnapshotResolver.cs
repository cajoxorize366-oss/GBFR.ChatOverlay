namespace GBFR.ChatOverlay.Native;

internal static class PartyRoomIdentitySnapshotResolver
{
    internal static PartyRoomHostState ResolveHostState(
        string? ownerEntityId,
        RelinkPartyMemberIdentitySnapshot snapshot,
        bool hasObservedRemoteHostPresent,
        out int ownerSlot)
    {
        ownerSlot = -1;
        if (string.IsNullOrWhiteSpace(ownerEntityId) ||
            !TryNormalizeSnapshot(snapshot.EntityIds, snapshot.LocalMemberSlot, out var entityIdSlots))
        {
            return PartyRoomHostState.Unknown;
        }

        if (!entityIdSlots.TryGetValue(ownerEntityId, out ownerSlot))
        {
            ownerSlot = -1;
            return hasObservedRemoteHostPresent
                ? PartyRoomHostState.RemoteHostMissing
                : PartyRoomHostState.Unknown;
        }

        return ownerSlot == snapshot.LocalMemberSlot
            ? PartyRoomHostState.LocalHost
            : PartyRoomHostState.RemoteHostPresent;
    }

    internal static bool TryNormalizeSnapshot(
        IReadOnlyList<string>? memberEntityIds,
        int localMemberSlot,
        out IReadOnlyDictionary<string, int> entityIdSlots)
    {
        entityIdSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        if (memberEntityIds is null ||
            memberEntityIds.Count != 4 ||
            localMemberSlot is < 0 or > 3)
        {
            return false;
        }

        var slots = (Dictionary<string, int>)entityIdSlots;
        for (var index = 0; index < memberEntityIds.Count; index++)
        {
            var entityId = memberEntityIds[index];
            if (entityId is null ||
                (entityId.Length != 0 && string.IsNullOrWhiteSpace(entityId)))
            {
                return false;
            }

            if (entityId.Length == 0)
                continue;

            if (!slots.TryAdd(entityId, index))
                return false;
        }

        return true;
    }
}
