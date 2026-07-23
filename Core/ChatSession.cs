namespace GBFR.ChatOverlay.Core;

/// <summary>
/// Coordinates composing, transport and local history without depending on ImGui.
/// </summary>
public sealed class ChatSession
{
    private readonly IChatTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly string _localSender;

    public ChatSession(
        ChatHistory history,
        ChatComposer composer,
        IChatTransport transport,
        string localSender = "You",
        TimeProvider? timeProvider = null)
    {
        History = history ?? throw new ArgumentNullException(nameof(history));
        Composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(localSender);
        _localSender = localSender;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ChatHistory History { get; }
    public ChatComposer Composer { get; }

    public ChatSendResult SendDraft()
    {
        if (!Composer.TryGetSubmittableText(out var text))
            return ChatSendResult.EmptyDraft();

        var result = _transport.Send(text);
        if (!result.Succeeded)
            return result;

        History.Add(_localSender, text, ChatMessageKind.Self, _timeProvider.GetUtcNow());
        Composer.MarkSubmitted();
        return result;
    }
}
