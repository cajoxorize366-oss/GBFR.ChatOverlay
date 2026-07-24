using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Native;

[Flags]
internal enum PartyChatPermissionOptions : uint
{
    None = 0x0000,
    SendMicrophoneAudio = 0x0001,
    ReceiveMicrophoneAudio = 0x0004,
}

internal enum PartyAudioDeviceSelectionType : uint
{
    None = 0,
    SystemDefault = 1,
    PlatformUserDefault = 2,
    Manual = 3,
}

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

    uint SetPermissions(
        nint localChatControl,
        nint targetChatControl,
        PartyChatPermissionOptions permissions);

    uint SetAudioInput(
        nint localChatControl,
        PartyAudioDeviceSelectionType selectionType,
        string? selectionContext,
        nint asyncIdentifier);

    uint SetAudioOutput(
        nint localChatControl,
        PartyAudioDeviceSelectionType selectionType,
        string? selectionContext,
        nint asyncIdentifier);

    uint ConnectChatControl(nint network, nint localChatControl, nint asyncIdentifier);

    uint DisconnectChatControl(nint network, nint localChatControl, nint asyncIdentifier);
}

/// <summary>
/// Exact flat-C bindings from Party_c.h in Microsoft.PlayFab.PlayFabParty.Cpp.Windows 1.10.12.
/// The caller supplies the already loaded, hash-verified game module. This class never loads or
/// initializes another Party runtime. The only exposed permission call is restricted by the caller
/// to microphone send/receive; no text, TTS, transcription or endpoint-send API is bound.
/// </summary>
internal sealed class PartyNativeApi : IPartyChatControlApi
{
    private readonly PartyGetLocalDeviceDelegate _getLocalDevice;
    private readonly PartyDeviceGetChatControlsDelegate _deviceGetChatControls;
    private readonly PartyDeviceCreateChatControlDelegate _deviceCreateChatControl;
    private readonly PartyDeviceDestroyChatControlDelegate _deviceDestroyChatControl;
    private readonly PartyChatControlSetAudioInputMutedDelegate _chatControlSetAudioInputMuted;
    private readonly PartyChatControlGetAudioInputMutedDelegate _chatControlGetAudioInputMuted;
    private readonly PartyChatControlSetPermissionsDelegate _chatControlSetPermissions;
    private readonly PartyChatControlSetAudioDeviceDelegate _chatControlSetAudioInput;
    private readonly PartyChatControlSetAudioDeviceDelegate _chatControlSetAudioOutput;
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
        _chatControlSetPermissions = Bind<PartyChatControlSetPermissionsDelegate>(
            verifiedPartyModule,
            "PartyChatControlSetPermissions");
        _chatControlSetAudioInput = Bind<PartyChatControlSetAudioDeviceDelegate>(
            verifiedPartyModule,
            "PartyChatControlSetAudioInput");
        _chatControlSetAudioOutput = Bind<PartyChatControlSetAudioDeviceDelegate>(
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

    public uint SetPermissions(
        nint localChatControl,
        nint targetChatControl,
        PartyChatPermissionOptions permissions) =>
        _chatControlSetPermissions(localChatControl, targetChatControl, (uint)permissions);

    public uint SetAudioInput(
        nint localChatControl,
        PartyAudioDeviceSelectionType selectionType,
        string? selectionContext,
        nint asyncIdentifier) =>
        SetAudioDevice(
            _chatControlSetAudioInput,
            localChatControl,
            selectionType,
            selectionContext,
            asyncIdentifier);

    public uint SetAudioOutput(
        nint localChatControl,
        PartyAudioDeviceSelectionType selectionType,
        string? selectionContext,
        nint asyncIdentifier) =>
        SetAudioDevice(
            _chatControlSetAudioOutput,
            localChatControl,
            selectionType,
            selectionContext,
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

    private static uint SetAudioDevice(
        PartyChatControlSetAudioDeviceDelegate callback,
        nint localChatControl,
        PartyAudioDeviceSelectionType selectionType,
        string? selectionContext,
        nint asyncIdentifier)
    {
        if (selectionType == PartyAudioDeviceSelectionType.Manual)
            ArgumentException.ThrowIfNullOrWhiteSpace(selectionContext);
        else if (selectionType == PartyAudioDeviceSelectionType.SystemDefault)
            selectionContext = null;
        else
            throw new ArgumentOutOfRangeException(
                nameof(selectionType),
                selectionType,
                "Only SystemDefault and Manual audio selection are supported by this Mod.");

        var nativeContext = nint.Zero;
        try
        {
            if (selectionContext is not null)
                nativeContext = Marshal.StringToCoTaskMemUTF8(selectionContext);
            return callback(
                localChatControl,
                (uint)selectionType,
                nativeContext,
                asyncIdentifier);
        }
        finally
        {
            if (nativeContext != nint.Zero)
                Marshal.FreeCoTaskMem(nativeContext);
        }
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
    private delegate uint PartyChatControlSetPermissionsDelegate(
        nint localChatControl,
        nint targetChatControl,
        uint permissions);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlSetAudioDeviceDelegate(
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
