using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Audio;

public enum AudioEndpointFlow
{
    Render = 0,
    Capture = 1,
}

public sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    bool IsDefaultCommunicationsDevice);

public interface IAudioEndpointCatalog
{
    IReadOnlyList<AudioEndpointInfo> GetActiveEndpoints(AudioEndpointFlow flow);
}

public readonly record struct ResolvedAudioEndpointSelection(
    bool UseSystemDefault,
    string? DeviceId,
    string DisplayName,
    bool FellBack)
{
    public static ResolvedAudioEndpointSelection SystemDefault(bool fellBack = false) =>
        new(
            UseSystemDefault: true,
            DeviceId: null,
            DisplayName: AudioEndpointIdTypeConverter.SystemDefaultLabel,
            FellBack: fellBack);
}

internal static class AudioEndpointSelectionResolver
{
    public static ResolvedAudioEndpointSelection Resolve(
        string? configuredDeviceId,
        AudioEndpointFlow flow,
        IAudioEndpointCatalog catalog,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(log);

        var role = flow == AudioEndpointFlow.Capture ? "microphone" : "playback";
        if (string.IsNullOrWhiteSpace(configuredDeviceId))
        {
            log($"Stage 3 voice {role}: following the Windows default communications device.");
            return ResolvedAudioEndpointSelection.SystemDefault();
        }

        IReadOnlyList<AudioEndpointInfo> endpoints;
        try
        {
            endpoints = catalog.GetActiveEndpoints(flow);
        }
        catch (Exception exception)
        {
            log(
                $"Stage 3 voice {role} enumeration failed with {exception.GetType().Name}: " +
                $"{exception.Message}; falling back to the Windows default communications device.");
            return ResolvedAudioEndpointSelection.SystemDefault(fellBack: true);
        }

        var endpoint = endpoints.FirstOrDefault(
            candidate => string.Equals(candidate.Id, configuredDeviceId, StringComparison.Ordinal));
        if (endpoint is null)
        {
            log(
                $"Stage 3 configured voice {role} endpoint is not active: {configuredDeviceId}; " +
                "falling back to the Windows default communications device.");
            return ResolvedAudioEndpointSelection.SystemDefault(fellBack: true);
        }

        log(
            $"Stage 3 voice {role}: selected \"{endpoint.FriendlyName}\" " +
            $"with manual Windows endpoint ID {endpoint.Id}.");
        return new ResolvedAudioEndpointSelection(
            UseSystemDefault: false,
            DeviceId: endpoint.Id,
            DisplayName: endpoint.FriendlyName,
            FellBack: false);
    }
}

/// <summary>
/// Supplies dynamic standard values to Reloaded-II's PropertyGrid while keeping the persisted
/// configuration value as the stable Windows endpoint ID.
/// </summary>
public abstract class AudioEndpointIdTypeConverter : StringConverter
{
    public const string SystemDefaultLabel = "Follow Windows default communications device (recommended)";

    private readonly AudioEndpointFlow _flow;
    private readonly IAudioEndpointCatalog _catalog;
    private readonly object _snapshotSync = new();
    private IReadOnlyList<AudioEndpointInfo> _snapshot = Array.Empty<AudioEndpointInfo>();

    protected AudioEndpointIdTypeConverter(AudioEndpointFlow flow)
        : this(flow, new WindowsAudioEndpointCatalog())
    {
    }

    internal AudioEndpointIdTypeConverter(AudioEndpointFlow flow, IAudioEndpointCatalog catalog)
    {
        _flow = flow;
        _catalog = catalog;
    }

    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => true;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
    {
        var endpoints = RefreshSnapshot();
        var values = new List<string>(endpoints.Count + 2) { string.Empty };
        values.AddRange(endpoints.Select(endpoint => endpoint.Id));

        var current = context?.PropertyDescriptor?.GetValue(context.Instance) as string;
        if (!string.IsNullOrWhiteSpace(current) &&
            !values.Contains(current, StringComparer.Ordinal))
        {
            // Preserve a disconnected endpoint in the dropdown long enough for the user to see
            // that the saved selection is unavailable and choose a replacement.
            values.Add(current);
        }

        return new StandardValuesCollection(values);
    }

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object value)
    {
        if (value is string text)
        {
            if (string.Equals(text, SystemDefaultLabel, StringComparison.Ordinal))
                return string.Empty;

            var endpoints = GetSnapshot(refreshWhenEmpty: true);
            foreach (var endpoint in endpoints)
            {
                if (string.Equals(text, endpoint.Id, StringComparison.Ordinal) ||
                    string.Equals(text, FormatEndpoint(endpoint, endpoints), StringComparison.Ordinal))
                {
                    return endpoint.Id;
                }
            }
        }

        return base.ConvertFrom(context, culture, value);
    }

    public override object? ConvertTo(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object? value,
        Type destinationType)
    {
        if (destinationType == typeof(string) && value is string endpointId)
        {
            if (string.IsNullOrWhiteSpace(endpointId))
                return SystemDefaultLabel;

            var endpoints = GetSnapshot(refreshWhenEmpty: true);
            var endpoint = endpoints.FirstOrDefault(
                candidate => string.Equals(candidate.Id, endpointId, StringComparison.Ordinal));
            return endpoint is null
                ? $"Unavailable saved device — {ShortenId(endpointId)}"
                : FormatEndpoint(endpoint, endpoints);
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }

    private IReadOnlyList<AudioEndpointInfo> RefreshSnapshot()
    {
        IReadOnlyList<AudioEndpointInfo> endpoints;
        try
        {
            endpoints = _catalog.GetActiveEndpoints(_flow);
        }
        catch
        {
            endpoints = Array.Empty<AudioEndpointInfo>();
        }

        lock (_snapshotSync)
            _snapshot = endpoints;
        return endpoints;
    }

    private IReadOnlyList<AudioEndpointInfo> GetSnapshot(bool refreshWhenEmpty)
    {
        lock (_snapshotSync)
        {
            if (_snapshot.Count != 0 || !refreshWhenEmpty)
                return _snapshot;
        }

        return RefreshSnapshot();
    }

    private static string FormatEndpoint(
        AudioEndpointInfo endpoint,
        IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        var duplicateName = endpoints.Count(candidate => string.Equals(
            candidate.FriendlyName,
            endpoint.FriendlyName,
            StringComparison.CurrentCultureIgnoreCase)) > 1;
        var defaultSuffix = endpoint.IsDefaultCommunicationsDevice
            ? " (Windows communications default)"
            : string.Empty;
        var disambiguator = duplicateName ? $" — {ShortenId(endpoint.Id)}" : string.Empty;
        return endpoint.FriendlyName + defaultSuffix + disambiguator;
    }

    private static string ShortenId(string id) =>
        id.Length <= 20 ? id : "…" + id[^19..];
}

