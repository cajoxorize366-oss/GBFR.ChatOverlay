using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Native;

[Flags]
internal enum PartyChatPermissionOptions : uint
{
    None = 0x0000,
    SendMicrophoneAudio = 0x0001,
    SendTextToSpeechAudio = 0x0002,
    SendAudio = SendMicrophoneAudio | SendTextToSpeechAudio,
    ReceiveMicrophoneAudio = 0x0004,
    ReceiveTextToSpeechAudio = 0x0008,
    ReceiveAudio = ReceiveMicrophoneAudio | ReceiveTextToSpeechAudio,
    ReceiveText = 0x0010,
}

internal enum PartyAudioDeviceSelectionType : uint
{
    None = 0,
    SystemDefault = 1,
    PlatformUserDefault = 2,
    Manual = 3,
}

internal enum PartyAudioSampleType : uint
{
    Integer = 0,
    Float = 1,
}

internal readonly record struct PartyAudioFormatDescriptor(
    uint SamplesPerSecond,
    uint ChannelMask,
    ushort ChannelCount,
    ushort BitsPerSample,
    PartyAudioSampleType SampleType,
    bool Interleaved);

internal enum PartyAudioInputState : uint
{
    NoInput = 0,
    Initialized = 1,
    NotFound = 2,
    UserConsentDenied = 3,
    UnsupportedFormat = 4,
    AlreadyInUse = 5,
    UnknownError = 6,
}

internal enum PartyAudioOutputState : uint
{
    NoOutput = 0,
    Initialized = 1,
    NotFound = 2,
    UnsupportedFormat = 3,
    AlreadyInUse = 4,
    UnknownError = 5,
}

internal enum PartyLocalChatControlChatIndicator : uint
{
    Silent = 0,
    Talking = 1,
    AudioInputMuted = 2,
    NoAudioInput = 3,
}

internal enum PartyThreadId : uint
{
    Audio = 0,
    Networking = 1,
}

internal enum PartyWorkMode : uint
{
    Automatic = 0,
    Manual = 1,
}

internal interface IPartyAudioWorkApi
{
    uint GetWorkMode(PartyThreadId threadId, out PartyWorkMode workMode);

    uint DoWork(nint manager, PartyThreadId threadId);
}

internal interface IPartyEndpointApi
{
    uint IsEndpointLocal(nint endpoint, out bool isLocal);

    uint GetEndpointEntityId(nint endpoint, out string? entityId);
}

internal enum PartyChatControlChatIndicator : uint
{
    Silent = 0,
    Talking = 1,
    IncomingVoiceDisabled = 2,
    IncomingCommunicationsMuted = 3,
    NoRemoteInput = 4,
    RemoteAudioInputMuted = 5,
}

internal interface IPartyChatControlApi
{
    uint GetLocalDevice(nint manager, out nint localDevice);

    uint GetLocalChatControlCount(nint localDevice, out uint chatControlCount);

    uint GetNetworkChatControls(nint network, out nint[] chatControls);

    uint CreateChatControl(
        nint localDevice,
        nint localUser,
        nint asyncIdentifier,
        out nint localChatControl);

    uint DestroyChatControl(nint localDevice, nint localChatControl, nint asyncIdentifier);

    uint SetAudioInputMuted(nint localChatControl, bool muted);

    uint GetAudioInputMuted(nint localChatControl, out bool muted);

    uint GetPermissions(
        nint localChatControl,
        nint targetChatControl,
        out PartyChatPermissionOptions permissions);

    uint GetAudioInput(
        nint localChatControl,
        out PartyAudioDeviceSelectionType selectionType,
        out string? selectionContext,
        out string? deviceId);

    uint GetAudioOutput(
        nint localChatControl,
        out PartyAudioDeviceSelectionType selectionType,
        out string? selectionContext,
        out string? deviceId);

    uint GetAudioRenderVolume(
        nint localChatControl,
        nint targetChatControl,
        out float volume);

    uint GetIncomingAudioMuted(
        nint localChatControl,
        nint targetChatControl,
        out bool muted);

    uint GetEntityId(nint chatControl, out string? entityId)
    {
        entityId = null;
        throw new NotSupportedException("Party ChatControl EntityId lookup is not bound by this API implementation.");
    }

