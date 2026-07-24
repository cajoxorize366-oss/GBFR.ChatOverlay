using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using ReloadedHooksApi = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace GBFR.ChatOverlay.Input;

/// <summary>
/// Observes IDirectInputDevice8::GetDeviceState and clears the keyboard state
/// while chat owns the keyboard. The original call still runs so acquisition
/// state remains healthy.
/// </summary>
public sealed unsafe class DirectInputKeyboardHook
{
    private const int CreateDeviceVtableIndex = 3;
    private const int GetDeviceStateVtableIndex = 9;
    private const int MaximumReasonableStateSize = 4_096;

    private static readonly Guid SystemKeyboardGuid =
        new(0x6F1D2B61, 0xD5A0, 0x11CF, 0xBF, 0xC7, 0x44, 0x45, 0x53, 0x54, 0x00, 0x00);

    private readonly ReloadedHooksApi _hooks;
    private readonly Func<bool> _tryActivate;
    private readonly Func<bool> _shouldCapture;
    private readonly Func<bool> _isVoicePushToTalkEnabled;
    private readonly Action<string> _log;
    private readonly DirectInputKeyboardStateFilter _stateFilter = new();
    private readonly VoicePushToTalkSafetyGate _voicePushToTalkGate;
    private readonly object _hookSync = new();

    private IHook<DirectInput8CreateDelegate>? _directInputCreateHook;
    private IHook<CreateDeviceDelegate>? _createDeviceHook;
    private IHook<GetDeviceStateDelegate>? _getDeviceStateHook;
    private nint _keyboardDevice;
    private bool _initialized;
    private int _filterFailureLogged;

    public DirectInputKeyboardHook(
        ReloadedHooksApi hooks,
        Func<bool> tryActivate,
        Func<bool> shouldCapture,
        Func<bool> isVoicePushToTalkEnabled,
        Action<bool> setVoicePushToTalkPressed,
        Action requestVoiceDiagnosticSample,
        Action<string> log)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _tryActivate = tryActivate ?? throw new ArgumentNullException(nameof(tryActivate));
        _shouldCapture = shouldCapture ?? throw new ArgumentNullException(nameof(shouldCapture));
        _isVoicePushToTalkEnabled = isVoicePushToTalkEnabled ??
            throw new ArgumentNullException(nameof(isVoicePushToTalkEnabled));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _voicePushToTalkGate = new VoicePushToTalkSafetyGate(
            setVoicePushToTalkPressed ?? throw new ArgumentNullException(nameof(setVoicePushToTalkPressed)),
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
        _log("DirectInput8 keyboard interception initialized.");
    }

    public void Suspend()
    {
        _getDeviceStateHook?.Disable();
        _createDeviceHook?.Disable();
        _directInputCreateHook?.Disable();
        _voicePushToTalkGate.Suspend();
    }

    public void Resume()
    {
        _voicePushToTalkGate.Resume();
        _directInputCreateHook?.Enable();
        _createDeviceHook?.Enable();
        _getDeviceStateHook?.Enable();
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
        }

        return result;
    }

    private int GetDeviceState(nint self, int byteCount, nint state)
    {
        var result = _getDeviceStateHook!.OriginalFunction(self, byteCount, state);
        if (result < 0 ||
            self != Volatile.Read(ref _keyboardDevice) ||
            state == nint.Zero ||
            byteCount <= 0 ||
            byteCount > MaximumReasonableStateSize)
        {
            return result;
        }

        try
        {
            _stateFilter.Process(
                new Span<byte>((void*)state, byteCount),
                _tryActivate,
                _shouldCapture,
                _isVoicePushToTalkEnabled,
                _voicePushToTalkGate.Report);
        }
        catch (Exception exception)
        {
            _voicePushToTalkGate.ForceMute();
            if (Interlocked.Exchange(ref _filterFailureLogged, 1) == 0)
            {
                _log(
                    $"DirectInput keyboard filtering failed; push-to-talk was forced muted and " +
                    $"further errors are suppressed: {exception.Message}");
            }
        }
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
}
