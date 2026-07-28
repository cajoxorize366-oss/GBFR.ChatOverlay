using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Input;

/// <summary>
/// Translates the native DirectInput broker's atomic state into chat/settings/voice actions. The
/// native hook never calls managed code; this object is polled by the existing ImGui Present
/// callback and only processes a keyboard snapshot when its sequence changes.
/// </summary>
public sealed class DirectInputKeyboardHook : IDisposable
{
    private const int KeyboardStateSize = 256;

    private readonly IDirectInputBrokerBackend _backend;
    private readonly Func<bool> _canActivate;
    private readonly Func<bool> _tryActivate;
    private readonly Func<bool> _shouldCapture;
    private readonly Func<bool> _shouldCaptureMouse;
    private readonly Func<bool> _isVoicePushToTalkEnabled;
    private readonly Func<bool> _isSettingsMenuAvailable;
    private readonly Action<bool> _reportSettingsMenuKey;
    private readonly Action<string> _log;
    private readonly DirectInputKeyboardStateFilter _keyboardStateFilter = new();
    private readonly VoicePushToTalkSafetyGate _voicePushToTalkGate;
    private readonly VoiceInputModeCoordinator _voiceInputModeCoordinator;
    private readonly object _lifecycleSync = new();

    private DirectInputBrokerPolicy _lastPolicy = unchecked((DirectInputBrokerPolicy)uint.MaxValue);
    private DirectInputBrokerReadiness _lastReadiness =
        unchecked((DirectInputBrokerReadiness)uint.MaxValue);
    private ulong _lastSequence = ulong.MaxValue;
    private int _initialized;
    private int _suspended;
    private int _disposed;
    private int _brokerFailureLogged;

    public DirectInputKeyboardHook(
        Func<bool> canActivate,
        Func<bool> tryActivate,
        Func<bool> shouldCapture,
        Func<bool> shouldCaptureMouse,
        Func<bool> isVoicePushToTalkEnabled,
        Action<bool> setVoicePushToTalkPressed,
        Action requestVoiceDiagnosticSample,
        Func<bool> isSettingsMenuAvailable,
        Action<bool> reportSettingsMenuKey,
        Action<bool> setLocalMicrophoneMonitorPressed,
        Action<string> log)
        : this(
            DirectInputBrokerBridge.Instance,
            canActivate,
            tryActivate,
            shouldCapture,
            shouldCaptureMouse,
            isVoicePushToTalkEnabled,
            setVoicePushToTalkPressed,
            requestVoiceDiagnosticSample,
            isSettingsMenuAvailable,
            reportSettingsMenuKey,
            setLocalMicrophoneMonitorPressed,
            log)
    {
    }

    internal DirectInputKeyboardHook(
        IDirectInputBrokerBackend backend,
        Func<bool> canActivate,
        Func<bool> tryActivate,
        Func<bool> shouldCapture,
        Func<bool> shouldCaptureMouse,
        Func<bool> isVoicePushToTalkEnabled,
        Action<bool> setVoicePushToTalkPressed,
        Action requestVoiceDiagnosticSample,
        Func<bool> isSettingsMenuAvailable,
        Action<bool> reportSettingsMenuKey,
        Action<bool> setLocalMicrophoneMonitorPressed,
        Action<string> log)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _canActivate = canActivate ?? throw new ArgumentNullException(nameof(canActivate));
        _tryActivate = tryActivate ?? throw new ArgumentNullException(nameof(tryActivate));
        _shouldCapture = shouldCapture ?? throw new ArgumentNullException(nameof(shouldCapture));
        _shouldCaptureMouse = shouldCaptureMouse ??
            throw new ArgumentNullException(nameof(shouldCaptureMouse));
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
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
                return;

