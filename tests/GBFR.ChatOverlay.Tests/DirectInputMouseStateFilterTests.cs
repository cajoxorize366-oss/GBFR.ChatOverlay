using GBFR.ChatOverlay.Input;
using GBFR.OverlayHub.Contracts;
using GBFR.OverlayHub.Runtime;

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
    public void Close_DrainsHeldButtonBeforeReturningMouseToGame()
    {
        var filter = new DirectInputMouseStateFilter();
        var state = new byte[20];
        state[12] = 0x80;

        Assert.True(filter.Process(state, capture: true));
        state[12] = 0x80;
        Assert.True(filter.Process(state, capture: false));
        Assert.True(filter.IsSuppressing);

        Assert.True(filter.Process(state, capture: false));
        Assert.False(filter.IsSuppressing);

        state[4] = 1;
        Assert.False(filter.Process(state, capture: false));
        Assert.Equal(1, state[4]);
    }

    [Theory]
    [InlineData(0x0100)]
    [InlineData(0x0201)]
    [InlineData(0x00A1)]
    [InlineData(0x010F)]
    public void WindowClassifier_AlwaysCapturesKeyboardMouseAndIme(uint message)
    {
        Assert.True(OverlayWindowInputClassifier.IsAlwaysCaptured(message));
    }

    [Theory]
    [InlineData(0x000F)]
    [InlineData(0x0119)]
    [InlineData(0x0240)]
    [InlineData(0x0312)]
    [InlineData(0x0319)]
    public void WindowClassifier_DoesNotCaptureUnrelatedWindowMessages(uint message)
    {
        Assert.False(OverlayWindowInputClassifier.IsAlwaysCaptured(message));
    }

    [Fact]
    public void WindowClassifier_TextOnlyCapture_DoesNotSwallowMouseOrKeyState()
    {
        Assert.True(OverlayWindowInputClassifier.ShouldCapture(
            0x0102,
            nint.Zero,
            OverlayInputDevices.Text));
        Assert.True(OverlayWindowInputClassifier.ShouldCapture(
            0x010F,
            nint.Zero,
            OverlayInputDevices.Text));
        Assert.False(OverlayWindowInputClassifier.ShouldCapture(
            0x0100,
            nint.Zero,
            OverlayInputDevices.Text));
        Assert.False(OverlayWindowInputClassifier.ShouldCapture(
            0x0201,
            nint.Zero,
            OverlayInputDevices.Text));
    }

    [Fact]
    public void WindowClassifier_DeviceMasks_AreIndependent()
    {
        Assert.True(OverlayWindowInputClassifier.ShouldCapture(
            0x0100,
            nint.Zero,
            OverlayInputDevices.Keyboard));
        Assert.False(OverlayWindowInputClassifier.ShouldCapture(
            0x0201,
            nint.Zero,
            OverlayInputDevices.Keyboard));
        Assert.True(OverlayWindowInputClassifier.ShouldCapture(
            0x0201,
            nint.Zero,
            OverlayInputDevices.Mouse));
        Assert.False(OverlayWindowInputClassifier.ShouldCapture(
            0x0100,
            nint.Zero,
            OverlayInputDevices.Mouse));
    }

    [Theory]
    [InlineData(0x0006)]
    [InlineData(0x0008)]
    [InlineData(0x001C)]
    [InlineData(0x001F)]
    [InlineData(0x0215)]
    public void BrokerHost_ForwardsWindowLifecycleMessages(uint message)
    {
        Assert.False(OverlayBrokerHost.ShouldSuppressWindowMessage(
            message,
            nint.Zero,
            OverlayInputDevices.Keyboard | OverlayInputDevices.Mouse));
    }

    [Theory]
    [InlineData(0x0100, OverlayInputDevices.Keyboard)]
    [InlineData(0x0201, OverlayInputDevices.Mouse)]
    public void BrokerHost_StillSuppressesCapturedKeyboardAndMouse(
        uint message,
        OverlayInputDevices devices)
    {
        Assert.True(OverlayBrokerHost.ShouldSuppressWindowMessage(
            message,
            nint.Zero,
            devices));
    }
}
