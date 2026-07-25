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

    [Fact]
    public void PackColor_UsesDearImguiAbgrByteOrderAndClampsAlpha()
    {
        Assert.Equal(0xFF38220Cu, VoiceIndicatorOverlay.PackColor(0x0C, 0x22, 0x38, 1.0f));
        Assert.Equal(0xB238220Cu, VoiceIndicatorOverlay.PackColor(0x0C, 0x22, 0x38, 0.70f));
        Assert.Equal(0x0038220Cu, VoiceIndicatorOverlay.PackColor(0x0C, 0x22, 0x38, -1.0f));
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
