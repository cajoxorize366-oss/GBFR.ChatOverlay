using GBFR.ChatOverlay.Stt;

namespace GBFR.ChatOverlay.Tests;

public sealed class AudioCaptureDeviceSelectorTests
{
    private static readonly AudioCaptureDeviceDescriptor[] Devices =
    {
        new("usb-headset-id", "USB Headset Microphone", IsDefault: false),
        new("desktop-mic-id", "Desktop Microphone", IsDefault: true),
        new("webcam-id", "Webcam Microphone", IsDefault: false),
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    public void DefaultSelectorUsesWindowsDefault(string? selector)
    {
        var result = AudioCaptureDeviceSelector.Select(selector, Devices);

        Assert.Equal("desktop-mic-id", result.Device.Id);
        Assert.False(result.UsedFallback);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void ExactEndpointIdWins()
    {
        var result = AudioCaptureDeviceSelector.Select("usb-headset-id", Devices);

        Assert.Equal("USB Headset Microphone", result.Device.Name);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void ExactFriendlyNameIsCaseInsensitive()
    {
        var result = AudioCaptureDeviceSelector.Select("webcam microphone", Devices);

        Assert.Equal("webcam-id", result.Device.Id);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void UniquePartialFriendlyNameIsAccepted()
    {
        var result = AudioCaptureDeviceSelector.Select("Headset", Devices);

        Assert.Equal("usb-headset-id", result.Device.Id);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void MissingSelectorFallsBackWithWarning()
    {
        var result = AudioCaptureDeviceSelector.Select("not-connected", Devices);

        Assert.Equal("desktop-mic-id", result.Device.Id);
        Assert.True(result.UsedFallback);
        Assert.Contains("not found", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmbiguousFriendlyNameFallsBackWithWarning()
    {
        var devices = new[]
        {
            new AudioCaptureDeviceDescriptor("default-id", "Built-in Microphone", IsDefault: true),
            new AudioCaptureDeviceDescriptor("usb-a", "USB Microphone", IsDefault: false),
            new AudioCaptureDeviceDescriptor("usb-b", "USB Microphone", IsDefault: false),
        };

        var result = AudioCaptureDeviceSelector.Select("USB Microphone", devices);

        Assert.Equal("default-id", result.Device.Id);
        Assert.True(result.UsedFallback);
        Assert.Contains("Multiple", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void NoActiveDevicesFailsClearly()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AudioCaptureDeviceSelector.Select("default", Array.Empty<AudioCaptureDeviceDescriptor>()));

        Assert.Contains("no active microphone", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
