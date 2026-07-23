using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Native;

internal interface IPartyChatControlApi
{
    uint GetLocalDevice(nint manager, out nint localDevice);

    uint GetLocalChatControlCount(nint localDevice, out uint chatControlCount);

    uint CreateChatControl(
        nint localDevice,
        nint localUser,
        nint asyncIdentifier,
        out nint localChatControl);

    uint DestroyChatControl(nint localDevice, nint localChatControl, nint asyncIdentifier);

    uint SetAudioInputMuted(nint localChatControl, bool muted);

    uint GetAudioInputMuted(nint localChatControl, out bool muted);

    uint SetSystemDefaultAudioInput(nint localChatControl, nint asyncIdentifier);

    uint SetSystemDefaultAudioOutput(nint localChatControl, nint asyncIdentifier);

    uint ConnectChatControl(nint network, nint localChatControl, nint asyncIdentifier);

    uint DisconnectChatControl(nint network, nint localChatControl, nint asyncIdentifier);
}

/// <summary>
/// Exact flat-C bindings from Party_c.h in Microsoft.PlayFab.PlayFabParty.Cpp.Windows 1.10.12.
/// The caller supplies the already loaded, hash-verified game module. This class never loads or
/// initializes another Party runtime and deliberately exposes no chat-permission API.
/// </summary>
internal sealed class PartyNativeApi : IPartyChatControlApi
{
    private const uint SystemDefaultAudioDevice = 1;

    private readonly PartyGetLocalDeviceDelegate _getLocalDevice;
    private readonly PartyDeviceGetChatControlsDelegate _deviceGetChatControls;
    private readonly PartyDeviceCreateChatControlDelegate _deviceCreateChatControl;
    private readonly PartyDeviceDestroyChatControlDelegate _deviceDestroyChatControl;
    private readonly PartyChatControlSetAudioInputMutedDelegate _chatControlSetAudioInputMuted;
    private readonly PartyChatControlGetAudioInputMutedDelegate _chatControlGetAudioInputMuted;
    private readonly PartyChatControlSetAudioInputDelegate _chatControlSetAudioInput;
    private readonly PartyChatControlSetAudioOutputDelegate _chatControlSetAudioOutput;
    private readonly PartyNetworkConnectChatControlDelegate _networkConnectChatControl;
    private readonly PartyNetworkDisconnectChatControlDelegate _networkDisconnectChatControl;

    public PartyNativeApi(nint verifiedPartyModule)
    {
        if (verifiedPartyModule == nint.Zero)
            throw new ArgumentException("The Party module handle is null.", nameof(verifiedPartyModule));
        if (nint.Size != 8)
            throw new PlatformNotSupportedException("The Relink Party bindings require a 64-bit process.");

        _getLocalDevice = Bind<PartyGetLocalDeviceDelegate>(verifiedPartyModule, "PartyGetLocalDevice");
        _deviceGetChatControls = Bind<PartyDeviceGetChatControlsDelegate>(
            verifiedPartyModule,
            "PartyDeviceGetChatControls");
        _deviceCreateChatControl = Bind<PartyDeviceCreateChatControlDelegate>(
            verifiedPartyModule,
            "PartyDeviceCreateChatControl");
        _deviceDestroyChatControl = Bind<PartyDeviceDestroyChatControlDelegate>(
            verifiedPartyModule,
            "PartyDeviceDestroyChatControl");
        _chatControlSetAudioInputMuted = Bind<PartyChatControlSetAudioInputMutedDelegate>(
            verifiedPartyModule,
            "PartyChatControlSetAudioInputMuted");
        _chatControlGetAudioInputMuted = Bind<PartyChatControlGetAudioInputMutedDelegate>(
            verifiedPartyModule,
            "PartyChatControlGetAudioInputMuted");
        _chatControlSetAudioInput = Bind<PartyChatControlSetAudioInputDelegate>(
            verifiedPartyModule,
            "PartyChatControlSetAudioInput");
        _chatControlSetAudioOutput = Bind<PartyChatControlSetAudioOutputDelegate>(
            verifiedPartyModule,
            "PartyChatControlSetAudioOutput");
        _networkConnectChatControl = Bind<PartyNetworkConnectChatControlDelegate>(
            verifiedPartyModule,
            "PartyNetworkConnectChatControl");
        _networkDisconnectChatControl = Bind<PartyNetworkDisconnectChatControlDelegate>(
            verifiedPartyModule,
            "PartyNetworkDisconnectChatControl");
    }

