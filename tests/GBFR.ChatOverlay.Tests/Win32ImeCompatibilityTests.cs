using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class Win32ImeCompatibilityTests
{
    [Fact]
    public void PackedCp936ImeCharacterDecodesAsOneChineseCharacter()
    {
        var decoded = Win32ImeCompatibility.TryDecodePackedAnsiCharacter(
            0xCED2,
            936,
            out var text);

        Assert.True(decoded);
        Assert.Equal("我", text);
        Assert.NotEqual("ÎÒ", text);
    }

    [Fact]
    public void SplitCp936WindowCharactersAreReassembledBeforeDecoding()
    {
        var pendingLeadByte = -1;

        var leadDecoded = Win32ImeCompatibility.TryConsumeAnsiWindowCharacter(
            0xCE,
            936,
            ref pendingLeadByte,
            out var leadText);
        var trailDecoded = Win32ImeCompatibility.TryConsumeAnsiWindowCharacter(
            0xD2,
            936,
            ref pendingLeadByte,
            out var trailText);

        Assert.True(leadDecoded);
        Assert.Empty(leadText);
        Assert.True(trailDecoded);
        Assert.Equal("我", trailText);
        Assert.Equal(-1, pendingLeadByte);
    }

    [Fact]
    public void AsciiWindowCharacterDoesNotWaitForAnotherByte()
    {
        var pendingLeadByte = -1;

        var decoded = Win32ImeCompatibility.TryConsumeAnsiWindowCharacter(
            'A',
            936,
            ref pendingLeadByte,
            out var text);

        Assert.True(decoded);
        Assert.Equal("A", text);
        Assert.Equal(-1, pendingLeadByte);
    }

    [Fact]
    public void InvalidCp936ByteIsRejectedInsteadOfBecomingLatinOneText()
    {
        var pendingLeadByte = -1;

        var decoded = Win32ImeCompatibility.TryConsumeAnsiWindowCharacter(
            0xFF,
            936,
            ref pendingLeadByte,
            out var text);

        Assert.False(decoded);
        Assert.Empty(text);
        Assert.Equal(-1, pendingLeadByte);
    }

    [Theory]
    [InlineData(Win32ImeCompatibility.WmImeStartComposition)]
    [InlineData(Win32ImeCompatibility.WmImeEndComposition)]
    [InlineData(Win32ImeCompatibility.WmImeComposition)]
    [InlineData(Win32ImeCompatibility.WmImeSetContext)]
    [InlineData(Win32ImeCompatibility.WmImeNotify)]
    [InlineData(Win32ImeCompatibility.WmImeControl)]
    [InlineData(Win32ImeCompatibility.WmImeCompositionFull)]
    [InlineData(Win32ImeCompatibility.WmImeSelect)]
    [InlineData(Win32ImeCompatibility.WmImeRequest)]
    public void ImeLifecycleMessagesAreRoutedToTheDefaultWindowProcedure(uint message)
    {
        Assert.True(Win32ImeCompatibility.IsImeUiMessage(message));
    }

    [Fact]
    public void CommittedImeCharacterIsHandledSeparatelyFromImeUiLifecycle()
    {
        Assert.False(Win32ImeCompatibility.IsImeUiMessage(Win32ImeCompatibility.WmImeChar));
    }
}
