using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using ReloadedHooksApi = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace GBFR.ChatOverlay.Input;

/// <summary>
/// Observes DirectInput keyboard and mouse state while chat/settings own input. The original calls
/// still run so acquisition stays healthy; the shared capture barrier decides when each device is
/// safe to return to the game.
/// </summary>
public sealed unsafe class DirectInputKeyboardHook
{
    private const int CreateDeviceVtableIndex = 3;
    private const int GetDeviceStateVtableIndex = 9;
    private const int GetDeviceDataVtableIndex = 10;
    private const int MaximumReasonableStateSize = 4_096;
    private const uint DigddPeek = 0x00000001;

    private static readonly Guid SystemKeyboardGuid =
        new(0x6F1D2B61, 0xD5A0, 0x11CF, 0xBF, 0xC7, 0x44, 0x45, 0x53, 0x54, 0x00, 0x00);
    private static readonly Guid SystemMouseGuid =
        new(0x6F1D2B60, 0xD5A0, 0x11CF, 0xBF, 0xC7, 0x44, 0x45, 0x53, 0x54, 0x00, 0x00);

    private readonly ReloadedHooksApi _hooks;
    private readonly Func<bool> _tryActivate;
    private readonly Func<InputCaptureDevices> _getEffectiveCapture;
    private readonly Func<bool> _isVoicePushToTalkEnabled;
    private readonly Func<bool> _isSettingsMenuAvailable;
    private readonly Action<bool> _reportSettingsMenuKey;
    private readonly Action<string> _log;
    private readonly DirectInputKeyboardStateFilter _keyboardStateFilter = new();
    private readonly DirectInputMouseStateFilter _mouseStateFilter = new();
    private readonly VoicePushToTalkSafetyGate _voicePushToTalkGate;
    private readonly VoiceInputModeCoordinator _voiceInputModeCoordinator;
    private readonly object _hookSync = new();

    private IHook<DirectInput8CreateDelegate>? _directInputCreateHook;
    private IHook<CreateDeviceDelegate>? _createDeviceHook;
    private IHook<GetDeviceStateDelegate>? _getDeviceStateHook;
    private IHook<GetDeviceDataDelegate>? _getDeviceDataHook;
    private nint _keyboardDevice;
    private nint _mouseDevice;
    private bool _initialized;
    private int _filterFailureLogged;

