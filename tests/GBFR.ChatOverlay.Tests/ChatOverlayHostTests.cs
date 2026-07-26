using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatOverlayHostTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void VoiceIndicators_DrawWithoutGameMenu(
        bool onlineRoomActive,
        bool showAllVoiceIndicatorSlots)
    {
        Assert.True(ChatOverlayHost.ShouldDrawVoiceIndicators(
            onlineRoomActive,
            showAllVoiceIndicatorSlots,
            gameMenuVisible: false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void VoiceIndicators_NeverDrawOverGameMenu(
        bool onlineRoomActive,
        bool showAllVoiceIndicatorSlots)
    {
        Assert.False(ChatOverlayHost.ShouldDrawVoiceIndicators(
            onlineRoomActive,
            showAllVoiceIndicatorSlots,
            gameMenuVisible: true));
    }
}
