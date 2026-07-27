using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class SignaturePatternTests
{
    [Fact]
    public void FindUniqueOffset_MatchesWildcards()
    {
        var pattern = SignaturePattern.Parse("48 8B ?? 05");
        byte[] source = [0x90, 0x48, 0x8B, 0x7D, 0x05, 0xC3];

        Assert.Equal(1, pattern.FindUniqueOffset(source, "test"));
    }

    [Fact]
    public void Matches_RequiresExactLengthAndHonorsWildcards()
    {
        var pattern = SignaturePattern.Parse("48 8B ?? 05");

        Assert.True(pattern.Matches([0x48, 0x8B, 0x7D, 0x05]));
        Assert.False(pattern.Matches([0x48, 0x8B, 0x7D]));
        Assert.False(pattern.Matches([0x48, 0x8B, 0x7D, 0x06]));
    }

    [Fact]
    public void FindUniqueOffset_HandlesLeadingWildcardAnchor()
    {
        var pattern = SignaturePattern.Parse("?? AA BB");
        byte[] source = [0x90, 0x11, 0xAA, 0xBB, 0x90];

        Assert.Equal(1, pattern.FindUniqueOffset(source, "leading wildcard"));
    }

    [Fact]
    public void FindUniqueOffset_RejectsAmbiguousPattern()
    {
        var pattern = SignaturePattern.Parse("AA ?? CC");
        byte[] source = [0xAA, 0x01, 0xCC, 0xAA, 0x02, 0xCC];

        var exception = Assert.Throws<InvalidOperationException>(
            () => pattern.FindUniqueOffset(source, "test"));

        Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?? ??")]
    [InlineData("GG")]
    public void Parse_RejectsUnsafePatterns(string value)
    {
        Assert.ThrowsAny<Exception>(() => SignaturePattern.Parse(value));
    }
}