            try
            {
                if (!_backend.Install())
                    throw new InvalidOperationException("The game-local DirectInput8 import could not be patched.");
                if (!_backend.SetActive(true))
                    throw new InvalidOperationException("The DirectInput broker could not be activated.");

                Volatile.Write(ref _suspended, 0);
                _lastPolicy = unchecked((DirectInputBrokerPolicy)uint.MaxValue);
                _lastReadiness = unchecked((DirectInputBrokerReadiness)uint.MaxValue);
                _lastSequence = ulong.MaxValue;
                _log(
                    "DirectInput keyboard/mouse interception initialized through the game-local IAT " +
                    "broker; the dinput8/ReShade export entry was not modified and controllers remain pass-through.");
            }
            catch
            {
                TryFailOpenNativeBroker();
                Volatile.Write(ref _initialized, 0);
                throw;
            }
        }
    }

    /// <summary>
    /// Synchronizes policy and consumes changed key snapshots. This performs no scans and starts no
    /// polling thread; it is called from the already-existing ImGui Present callback.
    /// </summary>
    public void Poll()
    {
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _initialized) == 0 ||
                Volatile.Read(ref _suspended) != 0 ||
                Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                var captureKeyboard = _shouldCapture();
                var captureMouse = _shouldCaptureMouse();
                var canActivate = _canActivate();
                var settingsAvailable = _isSettingsMenuAvailable();
                var voicePushToTalkEnabled = _isVoicePushToTalkEnabled();
                var policy = BuildPolicy(
                    captureKeyboard,
                    captureMouse,
                    canActivate,
                    settingsAvailable,
                    voicePushToTalkEnabled);

                if (policy != _lastPolicy)
                {
                    if (!_backend.SetPolicy(policy))
                        throw new InvalidOperationException("The DirectInput broker rejected its input policy.");
                    _lastPolicy = policy;
                }

                if (captureKeyboard || !voicePushToTalkEnabled)
                    _voicePushToTalkGate.Report(false);

                if (!_backend.TryGetSnapshot(out var snapshot))
                    throw new InvalidOperationException("The DirectInput broker snapshot could not be read.");
                if (!snapshot.HasExpectedLayout)
                {
                    throw new InvalidOperationException(
                        $"DirectInput broker ABI mismatch: native={snapshot.AbiVersion}/{snapshot.StructSize}, " +
                        $"managed={DirectInputBrokerSnapshot.ExpectedAbiVersion}/" +
                        $"{DirectInputBrokerSnapshot.ExpectedStructSize}.");
                }

                LogReadinessTransition(snapshot.Readiness);
                if (snapshot.Sequence == _lastSequence)
                    return;
                _lastSequence = snapshot.Sequence;

                Span<byte> keyboardState = stackalloc byte[KeyboardStateSize];
                SetTrackedKey(
                    keyboardState,
                    DirectInputKeyboardStateFilter.ActivationScanCode,
                    (snapshot.Keys & DirectInputBrokerKeys.Activation) != 0);
                SetTrackedKey(
                    keyboardState,
                    DirectInputKeyboardStateFilter.SettingsMenuScanCode,
                    (snapshot.Keys & DirectInputBrokerKeys.Settings) != 0);
                SetTrackedKey(
                    keyboardState,
                    DirectInputKeyboardStateFilter.VoicePushToTalkScanCode,
                    (snapshot.Keys & DirectInputBrokerKeys.PushToTalk) != 0);

                _keyboardStateFilter.Process(
                    keyboardState,
                    _tryActivate,
                    _shouldCapture,
                    () => voicePushToTalkEnabled,
                    _voicePushToTalkGate.Report,
                    () => settingsAvailable,
                    _reportSettingsMenuKey);
            }
            catch (Exception exception)
            {
                FailOpen(exception);
            }
        }
    }

    public void Suspend()
    {
        lock (_lifecycleSync)
        {
            Interlocked.Exchange(ref _suspended, 1);
            if (Volatile.Read(ref _initialized) != 0)
                TryFailOpenNativeBroker();
            _voicePushToTalkGate.Suspend();
            _voiceInputModeCoordinator.ReportLocalMonitor(false);
        }
    }

    public void Resume()
    {
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _initialized) == 0 || Volatile.Read(ref _disposed) != 0)
                return;

            try
            {
                if (!_backend.SetActive(true))
                    throw new InvalidOperationException("The DirectInput broker could not be resumed.");
                _lastPolicy = unchecked((DirectInputBrokerPolicy)uint.MaxValue);
                _lastSequence = ulong.MaxValue;
                _voicePushToTalkGate.Resume();
                Volatile.Write(ref _suspended, 0);
            }
            catch (Exception exception)
            {
                FailOpen(exception);
            }
        }
    }

    public void SetLocalMicrophoneMonitorPressed(bool pressed) =>
        _voiceInputModeCoordinator.ReportLocalMonitor(pressed);

    public void ForceReleaseVoiceInputs()
    {
        _voicePushToTalkGate.ForceMute();
        _voiceInputModeCoordinator.ReportLocalMonitor(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Suspend();
        _voicePushToTalkGate.Dispose();
    }

    private static DirectInputBrokerPolicy BuildPolicy(
        bool captureKeyboard,
        bool captureMouse,
        bool canActivate,
        bool settingsAvailable,
        bool voicePushToTalkEnabled)
    {
        var policy = DirectInputBrokerPolicy.None;
        if (captureKeyboard)
            policy |= DirectInputBrokerPolicy.CaptureKeyboard;
        if (captureMouse)
            policy |= DirectInputBrokerPolicy.CaptureMouse;
        if (canActivate)
            policy |= DirectInputBrokerPolicy.SuppressActivation;
        if (settingsAvailable)
            policy |= DirectInputBrokerPolicy.SuppressSettings;
        if (voicePushToTalkEnabled)
            policy |= DirectInputBrokerPolicy.SuppressPushToTalk;
        return policy;
    }

    private static void SetTrackedKey(Span<byte> keyboardState, int scanCode, bool pressed)
    {
        if (pressed)
            keyboardState[scanCode] = 0x80;
    }

    private void LogReadinessTransition(DirectInputBrokerReadiness readiness)
    {
        if (readiness == _lastReadiness)
            return;
        _lastReadiness = readiness;
        _log(
            $"DirectInput broker readiness: iat={HasFlag(readiness, DirectInputBrokerReadiness.GameImport)}, " +
            $"factory={HasFlag(readiness, DirectInputBrokerReadiness.Factory)}, " +
            $"keyboard={HasFlag(readiness, DirectInputBrokerReadiness.Keyboard)}, " +
            $"mouse={HasFlag(readiness, DirectInputBrokerReadiness.Mouse)}, controllers=pass-through.");
    }

    private static bool HasFlag(
        DirectInputBrokerReadiness value,
        DirectInputBrokerReadiness flag) =>
        (value & flag) != 0;

    private void FailOpen(Exception exception)
    {
        Interlocked.Exchange(ref _suspended, 1);
        TryFailOpenNativeBroker();
        _voicePushToTalkGate.Suspend();
        _voiceInputModeCoordinator.ReportLocalMonitor(false);
        if (Interlocked.Exchange(ref _brokerFailureLogged, 1) == 0)
        {
            _log(
                "DirectInput broker synchronization failed; keyboard/mouse interception was " +
                $"released fail-open and further errors are suppressed: {exception}");
        }
    }

    private void TryFailOpenNativeBroker()
    {
        try
        {
            _ = _backend.SetPolicy(DirectInputBrokerPolicy.None);
        }
        catch
        {
            // Best-effort release; SetActive(false) is still attempted below.
        }

        try
        {
            _ = _backend.SetActive(false);
        }
        catch
        {
            // Native failures must never escape an input-release path.
        }
    }
}
