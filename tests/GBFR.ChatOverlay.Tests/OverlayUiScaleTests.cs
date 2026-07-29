using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class OverlayUiScaleTests
{
    [Theory]
    [InlineData(0u, 1.0f)]
    [InlineData(96u, 1.0f)]
    [InlineData(144u, 1.5f)]
    [InlineData(192u, 2.0f)]
    [InlineData(384u, 2.0f)]
    public void FromDpi_ReturnsBoundedScale(uint dpi, float expected)
    {
        Assert.Equal(expected, OverlayUiScale.FromDpi(dpi));
    }
}
