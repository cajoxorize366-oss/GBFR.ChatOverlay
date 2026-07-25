using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class CjkConfiguredDx11HookTests
{
    [Fact]
    public void GlyphRanges_AreOrderedPairsWithATerminator()
    {
        var ranges = CjkConfiguredDx11Hook.BuildGlyphRanges();

        Assert.True(ranges.Length >= 3);
        Assert.Equal(1, ranges.Length % 2);
        Assert.Equal((ushort)0, ranges[^1]);
        for (var index = 0; index < ranges.Length - 1; index += 2)
        {
            Assert.NotEqual((ushort)0, ranges[index]);
            Assert.True(ranges[index] <= ranges[index + 1]);
            if (index > 0)
                Assert.True(ranges[index - 1] < ranges[index]);
        }
    }

    [Theory]
    [InlineData('A')]
    [InlineData('中')]
    [InlineData('文')]
    [InlineData('，')]
    public void GlyphRanges_ContainExpectedChatCharacters(char character)
    {
        var ranges = CjkConfiguredDx11Hook.BuildGlyphRanges();

        Assert.True(Contains(ranges, character));
    }

    private static bool Contains(IReadOnlyList<ushort> ranges, char character)
    {
        for (var index = 0; index < ranges.Count - 1; index += 2)
        {
            if (character >= ranges[index] && character <= ranges[index + 1])
                return true;
        }

        return false;
    }
}
