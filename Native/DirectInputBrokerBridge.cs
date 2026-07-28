using System.Runtime.InteropServices;

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
}

[Flags]
internal enum DirectInputBrokerKeys : uint
{
    None = 0,
    Activation = 1u << 0,
    Settings = 1u << 1,
    PushToTalk = 1u << 2,
}

[Flags]
internal enum DirectInputBrokerReadiness : uint
{
    None = 0,
    GameImport = 1u << 0,
    Factory = 1u << 1,
    Keyboard = 1u << 2,
    Mouse = 1u << 3,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DirectInputBrokerSnapshot
{
    internal const uint ExpectedAbiVersion = 1;
    internal const uint ExpectedStructSize = 32;

    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong Sequence;
    internal DirectInputBrokerKeys Keys;
    internal DirectInputBrokerReadiness Readiness;
    internal DirectInputBrokerPolicy Policy;
    internal uint Active;

    internal readonly bool HasExpectedLayout =>
        AbiVersion == ExpectedAbiVersion &&
        StructSize == ExpectedStructSize;
}

internal interface IDirectInputBrokerBackend
{
    bool Install();

    bool SetActive(bool active);

    bool SetPolicy(DirectInputBrokerPolicy policy);

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

    public bool TryGetSnapshot(out DirectInputBrokerSnapshot snapshot)
    {
        snapshot = default;
        return GBFRChatOverlay_GetDirectInputSnapshot(
                   ref snapshot,
                   DirectInputBrokerSnapshot.ExpectedStructSize) != 0;
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
    private static extern int GBFRChatOverlay_GetDirectInputSnapshot(
        ref DirectInputBrokerSnapshot snapshot,
        uint snapshotSize);

}
