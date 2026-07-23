using GBFR.ChatOverlay.Input;

namespace GBFR.ChatOverlay.Tests;

public sealed class PushToTalkChordDetectorTests
{
    [Fact]
    public void Poll_HoldChordStartsOnceAndReleaseStopsOnce()
    {
        var source = new FakeXInputSource();
        var detector = new PushToTalkChordDetector();
        var starts = 0;
        var stops = 0;
        source.Buttons[0] = PushToTalkChordDetector.DefaultChord;

        detector.Poll(source, true, Begin, () => stops++);
        detector.Poll(source, true, Begin, () => stops++);
        source.Buttons[0] = 0;
        detector.Poll(source, true, Begin, () => stops++);

        Assert.Equal(1, starts);
        Assert.Equal(1, stops);
        return;

        bool Begin()
        {
            starts++;
            return true;
        }
    }

    [Fact]
    public void Poll_DisconnectEndsAcceptedCapture()
    {
        var source = new FakeXInputSource();
        var detector = new PushToTalkChordDetector();
        var stops = 0;
        source.Buttons[2] = PushToTalkChordDetector.DefaultChord;
        detector.Poll(source, true, () => true, () => stops++);

        source.Buttons.Remove(2);
        detector.Poll(source, true, () => true, () => stops++);

        Assert.Equal(1, stops);
    }

    [Fact]
    public void Poll_DisablingInputEndsAcceptedCapture()
    {
        var source = new FakeXInputSource();
        var detector = new PushToTalkChordDetector();
        var stops = 0;
        source.Buttons[0] = PushToTalkChordDetector.DefaultChord;
        detector.Poll(source, true, () => true, () => stops++);

        detector.Poll(source, false, () => true, () => stops++);

        Assert.Equal(1, stops);
    }

    [Fact]
    public void Poll_RequiresBothButtons()
    {
        var source = new FakeXInputSource();
        var detector = new PushToTalkChordDetector();
        var starts = 0;
        source.Buttons[0] = PushToTalkChordDetector.LeftShoulder;

        detector.Poll(source, true, () => { starts++; return true; }, () => { });

        Assert.Equal(0, starts);
    }

    private sealed class FakeXInputSource : IXInputStateSource
    {
        public Dictionary<int, ushort> Buttons { get; } = new();

        public bool TryGetButtons(int playerIndex, out ushort buttons) =>
            Buttons.TryGetValue(playerIndex, out buttons);
    }
}
