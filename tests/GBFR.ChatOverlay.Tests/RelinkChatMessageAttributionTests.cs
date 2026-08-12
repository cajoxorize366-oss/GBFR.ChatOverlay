using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkChatMessageAttributionTests
{
    [Fact]
    public void ApplyRemoteIdentity_MachineCueResolvesToRealNameAndPlayerNumber()
    {
        var message = new IncomingChatMessage(
            "Player 00000000",
            "普通聊天文本",
            0,
            7,
            9,
            DateTimeOffset.UtcNow,
            CommunicationCue: ChatCommunicationCue.Victory);

        var result = RelinkChatMessageAttribution.ApplyRemoteIdentity(
            message,
            hasExplicitSenderLabel: false,
            memberSlot: 2,
            resolvedPlayerName: "Djeeta");

        Assert.Equal("Djeeta", result.Sender);
        Assert.Equal(3, result.PlayerNumber);
        Assert.Equal(ChatCommunicationCue.Victory, result.CommunicationCue);
    }

    [Fact]
    public void ApplyRemoteIdentity_KeepsStableFallbackWhenSlotIsUnavailable()
    {
        var message = new IncomingChatMessage(
            "Player 00000000",
            "普通聊天文本",
            0,
            7,
            9,
            DateTimeOffset.UtcNow,
            CommunicationCue: ChatCommunicationCue.Victory);

        var result = RelinkChatMessageAttribution.ApplyRemoteIdentity(
            message,
            hasExplicitSenderLabel: false,
            memberSlot: -1,
            resolvedPlayerName: null);

        Assert.Equal("Player 00000000", result.Sender);
        Assert.Equal(0, result.PlayerNumber);
        Assert.Equal(ChatCommunicationCue.Victory, result.CommunicationCue);
    }

    [Theory]
    [InlineData("vo_CMM_thanks")]
    [InlineData("\uFEFF\u200B\u0001vo_CMM_thanks")]
    public void ApplyRemoteIdentity_RejectsMachineCueAsResolvedPlayerName(
        string resolvedPlayerName)
    {
        var message = new IncomingChatMessage(
            "Player 00000000",
            "普通聊天文本",
            0,
            7,
            9,
            DateTimeOffset.UtcNow);

        var result = RelinkChatMessageAttribution.ApplyRemoteIdentity(
            message,
            hasExplicitSenderLabel: false,
            memberSlot: 1,
            resolvedPlayerName: resolvedPlayerName);

        Assert.Equal("Player 00000000", result.Sender);
        Assert.Equal(2, result.PlayerNumber);
    }
}
