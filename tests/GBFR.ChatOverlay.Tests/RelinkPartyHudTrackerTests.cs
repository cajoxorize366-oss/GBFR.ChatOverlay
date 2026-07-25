using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkPartyHudTrackerTests
{
    [Fact]
    public void BattleAnchor_PrefersActualHpFillGaugeOverNarrowMaskNode()
    {
        Assert.Equal(
            [0x3B0, 0x3D0],
            RelinkPartyHudTracker.BattleAnchorPointerOffsets.ToArray());
    }
}
