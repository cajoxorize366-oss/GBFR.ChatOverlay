using GBFR.ChatOverlay.Overlay;
using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatOverlayLayoutTests
{
    [Theory]
    [InlineData(false, false, false, "Full")]
    [InlineData(false, true, false, "Full")]
    [InlineData(true, false, false, "Hidden")]
    [InlineData(true, true, false, "Compact")]
    [InlineData(true, false, true, "Compact")]
    [InlineData(false, false, true, "Full")]
    public void ResolvePresentation_SelectsExpectedMode(
        bool compactMode,
        bool composerOpen,
        bool editMode,
        string expected)
    {
        Assert.Equal(
            expected,
            ChatOverlayLayout.ResolvePresentation(compactMode, composerOpen, editMode).ToString());
    }

    [Fact]
    public void ResolveCompactInputRect_PreservesWidthAndAnchorsToFullRectBottom()
    {
        var fullRect = new ChatOverlayRect(100, 200, 560, 260);

        var compactRect = ChatOverlayLayout.ResolveCompactInputRect(
            fullRect,
            voiceHeight: 0.0f,
            candidateHeight: 20.0f,
            statusHeight: 30.0f,
            fontScale: 1.0f,
            minimumY: 0.0f);

        Assert.Equal(100.0f, compactRect.X);
        Assert.Equal(560.0f, compactRect.Width);
        Assert.Equal(108.0f, compactRect.Height);
        Assert.Equal(352.0f, compactRect.Y);
        Assert.Equal(fullRect.Y + fullRect.Height, compactRect.Y + compactRect.Height);
    }

    [Fact]
    public void ResolveCompactInputRect_ClampsTopToViewportWorkArea()
    {
        var fullRect = new ChatOverlayRect(100, 20, 560, 160);

        var compactRect = ChatOverlayLayout.ResolveCompactInputRect(
            fullRect,
            voiceHeight: 0.0f,
            candidateHeight: 100.0f,
            statusHeight: 100.0f,
            fontScale: 1.0f,
            minimumY: 10.0f);

        Assert.Equal(10.0f, compactRect.Y);
        Assert.Equal(170.0f, compactRect.Height);
        Assert.Equal(fullRect.Y + fullRect.Height, compactRect.Y + compactRect.Height);
    }

    [Theory]
    [InlineData(float.NaN, float.PositiveInfinity, -1.0f, 58.0f)]
    [InlineData(-1.0f, 0.0f, 0.0f, 58.0f)]
    [InlineData(0.0f, 20.0f, 30.0f, 108.0f)]
    public void ResolveCompactInputHeight_UsesOnlyFinitePositiveAdditionalHeights(
        float voiceHeight,
        float candidateHeight,
        float statusHeight,
        float expected)
    {
        Assert.Equal(
            expected,
            ChatOverlayLayout.ResolveCompactInputHeight(
                voiceHeight,
                candidateHeight,
                statusHeight,
                fontScale: 1.0f));
    }

    [Fact]
    public void ResolveCompactInputHeight_IncludesVisibleVoiceHeightAndScalesWithFont()
    {
        Assert.Equal(
            296.0f,
            ChatOverlayLayout.ResolveCompactInputHeight(
                voiceHeight: 20.0f,
                candidateHeight: 30.0f,
                statusHeight: 40.0f,
                fontScale: 2.0f));
    }

    [Fact]
    public void ApplyCompactEditToFullRect_PreservesFullHeightAndBottomAnchor()
    {
        var fullRect = new ChatOverlayRect(100, 200, 560, 260);
        var compactRect = new ChatOverlayRect(100, 352, 560, 108);
        var editedCompactRect = new ChatOverlayRect(120, 362, 640, 108);

        var editedFullRect = ChatOverlayLayout.ApplyCompactEditToFullRect(
            fullRect,
            compactRect,
            editedCompactRect,
            workX: 0.0f,
            workY: 0.0f,
            workWidth: 1_000.0f,
            workHeight: 800.0f);

        Assert.Equal(120.0f, editedFullRect.X);
        Assert.Equal(210.0f, editedFullRect.Y);
        Assert.Equal(640.0f, editedFullRect.Width);
        Assert.Equal(260.0f, editedFullRect.Height);
        Assert.Equal(470.0f, editedFullRect.Y + editedFullRect.Height);
    }

    [Fact]
    public void CalculateHistoryChildHeight_UsesBaseComposerReserveWithoutCandidates()
    {
        Assert.Equal(-58.0f, ChatOverlayPeer.CalculateHistoryChildHeight(true, 0.0f));
    }

    [Theory]
    [InlineData(20.0f, -78.0f)]
    [InlineData(40.0f, -98.0f)]
    public void CalculateHistoryChildHeight_AddsExactWrappedCandidateHeight(
        float candidateHeight,
        float expected)
    {
        Assert.Equal(expected, ChatOverlayPeer.CalculateHistoryChildHeight(true, candidateHeight));
    }

    [Theory]
    [InlineData(-1.0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void CalculateHistoryChildHeight_IgnoresInvalidCandidateHeight(float candidateHeight)
    {
        Assert.Equal(-58.0f, ChatOverlayPeer.CalculateHistoryChildHeight(true, candidateHeight));
    }

    [Fact]
    public void CalculateHistoryChildHeight_DoesNotReserveComposerSpaceWhenClosed()
    {
        Assert.Equal(0.0f, ChatOverlayPeer.CalculateHistoryChildHeight(false, 40.0f));
    }

    [Fact]
    public void Resolve_PreservesSavedRelativePositionAcrossResolutions()
    {
        var configuration = new Config
        {
            OverlayWidth = 560,
            OverlayHeight = 260,
            OverlayPositionXRatio = 0.5,
            OverlayPositionYRatio = 0.25,
        };

        var first = ChatOverlayLayout.Resolve(configuration, 0, 0, 1920, 1080);
        var second = ChatOverlayLayout.Resolve(configuration, 0, 0, 2560, 1440);

        Assert.Equal(680.0f, first.X);
        Assert.Equal(205.0f, first.Y);
        Assert.Equal(1000.0f, second.X);
        Assert.Equal(295.0f, second.Y);
    }

    [Fact]
    public void Resize_ClampsToViewportAndMinimumSize()
    {
        var rect = new ChatOverlayRect(100, 100, 560, 260);

        var small = ChatOverlayLayout.Resize(rect, -1000, -1000, 0, 0, 800, 600);
        var large = ChatOverlayLayout.Resize(rect, 1000, 1000, 0, 0, 800, 600);

        Assert.Equal(320.0f, small.Width);
        Assert.Equal(160.0f, small.Height);
        Assert.Equal(700.0f, large.Width);
        Assert.Equal(500.0f, large.Height);
    }

    [Fact]
    public void ToRatios_RoundTripsEditedPosition()
    {
        var rect = new ChatOverlayRect(420, 310, 500, 250);
        var ratios = ChatOverlayLayout.ToRatios(rect, 20, 10, 1500, 900);
        var configuration = new Config
        {
            OverlayWidth = 500,
            OverlayHeight = 250,
            OverlayPositionXRatio = ratios.XRatio,
            OverlayPositionYRatio = ratios.YRatio,
        };

        var resolved = ChatOverlayLayout.Resolve(configuration, 20, 10, 1500, 900);

        Assert.Equal(rect.X, resolved.X, 3);
        Assert.Equal(rect.Y, resolved.Y, 3);
    }
}
