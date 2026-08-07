namespace GBFR.ChatOverlay.Core;

public enum ChatMessageKind
{
    Party,
    Self,
    System,
}

public sealed record ChatMessage(
    long Sequence,
    DateTimeOffset Timestamp,
    string Sender,
    string Text,
    ChatMessageKind Kind,
    uint SenderId = 0,
    int PlayerNumber = 0);
