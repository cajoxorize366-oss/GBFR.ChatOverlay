using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Overlay;
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
    [InlineData(0x0100, OverlayInputDevices.Keyboard, true)]
    [InlineData(0x0100, OverlayInputDevices.Mouse, false)]
    [InlineData(0x0102, OverlayInputDevices.Text, true)]
    [InlineData(0x0102, OverlayInputDevices.Keyboard, false)]
    [InlineData(0x0201, OverlayInputDevices.Mouse, true)]
    [InlineData(0x0201, OverlayInputDevices.Keyboard, false)]
    [InlineData(0x00A1, OverlayInputDevices.Mouse, true)]
    [InlineData(0x000F, OverlayInputDevices.Keyboard | OverlayInputDevices.Mouse | OverlayInputDevices.Text, false)]
    [InlineData(0x0240, OverlayInputDevices.Keyboard | OverlayInputDevices.Mouse | OverlayInputDevices.Text, false)]
    public void WindowClassifier_ExceptionFallbackUsesTheRequestedDeviceClass(
        uint message,
        OverlayInputDevices devices,
        bool expected)
    {
        Assert.Equal(
            expected,
            OverlayWindowInputClassifier.ShouldCaptureWithoutRawInput(message, devices));
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

    [Fact]
    public void BrokerHost_DoesNotSuppressInputWithoutARequestedDevice()
    {
        Assert.False(OverlayBrokerHost.ShouldSuppressWindowMessage(
            0x0100,
            nint.Zero,
            OverlayInputDevices.None));
    }

    [Theory]
    [InlineData(0, 7, 3, 7)]
    [InlineData(0, 7, 1, 5)]
    [InlineData(0, 7, 2, 2)]
    [InlineData(0, 7, 0, 0)]
    [InlineData(1, 7, 3, 3)]
    [InlineData(4, 7, 3, 6)]
    [InlineData(7, 0, 3, 7)]
    public void BrokerHost_TwoPhaseReleaseTracksNativeEffectiveCapture(
        int requested,
        int previous,
        int native,
        int expected)
    {
        Assert.Equal(
            (OverlayInputDevices)expected,
            OverlayBrokerHost.ResolveEffectiveInputDevices(
                (OverlayInputDevices)requested,
                (OverlayInputDevices)previous,
                (OverlayInputDevices)native));
    }

    [Theory]
    [InlineData(0, OverlayInputDevices.Mouse, true)]
    [InlineData(0, OverlayInputDevices.Keyboard, false)]
    [InlineData(1, OverlayInputDevices.Keyboard, true)]
    [InlineData(1, OverlayInputDevices.Mouse, false)]
    [InlineData(2, OverlayInputDevices.Keyboard | OverlayInputDevices.Mouse, false)]
    [InlineData(3, OverlayInputDevices.Keyboard | OverlayInputDevices.Mouse | OverlayInputDevices.Text, false)]
    public void WindowClassifier_RawInputLeavesControllersAndUnknownDevicesAlone(
        int rawInputType,
        OverlayInputDevices devices,
        bool expected)
    {
        Assert.Equal(expected, OverlayWindowInputClassifier.ShouldCaptureRawInputType(rawInputType, devices));
    }

    [Theory]
    [InlineData(false, OverlayInputDevices.None, false)]
    [InlineData(true, OverlayInputDevices.None, true)]
    [InlineData(false, OverlayInputDevices.Mouse, true)]
    public void BrokerHost_RoutesWndProcToImGuiOnlyForRenderingOrRequestedInput(
        bool hasRenderableClients,
        OverlayInputDevices requestedDevices,
        bool expected)
    {
        Assert.Equal(
            expected,
            OverlayBrokerHost.ShouldRouteWindowMessageToImGui(
                hasRenderableClients,
                requestedDevices));
    }

    [Theory]
    [InlineData(0x00FF, 0, true)]
    [InlineData(0x00FF, 1, false)]
    [InlineData(0x0200, 0, false)]
    public void BrokerHost_ForegroundRawInputUsesDefaultCleanup(
        uint message,
        long wParam,
        bool expected)
    {
        Assert.Equal(
            expected,
            OverlayBrokerHost.RequiresDefaultRawInputCleanup(message, new nint(wParam)));
    }

    [Fact]
    public void ImGuiInputResetGate_CoalescesRequestsUntilPresentConsumesThem()
    {
        _ = ImGuiInputResetGate.Consume();

        ImGuiInputResetGate.Request();
        ImGuiInputResetGate.Request();

        Assert.True(ImGuiInputResetGate.Consume());
        Assert.False(ImGuiInputResetGate.Consume());
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void PresentBackend_DetectsOnlyTheFirstFrontendFrameAfterSleep(
        bool renderedLastPresent,
        bool shouldRenderFrontend,
        bool expected)
    {
        Assert.Equal(
            expected,
            RtssSafeImguiHookDx11.IsFrontendWakeFrame(
                renderedLastPresent,
                shouldRenderFrontend));
    }
}
