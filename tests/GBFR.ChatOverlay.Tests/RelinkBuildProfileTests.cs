using System.IO;
using System.Security.Cryptography;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkBuildProfileTests
{
    [Fact]
    public void Relink203Fixture_MatchesRequiredRvasAndDerivedTargets()
    {
        var imagePath = Environment.GetEnvironmentVariable("GBFR_RELINK_203_EXE");
        if (string.IsNullOrWhiteSpace(imagePath))
            return;

        Assert.True(File.Exists(imagePath), $"Relink 2.0.3 fixture was not found: {imagePath}");
        using (var stream = File.OpenRead(imagePath))
        {
            var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            Assert.Equal(RelinkBuildLocator.SupportedSha256, sha256);
        }

        var chat = RelinkBuildLocator.Resolve(imagePath);
        Assert.Equal(0x00903A50, chat.SendMessage);
        Assert.Equal(0x00B969B0, chat.RpcMessage);
        Assert.Equal(0x07C221E0, chat.ManagerSlot);
        Assert.Equal(0x006CC580, chat.SenderSlotResolver);
        Assert.Equal(0x003760A0, chat.LobbyMemberLookup);
        Assert.Equal(0x07C20838, chat.LobbyMemberManagerSlot);
        Assert.Equal(0x07C47128, chat.PartyMemberIdentityManagerSlot);
        Assert.Equal(0x009026C0, chat.SendStamp);
        Assert.Equal(0x00903550, chat.SendFixedPhrase);
        Assert.Equal(0x00902400, chat.SendEmotion);
        Assert.Equal(0x006E2A60, chat.PlayFixedPhrase);
        Assert.Equal(0x006E1B90, chat.PlayEmotion);
        Assert.Equal(0x049AC6E0, chat.LobbyOwnerImportThunk);

        var hud = RelinkHudBuildLocator.Resolve(imagePath);
        Assert.Equal(0x0258F080, hud.TownFactory);
        Assert.Equal(0x02590720, hud.TownDestructor);
        Assert.Equal(0x02603410, hud.BattleFactory);
        Assert.Equal(0x02604870, hud.BattleDestructor);
        Assert.Equal(0x0262BC20, hud.ChainburstFactory);
        Assert.Equal(0x0262AE30, hud.ChainburstDestructor);
        Assert.Equal(0x07BFF318, hud.UiManagerSlot);
    }
}
