using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkChatMessageAttributionTests
{
    [Fact]
    public void ApplyRemoteIdentity_MachineCueResolvesToRealNameAndRelativePlayerNumber()
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
            localMemberSlot: 2,
            remoteMemberSlot: 0,
            resolvedPlayerName: "Djeeta");

        Assert.Equal("Djeeta", result.Sender);
        Assert.Equal(2, result.PlayerNumber);
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
            localMemberSlot: 2,
            remoteMemberSlot: -1,
            resolvedPlayerName: null);

        Assert.Equal("Player 00000000", result.Sender);
        Assert.Equal(0, result.PlayerNumber);
        Assert.Equal(ChatCommunicationCue.Victory, result.CommunicationCue);
    }

    [Fact]
    public void ApplyRemoteIdentity_AcceptsVerifiedLobbyNameEvenWhenItMatchesCueSyntax()
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
            localMemberSlot: 2,
            remoteMemberSlot: 1,
            resolvedPlayerName: "vo_CMM_thanks");

        Assert.Equal("vo_CMM_thanks", result.Sender);
        Assert.Equal(3, result.PlayerNumber);
        Assert.Equal(ChatCommunicationCue.None, result.CommunicationCue);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 3)]
    [InlineData(3, 4)]
    public void ApplyRemoteIdentity_UsesRelativePlayerNumberAroundLocalSlot(
        int remoteMemberSlot,
        int expectedPlayerNumber)
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
            localMemberSlot: 2,
            remoteMemberSlot,
            resolvedPlayerName: null);

        Assert.Equal(expectedPlayerNumber, result.PlayerNumber);
        Assert.Equal("Player 00000000", result.Sender);
    }

    [Fact]
    public void ApplyRemoteIdentity_FailsClosedWhenLocalSlotCannotBeProven()
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
            localMemberSlot: -1,
            remoteMemberSlot: 2,
            resolvedPlayerName: "Djeeta");

        Assert.Equal(0, result.PlayerNumber);
        Assert.Equal("Djeeta", result.Sender);
    }
}
