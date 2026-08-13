using System.Runtime.InteropServices;
using GBFR.ChatOverlay.Input;
using GBFR.OverlayHub.Contracts;

namespace GBFR.ChatOverlay.Native;

[Flags]
internal enum DirectInputBrokerPolicy : uint
{
    None = 0,
    CaptureKeyboard = 1u << 0,
    CaptureMouse = 1u << 1,
    SuppressActivation = 1u << 2,
    SuppressSettings = 1u << 3,
    SuppressPushToTalk = 1u << 4,
    SuppressQuickActions = 1u << 5,
}

[Flags]
internal enum DirectInputBrokerReadiness : uint
{
    None = 0,
    GameImport = 1u << 0,
    Factory = 1u << 1,
    Keyboard = 1u << 2,
    Mouse = 1u << 3,
    Controller = 1u << 4,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly record struct DirectInputHotkeyBinding(
    byte ScanCode,
    KeyboardModifiers Modifiers,
    byte PolicyFlag,
    byte Reserved = 0);

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DirectInputBrokerSnapshot
{
    internal const uint ExpectedAbiVersion = 2;
    internal const uint ExpectedStructSize = 64;

    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong Sequence;
    internal ulong KeyboardWord0;
    internal ulong KeyboardWord1;
    internal ulong KeyboardWord2;
    internal ulong KeyboardWord3;
    internal ControllerButtons ControllerButtons;
    internal ushort Reserved;
    internal DirectInputBrokerReadiness Readiness;
    internal DirectInputBrokerPolicy Policy;
    internal uint Active;

    internal readonly bool HasExpectedLayout =>
        AbiVersion == ExpectedAbiVersion &&
        StructSize == ExpectedStructSize;

    internal readonly bool IsScanCodePressed(byte scanCode)
    {
        var word = scanCode / 64;
        var mask = 1UL << (scanCode % 64);
        return word switch
        {
            0 => (KeyboardWord0 & mask) != 0,
            1 => (KeyboardWord1 & mask) != 0,
            2 => (KeyboardWord2 & mask) != 0,
            _ => (KeyboardWord3 & mask) != 0,
        };
    }

    internal readonly bool HasAnyKeyboardKey =>
        (KeyboardWord0 | KeyboardWord1 | KeyboardWord2 | KeyboardWord3) != 0;
}

internal interface IDirectInputBrokerBackend
{
    bool Install();

    bool SetActive(bool active);

    bool SetPolicy(DirectInputBrokerPolicy policy);

    bool SetHotkeyBindings(DirectInputHotkeyBinding[] bindings);

    bool TryGetSnapshot(out DirectInputBrokerSnapshot snapshot);
}

/// <summary>
/// Managed boundary for the process-lifetime DirectInput broker. The native side patches only the
/// game executable's import slot, then gates keyboard/mouse COM methods without modifying the
/// dinput8/ReShade export entry. No managed callback is ever invoked from a native input hook.
/// </summary>
internal sealed class DirectInputBrokerBridge : IDirectInputBrokerBackend
{
    internal static DirectInputBrokerBridge Instance { get; } = new();

    private DirectInputBrokerBridge()
    {
    }

    public bool Install() => GBFRChatOverlay_InstallDirectInputBroker() != 0;

    public bool SetActive(bool active) =>
        GBFRChatOverlay_SetDirectInputBrokerActive(active ? 1 : 0) != 0;

    public bool SetPolicy(DirectInputBrokerPolicy policy) =>
        GBFRChatOverlay_SetDirectInputPolicy(policy) != 0;

    public bool SetHotkeyBindings(DirectInputHotkeyBinding[] bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return GBFRChatOverlay_SetDirectInputHotkeyBindings(
                   bindings,
                   checked((uint)bindings.Length)) != 0;
    }

    public bool TryGetSnapshot(out DirectInputBrokerSnapshot snapshot)
    {
        snapshot = default;
        return GBFRChatOverlay_GetDirectInputSnapshot(
                   ref snapshot,
                   DirectInputBrokerSnapshot.ExpectedStructSize) != 0;
    }

    internal OverlayInputDevices GetEffectiveInputDevices()
    {
        var effective = (DirectInputBrokerPolicy)
            GBFRChatOverlay_GetDirectInputEffectiveCapture();
        var devices = OverlayInputDevices.None;
        if ((effective & DirectInputBrokerPolicy.CaptureKeyboard) != 0)
            devices |= OverlayInputDevices.Keyboard;
        if ((effective & DirectInputBrokerPolicy.CaptureMouse) != 0)
            devices |= OverlayInputDevices.Mouse;
        return devices;
    }

    [DllImport(
        DxgiPresentBridge.LibraryName,
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern int GBFRChatOverlay_InstallDirectInputBroker();

    [DllImport(
        DxgiPresentBridge.LibraryName,
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern int GBFRChatOverlay_SetDirectInputBrokerActive(int requested);

    [DllImport(
        DxgiPresentBridge.LibraryName,
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern int GBFRChatOverlay_SetDirectInputPolicy(
        DirectInputBrokerPolicy policyFlags);

    [DllImport(
        DxgiPresentBridge.LibraryName,
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern int GBFRChatOverlay_SetDirectInputHotkeyBindings(
        [In] DirectInputHotkeyBinding[] bindings,
        uint bindingCount);

    [DllImport(
        DxgiPresentBridge.LibraryName,
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern int GBFRChatOverlay_GetDirectInputSnapshot(
        ref DirectInputBrokerSnapshot snapshot,
        uint snapshotSize);

    [DllImport(
        DxgiPresentBridge.LibraryName,
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern uint GBFRChatOverlay_GetDirectInputEffectiveCapture();

}
