namespace GBFR.ChatOverlay.Core;

/// <summary>
/// Accepts messages without sending them outside the process. Used until the
/// version-specific Relink chat bridge is available.
/// </summary>
public sealed class LocalPreviewChatTransport : IChatTransport
{
    public ChatSendResult Send(string message) => ChatSendResult.Sent();
}