    public uint GetLocalDevice(nint manager, out nint localDevice) =>
        _getLocalDevice(manager, out localDevice);

    public uint GetLocalChatControlCount(nint localDevice, out uint chatControlCount)
    {
        return _deviceGetChatControls(localDevice, out chatControlCount, out _);
    }

    public uint CreateChatControl(
        nint localDevice,
        nint localUser,
        nint asyncIdentifier,
        out nint localChatControl)
    {
        return _deviceCreateChatControl(
            localDevice,
            localUser,
            languageCode: nint.Zero,
            asyncIdentifier,
            out localChatControl);
    }

    public uint DestroyChatControl(nint localDevice, nint localChatControl, nint asyncIdentifier) =>
        _deviceDestroyChatControl(localDevice, localChatControl, asyncIdentifier);

    public uint SetAudioInputMuted(nint localChatControl, bool muted) =>
        _chatControlSetAudioInputMuted(localChatControl, muted ? (byte)1 : (byte)0);

    public uint GetAudioInputMuted(nint localChatControl, out bool muted)
    {
        var result = _chatControlGetAudioInputMuted(localChatControl, out var nativeMuted);
        muted = nativeMuted != 0;
        return result;
    }

    public uint SetSystemDefaultAudioInput(nint localChatControl, nint asyncIdentifier) =>
        _chatControlSetAudioInput(
            localChatControl,
            SystemDefaultAudioDevice,
            audioDeviceSelectionContext: nint.Zero,
            asyncIdentifier);

    public uint SetSystemDefaultAudioOutput(nint localChatControl, nint asyncIdentifier) =>
        _chatControlSetAudioOutput(
            localChatControl,
            SystemDefaultAudioDevice,
            audioDeviceSelectionContext: nint.Zero,
            asyncIdentifier);

    public uint ConnectChatControl(nint network, nint localChatControl, nint asyncIdentifier) =>
        _networkConnectChatControl(network, localChatControl, asyncIdentifier);

    public uint DisconnectChatControl(nint network, nint localChatControl, nint asyncIdentifier) =>
        _networkDisconnectChatControl(network, localChatControl, asyncIdentifier);

    private static T Bind<T>(nint module, string exportName)
        where T : Delegate
    {
        var address = NativeLibrary.GetExport(module, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyGetLocalDeviceDelegate(nint manager, out nint localDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyDeviceGetChatControlsDelegate(
        nint device,
        out uint chatControlCount,
        out nint chatControls);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyDeviceCreateChatControlDelegate(
        nint device,
        nint localUser,
        nint languageCode,
        nint asyncIdentifier,
        out nint localChatControl);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyDeviceDestroyChatControlDelegate(
        nint device,
        nint localChatControl,
        nint asyncIdentifier);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlSetAudioInputMutedDelegate(nint localChatControl, byte muted);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlGetAudioInputMutedDelegate(
        nint localChatControl,
        out byte muted);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlSetAudioInputDelegate(
        nint localChatControl,
        uint audioDeviceSelectionType,
        nint audioDeviceSelectionContext,
        nint asyncIdentifier);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlSetAudioOutputDelegate(
        nint localChatControl,
        uint audioDeviceSelectionType,
        nint audioDeviceSelectionContext,
        nint asyncIdentifier);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyNetworkConnectChatControlDelegate(
        nint network,
        nint localChatControl,
        nint asyncIdentifier);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyNetworkDisconnectChatControlDelegate(
        nint network,
        nint localChatControl,
        nint asyncIdentifier);
}
