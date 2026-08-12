using System.Diagnostics.CodeAnalysis;
using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

internal static class RelinkChatMessageCueNormalization
{
    internal static bool IsMachineCue(string? senderLabel) =>
        ChatCommunicationCueClassifier.TryClassifySenderLabel(senderLabel, out _);

    internal static bool TryGetCacheableLocalSenderName(
        string? senderLabel,
        [NotNullWhen(true)] out string? senderName)
    {
        senderName = null;
        if (string.IsNullOrWhiteSpace(senderLabel) || IsMachineCue(senderLabel))
            return false;

        senderName = senderLabel.Trim();
        return true;
    }

    internal static IncomingChatMessage SanitizeIncomingSenderForEnqueue(
        IncomingChatMessage message)
    {
        if (!ChatCommunicationCueClassifier.TryClassifySenderLabel(
                message.Sender,
                out var communicationCue))
        {
            return message;
        }

        return message with
        {
            Sender = $"Player {message.SenderId:X8}",
            CommunicationCue = message.CommunicationCue == ChatCommunicationCue.None
                ? communicationCue
                : message.CommunicationCue,
        };
    }
}