    public DirectInputKeyboardHook(
        ReloadedHooksApi hooks,
        Func<bool> tryActivate,
        Func<InputCaptureDevices> getEffectiveCapture,
        Func<bool> isVoicePushToTalkEnabled,
        Action<bool> setVoicePushToTalkPressed,
        Action requestVoiceDiagnosticSample,
        Func<bool> isSettingsMenuAvailable,
        Action<bool> reportSettingsMenuKey,
        Action<bool> setLocalMicrophoneMonitorPressed,
        Action<string> log)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _tryActivate = tryActivate ?? throw new ArgumentNullException(nameof(tryActivate));
        _getEffectiveCapture = getEffectiveCapture ??
            throw new ArgumentNullException(nameof(getEffectiveCapture));
        _isVoicePushToTalkEnabled = isVoicePushToTalkEnabled ??
            throw new ArgumentNullException(nameof(isVoicePushToTalkEnabled));
        _isSettingsMenuAvailable = isSettingsMenuAvailable ??
            throw new ArgumentNullException(nameof(isSettingsMenuAvailable));
        _reportSettingsMenuKey = reportSettingsMenuKey ??
            throw new ArgumentNullException(nameof(reportSettingsMenuKey));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _voiceInputModeCoordinator = new VoiceInputModeCoordinator(
            setVoicePushToTalkPressed ?? throw new ArgumentNullException(nameof(setVoicePushToTalkPressed)),
            setLocalMicrophoneMonitorPressed ??
                throw new ArgumentNullException(nameof(setLocalMicrophoneMonitorPressed)));
        _voicePushToTalkGate = new VoicePushToTalkSafetyGate(
            _voiceInputModeCoordinator.ReportRemotePushToTalk,
            _log,
            requestVoiceDiagnosticSample ?? throw new ArgumentNullException(nameof(requestVoiceDiagnosticSample)));
    }

    public void Initialize()
    {
        if (_initialized)
            return;

        var module = NativeLibrary.Load("dinput8.dll");
        var export = NativeLibrary.GetExport(module, "DirectInput8Create");
        _directInputCreateHook = _hooks
            .CreateHook<DirectInput8CreateDelegate>(DirectInput8Create, export)
            .Activate();
        _initialized = true;
        _log("DirectInput8 keyboard and mouse interception initialized.");
    }

    public void Suspend()
    {
        _getDeviceDataHook?.Disable();
        _getDeviceStateHook?.Disable();
        _createDeviceHook?.Disable();
        _directInputCreateHook?.Disable();
        _voicePushToTalkGate.Suspend();
        _voiceInputModeCoordinator.ReportLocalMonitor(false);
    }

    public void Resume()
    {
        _voicePushToTalkGate.Resume();
        _directInputCreateHook?.Enable();
        _createDeviceHook?.Enable();
        _getDeviceStateHook?.Enable();
        _getDeviceDataHook?.Enable();
    }

    public void SetLocalMicrophoneMonitorPressed(bool pressed) =>
        _voiceInputModeCoordinator.ReportLocalMonitor(pressed);

    public void ForceReleaseVoiceInputs()
    {
        _voicePushToTalkGate.ForceMute();
        _voiceInputModeCoordinator.ReportLocalMonitor(false);
    }

    private int DirectInput8Create(
        nint instance,
        uint version,
        nint interfaceId,
        nint output,
        nint outer)
    {
        var result = _directInputCreateHook!.OriginalFunction(
            instance,
            version,
            interfaceId,
            output,
            outer);

        if (result < 0 || output == nint.Zero || *(nint*)output == nint.Zero)
            return result;

        lock (_hookSync)
        {
            if (_createDeviceHook is null)
            {
                var directInput = *(nint*)output;
                var function = GetVtableFunction(directInput, CreateDeviceVtableIndex);
                _createDeviceHook = _hooks
                    .CreateHook<CreateDeviceDelegate>(CreateDevice, function)
                    .Activate();
                _log($"IDirectInput8::CreateDevice hooked (DirectInput {version:X4}).");
            }
        }

        return result;
    }

    private int CreateDevice(nint self, nint deviceGuid, nint output, nint outer)
    {
        var result = _createDeviceHook!.OriginalFunction(self, deviceGuid, output, outer);
        if (result < 0 || deviceGuid == nint.Zero || output == nint.Zero || *(nint*)output == nint.Zero)
            return result;

        var device = *(nint*)output;
        var guid = *(Guid*)deviceGuid;
        if (guid == SystemKeyboardGuid)
        {
            Volatile.Write(ref _keyboardDevice, device);
            _log("DirectInput system keyboard device detected.");
        }
        else if (guid == SystemMouseGuid)
        {
            Volatile.Write(ref _mouseDevice, device);
            _log("DirectInput system mouse device detected.");
        }

        lock (_hookSync)
        {
            if (_getDeviceStateHook is null)
            {
                var function = GetVtableFunction(device, GetDeviceStateVtableIndex);
                _getDeviceStateHook = _hooks
                    .CreateHook<GetDeviceStateDelegate>(GetDeviceState, function)
                    .Activate();
                _log("IDirectInputDevice8::GetDeviceState hooked.");
            }

            if (_getDeviceDataHook is null)
            {
                var function = GetVtableFunction(device, GetDeviceDataVtableIndex);
                _getDeviceDataHook = _hooks
                    .CreateHook<GetDeviceDataDelegate>(GetDeviceData, function)
                    .Activate();
                _log("IDirectInputDevice8::GetDeviceData hooked.");
            }
        }

        return result;
    }

    private int GetDeviceState(nint self, int byteCount, nint state)
    {
        var result = _getDeviceStateHook!.OriginalFunction(self, byteCount, state);
        if (result < 0 || state == nint.Zero || byteCount <= 0 || byteCount > MaximumReasonableStateSize)
            return result;

        try
        {
            if (self == Volatile.Read(ref _keyboardDevice))
            {
                _keyboardStateFilter.Process(
                    new Span<byte>((void*)state, byteCount),
                    _tryActivate,
                    () => (_getEffectiveCapture() & InputCaptureDevices.Keyboard) != 0,
                    _isVoicePushToTalkEnabled,
                    _voicePushToTalkGate.Report,
                    _isSettingsMenuAvailable,
                    _reportSettingsMenuKey);
            }
            else if (self == Volatile.Read(ref _mouseDevice))
            {
                _mouseStateFilter.Process(
                    new Span<byte>((void*)state, byteCount),
                    (_getEffectiveCapture() & InputCaptureDevices.Mouse) != 0);
            }
        }
        catch (Exception exception)
        {
            _voicePushToTalkGate.ForceMute();
            _voiceInputModeCoordinator.ReportLocalMonitor(false);
            if (Interlocked.Exchange(ref _filterFailureLogged, 1) == 0)
            {
                _log(
                    "DirectInput filtering failed; push-to-talk was forced muted, local monitoring " +
                    $"stopped, and further errors are suppressed: {exception.Message}");
            }
        }
        return result;
    }

    private int GetDeviceData(
        nint self,
        int objectDataSize,
        nint objectData,
        nint objectCount,
        uint flags)
    {
        var isMouse = self == Volatile.Read(ref _mouseDevice);
        var suppress = isMouse &&
                       (_getEffectiveCapture() & InputCaptureDevices.Mouse) != 0;
        var effectiveFlags = suppress ? flags & ~DigddPeek : flags;
        var result = _getDeviceDataHook!.OriginalFunction(
            self,
            objectDataSize,
            objectData,
            objectCount,
            effectiveFlags);
        if (result >= 0 && suppress && objectCount != nint.Zero)
            *(uint*)objectCount = 0;
        return result;
    }

    private static nint GetVtableFunction(nint instance, int index)
    {
        var vtable = *(nint**)instance;
        return vtable[index];
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DirectInput8CreateDelegate(
        nint instance,
        uint version,
        nint interfaceId,
        nint output,
        nint outer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeviceDelegate(nint self, nint deviceGuid, nint output, nint outer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceStateDelegate(nint self, int byteCount, nint state);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceDataDelegate(
        nint self,
        int objectDataSize,
        nint objectData,
        nint objectCount,
        uint flags);
}
