using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatSessionTests
{
    [Fact]
    public void SendDraft_KeepsDraftWhenTransportFails()
    {
        var composer = new ChatComposer();
        composer.OpenKeyboard();
        composer.SetDraft("hello");
        var session = new ChatSession(
            new ChatHistory(10),
            composer,
            new StubTransport(ChatSendResult.Unavailable()));

        var result = session.SendDraft();

        Assert.Equal(ChatSendStatus.TransportUnavailable, result.Status);
        Assert.Equal("hello", composer.Draft);
        Assert.Equal(ChatInputMode.Keyboard, composer.Mode);
        Assert.Empty(session.History.Snapshot());
    }

    [Fact]
    public void SendDraft_ClearsDraftAndAddsLocalHistoryOnSuccess()
    {
        var composer = new ChatComposer();
        composer.OpenKeyboard();
        composer.SetDraft("  hello party  ");
        var transport = new StubTransport(ChatSendResult.Sent());
        var session = new ChatSession(new ChatHistory(10), composer, transport);

        var result = session.SendDraft();

        Assert.True(result.Succeeded);
        Assert.Equal("hello party", transport.LastMessage);
        Assert.Equal(ChatInputMode.Closed, composer.Mode);
        Assert.Empty(composer.Draft);
        Assert.Equal("hello party", Assert.Single(session.History.Snapshot()).Text);
        Assert.Equal(new[] { "hello party" }, session.InputHistory.Entries);
    }

    [Fact]
    public void SendText_NormalizesAndSendsWithoutChangingOpenDraft()
    {
        var composer = new ChatComposer();
        composer.OpenKeyboard();
        composer.SetDraft("work in progress");
        var transport = new StubTransport(ChatSendResult.Sent());
        var session = new ChatSession(new ChatHistory(10), composer, transport);

        var result = session.SendText("  Ready!\r\n✨  ");

        Assert.True(result.Succeeded);
        Assert.Equal("Ready! ✨", transport.LastMessage);
        Assert.Equal("work in progress", composer.Draft);
        Assert.Equal(ChatInputMode.Keyboard, composer.Mode);
        Assert.Equal("Ready! ✨", Assert.Single(session.History.Snapshot()).Text);
    }

    [Fact]
    public void SendText_RejectsEmptyTextWithoutCallingTransport()
    {
        var transport = new StubTransport(ChatSendResult.Sent());
        var session = new ChatSession(new ChatHistory(10), new ChatComposer(), transport);

        var result = session.SendText(" \r\n ");

        Assert.Equal(ChatSendStatus.EmptyDraft, result.Status);
        Assert.Null(transport.LastMessage);
        Assert.Empty(session.History.Snapshot());
    }

    [Fact]
    public void DrainIncoming_AppendsHookRecordsOnOwningThread()
    {
        var source = new StubIncomingSource(
            new IncomingChatMessage("Lyria", "Ready!", 1, 2, 3, DateTimeOffset.UtcNow, 3));
        var session = new ChatSession(
            new ChatHistory(10),
            new ChatComposer(),
            new StubTransport(ChatSendResult.Sent()),
            incoming: source);

        Assert.Equal(1, session.DrainIncoming());
        var message = Assert.Single(session.History.Snapshot());
        Assert.Equal("Lyria", message.Sender);
        Assert.Equal("Ready!", message.Text);
        Assert.Equal(ChatMessageKind.Party, message.Kind);
        Assert.Equal(1u, message.SenderId);
        Assert.Equal(3, message.PlayerNumber);
        Assert.Equal(0, session.DrainIncoming());
    }

    [Fact]
    public void DrainIncoming_UsesLocalHistoryForGameNativeChatInput()
    {
        var source = new StubIncomingSource(
            new IncomingChatMessage(
                "Kuro",
                "系统输入法发送",
                0,
                0,
                0,
                DateTimeOffset.UtcNow,
                PlayerNumber: 3,
                IsLocal: true));
        var session = new ChatSession(
            new ChatHistory(10),
            new ChatComposer(),
            new StubTransport(ChatSendResult.Sent()),
            localSender: "Kuro",
            incoming: source);

        Assert.Equal(1, session.DrainIncoming());
        var message = Assert.Single(session.History.Snapshot());
        Assert.Equal("Kuro", message.Sender);
        Assert.Equal("系统输入法发送", message.Text);
        Assert.Equal(ChatMessageKind.Self, message.Kind);
        Assert.Equal(3, message.PlayerNumber);
    }

    [Fact]
    public void AuthoritativeTransport_WaitsForTheGameEchoBeforeAddingLocalHistory()
    {
        var source = new StubIncomingSource(
            new IncomingChatMessage(
                "Actual Name",
                "Ready!",
                0,
                0,
                0,
                DateTimeOffset.UtcNow,
                PlayerNumber: 4,
                IsLocal: true));
        var transport = new AuthoritativeStubTransport();
        var session = new ChatSession(
            new ChatHistory(10),
            new ChatComposer(),
            transport,
            incoming: source,
            getLocalIdentity: () => new LocalChatIdentity("Wrong Fallback", 1));

        Assert.True(session.SendText("Ready!").Succeeded);
        Assert.Empty(session.History.Snapshot());

        Assert.Equal(1, session.DrainIncoming());
        var message = Assert.Single(session.History.Snapshot());
        Assert.Equal("Actual Name", message.Sender);
        Assert.Equal(4, message.PlayerNumber);
        Assert.Equal(ChatMessageKind.Self, message.Kind);
    }

    private sealed class StubTransport(ChatSendResult result) : IChatTransport
    {
        public string? LastMessage { get; private set; }

        public ChatSendResult Send(string message)
        {
            LastMessage = message;
            return result;
        }
    }

    private sealed class AuthoritativeStubTransport : IChatTransport, IAuthoritativeLocalEchoTransport
    {
        public ChatSendResult Send(string message) => ChatSendResult.Sent();
    }

    private sealed class StubIncomingSource(params IncomingChatMessage[] messages) : IIncomingChatSource
    {
        private readonly Queue<IncomingChatMessage> _messages = new(messages);

        public bool TryRead(out IncomingChatMessage message) => _messages.TryDequeue(out message);
    }
}
