namespace GBFR.ChatOverlay.Core;

public readonly record struct IncomingChatMessage(
    string Sender,
    string Text,
    uint SenderId,
    uint Category,
    uint Metadata,
    DateTimeOffset ReceivedAt,
    int PlayerNumber = 0,
    bool IsLocal = false,
    ChatCommunicationCue CommunicationCue = ChatCommunicationCue.None);

public interface IIncomingChatSource
{
    bool TryRead(out IncomingChatMessage message);
}
