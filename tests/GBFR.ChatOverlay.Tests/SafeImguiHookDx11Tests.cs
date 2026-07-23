using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class SafeImguiHookDx11Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0x7FFFFFFF)]
    public void HResultSucceeded_AcceptsNonNegativeValues(long value)
    {
        Assert.True(SafeImguiHookDx11.HResultSucceeded((nint)value));
    }

    [Fact]
    public void HResultSucceeded_RejectsSignExtendedFailure()
    {
        var value = unchecked((nint)(int)0x80004005);

        Assert.False(SafeImguiHookDx11.HResultSucceeded(value));
    }

    [Fact]
    public void HResultSucceeded_RejectsZeroExtendedFailureOnX64()
    {
        var value = unchecked((nint)0x0000000080004005L);

        Assert.False(SafeImguiHookDx11.HResultSucceeded(value));
    }
}
