using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkOutgoingChatPolicyTests
{
    [Theory]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 0, ChatCommunicationCue.Victory)]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 7, ChatCommunicationCue.LinkAttack)]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 19, ChatCommunicationCue.Thanks)]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 0, ChatCommunicationCue.Official)]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 7, ChatCommunicationCue.Victory)]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 19, ChatCommunicationCue.LinkAttack)]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 0, ChatCommunicationCue.Thanks)]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 7, ChatCommunicationCue.Official)]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 19, ChatCommunicationCue.Victory)]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 0, ChatCommunicationCue.LinkAttack)]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 7, ChatCommunicationCue.Thanks)]
    [InlineData(RelinkChatPacketDecoder.RawTextHash, 19, ChatCommunicationCue.Official)]
    public void RawTextWithMachineCue_NormalizesCategoryToManualSentinel(
        uint messageHash,
        int category,
        ChatCommunicationCue cue)
    {
        Assert.Equal(-1, RelinkOutgoingChatPolicy.NormalizeForwardedCategory(messageHash, category, cue));
    }

    [Theory]
    [InlineData(ChatCommunicationCue.None)]
    [InlineData(ChatCommunicationCue.Victory)]
    [InlineData(ChatCommunicationCue.Official)]
    public void RawTextCategoryMinusOne_IsPreserved(ChatCommunicationCue cue)
    {
        Assert.Equal(
            -1,
            RelinkOutgoingChatPolicy.NormalizeForwardedCategory(
                RelinkChatPacketDecoder.RawTextHash,
                -1,
                cue));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    [InlineData(20)]
    [InlineData(int.MaxValue)]
    public void RawTextOutsideAutoCommunicationRange_IsPreserved(int category)
    {
        Assert.Equal(
            category,
            RelinkOutgoingChatPolicy.NormalizeForwardedCategory(
                RelinkChatPacketDecoder.RawTextHash,
                category,
                ChatCommunicationCue.Official));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(19)]
    public void RawTextWithoutMachineCue_IsPreserved(int category)
    {
        Assert.Equal(
            category,
            RelinkOutgoingChatPolicy.NormalizeForwardedCategory(
                RelinkChatPacketDecoder.RawTextHash,
                category,
                ChatCommunicationCue.None));
    }

    [Theory]
    [InlineData(0, ChatCommunicationCue.Victory)]
    [InlineData(7, ChatCommunicationCue.LinkAttack)]
    [InlineData(19, ChatCommunicationCue.Official)]
    public void NonRawTextWithMachineCue_IsPreserved(int category, ChatCommunicationCue cue)
    {
        Assert.Equal(
            category,
            RelinkOutgoingChatPolicy.NormalizeForwardedCategory(0x12345678u, category, cue));
    }

    [Fact]
    public void UnknownMachineCueMapsToOfficialAndNormalizes()
    {
        var cue = RelinkChatPacketDecoder.ClassifyCommunicationCue("vo_CMM_unknown_action", out _);

        Assert.Equal(ChatCommunicationCue.Official, cue);
        Assert.Equal(
            -1,
            RelinkOutgoingChatPolicy.NormalizeForwardedCategory(
                RelinkChatPacketDecoder.RawTextHash,
                7,
                cue));
    }

    [Fact]
    public void UnreadableOrNonMachineSenderViewYieldsNoCueAndPreservesCategory()
    {
        var cue = RelinkChatPacketDecoder.ClassifyCommunicationCue(string.Empty, out _);
        Assert.Equal(ChatCommunicationCue.None, cue);
        Assert.Equal(
            7,
            RelinkOutgoingChatPolicy.NormalizeForwardedCategory(
                RelinkChatPacketDecoder.RawTextHash,
                7,
                cue));
    }
}
