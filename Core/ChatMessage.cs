namespace GBFR.ChatOverlay.Core;

public enum ChatMessageKind
{
    Party,
    Self,
    System,
}

public enum ChatCommunicationCue
{
    None,
    Victory,
    LinkAttack,
    Thanks,
    Official,
}

public sealed record ChatMessage(
    long Sequence,
    DateTimeOffset Timestamp,
    string Sender,
    string Text,
    ChatMessageKind Kind,
    uint SenderId = 0,
    int PlayerNumber = 0,
    ChatCommunicationCue CommunicationCue = ChatCommunicationCue.None);
