namespace GBFR.ChatOverlay.Stt;

public sealed record AudioCaptureDeviceDescriptor(string Id, string Name, bool IsDefault);

public sealed record AudioCaptureDeviceSelection(
    AudioCaptureDeviceDescriptor Device,
    bool UsedFallback,
    string? Warning);

public static class AudioCaptureDeviceSelector
{
    public const string DefaultSelector = "default";

    public static AudioCaptureDeviceSelection Select(
        string? selector,
        IReadOnlyList<AudioCaptureDeviceDescriptor> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
            throw new InvalidOperationException("Windows reported no active microphone endpoints.");

        var defaultDevice = devices.FirstOrDefault(device => device.IsDefault) ?? devices[0];
        var value = selector?.Trim();
        if (string.IsNullOrEmpty(value) ||
            value.Equals(DefaultSelector, StringComparison.OrdinalIgnoreCase))
        {
            var hasWindowsDefault = devices.Any(device => device.IsDefault);
            return new AudioCaptureDeviceSelection(
                defaultDevice,
                UsedFallback: !hasWindowsDefault,
                Warning: hasWindowsDefault
                    ? null
                    : "Windows did not expose a default microphone; using the first active endpoint.");
        }

        var idMatch = devices.FirstOrDefault(
            device => device.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (idMatch is not null)
            return new AudioCaptureDeviceSelection(idMatch, UsedFallback: false, Warning: null);

        var exactNameMatches = devices
            .Where(device => device.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactNameMatches.Length == 1)
            return new AudioCaptureDeviceSelection(exactNameMatches[0], UsedFallback: false, Warning: null);
        if (exactNameMatches.Length > 1)
        {
            return FallBack(
                defaultDevice,
                $"Multiple microphones are named '{value}'. Use the endpoint ID from microphones.json.");
        }

        var partialNameMatches = devices
            .Where(device => device.Name.Contains(value, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (partialNameMatches.Length == 1)
            return new AudioCaptureDeviceSelection(partialNameMatches[0], UsedFallback: false, Warning: null);
        if (partialNameMatches.Length > 1)
        {
            return FallBack(
                defaultDevice,
                $"Microphone selector '{value}' matched multiple endpoints. Use an exact name or endpoint ID.");
        }

        return FallBack(
            defaultDevice,
            $"Microphone selector '{value}' was not found. Falling back to the Windows default endpoint.");
    }

    private static AudioCaptureDeviceSelection FallBack(
        AudioCaptureDeviceDescriptor defaultDevice,
        string warning) =>
        new(defaultDevice, UsedFallback: true, Warning: warning);
}
