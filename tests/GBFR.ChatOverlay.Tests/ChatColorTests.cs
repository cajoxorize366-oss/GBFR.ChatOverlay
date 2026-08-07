using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatColorTests
{
    [Theory]
    [InlineData("#5ED9FF", "#5ED9FF")]
    [InlineData("71df8a", "#71DF8A")]
    public void HexColor_RoundTrips(string configured, string expected)
    {
        Assert.True(ChatColor.TryParseRgb(configured, out var color));
        Assert.Equal(expected, ChatColor.ToHex(color));
        Assert.True(ChatColor.TryParseImGuiColor(configured, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("not-a-color")]
    public void TryParseRgb_RejectsInvalidValues(string configured)
    {
        Assert.False(ChatColor.TryParseRgb(configured, out _));
    }
}
