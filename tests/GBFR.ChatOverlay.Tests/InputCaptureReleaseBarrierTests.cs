using GBFR.ChatOverlay.Input;

namespace GBFR.ChatOverlay.Tests;

public sealed class InputCaptureReleaseBarrierTests
{
    [Fact]
    public void Request_AddsAllDevicesImmediately()
    {
        var barrier = new InputCaptureReleaseBarrier();

        var transition = barrier.SetRequested(InputCaptureDevices.All);

        Assert.Equal(InputCaptureDevices.None, transition.Previous);
        Assert.Equal(InputCaptureDevices.All, transition.Current);
        Assert.Equal(InputCaptureDevices.All, barrier.Requested);
        Assert.Equal(InputCaptureDevices.All, barrier.Effective);
    }

    [Fact]
    public void Release_WaitsForTwoConsecutiveNeutralFrames()
    {
        var barrier = new InputCaptureReleaseBarrier();
        barrier.SetRequested(InputCaptureDevices.All);
        barrier.SetRequested(InputCaptureDevices.None);

        Assert.Equal(InputCaptureDevices.All, barrier.Effective);
        barrier.Tick(keyboardNeutral: false, mouseNeutral: false);
        barrier.Tick(keyboardNeutral: true, mouseNeutral: true);
        Assert.Equal(InputCaptureDevices.All, barrier.Effective);

        barrier.Tick(keyboardNeutral: true, mouseNeutral: true);

        Assert.Equal(InputCaptureDevices.None, barrier.Effective);
    }

    [Fact]
    public void Release_DropsOnlyTheDeviceGroupThatBecameNeutral()
    {
        var barrier = new InputCaptureReleaseBarrier();
        var chatDevices = InputCaptureDevices.Keyboard | InputCaptureDevices.Text;
        barrier.SetRequested(InputCaptureDevices.All);

        var transition = barrier.SetRequested(chatDevices);

        Assert.Equal(InputCaptureDevices.All, transition.Current);
        barrier.Tick(keyboardNeutral: false, mouseNeutral: true);
        Assert.Equal(InputCaptureDevices.All, barrier.Effective);
        barrier.Tick(keyboardNeutral: false, mouseNeutral: true);
        Assert.Equal(chatDevices, barrier.Effective);
    }

    [Fact]
    public void Request_DuringPendingReleaseCancelsTheDrain()
    {
        var barrier = new InputCaptureReleaseBarrier();
        barrier.SetRequested(InputCaptureDevices.All);
        barrier.SetRequested(InputCaptureDevices.None);
        barrier.Tick(keyboardNeutral: true, mouseNeutral: true);

        barrier.SetRequested(InputCaptureDevices.All);
        barrier.Tick(keyboardNeutral: true, mouseNeutral: true);
        barrier.Tick(keyboardNeutral: true, mouseNeutral: true);

        Assert.Equal(InputCaptureDevices.All, barrier.Requested);
        Assert.Equal(InputCaptureDevices.All, barrier.Effective);
    }

    [Fact]
    public void RepeatingTheSameRequest_DoesNotResetNeutralProgress()
    {
        var barrier = new InputCaptureReleaseBarrier();
        barrier.SetRequested(InputCaptureDevices.All);
        barrier.SetRequested(InputCaptureDevices.None);
        barrier.Tick(keyboardNeutral: true, mouseNeutral: true);

        barrier.SetRequested(InputCaptureDevices.None);
        barrier.Tick(keyboardNeutral: true, mouseNeutral: true);

        Assert.Equal(InputCaptureDevices.None, barrier.Effective);
    }

    [Fact]
    public void TextCapture_AlsoCapturesTheKeyboardDevice()
    {
        var barrier = new InputCaptureReleaseBarrier();

        barrier.SetRequested(InputCaptureDevices.Text);

        Assert.Equal(
            InputCaptureDevices.Keyboard | InputCaptureDevices.Text,
            barrier.Effective);
    }

    [Fact]
    public void ForceRelease_DoesNotWaitForNeutralInput()
    {
        var barrier = new InputCaptureReleaseBarrier();
        barrier.SetRequested(InputCaptureDevices.All);

        var transition = barrier.ForceRelease();

        Assert.Equal(InputCaptureDevices.All, transition.Previous);
        Assert.Equal(InputCaptureDevices.None, transition.Current);
        Assert.Equal(InputCaptureDevices.None, barrier.Requested);
        Assert.Equal(InputCaptureDevices.None, barrier.Effective);
    }
}
