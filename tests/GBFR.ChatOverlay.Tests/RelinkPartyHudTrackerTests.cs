using GBFR.ChatOverlay.Native;
using System.Numerics;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkPartyHudTrackerTests
{
    [Fact]
    public void BattleAnchor_PrefersActualHpFillGaugeOverNarrowMaskNode()
    {
        Assert.Equal(
            [0x250, 0x270],
            RelinkPartyHudTracker.BattleAnchorPointerOffsets.ToArray());
    }

    [Theory]
    [InlineData(1504.0f, 197.33334f, 136.00002f, 710.6667f)]
    [InlineData(816.0f, 181.33334f, 288.0f, 465.33334f)]
    public void BattleAnchor_LiveRelinkSnapshotProjectsToHpBarRightEdge(
        float nativeWidth,
        float translationX,
        float translationY,
        float expectedX)
    {
        var transform = Matrix4x4.Identity;
        transform.M11 = 0.6666667f;
        transform.M22 = -0.6666667f;
        transform.M41 = translationX;
        transform.M42 = translationY;
        var localRightEdge = new Vector2((nativeWidth * 0.5f) + 18.0f, 0.0f);

        Assert.True(RelinkUiProjection.TryProject(
            transform,
            localRightEdge,
            0.0f,
            0.0f,
            2560.0f,
            1440.0f,
            out var center));
        Assert.InRange(center.X, expectedX - 0.01f, expectedX + 0.01f);
        Assert.InRange(center.Y, translationY - 0.01f, translationY + 0.01f);

        Assert.True(RelinkUiProjection.TryMeasureLogicalLength(
            transform,
            36.0f,
            0.0f,
            0.0f,
            2560.0f,
            1440.0f,
            out var iconSize));
        Assert.InRange(iconSize, 23.99f, 24.01f);
    }
}