    uint IsLocal(nint chatControl, out bool isLocal)
    {
        isLocal = false;
        throw new NotSupportedException("Party ChatControl locality lookup is not bound by this API implementation.");
    }

    uint SetIncomingAudioMuted(
        nint localChatControl,
        nint targetChatControl,
        bool muted) =>
        throw new NotSupportedException("Party incoming-audio mute is not bound by this API implementation.");

    uint GetLocalChatIndicator(
        nint localChatControl,
        out PartyLocalChatControlChatIndicator indicator);

    uint GetChatIndicator(
        nint localChatControl,
        nint targetChatControl,
        out PartyChatControlChatIndicator indicator);

    uint GetErrorMessage(uint error, out string? errorMessage);

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

    uint ConfigureAudioManipulationCaptureStream(
        nint localChatControl,
        nint asyncIdentifier) =>
        throw new NotSupportedException("Party audio manipulation capture is not bound by this API implementation.");

    uint GetAudioManipulationCaptureStream(
        nint localChatControl,
        out nint captureStream)
    {
        captureStream = nint.Zero;
        throw new NotSupportedException("Party audio manipulation capture is not bound by this API implementation.");
    }

    uint GetAudioManipulationSinkFormat(
        nint captureStream,
        out PartyAudioFormatDescriptor format)
    {
        format = default;
        throw new NotSupportedException("Party audio manipulation capture is not bound by this API implementation.");
    }

    uint SubmitAudioManipulationCaptureBuffer(
        nint captureStream,
        byte[] buffer,
        int count) =>
        throw new NotSupportedException("Party audio manipulation capture is not bound by this API implementation.");
}

/// <summary>
/// Exact flat-C bindings from Party_c.h in Microsoft.PlayFab.PlayFabParty.Cpp.Windows 1.10.12.
/// The caller supplies the already loaded, path- and export-verified game module. This class never loads or
/// initializes another Party runtime. The only exposed permission call is restricted by the caller
/// to microphone send/receive; no text, TTS, transcription or endpoint-send API is bound.
/// </summary>
internal sealed class PartyNativeApi : IPartyChatControlApi, IPartyAudioWorkApi, IPartyEndpointApi
{
    private const uint MaximumNetworkChatControls = 64;

    private readonly PartyGetWorkModeDelegate _getWorkMode;
    private readonly PartyDoWorkDelegate _doWork;
    private readonly PartyGetLocalDeviceDelegate _getLocalDevice;
    private readonly PartyDeviceGetChatControlsDelegate _deviceGetChatControls;
    private readonly PartyNetworkGetChatControlsDelegate _networkGetChatControls;
    private readonly PartyDeviceCreateChatControlDelegate _deviceCreateChatControl;
    private readonly PartyDeviceDestroyChatControlDelegate _deviceDestroyChatControl;
    private readonly PartyChatControlSetAudioInputMutedDelegate _chatControlSetAudioInputMuted;
    private readonly PartyChatControlGetAudioInputMutedDelegate _chatControlGetAudioInputMuted;
    private readonly PartyChatControlGetPermissionsDelegate _chatControlGetPermissions;
    private readonly PartyChatControlGetAudioDeviceDelegate _chatControlGetAudioInput;
    private readonly PartyChatControlGetAudioDeviceDelegate _chatControlGetAudioOutput;
    private readonly PartyChatControlGetAudioRenderVolumeDelegate _chatControlGetAudioRenderVolume;
    private readonly PartyChatControlGetIncomingAudioMutedDelegate _chatControlGetIncomingAudioMuted;
    private readonly PartyChatControlGetEntityIdDelegate _chatControlGetEntityId;
    private readonly PartyChatControlIsLocalDelegate _chatControlIsLocal;
    private readonly PartyEndpointIsLocalDelegate _endpointIsLocal;
    private readonly PartyEndpointGetEntityIdDelegate _endpointGetEntityId;
    private readonly PartyChatControlSetIncomingAudioMutedDelegate
        _chatControlSetIncomingAudioMuted;
    private readonly PartyChatControlGetLocalChatIndicatorDelegate _chatControlGetLocalChatIndicator;
    private readonly PartyChatControlGetChatIndicatorDelegate _chatControlGetChatIndicator;
    private readonly PartyGetErrorMessageDelegate _getErrorMessage;
    private readonly PartyChatControlSetPermissionsDelegate _chatControlSetPermissions;
    private readonly PartyChatControlSetAudioDeviceDelegate _chatControlSetAudioInput;
    private readonly PartyChatControlSetAudioDeviceDelegate _chatControlSetAudioOutput;
    private readonly PartyNetworkConnectChatControlDelegate _networkConnectChatControl;
    private readonly PartyNetworkDisconnectChatControlDelegate _networkDisconnectChatControl;
    private readonly PartyChatControlConfigureAudioManipulationCaptureStreamDelegate
        _chatControlConfigureAudioManipulationCaptureStream;
    private readonly PartyChatControlGetAudioManipulationCaptureStreamDelegate
        _chatControlGetAudioManipulationCaptureStream;
    private readonly PartyAudioManipulationSinkStreamGetFormatDelegate
        _audioManipulationSinkStreamGetFormat;
    private readonly PartyAudioManipulationSinkStreamSubmitBufferDelegate
        _audioManipulationSinkStreamSubmitBuffer;
    private readonly nint _captureAudioFormatMemory;
    private readonly nint _captureStreamConfigurationMemory;

