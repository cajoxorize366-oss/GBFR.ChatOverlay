using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkChatSenderPolicyTests
{
    [Fact]
    public void ChatBlacklist_MapsActualSlotsAroundNonZeroLocalSlot()
    {
        var blacklist = new ChatBlacklist();
        blacklist.ToggleAllRemotePlayers();

        Assert.True(blacklist.IsMemberSlotMuted(0, localMemberSlot: 2));
        Assert.True(blacklist.IsMemberSlotMuted(1, localMemberSlot: 2));
        Assert.True(blacklist.IsMemberSlotMuted(3, localMemberSlot: 2));
        Assert.False(blacklist.IsMemberSlotMuted(2, localMemberSlot: 2));
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(-1, -1)]
    [InlineData(4, 2)]
    [InlineData(0, -1)]
    [InlineData(0, 4)]
    public void ReceiveGate_FailsOpenWhenEitherSlotIsOutsideTheFourPartySlots(
        int partyMemberSlot,
        int localMemberSlot)
    {
        var blacklist = new ChatBlacklist();
        blacklist.ToggleAllRemotePlayers();

        Assert.False(RelinkChatSenderPolicy.ShouldBlockBlacklistedRpc(
            partyMemberSlot,
            localMemberSlot,
            blacklist));
    }

    [Fact]
    public void ReceiveGate_BlocksRemoteActualSlotZeroAroundLocalSlotTwo()
    {
        var globalMute = new ChatBlacklist();
        globalMute.ToggleAllRemotePlayers();
        Assert.True(RelinkChatSenderPolicy.ShouldBlockBlacklistedRpc(0, 2, globalMute));

        var ordinalMute = new ChatBlacklist();
        Assert.True(ordinalMute.SetMuted(2, true));
        Assert.True(RelinkChatSenderPolicy.ShouldBlockBlacklistedRpc(0, 2, ordinalMute));
        Assert.False(RelinkChatSenderPolicy.ShouldBlockBlacklistedRpc(1, 2, ordinalMute));
        Assert.False(RelinkChatSenderPolicy.ShouldBlockBlacklistedRpc(3, 2, ordinalMute));
    }

    [Fact]
    public void ReceiveGate_NeverBlocksLocalSlotEvenWhenGlobalMuteIsActive()
    {
        var blacklist = new ChatBlacklist();
        blacklist.ToggleAllRemotePlayers();

        Assert.False(RelinkChatSenderPolicy.ShouldBlockBlacklistedRpc(2, 2, blacklist));
    }

    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(2, 2, true)]
    [InlineData(-1, 2, false)]
    [InlineData(0, -1, false)]
    [InlineData(4, 2, false)]
    [InlineData(0, 4, false)]
    public void ModerationGate_RequiresBothVerifiedFourPartySlots(
        int partyMemberSlot,
        int localMemberSlot,
        bool expected)
    {
        Assert.Equal(
            expected,
            RelinkChatSenderPolicy.CanApplyModeration(partyMemberSlot, localMemberSlot));
    }

    [Fact]
    public void EchoConsumption_RequiresProvenLocalSender()
    {
        var suppressor = new RecentEchoSuppressor(TimeSpan.FromSeconds(5));
        var now = DateTimeOffset.UtcNow;
        suppressor.Register("same text", now);

        Assert.False(RelinkChatSenderPolicy.TryConsumeAuthoritativeLocalEcho(
            suppressor,
            isLocal: false,
            "same text",
            now,
            out var remoteWasLocal));
        Assert.False(remoteWasLocal);

        Assert.True(RelinkChatSenderPolicy.TryConsumeAuthoritativeLocalEcho(
            suppressor,
            isLocal: true,
            "same text",
            now,
            out var localWasLocal));
        Assert.True(localWasLocal);
    }

    [Fact]
    public void LocalIdentityCache_AlwaysUsesUiPlayerOneAcrossNameUpdates()
    {
        var cache = new LocalChatIdentityCache("Local");
        cache.UpdateName("Djeeta");

        cache.UpdateName(null);
        var identity = cache.Read();

        Assert.Equal("Djeeta", identity.Sender);
        Assert.Equal(1, identity.PlayerNumber);
        Assert.True(cache.TryReadVerifiedName(out var verifiedName));
        Assert.Equal("Djeeta", verifiedName);

        cache.Clear();
        Assert.Equal("Local", cache.Read().Sender);
        Assert.Equal(1, cache.Read().PlayerNumber);
        Assert.False(cache.TryReadVerifiedName(out _));
    }

    [Fact]
    public void LocalEcho_PreservesVerifiedIdentityAndPresentationCue()
    {
        var completedAt = new DateTimeOffset(2026, 8, 12, 20, 0, 0, TimeSpan.FromHours(8));

        var message = RelinkChatBridge.CreateLocalEchoMessage(
            "Victory!",
            new LocalChatIdentity("Kuro", 1),
            completedAt,
            ChatCommunicationCue.Victory);

        Assert.Equal("Kuro", message.Sender);
        Assert.Equal("Victory!", message.Text);
        Assert.Equal(1, message.PlayerNumber);
        Assert.True(message.IsLocal);
        Assert.Equal(ChatCommunicationCue.Victory, message.CommunicationCue);
        Assert.Equal(completedAt, message.ReceivedAt);
    }
}
