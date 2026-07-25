using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatOverlayLayoutTests
{
    [Fact]
    public void CalculateHistoryChildHeight_UsesBaseComposerReserveWithoutCandidates()
    {
        Assert.Equal(-58.0f, ChatOverlayHost.CalculateHistoryChildHeight(true, 0.0f));
    }

    [Theory]
    [InlineData(20.0f, -78.0f)]
    [InlineData(40.0f, -98.0f)]
    public void CalculateHistoryChildHeight_AddsExactWrappedCandidateHeight(
        float candidateHeight,
        float expected)
    {
        Assert.Equal(expected, ChatOverlayHost.CalculateHistoryChildHeight(true, candidateHeight));
    }

    [Theory]
    [InlineData(-1.0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void CalculateHistoryChildHeight_IgnoresInvalidCandidateHeight(float candidateHeight)
    {
        Assert.Equal(-58.0f, ChatOverlayHost.CalculateHistoryChildHeight(true, candidateHeight));
    }

    [Fact]
    public void CalculateHistoryChildHeight_DoesNotReserveComposerSpaceWhenClosed()
    {
        Assert.Equal(0.0f, ChatOverlayHost.CalculateHistoryChildHeight(false, 40.0f));
    }
}
