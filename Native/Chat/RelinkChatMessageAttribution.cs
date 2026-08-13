using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

internal static class RelinkChatMessageAttribution
{
    internal static IncomingChatMessage ApplyRemoteIdentity(
        IncomingChatMessage message,
        int localMemberSlot,
        int remoteMemberSlot,
        string? resolvedPlayerName)
    {
        if (!PartyMemberSlotMap.TryGetPlayerNumber(localMemberSlot, remoteMemberSlot, out var playerNumber))
        {
            message = message with { PlayerNumber = 0 };
        }
        else
        {
            message = message with { PlayerNumber = playerNumber };
        }

        if (!string.IsNullOrWhiteSpace(resolvedPlayerName))
            message = message with { Sender = resolvedPlayerName.Trim() };

        return message;
    }
}