public sealed class VoiceMicrophoneDeviceIdConverter : AudioEndpointIdTypeConverter
{
    public VoiceMicrophoneDeviceIdConverter()
        : base(AudioEndpointFlow.Capture)
    {
    }

    internal VoiceMicrophoneDeviceIdConverter(IAudioEndpointCatalog catalog)
        : base(AudioEndpointFlow.Capture, catalog)
    {
    }
}

public sealed class VoicePlaybackDeviceIdConverter : AudioEndpointIdTypeConverter
{
    public VoicePlaybackDeviceIdConverter()
        : base(AudioEndpointFlow.Render)
    {
    }

    internal VoicePlaybackDeviceIdConverter(IAudioEndpointCatalog catalog)
        : base(AudioEndpointFlow.Render, catalog)
    {
    }
}

/// <summary>
/// Zero-dependency Windows Core Audio endpoint enumeration. Party's Manual selection consumes the
/// IMMDevice endpoint ID returned here; friendly names are display-only and are never persisted.
/// </summary>
public sealed class WindowsAudioEndpointCatalog : IAudioEndpointCatalog
{
    private const uint DeviceStateActive = 0x00000001;
    private const int StgmRead = 0;
    private const ushort VtLpwstr = 31;
    private static readonly PropertyKey FriendlyNameProperty = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);

    public IReadOnlyList<AudioEndpointInfo> GetActiveEndpoints(AudioEndpointFlow flow)
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<AudioEndpointInfo>();

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            var nativeFlow = (EDataFlow)flow;
            var defaultId = TryGetDefaultCommunicationsId(enumerator, nativeFlow);
            ThrowIfFailed(enumerator.EnumAudioEndpoints(
                nativeFlow,
                DeviceStateActive,
                out collection));
            ThrowIfFailed(collection.GetCount(out var count));

            var endpoints = new List<AudioEndpointInfo>(checked((int)count));
            for (var index = 0u; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    ThrowIfFailed(collection.Item(index, out device));
                    ThrowIfFailed(device.GetId(out var id));
                    var friendlyName = ReadFriendlyName(device);
                    endpoints.Add(new AudioEndpointInfo(
                        id,
                        string.IsNullOrWhiteSpace(friendlyName) ? id : friendlyName,
                        string.Equals(id, defaultId, StringComparison.Ordinal)));
                }
                finally
                {
                    ReleaseComObject(device);
                }
            }

            return endpoints
                .OrderByDescending(endpoint => endpoint.IsDefaultCommunicationsDevice)
                .ThenBy(endpoint => endpoint.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(endpoint => endpoint.Id, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            ReleaseComObject(collection);
            ReleaseComObject(enumerator);
        }
    }

    private static string? TryGetDefaultCommunicationsId(
        IMMDeviceEnumerator enumerator,
        EDataFlow flow)
    {
        IMMDevice? device = null;
        try
        {
            var result = enumerator.GetDefaultAudioEndpoint(flow, ERole.Communications, out device);
            if (result < 0 || device is null)
                return null;
            return device.GetId(out var id) < 0 ? null : id;
        }
        finally
        {
            ReleaseComObject(device);
        }
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        var value = default(PropVariant);
        try
        {
            ThrowIfFailed(device.OpenPropertyStore(StgmRead, out store));
            var key = FriendlyNameProperty;
            ThrowIfFailed(store.GetValue(ref key, out value));
            return value.VariantType == VtLpwstr && value.PointerValue != nint.Zero
                ? Marshal.PtrToStringUni(value.PointerValue)
                : null;
        }
        finally
        {
            _ = PropVariantClear(ref value);
            ReleaseComObject(store);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            _ = Marshal.ReleaseComObject(value);
    }

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2,
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(8)]
        public nint PointerValue;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class MMDeviceEnumeratorComObject;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            EDataFlow dataFlow,
            uint stateMask,
            out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            EDataFlow dataFlow,
            ERole role,
            out IMMDevice device);

        [PreserveSig]
        int GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string id,
            out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(nint client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(
            uint index,
            out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, uint classContext, nint activationParameters, out nint instance);

        [PreserveSig]
        int OpenPropertyStore(
            int storageAccessMode,
            out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
