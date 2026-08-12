using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

internal static class RelinkChatMessageAttribution
{
    internal static bool IsMachineCue(string? senderLabel) =>
        RelinkChatMessageCueNormalization.IsMachineCue(senderLabel);

    internal static string CreateFallbackSender(uint senderId) => $"Player {senderId:X8}";

    internal static IncomingChatMessage ApplyRemoteIdentity(
        IncomingChatMessage message,
        bool hasExplicitSenderLabel,
        int memberSlot,
        string? resolvedPlayerName)
    {
        if (IsMachineCue(message.Sender))
            message = message with { Sender = CreateFallbackSender(message.SenderId) };

        if (memberSlot is < 0 or >= 4)
            return message;

        message = message with { PlayerNumber = memberSlot + 1 };
        if (hasExplicitSenderLabel)
            return message;

        if (!string.IsNullOrWhiteSpace(resolvedPlayerName) && !IsMachineCue(resolvedPlayerName))
            message = message with { Sender = resolvedPlayerName.Trim() };

        return message;
    }
}
