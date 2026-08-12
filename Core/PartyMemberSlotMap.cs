namespace GBFR.ChatOverlay.Core;

internal static class PartyMemberSlotMap
{
    internal const int MemberCount = 4;

    internal static bool TryGetRemoteOrdinal(int localSlot, int actualSlot, out int remoteOrdinal)
    {
        remoteOrdinal = 0;
        if (!IsValidSlot(localSlot) || !IsValidSlot(actualSlot) || localSlot == actualSlot)
            return false;

        var ordinal = 0;
        for (var slot = 0; slot < MemberCount; slot++)
        {
            if (slot == localSlot)
                continue;

            ordinal++;
            if (slot == actualSlot)
            {
                remoteOrdinal = ordinal;
                return true;
            }
        }

        return false;
    }

    internal static bool TryGetActualSlot(int localSlot, int remoteOrdinal, out int actualSlot)
    {
        actualSlot = -1;
        if (!IsValidSlot(localSlot) || remoteOrdinal is < 1 or > 3)
            return false;

        var ordinal = 0;
        for (var slot = 0; slot < MemberCount; slot++)
        {
            if (slot == localSlot)
                continue;

            ordinal++;
            if (ordinal == remoteOrdinal)
            {
                actualSlot = slot;
                return true;
            }
        }

        return false;
    }

    internal static bool TryGetPlayerNumber(int localSlot, int actualSlot, out int playerNumber)
    {
        if (!TryGetRemoteOrdinal(localSlot, actualSlot, out var remoteOrdinal))
        {
            playerNumber = 0;
            return false;
        }

        playerNumber = remoteOrdinal + 1;
        return true;
    }

    internal static bool IsValidSlot(int slot) => slot is >= 0 and < MemberCount;
}