    public PartyNativeApi(nint verifiedPartyModule)
    {
        if (verifiedPartyModule == nint.Zero)
            throw new ArgumentException("The Party module handle is null.", nameof(verifiedPartyModule));
        if (nint.Size != 8)
            throw new PlatformNotSupportedException("The Relink Party bindings require a 64-bit process.");

        _getWorkMode = Bind<PartyGetWorkModeDelegate>(verifiedPartyModule, "PartyGetWorkMode");
        _doWork = Bind<PartyDoWorkDelegate>(verifiedPartyModule, "PartyDoWork");
        _getLocalDevice = Bind<PartyGetLocalDeviceDelegate>(verifiedPartyModule, "PartyGetLocalDevice");
        _deviceGetChatControls = Bind<PartyDeviceGetChatControlsDelegate>(
            verifiedPartyModule,
            "PartyDeviceGetChatControls");
        _networkGetChatControls = Bind<PartyNetworkGetChatControlsDelegate>(
            verifiedPartyModule,
            "PartyNetworkGetChatControls");
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
        _chatControlGetPermissions = Bind<PartyChatControlGetPermissionsDelegate>(
            verifiedPartyModule,
            "PartyChatControlGetPermissions");
        _chatControlGetAudioInput = Bind<PartyChatControlGetAudioDeviceDelegate>(
            verifiedPartyModule,
            "PartyChatControlGetAudioInput");
        _chatControlGetAudioOutput = Bind<PartyChatControlGetAudioDeviceDelegate>(
            verifiedPartyModule,
            "PartyChatControlGetAudioOutput");
        _chatControlGetAudioRenderVolume = Bind<PartyChatControlGetAudioRenderVolumeDelegate>(
            verifiedPartyModule,
            "PartyChatControlGetAudioRenderVolume");
        _chatControlGetIncomingAudioMuted = Bind<PartyChatControlGetIncomingAudioMutedDelegate>(
            verifiedPartyModule,
            "PartyChatControlGetIncomingAudioMuted");
        _chatControlGetEntityId = Bind<PartyChatControlGetEntityIdDelegate>(
            verifiedPartyModule,
            "PartyChatControlGetEntityId");
        _chatControlIsLocal = Bind<PartyChatControlIsLocalDelegate>(
            verifiedPartyModule,
            "PartyChatControlIsLocal");
        _endpointIsLocal = Bind<PartyEndpointIsLocalDelegate>(
            verifiedPartyModule,
            "PartyEndpointIsLocal");
        _endpointGetEntityId = Bind<PartyEndpointGetEntityIdDelegate>(
            verifiedPartyModule,
            "PartyEndpointGetEntityId");
        _chatControlSetIncomingAudioMuted =
            Bind<PartyChatControlSetIncomingAudioMutedDelegate>(
                verifiedPartyModule,
                "PartyChatControlSetIncomingAudioMuted");
        _chatControlGetLocalChatIndicator = Bind<PartyChatControlGetLocalChatIndicatorDelegate>(
            verifiedPartyModule,
            "PartyChatControlGetLocalChatIndicator");
        _chatControlGetChatIndicator = Bind<PartyChatControlGetChatIndicatorDelegate>(
            verifiedPartyModule,
            "PartyChatControlGetChatIndicator");
        _getErrorMessage = Bind<PartyGetErrorMessageDelegate>(verifiedPartyModule, "PartyGetErrorMessage");
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
        _chatControlConfigureAudioManipulationCaptureStream =
            Bind<PartyChatControlConfigureAudioManipulationCaptureStreamDelegate>(
                verifiedPartyModule,
                "PartyChatControlConfigureAudioManipulationCaptureStream");
        _chatControlGetAudioManipulationCaptureStream =
            Bind<PartyChatControlGetAudioManipulationCaptureStreamDelegate>(
                verifiedPartyModule,
                "PartyChatControlGetAudioManipulationCaptureStream");
        _audioManipulationSinkStreamGetFormat =
            Bind<PartyAudioManipulationSinkStreamGetFormatDelegate>(
                verifiedPartyModule,
                "PartyAudioManipulationSinkStreamGetFormat");
        _audioManipulationSinkStreamSubmitBuffer =
            Bind<PartyAudioManipulationSinkStreamSubmitBufferDelegate>(
                verifiedPartyModule,
                "PartyAudioManipulationSinkStreamSubmitBuffer");

        _captureAudioFormatMemory = Marshal.AllocHGlobal(Marshal.SizeOf<PartyAudioFormatNative>());
        Marshal.StructureToPtr(
            new PartyAudioFormatNative
            {
                SamplesPerSecond = 24_000,
                ChannelMask = 0,
                ChannelCount = 1,
                BitsPerSample = 32,
                SampleType = PartyAudioSampleType.Float,
                Interleaved = 0,
            },
            _captureAudioFormatMemory,
            fDeleteOld: false);
        _captureStreamConfigurationMemory = Marshal.AllocHGlobal(
            Marshal.SizeOf<PartyAudioManipulationSinkStreamConfigurationNative>());
        Marshal.StructureToPtr(
            new PartyAudioManipulationSinkStreamConfigurationNative
            {
                Format = _captureAudioFormatMemory,
                MaxTotalAudioBufferSizeInMilliseconds = 200,
            },
            _captureStreamConfigurationMemory,
            fDeleteOld: false);
    }

