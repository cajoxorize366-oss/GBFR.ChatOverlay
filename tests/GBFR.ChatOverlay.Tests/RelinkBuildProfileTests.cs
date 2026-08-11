using System.IO;
using System.Security.Cryptography;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkBuildProfileTests
{
    [Fact]
    public void Relink204Fixture_MatchesRequiredRvasAndDerivedTargets()
    {
        var imagePath = Environment.GetEnvironmentVariable("GBFR_RELINK_204_EXE");
        if (string.IsNullOrWhiteSpace(imagePath))
            return;

        Assert.True(File.Exists(imagePath), $"Relink 2.0.4 fixture was not found: {imagePath}");
        using (var stream = File.OpenRead(imagePath))
        {
            var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            Assert.Equal(RelinkBuildLocator.SupportedSha256, sha256);
        }

        var chat = RelinkBuildLocator.Resolve(imagePath);
        Assert.Equal(0x009049F0, chat.SendMessage);
        Assert.Equal(0x00B97950, chat.RpcMessage);
        Assert.Equal(0x07C23460, chat.ManagerSlot);
        Assert.Equal(0x006CD520, chat.SenderSlotResolver);
        Assert.Equal(0x003760A0, chat.LobbyMemberLookup);
        Assert.Equal(0x07C21AB8, chat.LobbyMemberManagerSlot);
        Assert.Equal(0x07C483A8, chat.PartyMemberIdentityManagerSlot);
        Assert.Equal(0x00903660, chat.SendStamp);
        Assert.Equal(0x009044F0, chat.SendFixedPhrase);
        Assert.Equal(0x009033A0, chat.SendEmotion);
        Assert.Equal(0x006E3A00, chat.PlayFixedPhrase);
        Assert.Equal(0x006E2B30, chat.PlayEmotion);
        Assert.Equal(0x049AD680, chat.LobbyOwnerImportThunk);

        var hud = RelinkHudBuildLocator.Resolve(imagePath);
        Assert.Equal(0x02590020, hud.TownFactory);
        Assert.Equal(0x025916C0, hud.TownDestructor);
        Assert.Equal(0x026043B0, hud.BattleFactory);
        Assert.Equal(0x02605810, hud.BattleDestructor);
        Assert.Equal(0x0262ACA0, hud.ChainburstFactory);
        Assert.Equal(0x0262BDD0, hud.ChainburstDestructor);
        Assert.Equal(0x07C00598, hud.UiManagerSlot);
        Assert.Equal(0x05A50BD8, hud.TownVtable);
        Assert.Equal(0x05A60088, hud.BattleVtable);
        Assert.Equal(0x05A65B98, hud.ChainburstVtable);
    }
}
