using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkChatMessageCueNormalizationTests
{
    [Theory]
    [InlineData("vo_CMM_win_3")]
    [InlineData("vo_CMM_chance")]
    [InlineData("vo_CMM_thanks")]
    [InlineData("\uFEFFvo_CMM_win_3")]
    [InlineData("\u200Bvo_CMM_chance")]
    [InlineData("\u0001vo_CMM_thanks")]
    [InlineData("\tvo_CMM_win_3")]
    [InlineData("vo_CMM_unknown_action")]
    public void TryGetCacheableLocalSenderName_RejectsMachineLabels(string senderLabel)
    {
        Assert.False(RelinkChatMessageCueNormalization.TryGetCacheableLocalSenderName(
            senderLabel,
            out var senderName));
        Assert.Null(senderName);
    }

    [Theory]
    [InlineData("Kuro", "Kuro")]
    [InlineData("Kuro_vo_CMM_win_3", "Kuro_vo_CMM_win_3")]
    [InlineData("Local", "Local")]
    [InlineData("  Djeeta  ", "Djeeta")]
    public void TryGetCacheableLocalSenderName_AcceptsNormalPlayerNames(
        string senderLabel,
        string expected)
    {
        Assert.True(RelinkChatMessageCueNormalization.TryGetCacheableLocalSenderName(
            senderLabel,
            out var senderName));
        Assert.Equal(expected, senderName);
    }

    [Fact]
    public void TryGetCacheableLocalSenderName_RejectsEmptyAndWhitespace()
    {
        Assert.False(RelinkChatMessageCueNormalization.TryGetCacheableLocalSenderName(
            string.Empty,
            out var emptyName));
        Assert.Null(emptyName);

        Assert.False(RelinkChatMessageCueNormalization.TryGetCacheableLocalSenderName(
            "   ",
            out var whitespaceName));
        Assert.Null(whitespaceName);
    }

    [Theory]
    [InlineData("vo_CMM_win_3", 0x89ABCDEFu, "Player 89ABCDEF", ChatCommunicationCue.Victory)]
    [InlineData("\uFEFFvo_CMM_win_3", 0u, "Player 00000000", ChatCommunicationCue.Victory)]
    [InlineData("\u200Bvo_CMM_chance", 0x1234u, "Player 00001234", ChatCommunicationCue.LinkAttack)]
    [InlineData("\u0001vo_CMM_thanks", 0x1234u, "Player 00001234", ChatCommunicationCue.Thanks)]
    [InlineData("vo_CMM_unknown_action", 0x89ABCDEFu, "Player 89ABCDEF", ChatCommunicationCue.None)]
    public void NormalizeIncomingForEnqueue_ReplacesRawMachineSender(
        string rawSender,
        uint senderId,
        string expectedSender,
        ChatCommunicationCue cue)
    {
        var message = new IncomingChatMessage(
            rawSender,
            "hello",
            senderId,
            7,
            9,
            DateTimeOffset.UtcNow,
            CommunicationCue: cue);

        var normalized = RelinkChatBridge.NormalizeIncomingForEnqueue(message);

        Assert.Equal(expectedSender, normalized.Sender);
        Assert.Equal(senderId, normalized.SenderId);
        Assert.Equal("hello", normalized.Text);
        Assert.Equal(cue, normalized.CommunicationCue);
    }

    [Theory]
    [InlineData("vo_CMM_win_3", ChatCommunicationCue.Victory)]
    [InlineData("vo_CMM_chance", ChatCommunicationCue.LinkAttack)]
    [InlineData("vo_CMM_thanks", ChatCommunicationCue.Thanks)]
    [InlineData("vo_CMM_unknown_action", ChatCommunicationCue.None)]
    public void NormalizeIncomingForEnqueue_RecoversMissingCue(
        string rawSender,
        ChatCommunicationCue expectedCue)
    {
        var message = new IncomingChatMessage(
            rawSender,
            "hello",
            0x1234,
            7,
            9,
            DateTimeOffset.UtcNow);

        var normalized = RelinkChatBridge.NormalizeIncomingForEnqueue(message);

        Assert.Equal("Player 00001234", normalized.Sender);
        Assert.Equal(expectedCue, normalized.CommunicationCue);
    }

    [Theory]
    [InlineData("Kuro", 0x1234u)]
    [InlineData("Kuro_vo_CMM_win_3", 0x1234u)]
    [InlineData("Local", 0u)]
    public void NormalizeIncomingForEnqueue_KeepsNormalPlayerName(
        string sender,
        uint senderId)
    {
        var message = new IncomingChatMessage(
            sender,
            "hello",
            senderId,
            0,
            0,
            DateTimeOffset.UtcNow);

        var normalized = RelinkChatBridge.NormalizeIncomingForEnqueue(message);

        Assert.Equal(sender, normalized.Sender);
    }
}
