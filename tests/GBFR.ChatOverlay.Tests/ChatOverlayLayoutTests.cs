using GBFR.ChatOverlay.Overlay;
using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatOverlayLayoutTests
{
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
