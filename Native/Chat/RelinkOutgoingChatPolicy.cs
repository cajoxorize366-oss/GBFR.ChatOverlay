using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

internal static class RelinkOutgoingChatPolicy
{
    // 2.0.4 auto-communication categories are separated from manual raw text in the official chat history.
    internal static int NormalizeForwardedCategory(
        uint messageHash,
        int category,
        ChatCommunicationCue communicationCue) =>
        messageHash == RelinkChatPacketDecoder.RawTextHash &&
        category is >= 0 and <= 19 &&
        communicationCue != ChatCommunicationCue.None
            ? -1
            : category;
}
