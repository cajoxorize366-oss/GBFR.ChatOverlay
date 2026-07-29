using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Input;

/// <summary>
/// Translates the native DirectInput broker's atomic state into chat/settings/voice actions. The
/// native hook never calls managed code; this object is polled by the existing ImGui Present
/// callback and only processes a keyboard snapshot when its sequence changes.
/// </summary>
public sealed class DirectInputKeyboardHook : IDisposable
{
    private readonly IDirectInputBrokerBackend _backend;
    private readonly Func<bool> _canActivate;
    private readonly Func<bool> _tryActivate;
    private readonly Func<bool> _shouldCapture;
    private readonly Func<bool> _shouldCaptureMouse;
    private readonly Func<bool> _isVoicePushToTalkEnabled;
    private readonly Func<bool> _isSettingsMenuAvailable;
    private readonly Action<bool> _reportSettingsMenuKey;
    private readonly Func<Config> _getConfiguration;
    private readonly Action<DirectInputBrokerSnapshot> _observeInputSnapshot;
    private readonly Action<bool> _reportQuickActionsMenuKey;
    private readonly Action<string, bool> _reportQuickActionKey;
    private readonly Action<int, bool> _reportPlayerMuteKey;
    private readonly Action<string> _log;
    private readonly VoicePushToTalkSafetyGate _voicePushToTalkGate;
    private readonly VoiceInputModeCoordinator _voiceInputModeCoordinator;
    private readonly object _lifecycleSync = new();

