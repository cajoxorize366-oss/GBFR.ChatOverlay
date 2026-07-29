using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using HandyControl.Controls;

namespace GBFR.ChatOverlay.ConfiguratorUI;

public sealed record AudioEndpointChoice(string Id, string DisplayName);

public abstract class AudioEndpointPropertyEditor : PropertyEditorBase
{
    public const string SystemDefaultValue = "default";
    public const string SystemDefaultLabel = "系统默认 / Default (Windows system default)";

    private readonly AudioEndpointFlow _flow;

    protected AudioEndpointPropertyEditor(AudioEndpointFlow flow)
    {
        _flow = flow;
    }

    public override FrameworkElement CreateElement(PropertyItem propertyItem)
    {
        ArgumentNullException.ThrowIfNull(propertyItem);
        var propertyDescriptor = TypeDescriptor.GetProperties(propertyItem.Value)
            [propertyItem.PropertyName];
        var currentId = propertyDescriptor?.GetValue(propertyItem.Value) as string;
        if (string.IsNullOrWhiteSpace(currentId) ||
            string.Equals(currentId, SystemDefaultValue, StringComparison.OrdinalIgnoreCase))
        {
            currentId = SystemDefaultValue;
            if (propertyDescriptor?.IsReadOnly == false)
                propertyDescriptor.SetValue(propertyItem.Value, currentId);
        }

        return new System.Windows.Controls.ComboBox
        {
            IsEnabled = !propertyItem.IsReadOnly,
            ItemsSource = AudioEndpointChoiceCatalog.GetChoices(_flow, currentId),
            DisplayMemberPath = nameof(AudioEndpointChoice.DisplayName),
            SelectedValuePath = nameof(AudioEndpointChoice.Id),
            IsTextSearchEnabled = true,
            IsTextSearchCaseSensitive = false,
            MaxDropDownHeight = 360,
            MinWidth = 280,
        };
    }

    public override DependencyProperty GetDependencyProperty() => Selector.SelectedValueProperty;
}

public sealed class VoiceMicrophonePropertyEditor : AudioEndpointPropertyEditor
{
    public VoiceMicrophonePropertyEditor()
        : base(AudioEndpointFlow.Capture)
    {
    }
}

public sealed class VoicePlaybackPropertyEditor : AudioEndpointPropertyEditor
{
    public VoicePlaybackPropertyEditor()
        : base(AudioEndpointFlow.Render)
    {
    }
}

internal static class AudioEndpointChoiceCatalog
{
    public static IReadOnlyList<AudioEndpointChoice> GetChoices(
        AudioEndpointFlow flow,
        string? currentId)
    {
        IReadOnlyList<AudioEndpointInfo> endpoints;
        var defaultLabel = AudioEndpointPropertyEditor.SystemDefaultLabel;
        try
        {
            endpoints = WindowsAudioEndpointCatalog.GetActiveEndpoints(flow);
        }
        catch (Exception exception)
        {
            endpoints = Array.Empty<AudioEndpointInfo>();
            defaultLabel += $" — 设备扫描失败 / device scan failed ({exception.GetType().Name})";
        }

        var choices = new List<AudioEndpointChoice>(endpoints.Count + 2)
        {
            new(AudioEndpointPropertyEditor.SystemDefaultValue, defaultLabel),
        };
        foreach (var endpoint in endpoints)
        {
            var duplicateName = endpoints.Count(candidate => string.Equals(
                candidate.FriendlyName,
                endpoint.FriendlyName,
                StringComparison.CurrentCultureIgnoreCase)) > 1;
            var defaultSuffix = endpoint.IsDefaultCommunicationsDevice
                ? " (Windows 通信默认 / communications default)"
                : string.Empty;
            var disambiguator = duplicateName ? $" — {ShortenId(endpoint.Id)}" : string.Empty;
            choices.Add(new AudioEndpointChoice(
                endpoint.Id,
                endpoint.FriendlyName + defaultSuffix + disambiguator));
        }

        if (!string.IsNullOrWhiteSpace(currentId) &&
            !string.Equals(
                currentId,
                AudioEndpointPropertyEditor.SystemDefaultValue,
                StringComparison.OrdinalIgnoreCase) &&
            choices.All(choice => !string.Equals(choice.Id, currentId, StringComparison.Ordinal)))
        {
            choices.Add(new AudioEndpointChoice(
                currentId,
                $"已保存设备不可用 / Unavailable saved device — {ShortenId(currentId)}"));
        }

        return choices;
    }

    private static string ShortenId(string id) =>
        id.Length <= 20 ? id : "…" + id[^19..];
}

public enum AudioEndpointFlow
{
    Render = 0,
    Capture = 1,
}

internal sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    bool IsDefaultCommunicationsDevice);

internal static class WindowsAudioEndpointCatalog
{
    private const uint DeviceStateActive = 0x00000001;
    private const int StgmRead = 0;
    private const ushort VtLpwstr = 31;
    private static readonly PropertyKey FriendlyNameProperty = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);

    public static IReadOnlyList<AudioEndpointInfo> GetActiveEndpoints(AudioEndpointFlow flow)
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
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

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
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, uint classContext, nint activationParameters, out nint instance);

        [PreserveSig]
        int OpenPropertyStore(int storageAccessMode, out IPropertyStore properties);

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
