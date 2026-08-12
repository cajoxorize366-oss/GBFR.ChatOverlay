namespace GBFR.ChatOverlay.Core;

public readonly record struct LocalChatIdentity(string Sender, int PlayerNumber);

public interface IAuthoritativeLocalEchoTransport
{
}

/// <summary>
/// Coordinates composing, transport and local history without depending on ImGui.
/// </summary>
public sealed class ChatSession
{
    private readonly IChatTransport _transport;
    private readonly IIncomingChatSource? _incoming;
    private readonly TimeProvider _timeProvider;
    private readonly string _localSender;
    private readonly Func<LocalChatIdentity> _getLocalIdentity;

    public ChatSession(
        ChatHistory history,
        ChatComposer composer,
        IChatTransport transport,
        string localSender = "You",
        TimeProvider? timeProvider = null,
        IIncomingChatSource? incoming = null,
        string? transportStatusText = null,
        Func<LocalChatIdentity>? getLocalIdentity = null)
    {
        History = history ?? throw new ArgumentNullException(nameof(history));
        Composer = composer ?? throw new ArgumentNullException(nameof(composer));
        InputHistory = new ChatInputHistory();
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(localSender);
        _localSender = localSender;
        _getLocalIdentity = getLocalIdentity ?? (() => new LocalChatIdentity(_localSender, 0));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _incoming = incoming;
        TransportStatusText = transportStatusText ?? "Local preview: the Relink chat bridge is not attached yet.";
    }

    public ChatHistory History { get; }
    public ChatComposer Composer { get; }
    public ChatInputHistory InputHistory { get; }
    public string TransportStatusText { get; }

    public int DrainIncoming(int maximumMessages = 128)
    {
        if (maximumMessages <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMessages));

        var count = 0;
        while (count < maximumMessages && _incoming?.TryRead(out var message) is true)
        {
            var sender = message.IsLocal && string.IsNullOrWhiteSpace(message.Sender)
                ? GetLocalIdentity().Sender
                : message.Sender;
            History.Add(
                sender,
                message.Text,
                message.IsLocal ? ChatMessageKind.Self : ChatMessageKind.Party,
                message.ReceivedAt,
                message.SenderId,
                message.PlayerNumber,
                message.CommunicationCue);
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
        InputHistory.Record(text);
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
        if (result.Succeeded && _transport is not IAuthoritativeLocalEchoTransport)
        {
            var identity = GetLocalIdentity();
            History.Add(
                identity.Sender,
                text,
                ChatMessageKind.Self,
                _timeProvider.GetUtcNow(),
                playerNumber: identity.PlayerNumber);
        }

        return result;
    }

    private LocalChatIdentity GetLocalIdentity()
    {
        try
        {
            var identity = _getLocalIdentity();
            if (!string.IsNullOrWhiteSpace(identity.Sender))
                return identity with { PlayerNumber = Math.Clamp(identity.PlayerNumber, 0, 4) };
        }
        catch
        {
            // Identity lookup is diagnostic UI data and must never block chat sending.
        }

        return new LocalChatIdentity(_localSender, 0);
    }
}
