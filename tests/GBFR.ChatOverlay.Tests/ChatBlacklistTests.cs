using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatBlacklistTests
{
    [Fact]
    public void PlayerMute_UsesUiPlayerNumbersAndNeverBlocksLocalPlayer()
    {
        var blacklist = new ChatBlacklist();

        Assert.True(blacklist.SetMuted(3, true));

        Assert.False(blacklist.IsMemberSlotMuted(0));
        Assert.False(blacklist.IsMemberSlotMuted(1));
        Assert.True(blacklist.IsMemberSlotMuted(2));
        Assert.False(blacklist.IsMemberSlotMuted(3));
    }

    [Fact]
    public void GlobalToggle_BlocksAndClearsAllRemotePlayers()
    {
        var blacklist = new ChatBlacklist();

        Assert.True(blacklist.ToggleAllRemotePlayers());
        Assert.True(blacklist.AreAllRemotePlayersMuted);
        Assert.False(blacklist.IsMuted(1));

        Assert.False(blacklist.ToggleAllRemotePlayers());
        Assert.False(blacklist.AreAllRemotePlayersMuted);
    }

    [Fact]
    public void Clear_RemovesRoomScopedEntries()
    {
        var blacklist = new ChatBlacklist();
        blacklist.SetMuted(2, true);
        blacklist.SetMuted(4, true);

        blacklist.Clear();

        Assert.False(blacklist.IsMuted(2));
        Assert.False(blacklist.IsMuted(4));
    }
}
