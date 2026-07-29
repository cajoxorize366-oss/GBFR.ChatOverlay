namespace GBFR.ChatOverlay.Core;

/// <summary>
/// Coordinates composing, transport and local history without depending on ImGui.
/// </summary>
public sealed class ChatSession
{
    private readonly IChatTransport _transport;
    private readonly IIncomingChatSource? _incoming;
    private readonly TimeProvider _timeProvider;
    private readonly string _localSender;

    public ChatSession(
        ChatHistory history,
        ChatComposer composer,
        IChatTransport transport,
        string localSender = "You",
        TimeProvider? timeProvider = null,
        IIncomingChatSource? incoming = null,
        string? transportStatusText = null)
    {
        History = history ?? throw new ArgumentNullException(nameof(history));
        Composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(localSender);
        _localSender = localSender;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _incoming = incoming;
        TransportStatusText = transportStatusText ?? "Local preview: the Relink chat bridge is not attached yet.";
    }

    public ChatHistory History { get; }
    public ChatComposer Composer { get; }
    public string TransportStatusText { get; }

    public int DrainIncoming(int maximumMessages = 128)
    {
        if (maximumMessages <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMessages));

        var count = 0;
        while (count < maximumMessages && _incoming?.TryRead(out var message) is true)
        {
            History.Add(message.Sender, message.Text, ChatMessageKind.Party, message.ReceivedAt);
            count++;
        }

        return count;
    }

    public ChatSendResult SendDraft()
    {
        if (!Composer.TryGetSubmittableText(out var text))
            return ChatSendResult.EmptyDraft();

        var result = SendNormalizedText(text);
        if (!result.Succeeded)
            return result;

        Composer.MarkSubmitted();
        return result;
    }

    public ChatSendResult SendText(string? text)
    {
        var quickComposer = new ChatComposer(Composer.MaximumDraftLength);
        quickComposer.SetDraft(text);
        if (!quickComposer.TryGetSubmittableText(out var normalizedText))
            return ChatSendResult.EmptyDraft();

        return SendNormalizedText(normalizedText);
    }

    private ChatSendResult SendNormalizedText(string text)
    {
        var result = _transport.Send(text);
        if (result.Succeeded)
            History.Add(_localSender, text, ChatMessageKind.Self, _timeProvider.GetUtcNow());

        return result;
    }
}
