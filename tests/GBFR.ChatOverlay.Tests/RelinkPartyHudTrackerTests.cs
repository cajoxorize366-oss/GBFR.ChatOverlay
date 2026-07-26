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
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(-1, false)]
    [InlineData(4, false)]
    public void ControllerVisibilityState_MatchesRelinkStateMachine(int state, bool expected)
    {
        Assert.Equal(expected, RelinkPartyHudTracker.IsControllerVisibilityStateVisible(state));
    }

    [Theory]
    [InlineData(1504.0f, 197.33334f, 136.00002f, 722.6667f)]
    [InlineData(816.0f, 181.33334f, 288.0f, 477.33334f)]
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
        var localRightEdge = new Vector2(
            (nativeWidth * 0.5f) + RelinkPartyHudTracker.NativeRightEdgeGap,
            0.0f);

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
            RelinkPartyHudTracker.NativeIconLogicalSize,
            0.0f,
            0.0f,
            2560.0f,
            1440.0f,
            out var iconSize));
        Assert.InRange(iconSize, 47.99f, 48.01f);
    }
}
