using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class VoiceIndicatorOverlayTests
{
    [Fact]
    public void CreatePlacements_UsesLiveNativeHudCoordinatesWithoutRescaling()
    {
        PartyHudAnchor[] anchors =
        [
            new(0, true, PartyHudLayout.OnlineLobby, 321.5f, 98.25f, 44.0f),
            new(1, false, PartyHudLayout.OnlineLobby, 333.0f, 211.0f, 43.5f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: true,
            new PartyVoiceUiStatus(PartyVoiceUiState.WaitingForPeer),
            anchors);

        Assert.Collection(
            placements,
            placement => AssertPlacement(placement, 0, 321.5f, 98.25f, 44.0f),
            placement => AssertPlacement(placement, 1, 333.0f, 211.0f, 43.5f));
    }

    [Fact]
    public void CreatePlacements_HidesUnmappedSlotsWhenDebugOverrideIsOff()
    {
        PartyHudAnchor[] anchors =
        [
            new(0, true, PartyHudLayout.Battle, 500.0f, 100.0f, 48.0f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: false,
            new PartyVoiceUiStatus(PartyVoiceUiState.Speaking),
            anchors);

        Assert.Empty(placements);
    }

    [Fact]
    public void CreatePlacements_ShowsSpeakingOpacityOnlyOnNativeLocalRow()
    {
        PartyHudAnchor[] anchors =
        [
            new(0, false, PartyHudLayout.Battle, 420.0f, 240.0f, 36.0f),
            new(1, true, PartyHudLayout.Battle, 650.0f, 100.0f, 48.0f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: true,
            new PartyVoiceUiStatus(PartyVoiceUiState.Speaking),
            anchors);

        Assert.Equal(VoiceIndicatorOverlay.IdleOpacity, placements[0].Opacity);
        Assert.False(placements[0].IsSpeaking);
        Assert.Equal(VoiceIndicatorOverlay.SpeakingOpacity, placements[1].Opacity);
        Assert.True(placements[1].IsSpeaking);
    }

    [Fact]
    public void CreatePlacements_DropsInvalidNativeAnchorFailClosed()
    {
        PartyHudAnchor[] anchors =
        [
            new(0, true, PartyHudLayout.OnlineLobby, float.NaN, 20.0f, 40.0f),
            new(1, false, PartyHudLayout.OnlineLobby, 10.0f, 20.0f, 0.0f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: true,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            anchors);

        Assert.Empty(placements);
    }

    [Theory]
    [InlineData((int)PartyHudLayout.OnlineLobby)]
    [InlineData((int)PartyHudLayout.Battle)]
    public void CreatePlacements_FormalModeMapsSparseEstablishedRemoteRowsDeterministically(
        int layoutValue)
    {
        var layout = (PartyHudLayout)layoutValue;
        PartyHudAnchor[] anchors =
        [
            new(0, true, layout, 640.0f, 100.0f, 48.0f),
            new(12, false, layout, 700.0f, 300.0f, 44.0f),
            new(11, false, layout, 680.0f, 200.0f, 44.0f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: false,
            new PartyVoiceUiStatus(PartyVoiceUiState.Speaking),
            anchors,
            establishedRemotePlayers: [3, 3, 0, 4],
            occupiedRemotePlayers: [3, 1, 3],
            talkingRemotePlayers: [3, 9],
            snapshotsValid: true);

        Assert.Collection(
            placements,
            local =>
            {
                Assert.Equal(0, local.SlotIndex);
                Assert.Equal(VoiceIndicatorOverlay.SpeakingOpacity, local.Opacity);
                Assert.True(local.IsSpeaking);
            },
            remote =>
            {
                Assert.Equal(12, remote.SlotIndex);
                Assert.Equal(VoiceIndicatorOverlay.SpeakingOpacity, remote.Opacity);
                Assert.True(remote.IsSpeaking);
            });
    }

    [Fact]
    public void CreatePlacements_FormalModeMapsFixedThreeRemoteRowsWithoutCpuIdentityGuessing()
    {
        PartyHudAnchor[] anchors =
        [
            new(0, true, PartyHudLayout.Battle, 640.0f, 100.0f, 48.0f),
            new(1, false, PartyHudLayout.Battle, 680.0f, 180.0f, 44.0f),
            new(2, false, PartyHudLayout.Battle, 680.0f, 240.0f, 44.0f),
            new(3, false, PartyHudLayout.Battle, 680.0f, 300.0f, 44.0f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: false,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            anchors,
            establishedRemotePlayers: [1],
            occupiedRemotePlayers: [1],
            talkingRemotePlayers: [1],
            snapshotsValid: true);

        Assert.Collection(
            placements,
            local =>
            {
                Assert.Equal(0, local.SlotIndex);
                Assert.Equal(VoiceIndicatorOverlay.IdleOpacity, local.Opacity);
                Assert.False(local.IsSpeaking);
            },
            remote =>
            {
                Assert.Equal(1, remote.SlotIndex);
                Assert.Equal(VoiceIndicatorOverlay.SpeakingOpacity, remote.Opacity);
                Assert.True(remote.IsSpeaking);
            });
    }

    [Fact]
    public void CreatePlacements_DebugModeUsesTheFormalFixedThreeRowSpeakingMap()
    {
        PartyHudAnchor[] anchors =
        [
            new(0, true, PartyHudLayout.Battle, 640.0f, 100.0f, 48.0f),
            new(1, false, PartyHudLayout.Battle, 680.0f, 180.0f, 44.0f),
            new(2, false, PartyHudLayout.Battle, 680.0f, 240.0f, 44.0f),
            new(3, false, PartyHudLayout.Battle, 680.0f, 300.0f, 44.0f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: true,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            anchors,
            establishedRemotePlayers: [2],
            occupiedRemotePlayers: [2],
            talkingRemotePlayers: [2],
            snapshotsValid: true);

        Assert.Equal(4, placements.Count);
        Assert.False(placements.Single(placement => placement.SlotIndex == 1).IsSpeaking);
        Assert.True(placements.Single(placement => placement.SlotIndex == 2).IsSpeaking);
        Assert.False(placements.Single(placement => placement.SlotIndex == 3).IsSpeaking);
    }

    [Fact]
    public void CreatePlacements_FormalModeShowsEstablishedIdleRemoteChannel()
    {
        PartyHudAnchor[] anchors =
        [
            new(0, true, PartyHudLayout.OnlineLobby, 321.0f, 98.0f, 44.0f),
            new(1, false, PartyHudLayout.OnlineLobby, 333.0f, 211.0f, 43.5f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: false,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            anchors,
            establishedRemotePlayers: [1],
            occupiedRemotePlayers: [1],
            talkingRemotePlayers: [],
            snapshotsValid: true);

        Assert.Collection(
            placements,
            local => Assert.Equal(VoiceIndicatorOverlay.IdleOpacity, local.Opacity),
            remote =>
            {
                Assert.Equal(1, remote.SlotIndex);
                Assert.Equal(VoiceIndicatorOverlay.IdleOpacity, remote.Opacity);
                Assert.False(remote.IsSpeaking);
            });
    }

    [Fact]
    public void CreatePlacements_FormalModeFailsClosedWhenHudRowMappingIsIncoherent()
    {
        PartyHudAnchor[] anchors =
        [
            new(0, true, PartyHudLayout.Battle, 500.0f, 100.0f, 48.0f),
            new(1, false, PartyHudLayout.Battle, 520.0f, 180.0f, 44.0f),
            new(2, false, PartyHudLayout.Battle, 540.0f, 260.0f, 44.0f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: false,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            anchors,
            establishedRemotePlayers: [1],
            occupiedRemotePlayers: [1],
            talkingRemotePlayers: [],
            snapshotsValid: true);

        Assert.Empty(placements);
    }

    [Fact]
    public void CreatePlacements_FormalModeFailsClosedForMixedLobbyAndBattleAnchors()
    {
        PartyHudAnchor[] anchors =
        [
            new(0, true, PartyHudLayout.OnlineLobby, 500.0f, 100.0f, 48.0f),
            new(1, false, PartyHudLayout.Battle, 520.0f, 180.0f, 44.0f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: false,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            anchors,
            establishedRemotePlayers: [1],
            occupiedRemotePlayers: [1],
            talkingRemotePlayers: [],
            snapshotsValid: true);

        Assert.Empty(placements);
    }

    [Fact]
    public void CreatePlacements_DebugModeShowsValidAnchorsWithoutEstablishedChannels()
    {
        PartyHudAnchor[] anchors =
        [
            new(0, true, PartyHudLayout.Battle, 500.0f, 100.0f, 48.0f),
            new(1, false, PartyHudLayout.Battle, 520.0f, 180.0f, 44.0f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: true,
            new PartyVoiceUiStatus(PartyVoiceUiState.WaitingForPeer),
            anchors,
            establishedRemotePlayers: [],
            occupiedRemotePlayers: [],
            talkingRemotePlayers: [],
            snapshotsValid: false);

        Assert.Equal(2, placements.Count);
        Assert.All(placements, placement => Assert.Equal(VoiceIndicatorOverlay.IdleOpacity, placement.Opacity));
    }

    [Fact]
    public void CreatePlacements_FormalModeFailsClosedWhenSnapshotGetterThrows()
    {
        PartyHudAnchor[] anchors =
        [
            new(0, true, PartyHudLayout.OnlineLobby, 321.0f, 98.0f, 44.0f),
            new(1, false, PartyHudLayout.OnlineLobby, 333.0f, 211.0f, 43.5f),
        ];

        var placements = VoiceIndicatorOverlay.CreatePlacements(
            showAllSlots: false,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            anchors,
            establishedRemotePlayers: [1],
            occupiedRemotePlayers: [1],
            talkingRemotePlayers: [],
            snapshotsValid: false);

        Assert.Empty(placements);
    }

    [Fact]
    public void PackColor_UsesDearImguiAbgrByteOrderAndClampsAlpha()
    {
        Assert.Equal(0xFF38220Cu, VoiceIndicatorOverlay.PackColor(0x0C, 0x22, 0x38, 1.0f));
        Assert.Equal(0xB238220Cu, VoiceIndicatorOverlay.PackColor(0x0C, 0x22, 0x38, 0.70f));
        Assert.Equal(0x0038220Cu, VoiceIndicatorOverlay.PackColor(0x0C, 0x22, 0x38, -1.0f));
    }

    [Fact]
    public void CreatePalette_KeepsSeventyPercentIdleAlphaButMakesIdleVisiblyMuted()
    {
        var idle = VoiceIndicatorOverlay.CreatePalette(
            isSpeaking: false,
            VoiceIndicatorOverlay.IdleOpacity);
        var speaking = VoiceIndicatorOverlay.CreatePalette(
            isSpeaking: true,
            VoiceIndicatorOverlay.SpeakingOpacity);

        Assert.Equal(0xB2000000u, idle.Foreground & 0xFF000000u);
        Assert.Equal(0xFF000000u, speaking.Foreground & 0xFF000000u);
        Assert.NotEqual(idle.Foreground & 0x00FFFFFFu, speaking.Foreground & 0x00FFFFFFu);
        Assert.NotEqual(idle.Accent & 0x00FFFFFFu, speaking.Accent & 0x00FFFFFFu);
    }

    private static void AssertPlacement(
        VoiceIndicatorPlacement placement,
        int slotIndex,
        float centerX,
        float centerY,
        float size)
    {
        Assert.Equal(slotIndex, placement.SlotIndex);
        Assert.Equal(centerX, placement.CenterX);
        Assert.Equal(centerY, placement.CenterY);
        Assert.Equal(size, placement.Size);
        Assert.Equal(VoiceIndicatorOverlay.IdleOpacity, placement.Opacity);
        Assert.False(placement.IsSpeaking);
    }
}
