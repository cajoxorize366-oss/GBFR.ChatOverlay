using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkPartyHudTrackerTests
{
    [Fact]
    public void BattleAnchor_PrefersActualHpFillGaugeOverNarrowMaskNode()
    {
        Assert.Equal(
            [0x370, 0x390],
            RelinkPartyHudTracker.BattleAnchorPointerOffsets.ToArray());
    }
}
