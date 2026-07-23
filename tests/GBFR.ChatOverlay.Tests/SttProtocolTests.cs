using GBFR.ChatOverlay.Stt;

namespace GBFR.ChatOverlay.Tests;

public sealed class SttProtocolTests
{
    [Fact]
    public void Command_RoundTripsAsSingleJsonLine()
    {
        var line = SttProtocol.Serialize(new SttCommand(SttMessageTypes.Start, 42));

        Assert.DoesNotContain('\n', line);
        Assert.True(SttProtocol.TryParseCommand(line, out var command, out var error), error);
        Assert.Equal(new SttCommand(SttMessageTypes.Start, 42), command);
    }

    [Fact]
    public void Event_PreservesUnicodeTranscript()
    {
        var line = SttProtocol.Serialize(new SttEvent(SttMessageTypes.Result, 7, Text: "准备出发"));

        Assert.True(SttProtocol.TryParseEvent(line, out var message, out var error), error);
        Assert.Equal("准备出发", message!.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"unknown\",\"requestId\":1}")]
    [InlineData("{\"type\":\"start\",\"requestId\":0}")]
    public void Command_RejectsMalformedOrUnknownMessages(string line)
    {
        Assert.False(SttProtocol.TryParseCommand(line, out var command, out var error));
        Assert.Null(command);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parser_RejectsOversizedMessages()
    {
        var line = new string('x', SttProtocol.MaximumMessageCharacters + 1);

        Assert.False(SttProtocol.TryParseEvent(line, out _, out var error));
        Assert.Contains("exceeds", error);
    }
}
