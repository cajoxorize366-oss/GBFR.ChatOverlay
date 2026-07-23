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
}