    public uint GetLocalDevice(nint manager, out nint localDevice) =>
        _getLocalDevice(manager, out localDevice);

    public uint GetWorkMode(PartyThreadId threadId, out PartyWorkMode workMode)
    {
        var result = _getWorkMode((uint)threadId, out var nativeWorkMode);
        workMode = (PartyWorkMode)nativeWorkMode;
        return result;
    }

    public uint DoWork(nint manager, PartyThreadId threadId) =>
        _doWork(manager, (uint)threadId);

    public uint GetLocalChatControlCount(nint localDevice, out uint chatControlCount)
    {
        return _deviceGetChatControls(localDevice, out chatControlCount, out _);
    }

    public uint GetNetworkChatControls(nint network, out nint[] chatControls)
    {
        var result = _networkGetChatControls(network, out var chatControlCount, out var nativeChatControls);
        if (result != 0 || chatControlCount == 0)
        {
            chatControls = [];
            return result;
        }

        if (nativeChatControls == nint.Zero)
        {
            throw new InvalidOperationException(
                "PartyNetworkGetChatControls returned a nonzero count with a null array.");
        }
        if (chatControlCount > MaximumNetworkChatControls)
        {
            throw new InvalidOperationException(
                $"PartyNetworkGetChatControls returned an implausible count of {chatControlCount}.");
        }

        chatControls = new nint[checked((int)chatControlCount)];
        for (var index = 0; index < chatControls.Length; index++)
        {
            var chatControl = Marshal.ReadIntPtr(nativeChatControls, checked(index * nint.Size));
            if (chatControl == nint.Zero)
            {
                throw new InvalidOperationException(
                    $"PartyNetworkGetChatControls returned a null handle at index {index}.");
            }

            chatControls[index] = chatControl;
        }

        return result;
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

    public uint GetPermissions(
        nint localChatControl,
        nint targetChatControl,
        out PartyChatPermissionOptions permissions)
    {
        var result = _chatControlGetPermissions(localChatControl, targetChatControl, out var nativePermissions);
        permissions = (PartyChatPermissionOptions)nativePermissions;
        return result;
    }

    public uint GetAudioInput(
        nint localChatControl,
        out PartyAudioDeviceSelectionType selectionType,
        out string? selectionContext,
        out string? deviceId) =>
        GetAudioDevice(
            _chatControlGetAudioInput,
            localChatControl,
            out selectionType,
            out selectionContext,
            out deviceId);

    public uint GetAudioOutput(
        nint localChatControl,
        out PartyAudioDeviceSelectionType selectionType,
        out string? selectionContext,
        out string? deviceId) =>
        GetAudioDevice(
            _chatControlGetAudioOutput,
            localChatControl,
            out selectionType,
            out selectionContext,
            out deviceId);

    public uint GetAudioRenderVolume(
        nint localChatControl,
        nint targetChatControl,
        out float volume) =>
        _chatControlGetAudioRenderVolume(localChatControl, targetChatControl, out volume);

    public uint GetIncomingAudioMuted(
        nint localChatControl,
        nint targetChatControl,
        out bool muted)
    {
        var result = _chatControlGetIncomingAudioMuted(
            localChatControl,
            targetChatControl,
            out var nativeMuted);
        muted = nativeMuted != 0;
        return result;
    }

    public uint GetEntityId(nint chatControl, out string? entityId)
    {
        var result = _chatControlGetEntityId(chatControl, out var nativeEntityId);
        entityId = result == 0 && nativeEntityId != nint.Zero
            ? Marshal.PtrToStringUTF8(nativeEntityId)
            : null;
        return result;
    }

    public uint IsLocal(nint chatControl, out bool isLocal)
    {
        var result = _chatControlIsLocal(chatControl, out var nativeIsLocal);
        isLocal = nativeIsLocal != 0;
        return result;
    }

    public uint IsEndpointLocal(nint endpoint, out bool isLocal)
    {
        var result = _endpointIsLocal(endpoint, out var nativeIsLocal);
        isLocal = nativeIsLocal != 0;
        return result;
    }

    public uint GetEndpointEntityId(nint endpoint, out string? entityId)
    {
        var result = _endpointGetEntityId(endpoint, out var nativeEntityId);
        entityId = result == 0 && nativeEntityId != nint.Zero
            ? Marshal.PtrToStringUTF8(nativeEntityId)
            : null;
        return result;
    }

    public uint SetIncomingAudioMuted(
        nint localChatControl,
        nint targetChatControl,
        bool muted) =>
        _chatControlSetIncomingAudioMuted(
            localChatControl,
            targetChatControl,
            muted ? (byte)1 : (byte)0);

    public uint GetLocalChatIndicator(
        nint localChatControl,
        out PartyLocalChatControlChatIndicator indicator)
    {
        var result = _chatControlGetLocalChatIndicator(localChatControl, out var nativeIndicator);
        indicator = (PartyLocalChatControlChatIndicator)nativeIndicator;
        return result;
    }

    public uint GetChatIndicator(
        nint localChatControl,
        nint targetChatControl,
        out PartyChatControlChatIndicator indicator)
    {
        var result = _chatControlGetChatIndicator(
            localChatControl,
            targetChatControl,
            out var nativeIndicator);
        indicator = (PartyChatControlChatIndicator)nativeIndicator;
        return result;
    }

    public uint GetErrorMessage(uint error, out string? errorMessage)
    {
        var result = _getErrorMessage(error, out var nativeMessage);
        errorMessage = nativeMessage == nint.Zero ? null : Marshal.PtrToStringUTF8(nativeMessage);
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

    public uint ConfigureAudioManipulationCaptureStream(
        nint localChatControl,
        nint asyncIdentifier)
    {
        // The header does not explicitly document when the asynchronous operation stops referencing
        // nested configuration pointers. This Mod cannot unload, so two tiny process-lifetime native
        // allocations avoid relying on an undocumented copy/lifetime assumption.
        return _chatControlConfigureAudioManipulationCaptureStream(
            localChatControl,
            _captureStreamConfigurationMemory,
            asyncIdentifier);
    }

    public uint GetAudioManipulationCaptureStream(
        nint localChatControl,
        out nint captureStream) =>
        _chatControlGetAudioManipulationCaptureStream(localChatControl, out captureStream);

    public uint GetAudioManipulationSinkFormat(
        nint captureStream,
        out PartyAudioFormatDescriptor format)
    {
        var result = _audioManipulationSinkStreamGetFormat(captureStream, out var nativeFormat);
        format = new PartyAudioFormatDescriptor(
            nativeFormat.SamplesPerSecond,
            nativeFormat.ChannelMask,
            nativeFormat.ChannelCount,
            nativeFormat.BitsPerSample,
            nativeFormat.SampleType,
            nativeFormat.Interleaved != 0);
        return result;
    }

    public unsafe uint SubmitAudioManipulationCaptureBuffer(
        nint captureStream,
        byte[] buffer,
        int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)count > (uint)buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        fixed (byte* bufferPointer = buffer)
        {
            var dataBuffer = new PartyDataBufferNative
            {
                Buffer = (nint)bufferPointer,
                BufferByteCount = checked((uint)count),
            };
            return _audioManipulationSinkStreamSubmitBuffer(
                captureStream,
                (nint)(&dataBuffer));
        }
    }

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

    private static uint GetAudioDevice(
        PartyChatControlGetAudioDeviceDelegate callback,
        nint localChatControl,
        out PartyAudioDeviceSelectionType selectionType,
        out string? selectionContext,
        out string? deviceId)
    {
        var result = callback(
            localChatControl,
            out var nativeSelectionType,
            out var nativeSelectionContext,
            out var nativeDeviceId);
        selectionType = (PartyAudioDeviceSelectionType)nativeSelectionType;
        selectionContext = nativeSelectionContext == nint.Zero
            ? null
            : Marshal.PtrToStringUTF8(nativeSelectionContext);
        deviceId = nativeDeviceId == nint.Zero ? null : Marshal.PtrToStringUTF8(nativeDeviceId);
        return result;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyGetWorkModeDelegate(uint threadId, out uint workMode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyDoWorkDelegate(nint manager, uint threadId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyGetLocalDeviceDelegate(nint manager, out nint localDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyDeviceGetChatControlsDelegate(
        nint device,
        out uint chatControlCount,
        out nint chatControls);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyNetworkGetChatControlsDelegate(
        nint network,
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
    private delegate uint PartyChatControlGetPermissionsDelegate(
        nint localChatControl,
        nint targetChatControl,
        out uint permissions);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlGetAudioDeviceDelegate(
        nint localChatControl,
        out uint audioDeviceSelectionType,
        out nint audioDeviceSelectionContext,
        out nint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlGetAudioRenderVolumeDelegate(
        nint localChatControl,
        nint targetChatControl,
        out float volume);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlGetIncomingAudioMutedDelegate(
        nint localChatControl,
        nint targetChatControl,
        out byte muted);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlGetEntityIdDelegate(
        nint chatControl,
        out nint entityId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlIsLocalDelegate(
        nint chatControl,
        out byte isLocal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyEndpointIsLocalDelegate(
        nint endpoint,
        out byte isLocal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyEndpointGetEntityIdDelegate(
        nint endpoint,
        out nint entityId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlSetIncomingAudioMutedDelegate(
        nint localChatControl,
        nint targetChatControl,
        byte muted);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlGetLocalChatIndicatorDelegate(
        nint localChatControl,
        out uint indicator);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlGetChatIndicatorDelegate(
        nint localChatControl,
        nint targetChatControl,
        out uint indicator);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyGetErrorMessageDelegate(uint error, out nint errorMessage);

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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlConfigureAudioManipulationCaptureStreamDelegate(
        nint localChatControl,
        nint configuration,
        nint asyncIdentifier);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyChatControlGetAudioManipulationCaptureStreamDelegate(
        nint localChatControl,
        out nint captureStream);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyAudioManipulationSinkStreamGetFormatDelegate(
        nint captureStream,
        out PartyAudioFormatNative format);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PartyAudioManipulationSinkStreamSubmitBufferDelegate(
        nint captureStream,
        nint buffer);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PartyAudioFormatNative
    {
        public uint SamplesPerSecond;
        public uint ChannelMask;
        public ushort ChannelCount;
        public ushort BitsPerSample;
        public PartyAudioSampleType SampleType;
        public byte Interleaved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PartyAudioManipulationSinkStreamConfigurationNative
    {
        public nint Format;
        public uint MaxTotalAudioBufferSizeInMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PartyDataBufferNative
    {
        public nint Buffer;
        public uint BufferByteCount;
    }
}
