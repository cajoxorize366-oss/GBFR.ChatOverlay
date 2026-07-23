using GBFR.ChatOverlay.Stt;
using NAudio.CoreAudioApi;

namespace GBFR.ChatOverlay.SttWorker;

internal sealed class AudioDeviceLease : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private int _disposed;

    public AudioDeviceLease(
        MMDeviceEnumerator enumerator,
        MMDevice device,
        AudioCaptureDeviceSelection selection)
    {
        _enumerator = enumerator;
        Device = device;
        Selection = selection;
    }

    public MMDevice Device { get; }
    public AudioCaptureDeviceSelection Selection { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Device.Dispose();
        _enumerator.Dispose();
    }
}

internal static class AudioDeviceCatalog
{
    public static IReadOnlyList<AudioCaptureDeviceDescriptor> GetSnapshot()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = TryGetDefaultId(enumerator);
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .ToArray();
        try
        {
            return devices
                .Select(device => Describe(device, defaultId))
                .ToArray();
        }
        finally
        {
            foreach (var device in devices)
                device.Dispose();
        }
    }

    public static AudioDeviceLease Resolve(string? selector)
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = Array.Empty<MMDevice>();
        try
        {
            var defaultId = TryGetDefaultId(enumerator);
            devices = enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .ToArray();
            var descriptors = devices
                .Select(device => Describe(device, defaultId))
                .ToArray();
            var selection = AudioCaptureDeviceSelector.Select(selector, descriptors);
            var selectedIndex = Array.FindIndex(
                descriptors,
                descriptor => descriptor.Id.Equals(selection.Device.Id, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex < 0)
                throw new InvalidOperationException("The selected microphone disappeared during enumeration.");

            var selectedDevice = devices[selectedIndex];
            for (var index = 0; index < devices.Length; index++)
            {
                if (index != selectedIndex)
                    devices[index].Dispose();
            }

            return new AudioDeviceLease(enumerator, selectedDevice, selection);
        }
        catch
        {
            foreach (var device in devices)
                device.Dispose();
            enumerator.Dispose();
            throw;
        }
    }

    private static AudioCaptureDeviceDescriptor Describe(MMDevice device, string? defaultId) =>
        new(
            device.ID,
            string.IsNullOrWhiteSpace(device.FriendlyName) ? device.ID : device.FriendlyName,
            device.ID.Equals(defaultId, StringComparison.OrdinalIgnoreCase));

    private static string? TryGetDefaultId(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }
}
