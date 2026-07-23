namespace GBFR.ChatOverlay.Core;

public enum ChatSendStatus
{
    Sent,
    EmptyDraft,
    TransportUnavailable,
    Rejected,
    Failed,
}

public readonly record struct ChatSendResult(ChatSendStatus Status, string? Error = null)
{
    public bool Succeeded => Status is ChatSendStatus.Sent;

    public static ChatSendResult Sent() => new(ChatSendStatus.Sent);
    public static ChatSendResult EmptyDraft() => new(ChatSendStatus.EmptyDraft);
    public static ChatSendResult Unavailable(string? error = null) =>
        new(ChatSendStatus.TransportUnavailable, error);
    public static ChatSendResult Rejected(string error) => new(ChatSendStatus.Rejected, error);
    public static ChatSendResult Failed(string error) => new(ChatSendStatus.Failed, error);
}

public interface IChatTransport
{
    ChatSendResult Send(string message);
}

public sealed class UnavailableChatTransport : IChatTransport
{
    public ChatSendResult Send(string message) =>
        ChatSendResult.Unavailable("Relink chat bridge has not been attached yet.");
}