    private DirectInputBrokerPolicy _lastPolicy = unchecked((DirectInputBrokerPolicy)uint.MaxValue);
    private DirectInputBrokerReadiness _lastReadiness =
        unchecked((DirectInputBrokerReadiness)uint.MaxValue);
    private ulong _lastSequence = ulong.MaxValue;
    private string? _lastHotkeySignature;
    private HotkeyConfigurationSnapshot? _hotkeys;
    private bool _activationWasDown;
    private bool _settingsWasDown;
    private bool _quickActionsWasDown;
    private bool _bindingReleasePending = true;
    private readonly Dictionary<string, bool> _quickActionWasDown = new(StringComparer.Ordinal);
    private readonly bool[] _playerMuteWasDown = new bool[3];
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
        Action<string> log,
        Func<Config>? getConfiguration = null,
        Action<DirectInputBrokerSnapshot>? observeInputSnapshot = null,
        Action<bool>? reportQuickActionsMenuKey = null,
        Action<string, bool>? reportQuickActionKey = null,
        Action<int, bool>? reportPlayerMuteKey = null)
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
        _getConfiguration = getConfiguration ?? (() => new Config());
        _observeInputSnapshot = observeInputSnapshot ?? (_ => { });
        _reportQuickActionsMenuKey = reportQuickActionsMenuKey ?? (_ => { });
        _reportQuickActionKey = reportQuickActionKey ?? ((_, _) => { });
        _reportPlayerMuteKey = reportPlayerMuteKey ?? ((_, _) => { });
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
                _lastHotkeySignature = null;
                _hotkeys = null;
                _bindingReleasePending = true;
                ResetActionEdges();
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
                var configuration = _getConfiguration();
                var hotkeys = HotkeyConfigurationSnapshot.Create(configuration);
                if (!string.Equals(
                        hotkeys.Signature,
                        _lastHotkeySignature,
                        StringComparison.Ordinal))
                {
                    if (!_backend.SetHotkeyBindings(hotkeys.NativeBindings))
                        throw new InvalidOperationException("The DirectInput broker rejected its hotkey bindings.");
                    _hotkeys = hotkeys;
                    _lastHotkeySignature = hotkeys.Signature;
                    _bindingReleasePending = true;
                    ResetActionEdges();
                }
                else
                {
                    hotkeys = _hotkeys ?? hotkeys;
                }

                var officialActionsAvailable = settingsAvailable && configuration.EnableOverlay;
                var customActionsAvailable = officialActionsAvailable;
                var quickActionsPanelAvailable = officialActionsAvailable || customActionsAvailable;
                var quickActionsAvailable =
                    (quickActionsPanelAvailable &&
                     (hotkeys.QuickActionsKeyboard.IsBound || hotkeys.QuickActionsController.IsBound)) ||
                    hotkeys.QuickActions.Any(action =>
                        action.Enabled &&
                        action.IsConfigured &&
                        (action.Kind == QuickActionKind.CustomText
                            ? customActionsAvailable
                            : officialActionsAvailable) &&
                        (action.Keyboard.IsBound || action.Controller.IsBound));
                var playerMuteAvailable = canActivate &&
                    (hotkeys.Player2MuteKeyboard.IsBound || hotkeys.Player2MuteController.IsBound ||
                     hotkeys.Player3MuteKeyboard.IsBound || hotkeys.Player3MuteController.IsBound ||
                     hotkeys.Player4MuteKeyboard.IsBound || hotkeys.Player4MuteController.IsBound);
                var policy = BuildPolicy(
                    captureKeyboard,
                    captureMouse,
                    canActivate,
                    settingsAvailable,
                    voicePushToTalkEnabled,
                    quickActionsAvailable || playerMuteAvailable);

                if (policy != _lastPolicy)
                {
                    if (!_backend.SetPolicy(policy))
                        throw new InvalidOperationException("The DirectInput broker rejected its input policy.");
                    _lastPolicy = policy;
                }

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
                var keyboardSnapshot = snapshot;
                keyboardSnapshot.ControllerButtons = ControllerButtons.None;
                var changed = snapshot.Sequence != _lastSequence;
                if (changed)
                {
                    _lastSequence = snapshot.Sequence;
                    _observeInputSnapshot(snapshot);
                }

                if (_bindingReleasePending)
                {
                    _voicePushToTalkGate.Report(false);
                    if (!snapshot.HasAnyKeyboardKey)
                    {
                        _bindingReleasePending = false;
                        ResetActionEdges();
                    }
                    return;
                }

                var settingsDown = settingsAvailable && HotkeyConfigurationSnapshot.IsPressed(
                    keyboardSnapshot,
                    hotkeys.SettingsKeyboard,
                    hotkeys.SettingsController);
                settingsDown |= settingsAvailable &&
                    HotkeyConfigurationSnapshot.IsKeyboardPressed(
                        keyboardSnapshot,
                        HotkeyConfigurationSnapshot.EmergencySettingsKeyboard);
                if (settingsDown != _settingsWasDown)
                    _reportSettingsMenuKey(settingsDown);
                _settingsWasDown = settingsDown;

                var pushToTalkDown = voicePushToTalkEnabled &&
                    !captureKeyboard &&
                    HotkeyConfigurationSnapshot.IsPressed(
                        keyboardSnapshot,
                        hotkeys.PushToTalkKeyboard,
                        hotkeys.PushToTalkController);
                _voicePushToTalkGate.Report(pushToTalkDown);

                if (!changed)
                    return;

                var activationDown = canActivate &&
                    !captureKeyboard &&
                    HotkeyConfigurationSnapshot.IsPressed(
                        keyboardSnapshot,
                        hotkeys.OpenChatKeyboard,
                        hotkeys.OpenChatController);
                if (activationDown && !_activationWasDown)
                    _tryActivate();
                _activationWasDown = activationDown;

                var quickActionsDown = quickActionsPanelAvailable &&
                    !captureKeyboard &&
                    HotkeyConfigurationSnapshot.IsPressed(
                        keyboardSnapshot,
                        hotkeys.QuickActionsKeyboard,
                        hotkeys.QuickActionsController);
                if (quickActionsDown != _quickActionsWasDown)
                    _reportQuickActionsMenuKey(quickActionsDown);
                _quickActionsWasDown = quickActionsDown;

                ProcessQuickActionBindings(
                    keyboardSnapshot,
                    hotkeys,
                    officialActionsAvailable && !captureKeyboard,
                    customActionsAvailable && !captureKeyboard);
                ProcessPlayerMuteBindings(
                    keyboardSnapshot,
                    hotkeys,
                    canActivate && !captureKeyboard);
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
            ResetActionEdges();
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
                _bindingReleasePending = true;
                ResetActionEdges();
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
        bool voicePushToTalkEnabled,
        bool quickActionsAvailable)
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
        if (quickActionsAvailable)
            policy |= DirectInputBrokerPolicy.SuppressQuickActions;
        return policy;
    }

    private void ProcessQuickActionBindings(
        in DirectInputBrokerSnapshot snapshot,
        HotkeyConfigurationSnapshot hotkeys,
        bool officialActionsAvailable,
        bool customActionsAvailable)
    {
        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in hotkeys.QuickActions)
        {
            if (string.IsNullOrWhiteSpace(action.Id))
                continue;
            liveIds.Add(action.Id);
            var actionAvailable = action.Kind == QuickActionKind.CustomText
                ? customActionsAvailable
                : officialActionsAvailable;
            var down = actionAvailable &&
                action.Enabled &&
                action.IsConfigured &&
                HotkeyConfigurationSnapshot.IsPressed(
                    snapshot,
                    action.Keyboard,
                    action.Controller);
            _quickActionWasDown.TryGetValue(action.Id, out var wasDown);
            if (down != wasDown)
                _reportQuickActionKey(action.Id, down);
            _quickActionWasDown[action.Id] = down;
        }

        foreach (var staleId in _quickActionWasDown.Keys.Where(id => !liveIds.Contains(id)).ToArray())
            _quickActionWasDown.Remove(staleId);
    }

    private void ProcessPlayerMuteBindings(
        in DirectInputBrokerSnapshot snapshot,
        HotkeyConfigurationSnapshot hotkeys,
        bool available)
    {
        for (var player = 2; player <= 4; player++)
        {
            var (keyboard, controller) = player switch
            {
                2 => (hotkeys.Player2MuteKeyboard, hotkeys.Player2MuteController),
                3 => (hotkeys.Player3MuteKeyboard, hotkeys.Player3MuteController),
                _ => (hotkeys.Player4MuteKeyboard, hotkeys.Player4MuteController),
            };
            var down = available && HotkeyConfigurationSnapshot.IsPressed(
                snapshot,
                keyboard,
                controller);
            var index = player - 2;
            if (down != _playerMuteWasDown[index])
                _reportPlayerMuteKey(player, down);
            _playerMuteWasDown[index] = down;
        }
    }

    private void ResetActionEdges()
    {
        if (_settingsWasDown)
            _reportSettingsMenuKey(false);
        if (_quickActionsWasDown)
            _reportQuickActionsMenuKey(false);
        _activationWasDown = false;
        _settingsWasDown = false;
        _quickActionsWasDown = false;
        foreach (var pressedAction in _quickActionWasDown.Where(item => item.Value))
            _reportQuickActionKey(pressedAction.Key, false);
        _quickActionWasDown.Clear();
        for (var index = 0; index < _playerMuteWasDown.Length; index++)
        {
            if (_playerMuteWasDown[index])
                _reportPlayerMuteKey(index + 2, false);
            _playerMuteWasDown[index] = false;
        }
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
            $"mouse={HasFlag(readiness, DirectInputBrokerReadiness.Mouse)}, " +
            $"xinput-observer={HasFlag(readiness, DirectInputBrokerReadiness.Controller)}, " +
            "controllers=pass-through.");
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
        ResetActionEdges();
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
