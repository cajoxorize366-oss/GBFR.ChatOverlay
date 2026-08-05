using GBFR.ChatOverlay.Input;

namespace GBFR.ChatOverlay.Tests;

public sealed class DirectInputMouseStateFilterTests
{
    [Fact]
    public void Capture_ClearsMovementAndButtons()
    {
        var filter = new DirectInputMouseStateFilter();
        var state = Enumerable.Repeat((byte)0x80, 20).ToArray();

        var filtered = filter.Process(state, capture: true);

        Assert.True(filtered);
        Assert.All(state, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Release_DoesNotKeepASeparateButtonLatch()
    {
        var filter = new DirectInputMouseStateFilter();
        var state = new byte[20];
        state[12] = 0x80;

        Assert.True(filter.Process(state, capture: true));
        state[12] = 0x80;
        Assert.False(filter.Process(state, capture: false));
        Assert.Equal(0x80, state[12]);
    }

    [Theory]
    [InlineData(0x0100, InputCaptureDevices.Keyboard)]
    [InlineData(0x0104, InputCaptureDevices.Keyboard)]
    [InlineData(0x0102, InputCaptureDevices.Text)]
    [InlineData(0x010F, InputCaptureDevices.Text)]
    [InlineData(0x0201, InputCaptureDevices.Mouse)]
    [InlineData(0x00A1, InputCaptureDevices.Mouse)]
    public void WindowClassifier_CapturesOnlyTheRequestedDeviceClass(
        uint message,
        InputCaptureDevices devices)
    {
        Assert.True(WindowInputClassifier.IsAlwaysCaptured(message, devices));
        Assert.False(
            WindowInputClassifier.IsAlwaysCaptured(
                message,
                InputCaptureDevices.All & ~devices));
    }

    [Theory]
    [InlineData(0x000F)]
    [InlineData(0x0119)]
    [InlineData(0x0240)]
    [InlineData(0x0312)]
    [InlineData(0x0319)]
    public void WindowClassifier_DoesNotCaptureUnrelatedWindowMessages(uint message)
    {
        Assert.False(WindowInputClassifier.IsAlwaysCaptured(message, InputCaptureDevices.All));
    }
}
