using System.ComponentModel;
using GBFR.ChatOverlay.Audio;
using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Tests;

public sealed class AudioEndpointCatalogTests
{
    [Fact]
    public void Resolver_UsesIndependentManualCaptureAndRenderEndpoints()
    {
        var catalog = new FakeAudioEndpointCatalog(
            capture:
            [
                new AudioEndpointInfo("capture-id", "Desk Microphone", true),
            ],
            render:
            [
                new AudioEndpointInfo("render-id", "USB Headset", true),
            ]);
        var logs = new List<string>();

        var input = AudioEndpointSelectionResolver.Resolve(
            "capture-id",
            AudioEndpointFlow.Capture,
            catalog,
            logs.Add);
        var output = AudioEndpointSelectionResolver.Resolve(
            "render-id",
            AudioEndpointFlow.Render,
            catalog,
            logs.Add);

        Assert.False(input.UseSystemDefault);
        Assert.Equal("capture-id", input.DeviceId);
        Assert.Equal("Desk Microphone", input.DisplayName);
        Assert.False(output.UseSystemDefault);
        Assert.Equal("render-id", output.DeviceId);
        Assert.Equal("USB Headset", output.DisplayName);
        Assert.Contains(logs, line => line.Contains("voice microphone", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("voice playback", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolver_StaleEndpointFallsBackToWindowsDefault()
    {
        var catalog = new FakeAudioEndpointCatalog([], []);
        var logs = new List<string>();

        var selection = AudioEndpointSelectionResolver.Resolve(
            "disconnected-device",
            AudioEndpointFlow.Capture,
            catalog,
            logs.Add);

        Assert.True(selection.UseSystemDefault);
        Assert.True(selection.FellBack);
        Assert.Null(selection.DeviceId);
        Assert.Contains(logs, line => line.Contains("not active", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void Resolver_ExplicitOrLegacyDefaultUsesWindowsSystemDefault(string? configuredValue)
    {
        var catalog = new FakeAudioEndpointCatalog([], []);

        var selection = AudioEndpointSelectionResolver.Resolve(
            configuredValue,
            AudioEndpointFlow.Capture,
            catalog,
            _ => { });

        Assert.True(selection.UseSystemDefault);
        Assert.False(selection.FellBack);
        Assert.Null(selection.DeviceId);
        Assert.Equal(AudioEndpointSelectionValues.SystemDefaultLabel, selection.DisplayName);
    }

    [Fact]
    public void Resolver_EnumerationFailureFallsBackToWindowsDefault()
    {
        var logs = new List<string>();

        var selection = AudioEndpointSelectionResolver.Resolve(
            "configured-device",
            AudioEndpointFlow.Render,
            new ThrowingAudioEndpointCatalog(),
            logs.Add);

        Assert.True(selection.UseSystemDefault);
        Assert.True(selection.FellBack);
        Assert.Null(selection.DeviceId);
        Assert.Contains(logs, line =>
            line.Contains("enumeration failed", StringComparison.Ordinal) &&
            line.Contains("falling back", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeConverters_ExposeLiveEndpointIdsButDisplayFriendlyNames()
    {
        var endpoints = new[]
        {
            new AudioEndpointInfo("mic-default", "Studio Mic", true),
            new AudioEndpointInfo("mic-other", "Webcam Mic", false),
        };
        var converter = new VoiceMicrophoneDeviceIdConverter(
            new FakeAudioEndpointCatalog(endpoints, []));

        var values = converter.GetStandardValues(context: null)!
            .Cast<string>()
            .ToArray();

        Assert.Equal(
            new[] { AudioEndpointSelectionValues.SystemDefault, "mic-default", "mic-other" },
            values);
        Assert.Equal(
            AudioEndpointSelectionValues.SystemDefaultLabel,
            converter.ConvertToInvariantString(string.Empty));
        Assert.Equal(
            AudioEndpointSelectionValues.SystemDefaultLabel,
            converter.ConvertToInvariantString(AudioEndpointSelectionValues.SystemDefault));
        Assert.Equal(
            "Studio Mic (Windows communications default)",
            converter.ConvertToInvariantString("mic-default"));
        Assert.Equal("Webcam Mic", converter.ConvertToInvariantString("mic-other"));
    }

    [Fact]
    public void Config_UsesDynamicConvertersAndDefaultsToFollowingWindows()
    {
        var config = new Config();
        var properties = TypeDescriptor.GetProperties(config);

        Assert.Equal(AudioEndpointSelectionValues.SystemDefault, config.VoiceMicrophoneDeviceId);
        Assert.Equal(AudioEndpointSelectionValues.SystemDefault, config.VoicePlaybackDeviceId);
        Assert.IsType<VoiceMicrophoneDeviceIdConverter>(
            properties[nameof(Config.VoiceMicrophoneDeviceId)]!.Converter);
        Assert.IsType<VoicePlaybackDeviceIdConverter>(
            properties[nameof(Config.VoicePlaybackDeviceId)]!.Converter);
    }

    [Fact]
    public void WindowsCatalog_ReturnsOnlyWellFormedUniqueEndpointIds()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var catalog = new WindowsAudioEndpointCatalog();
        foreach (var flow in new[] { AudioEndpointFlow.Capture, AudioEndpointFlow.Render })
        {
            var endpoints = catalog.GetActiveEndpoints(flow);
            Assert.All(endpoints, endpoint =>
            {
                Assert.False(string.IsNullOrWhiteSpace(endpoint.Id));
                Assert.False(string.IsNullOrWhiteSpace(endpoint.FriendlyName));
            });
            Assert.Equal(
                endpoints.Count,
                endpoints.Select(endpoint => endpoint.Id).Distinct(StringComparer.Ordinal).Count());
        }
    }

    private sealed class FakeAudioEndpointCatalog(
        IReadOnlyList<AudioEndpointInfo> capture,
        IReadOnlyList<AudioEndpointInfo> render) : IAudioEndpointCatalog
    {
        public IReadOnlyList<AudioEndpointInfo> GetActiveEndpoints(AudioEndpointFlow flow) =>
            flow == AudioEndpointFlow.Capture ? capture : render;
    }

    private sealed class ThrowingAudioEndpointCatalog : IAudioEndpointCatalog
    {
        public IReadOnlyList<AudioEndpointInfo> GetActiveEndpoints(AudioEndpointFlow flow) =>
            throw new InvalidOperationException("Synthetic Core Audio failure.");
    }
}
