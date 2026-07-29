using System.Runtime.InteropServices;
using System.Text;
using System.Numerics;
using DearImguiSharp;
using GBFR.ChatOverlay.Audio;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Native;
using GBFR.OverlayHub.Contracts;

namespace GBFR.ChatOverlay.Overlay;

/// <summary>
/// Chat frontend peer. It never installs Present/WndProc hooks and renders only
/// through the neutral process-local Overlay Broker.
/// </summary>
public sealed class ChatOverlayPeer : IGbfrOverlayGraphicsClient, IDisposable
{
    private const int VirtualKeyBackspace = 0x08;
    private const int VirtualKeyEscape = 0x1B;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyAlt = 0x12;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmKillFocus = 0x0008;
    private const uint WmActivate = 0x0006;
    private const uint WmActivateApp = 0x001C;
    private const int InputBufferSize = 2_048;
    private const float ComposerReservedHeight = 58.0f;
    private const int WindowHotkeySource = 1 << 0;
    private const int NativeHotkeySource = 1 << 1;
    private const int ControllerHotkeySource = 1 << 2;

    private readonly ChatSession _session;
    private readonly Func<Config> _getConfiguration;
    private readonly Func<bool> _isOnlineRoomActive;
    private readonly Action _onOnlineRoomUnavailable;
    private readonly Func<PartyVoiceUiStatus> _getVoiceUiStatus;
    private readonly Func<float, float, float, float, IReadOnlyList<PartyHudAnchor>> _getPartyHudAnchors;
    private readonly Func<IReadOnlyList<PartyPlayerMuteSlotStatus>> _getPlayerMuteSlots;
    private readonly Func<int, bool, PartyPlayerMuteOperationResult> _setPlayerMuted;
    private readonly Func<QuickActionKind, int, ChatSendResult> _sendOfficialQuickAction;
    private readonly InGameAudioSettingsController? _audioSettings;
    private readonly Action<Action<Config>> _updateConfiguration;
    private readonly Action<bool> _setLocalSelfTestRequested;
    private readonly Func<bool> _canUseVoicePushToTalk;
    private readonly Action<bool> _setVoicePushToTalkPressed;
    private readonly Action _forceReleaseVoiceInputs;
    private readonly Action<string> _requestOverlayBrokerRecovery;
    private readonly Action<string> _log;
    private readonly XInputControllerPoller _controllerInputPoller = new();
    private readonly MouseInteractionGate _mouseInteractionGate = new();
    private readonly byte[] _inputBuffer = new byte[InputBufferSize];
    private readonly byte[] _quickActionNameBuffer = new byte[256];
    private readonly byte[] _quickActionTextBuffer = new byte[InputBufferSize];
    private readonly Dictionary<string, int> _quickActionKeyDown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _controllerQuickActionWasDown = new(StringComparer.Ordinal);
    private int _globalMuteKeyDown;
    private bool _controllerGlobalMuteWasDown;
    private readonly object _bindingCaptureSync = new();
    private ImeCandidateSnapshot? _imeCandidateSnapshot;
    private int _openRequested;
    private int _settingsToggleRequested;
    private int _settingsToggleKeyDown;
    private int _quickActionsToggleRequested;
    private int _quickActionsToggleKeyDown;
    private int _quickActionsPanelOpen;
    private int _settingsMenuOpen;
    private int _suspended;
    private int _captureKeyboard;
    private int _swallowActivationKeyUntilRelease;
    private int _imeCompatibilityLogged;
    private int _imeCandidateUiLogged;
    private int _imeDecodeFailureLogged;
    private int _imeCandidateCaptureLogged;
    private int _imeCandidateReadFailureLogged;
    private int _imeCompositionWithoutCandidatesLogged;
    private int _imeCompositionObserved;
    private int _imeCandidateCapturedInComposition;
    private int _platformImeBridgeLogged;
    private int _onlineRoomGateFailureLogged;
    private int _onlineRoomWasInactive = 1;
    private int _releaseCaptureFrames;
    private int _pendingAnsiLeadByte = -1;
    private nint _windowHandle;
    private bool _focusInputNextFrame;
    private bool _windowOpen = true;
    private bool _settingsWindowOpen = true;
    private bool _initialized;
    private IGbfrOverlayRegistration? _registration;
    private ChatOverlayRect? _editedChatRect;
    private float _editWorkX;
    private float _editWorkY;
    private float _editWorkWidth;
    private float _editWorkHeight;
    private long _lastRenderedSequence;
    private string? _statusText;
    private string? _selectedQuickActionId;
    private BindingCaptureRequest? _bindingCapture;
    private PendingBindingConflict? _bindingConflictPending;
    private KeyboardBinding _keyboardCaptureCandidate;
    private ControllerButtons _controllerCaptureCandidate;
    private int _latestControllerButtonsMask;
    private int _controllerInputAvailable;
    private int _nativeControllerInputAvailable;
    private int _managedControllerApiAvailable;
    private int _managedControllerConnected;
    private ulong _lastManagedControllerSequence = ulong.MaxValue;
    private bool _controllerSettingsWasDown;
    private bool _controllerOpenChatWasDown;
    private bool _controllerPushToTalkWasDown;
    private bool _controllerQuickActionsWasDown;
    private bool _managedControllerReleasePending = true;
    private bool _captureWaitingForRelease;
    private string? _captureStatusText;
    private string? _playerMuteStatusText;

    internal ChatOverlayPeer(
        ChatSession session,
        Func<Config> getConfiguration,
        Func<bool> isOnlineRoomActive,
        Action onOnlineRoomUnavailable,
        Func<PartyVoiceUiStatus> getVoiceUiStatus,
        Func<float, float, float, float, IReadOnlyList<PartyHudAnchor>> getPartyHudAnchors,
        Func<IReadOnlyList<PartyPlayerMuteSlotStatus>> getPlayerMuteSlots,
        Func<int, bool, PartyPlayerMuteOperationResult> setPlayerMuted,
        Func<QuickActionKind, int, ChatSendResult> sendOfficialQuickAction,
        InGameAudioSettingsController? audioSettings,
        Action<Action<Config>> updateConfiguration,
        Action<bool> setLocalSelfTestRequested,
        Func<bool> canUseVoicePushToTalk,
        Action<bool> setVoicePushToTalkPressed,
        Action forceReleaseVoiceInputs,
        Action<string> requestOverlayBrokerRecovery,
        Action<string> log)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _getConfiguration = getConfiguration ?? throw new ArgumentNullException(nameof(getConfiguration));
        _isOnlineRoomActive = isOnlineRoomActive ?? throw new ArgumentNullException(nameof(isOnlineRoomActive));
        _onOnlineRoomUnavailable = onOnlineRoomUnavailable ??
            throw new ArgumentNullException(nameof(onOnlineRoomUnavailable));
        _getVoiceUiStatus = getVoiceUiStatus ?? throw new ArgumentNullException(nameof(getVoiceUiStatus));
        _getPartyHudAnchors = getPartyHudAnchors ?? throw new ArgumentNullException(nameof(getPartyHudAnchors));
        _getPlayerMuteSlots = getPlayerMuteSlots ?? throw new ArgumentNullException(nameof(getPlayerMuteSlots));
        _setPlayerMuted = setPlayerMuted ?? throw new ArgumentNullException(nameof(setPlayerMuted));
        _sendOfficialQuickAction = sendOfficialQuickAction ??
            throw new ArgumentNullException(nameof(sendOfficialQuickAction));
        _audioSettings = audioSettings;
        _updateConfiguration = updateConfiguration ?? throw new ArgumentNullException(nameof(updateConfiguration));
        _setLocalSelfTestRequested = setLocalSelfTestRequested ??
            throw new ArgumentNullException(nameof(setLocalSelfTestRequested));
        _canUseVoicePushToTalk = canUseVoicePushToTalk ??
            throw new ArgumentNullException(nameof(canUseVoicePushToTalk));
        _setVoicePushToTalkPressed = setVoicePushToTalkPressed ??
            throw new ArgumentNullException(nameof(setVoicePushToTalkPressed));
        _forceReleaseVoiceInputs = forceReleaseVoiceInputs ??
            throw new ArgumentNullException(nameof(forceReleaseVoiceInputs));
        _requestOverlayBrokerRecovery = requestOverlayBrokerRecovery ??
            throw new ArgumentNullException(nameof(requestOverlayBrokerRecovery));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsInitialized => Volatile.Read(ref _initialized);

    public bool IsSuspended => Volatile.Read(ref _suspended) != 0;

    internal bool IsQuickActionsPanelOpen => Volatile.Read(ref _quickActionsPanelOpen) != 0;

    public string ModId => "GBFR.ChatOverlay";

    private UiLanguage CurrentLanguage => _getConfiguration().InterfaceLanguage;

    private string T(string chinese, string english) =>
        UiLocalization.Select(CurrentLanguage, chinese, english);

    private string LocalizeLegacyText(string? value) =>
        UiLocalization.FromLegacyBilingual(CurrentLanguage, value);

    public bool TryRequestOpen()
    {
        if (!CanRequestOpen())
            return false;

        Interlocked.Exchange(ref _captureKeyboard, 1);
        Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
        ClearImeCandidateSnapshot();
        Interlocked.Exchange(ref _openRequested, 1);
        UpdateInputCapture();
        return true;
    }

    public bool CanRequestOpen() =>
        Volatile.Read(ref _initialized) &&
        Volatile.Read(ref _suspended) == 0 &&
        _getConfiguration().EnableOverlay &&
        IsOnlineRoomActive() &&
        !_session.Composer.IsOpen;

    public bool ShouldCaptureKeyboard() =>
        Volatile.Read(ref _initialized) &&
        (Volatile.Read(ref _settingsMenuOpen) != 0 ||
         Volatile.Read(ref _quickActionsPanelOpen) != 0 ||
         (_getConfiguration().EnableOverlay &&
          IsOnlineRoomActive() &&
          Volatile.Read(ref _captureKeyboard) != 0));

    public void ObserveSettingsMenuKey(bool pressed) =>
        ObserveSettingsMenuKey(pressed, NativeHotkeySource);

    private void ObserveSettingsMenuKey(bool pressed, int source)
    {
        if (!Volatile.Read(ref _initialized) || Volatile.Read(ref _suspended) != 0)
            return;
        var previous = UpdateSourceMask(ref _settingsToggleKeyDown, source, pressed);
        if (pressed && previous == 0)
        {
            Interlocked.Increment(ref _settingsToggleRequested);
            _registration?.SetInputCapture(
                OverlayInputDevices.Keyboard |
                OverlayInputDevices.Mouse |
                OverlayInputDevices.Text);
        }
    }

    public void ObserveQuickActionsMenuKey(bool pressed) =>
        ObserveQuickActionsMenuKey(pressed, NativeHotkeySource);

    private void ObserveQuickActionsMenuKey(bool pressed, int source)
    {
        if (!Volatile.Read(ref _initialized) || Volatile.Read(ref _suspended) != 0)
            return;
        var previous = UpdateSourceMask(ref _quickActionsToggleKeyDown, source, pressed);
        if (pressed && previous == 0)
            Interlocked.Increment(ref _quickActionsToggleRequested);
    }

    public void ObserveQuickActionKey(string actionId, bool pressed) =>
        ObserveQuickActionKey(actionId, pressed, NativeHotkeySource);

    private void ObserveQuickActionKey(string actionId, bool pressed, int source)
    {
        if (string.IsNullOrWhiteSpace(actionId))
            return;
        var shouldSend = false;
        lock (_quickActionKeyDown)
        {
            _quickActionKeyDown.TryGetValue(actionId, out var previous);
            var current = pressed ? previous | source : previous & ~source;
            _quickActionKeyDown[actionId] = current;
            if (pressed && previous == 0)
                shouldSend = true;
        }
        if (shouldSend)
            SendQuickAction(actionId);
    }

    public void ObserveGlobalMuteKey(bool pressed) =>
        ObserveGlobalMuteKey(pressed, NativeHotkeySource);

    private void ObserveGlobalMuteKey(bool pressed, int source)
    {
        var previous = UpdateSourceMask(ref _globalMuteKeyDown, source, pressed);
        if (pressed && previous == 0)
            ToggleGlobalMute();
    }

    private void ToggleGlobalMute()
    {
        try
        {
            var available = _getPlayerMuteSlots()
                .Where(status =>
                    status.PlayerNumber is >= 2 and <= 4 &&
                    status.IsAvailable)
                .GroupBy(status => status.PlayerNumber)
                .Select(group => group.First())
                .OrderBy(status => status.PlayerNumber)
                .ToArray();
            if (available.Length == 0)
            {
                var unavailable = T("玩家状态不可用。", "Player status unavailable.");
                _playerMuteStatusText = unavailable;
                _statusText = unavailable;
                LogSafely("Global mute hotkey ignored: no available player slots.");
                return;
            }

            var targetMuted = available.Any(status => !status.IsMuted);
            var changed = 0;
            var failed = 0;
            foreach (var status in available)
            {
                if (status.IsMuted == targetMuted)
                    continue;
                changed++;
                var operation = _setPlayerMuted(status.PlayerNumber, targetMuted);
                if (!operation.Succeeded)
                    failed++;
            }

            var message = failed == 0
                ? targetMuted
                    ? T("已全局禁言。", "All players muted.")
                    : T("已取消全局禁言。", "All players unmuted.")
                : T("部分玩家操作失败。", "Some player mute changes failed.");
            _playerMuteStatusText = message;
            _statusText = message;
            LogSafely(
                $"Global mute hotkey completed: target_muted={targetMuted}, " +
                $"available={available.Length}, changed={changed}, failed={failed}.");
        }
        catch (Exception exception)
        {
            var message = T("全局禁言失败。", "Could not toggle global mute.");
            _playerMuteStatusText = message;
            _statusText = message;
            LogSafely(
                $"Global mute hotkey recovered from an exception: " +
                $"{exception.GetType().Name}: {exception.Message}.");
        }
    }

    private void TogglePlayerMute(int playerNumber)
    {
        try
        {
            var status = _getPlayerMuteSlots().FirstOrDefault(candidate =>
                candidate.PlayerNumber == playerNumber);
            if (status.PlayerNumber != playerNumber || !status.IsAvailable)
            {
                var message = status.PlayerNumber == playerNumber &&
                              !string.IsNullOrWhiteSpace(status.Detail)
                    ? status.Detail
                    : T("玩家状态不可用。", "Player status unavailable.");
                _playerMuteStatusText = message;
                _statusText = LocalizeLegacyText(message);
                LogSafely($"Player {playerNumber} mute hotkey ignored: status unavailable.");
                return;
            }

            var operation = _setPlayerMuted(playerNumber, !status.IsMuted);
            _playerMuteStatusText = operation.Message;
            _statusText = LocalizeLegacyText(operation.Message);
            LogSafely(
                $"Player {playerNumber} mute hotkey completed: target_muted={!status.IsMuted}, " +
                $"succeeded={operation.Succeeded}.");
        }
        catch (Exception exception)
        {
            var message = T("切换禁言失败。", "Could not toggle player mute.");
            _playerMuteStatusText = message;
            _statusText = message;
            LogSafely(
                $"Player {playerNumber} mute hotkey recovered from an exception: " +
                $"{exception.GetType().Name}: {exception.Message}.");
        }
    }

    internal void ObserveNativeInputSnapshot(DirectInputBrokerSnapshot snapshot)
    {
        Volatile.Write(ref _latestControllerButtonsMask, (int)snapshot.ControllerButtons);
        Volatile.Write(
            ref _nativeControllerInputAvailable,
            (snapshot.Readiness & DirectInputBrokerReadiness.Controller) != 0 ? 1 : 0);
        UpdateControllerInputAvailability();

        if (Volatile.Read(ref _nativeControllerInputAvailable) != 0 &&
            Volatile.Read(ref _managedControllerApiAvailable) == 0)
            ObserveControllerBindingCapture(snapshot.ControllerButtons);
    }

    private void PollManagedControllerInput()
    {
        var snapshot = _controllerInputPoller.Poll();
        Volatile.Write(ref _managedControllerApiAvailable, snapshot.ApiAvailable ? 1 : 0);
        Volatile.Write(ref _managedControllerConnected, snapshot.IsConnected ? 1 : 0);
        UpdateControllerInputAvailability();

        if (!snapshot.ApiAvailable)
        {
            ResetManagedControllerHotkeys();
            return;
        }
        if (snapshot.Sequence == _lastManagedControllerSequence)
            return;

        _lastManagedControllerSequence = snapshot.Sequence;
        Volatile.Write(ref _latestControllerButtonsMask, (int)snapshot.Buttons);
        var captureWasActive = IsControllerBindingCaptureActive();
        ObserveControllerBindingCapture(snapshot.Buttons);
        if (captureWasActive || IsControllerBindingCaptureActive())
        {
            ResetManagedControllerHotkeys();
            return;
        }
        if (_managedControllerReleasePending)
        {
            ResetManagedControllerHotkeys();
            if (snapshot.Buttons == ControllerButtons.None)
                _managedControllerReleasePending = false;
            return;
        }
        ProcessManagedControllerHotkeys(snapshot.Buttons);
    }

    private void ObserveControllerBindingCapture(ControllerButtons buttons)
    {
        lock (_bindingCaptureSync)
        {
            if (_bindingCapture is not { Device: BindingCaptureDevice.Controller })
                return;
            if (_captureWaitingForRelease)
            {
                if (buttons == ControllerButtons.None)
                {
                    _captureWaitingForRelease = false;
                    _captureStatusText = T(
                        "请按下一个或两个手柄按键，然后松开确认。",
                        "Press one or two controller buttons, then release to confirm.");
                }
                return;
            }
            if (_controllerCaptureCandidate == ControllerButtons.None)
            {
                if (buttons == ControllerButtons.None)
                    return;
                if (BitOperations.PopCount((uint)buttons) > 2)
                {
                    _captureStatusText = T(
                        "最多绑定两个手柄按键。",
                        "Controller bindings are limited to two buttons.");
                    return;
                }
                _controllerCaptureCandidate = buttons;
                _captureStatusText = T(
                    $"已捕获 {new ControllerBinding(buttons).Format()}，松开确认。",
                    $"Captured {new ControllerBinding(buttons).Format()}. Release to confirm.");
                return;
            }
            if (buttons == ControllerButtons.None)
                CompleteBindingCapture(new ControllerBinding(_controllerCaptureCandidate).Format());
        }
    }

    private bool IsControllerBindingCaptureActive()
    {
        lock (_bindingCaptureSync)
            return _bindingCapture is { Device: BindingCaptureDevice.Controller };
    }

    private void ProcessManagedControllerHotkeys(ControllerButtons buttons)
    {
        var configuration = _getConfiguration();
        var settingsDown = IsControllerBindingPressed(
            configuration.SettingsMenuControllerBinding,
            buttons);
        if (settingsDown != _controllerSettingsWasDown)
            ObserveSettingsMenuKey(settingsDown, ControllerHotkeySource);
        _controllerSettingsWasDown = settingsDown;

        var inputCaptured = ShouldCaptureKeyboard();
        var openChatDown = !inputCaptured &&
            IsControllerBindingPressed(configuration.OpenChatControllerBinding, buttons);
        if (openChatDown && !_controllerOpenChatWasDown)
            TryRequestOpen();
        _controllerOpenChatWasDown = openChatDown;

        var pushToTalkDown = !inputCaptured &&
            _canUseVoicePushToTalk() &&
            IsControllerBindingPressed(configuration.PushToTalkControllerBinding, buttons);
        if (pushToTalkDown != _controllerPushToTalkWasDown)
            _setVoicePushToTalkPressed(pushToTalkDown);
        _controllerPushToTalkWasDown = pushToTalkDown;

        var officialActionsAvailable = configuration.EnableOverlay;
        var customActionsAvailable = configuration.EnableOverlay;
        var quickActionsPanelAvailable = officialActionsAvailable || customActionsAvailable;
        var quickActionsDown = !inputCaptured &&
            quickActionsPanelAvailable &&
            IsControllerBindingPressed(configuration.QuickActionsControllerBinding, buttons);
        if (quickActionsDown != _controllerQuickActionsWasDown)
            ObserveQuickActionsMenuKey(quickActionsDown, ControllerHotkeySource);
        _controllerQuickActionsWasDown = quickActionsDown;

        var globalMuteDown = !inputCaptured &&
            IsOnlineRoomActive() &&
            IsControllerBindingPressed(configuration.GlobalMuteControllerBinding, buttons);
        if (globalMuteDown != _controllerGlobalMuteWasDown)
            ObserveGlobalMuteKey(globalMuteDown, ControllerHotkeySource);
        _controllerGlobalMuteWasDown = globalMuteDown;

        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in configuration.QuickActions ?? [])
        {
            if (string.IsNullOrWhiteSpace(action.Id))
                continue;
            liveIds.Add(action.Id);
            var available = action.Kind == QuickActionKind.CustomText
                ? customActionsAvailable
                : officialActionsAvailable;
            var down = !inputCaptured &&
                available &&
                action.Enabled &&
                action.IsConfigured &&
                IsControllerBindingPressed(action.ControllerBinding, buttons);
            _controllerQuickActionWasDown.TryGetValue(action.Id, out var wasDown);
            if (down != wasDown)
                ObserveQuickActionKey(action.Id, down, ControllerHotkeySource);
            _controllerQuickActionWasDown[action.Id] = down;
        }
        foreach (var staleId in _controllerQuickActionWasDown.Keys
                     .Where(id => !liveIds.Contains(id))
                     .ToArray())
        {
            if (_controllerQuickActionWasDown[staleId])
                ObserveQuickActionKey(staleId, false, ControllerHotkeySource);
            _controllerQuickActionWasDown.Remove(staleId);
        }
    }

    private void ResetManagedControllerHotkeys()
    {
        if (_controllerSettingsWasDown)
            ObserveSettingsMenuKey(false, ControllerHotkeySource);
        if (_controllerPushToTalkWasDown)
            _setVoicePushToTalkPressed(false);
        if (_controllerQuickActionsWasDown)
            ObserveQuickActionsMenuKey(false, ControllerHotkeySource);
        foreach (var action in _controllerQuickActionWasDown.Where(item => item.Value))
            ObserveQuickActionKey(action.Key, false, ControllerHotkeySource);
        if (_controllerGlobalMuteWasDown)
            ObserveGlobalMuteKey(false, ControllerHotkeySource);
        _controllerSettingsWasDown = false;
        _controllerOpenChatWasDown = false;
        _controllerPushToTalkWasDown = false;
        _controllerQuickActionsWasDown = false;
        _controllerGlobalMuteWasDown = false;
        _controllerQuickActionWasDown.Clear();
    }

    private static bool IsControllerBindingPressed(
        string? value,
        ControllerButtons buttons) =>
        ControllerBinding.TryParse(value, out var binding) && binding.IsPressed(buttons);

    private void UpdateControllerInputAvailability() =>
        Volatile.Write(
            ref _controllerInputAvailable,
            Volatile.Read(ref _managedControllerConnected) != 0 ||
            (Volatile.Read(ref _managedControllerApiAvailable) == 0 &&
             Volatile.Read(ref _nativeControllerInputAvailable) != 0)
                ? 1
                : 0);

    private static int UpdateSourceMask(ref int destination, int source, bool pressed)
    {
        while (true)
        {
            var previous = Volatile.Read(ref destination);
            var current = pressed ? previous | source : previous & ~source;
            if (Interlocked.CompareExchange(ref destination, current, previous) == previous)
                return previous;
        }
    }

    public bool WantsRender
    {
        get
        {
            if (!Volatile.Read(ref _initialized) || Volatile.Read(ref _suspended) != 0)
                return false;
            var configuration = _getConfiguration();
            return Volatile.Read(ref _settingsMenuOpen) != 0 ||
                   Volatile.Read(ref _settingsToggleRequested) != 0 ||
                   Volatile.Read(ref _quickActionsPanelOpen) != 0 ||
                   Volatile.Read(ref _quickActionsToggleRequested) != 0 ||
                   Volatile.Read(ref _openRequested) != 0 ||
                   _session.Composer.IsOpen ||
                   configuration.EffectiveShowAllVoiceIndicatorSlots ||
                   (configuration.EnableOverlay && IsOnlineRoomActive());
        }
    }

    public void Tick()
    {
        if (!Volatile.Read(ref _initialized) || Volatile.Read(ref _suspended) != 0)
            return;
        PollManagedControllerInput();
        _session.DrainIncoming();
        var configuration = _getConfiguration();
        if (!configuration.EnableImeCandidateFallback)
            ClearImeCandidateSnapshot();
        var onlineRoomActive = IsOnlineRoomActive();
        var previousOnlineRoomInactive = Interlocked.Exchange(
            ref _onlineRoomWasInactive,
            onlineRoomActive ? 0 : 1);
        if (!onlineRoomActive && previousOnlineRoomInactive == 0)
        {
            _playerMuteStatusText = null;
            NotifyOnlineRoomUnavailable();
            ResetChatInteractionState();
        }
        else if (onlineRoomActive && previousOnlineRoomInactive != 0)
        {
            _playerMuteStatusText = null;
            LogSafely(
                "Relink online Party room became active; configured chat, voice and quick-action hotkeys are enabled. " +
                "The configured settings binding remains available in every scene.");
        }
    }

    public bool BindGraphics(OverlayGraphicsBinding binding)
    {
        var bound = HostedImguiBinding.TryBind(binding, LogSafely);
        Volatile.Write(ref _initialized, bound);
        return bound;
    }

    internal void AttachRegistration(IGbfrOverlayRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (Interlocked.CompareExchange(ref _registration, registration, null) is not null)
            throw new InvalidOperationException("The Chat peer is already registered with an Overlay Broker.");
        if (!registration.SetEnabled(true))
            throw new InvalidOperationException("The Overlay Broker rejected Chat peer activation.");
        UpdateInputCapture();
    }

    public void Suspend()
    {
        Interlocked.Exchange(ref _suspended, 1);
        SetSettingsMenuOpen(false);
        _session.Composer.Cancel();
        Interlocked.Exchange(ref _openRequested, 0);
        Interlocked.Exchange(ref _settingsToggleRequested, 0);
        Interlocked.Exchange(ref _settingsToggleKeyDown, 0);
        Interlocked.Exchange(ref _quickActionsToggleRequested, 0);
        Interlocked.Exchange(ref _quickActionsToggleKeyDown, 0);
        Interlocked.Exchange(ref _quickActionsPanelOpen, 0);
        lock (_quickActionKeyDown)
            _quickActionKeyDown.Clear();
        ResetManagedControllerHotkeys();
        Interlocked.Exchange(ref _globalMuteKeyDown, 0);
        CancelBindingCapture();
        lock (_bindingCaptureSync)
            _bindingConflictPending = null;
        Interlocked.Exchange(ref _captureKeyboard, 0);
        Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 0);
        Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
        ClearImeCandidateSnapshot();
        _releaseCaptureFrames = 0;
        _focusInputNextFrame = false;
        _statusText = null;
        _playerMuteStatusText = null;
        _registration?.SetInputCapture(OverlayInputDevices.None);
        _registration?.SetEnabled(false);
    }

    public void Resume()
    {
        Interlocked.Exchange(ref _suspended, 0);
        _registration?.SetEnabled(true);
        UpdateInputCapture();
    }

    public void Render()
    {
        try
        {
            if (!HostedImguiBinding.EnsureCurrentContext())
                throw new InvalidOperationException("The Chat peer lost the Broker ImGui context.");
            var configuration = _getConfiguration();
            var onlineRoomActive = IsOnlineRoomActive();
            var voiceUiStatus = _getVoiceUiStatus();
            if (onlineRoomActive || configuration.EffectiveShowAllVoiceIndicatorSlots)
                VoiceIndicatorOverlay.Draw(configuration, voiceUiStatus, _getPartyHudAnchors);

            if ((Interlocked.Exchange(ref _settingsToggleRequested, 0) & 1) != 0)
                SetSettingsMenuOpen(Volatile.Read(ref _settingsMenuOpen) == 0);
            if ((Interlocked.Exchange(ref _quickActionsToggleRequested, 0) & 1) != 0 &&
                configuration.EnableOverlay &&
                Volatile.Read(ref _settingsMenuOpen) == 0)
            {
                var open = Volatile.Read(ref _quickActionsPanelOpen) == 0;
                SetQuickActionsPanelOpen(open);
            }
            var settingsOpen = Volatile.Read(ref _settingsMenuOpen) != 0;
            if (settingsOpen &&
                !HasActiveBindingCapture() &&
                ImGui.IsKeyPressed((int)ImGuiKey.Escape, false))
            {
                SetSettingsMenuOpen(false);
                settingsOpen = false;
            }

            var quickActionsOpen = Volatile.Read(ref _quickActionsPanelOpen) != 0;
            if (quickActionsOpen && (!configuration.EnableOverlay || settingsOpen))
            {
                SetQuickActionsPanelOpen(false);
                quickActionsOpen = false;
            }
            else if (quickActionsOpen && ImGui.IsKeyPressed((int)ImGuiKey.Escape, false))
            {
                SetQuickActionsPanelOpen(false);
                quickActionsOpen = false;
            }

            if (settingsOpen || (configuration.EnableOverlay && onlineRoomActive))
                BindPlatformImeWindow();

            if (settingsOpen)
            {
                _mouseInteractionGate.Observe(MouseButtonStateTracker.PressedButtons != 0);
                DrawSettingsMenu();
                if (!_settingsWindowOpen)
                {
                    SetSettingsMenuOpen(false);
                    settingsOpen = false;
                }
            }
            else if (quickActionsOpen)
            {
                DrawQuickActionsPanel(configuration);
            }
            if (!configuration.EnableOverlay || !onlineRoomActive)
            {
                ResetChatInteractionState();
                if (settingsOpen)
                    DrawChatWindow(configuration, openedThisFrame: false, voiceUiStatus, editMode: true);
                return;
            }

            if (settingsOpen)
            {
                ResetChatInteractionState();
                DrawChatWindow(configuration, openedThisFrame: false, voiceUiStatus, editMode: true);
                return;
            }

            if (_releaseCaptureFrames > 0 && --_releaseCaptureFrames == 0 && !_session.Composer.IsOpen)
            {
                Interlocked.Exchange(ref _captureKeyboard, 0);
                UpdateInputCapture();
            }

            var openedThisFrame = Interlocked.Exchange(ref _openRequested, 0) != 0;
            if (openedThisFrame)
            {
                _session.Composer.OpenKeyboard();
                SyncInputBufferFromDraft();
                _focusInputNextFrame = true;
                _statusText = _session.TransportStatusText;
            }

            if (_session.Composer.IsOpen && ImGui.IsKeyPressed((int)ImGuiKey.Escape, false))
            {
                _session.Composer.Cancel();
                _releaseCaptureFrames = 2;
                Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
                ClearImeCandidateSnapshot();
                _statusText = null;
            }

            DrawChatWindow(configuration, openedThisFrame, voiceUiStatus, editMode: false);
        }
        catch (Exception exception)
        {
            ResetInteractionState();
            LogSafely($"Render callback recovered from an exception: {exception}");
        }
    }

    private void DrawChatWindow(
        Config configuration,
        bool openedThisFrame,
        PartyVoiceUiStatus voiceUiStatus,
        bool editMode)
    {
        var viewport = ImGui.GetMainViewport();
        var workPosition = viewport.WorkPos;
        var workSize = viewport.WorkSize;
        var rect = editMode && _editedChatRect is { } edited
            ? edited
            : ChatOverlayLayout.Resolve(
                configuration,
                workPosition.X,
                workPosition.Y,
                workSize.X,
                workSize.Y);
        if (editMode)
        {
            _editedChatRect = rect;
            _editWorkX = workPosition.X;
            _editWorkY = workPosition.Y;
            _editWorkWidth = workSize.X;
            _editWorkHeight = workSize.Y;
        }

        using var position = CreateVector2(rect.X, rect.Y);
        using var size = CreateVector2(rect.Width, rect.Height);
        using var pivot = CreateVector2(0.0f, 0.0f);
        ImGui.SetNextWindowPos(position, (int)ImGuiCond.Always, pivot);
        ImGui.SetNextWindowSize(size, (int)ImGuiCond.Always);

        var composerOpen = _session.Composer.IsOpen;
        var opacity = editMode
            ? Math.Clamp((float)configuration.BackgroundOpacity, 0.25f, 1.0f)
            : composerOpen
            ? Math.Clamp((float)configuration.BackgroundOpacity, 0.0f, 1.0f)
            : Math.Clamp((float)configuration.BackgroundOpacity * 0.45f, 0.0f, 1.0f);
        ImGui.SetNextWindowBgAlpha(opacity);

        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoResize;
        if (!composerOpen && !editMode)
            flags |= ImGuiWindowFlags.NoInputs;

        var began = ImGui.Begin("GBFR Chat##GBFRChatOverlay", ref _windowOpen, (int)flags);
        try
        {
            if (!began)
                return;

            var imeCandidateText = composerOpen
                ? GetImeCandidateFallbackText(configuration.EnableImeCandidateFallback)
                : null;
            DrawVoiceStatus(voiceUiStatus);
            DrawHistory(composerOpen, imeCandidateText);
            if (composerOpen)
                DrawComposer(openedThisFrame, imeCandidateText);
            else if (!string.IsNullOrEmpty(_statusText))
                ImGui.TextWrapped(_statusText);
            if (editMode)
            {
                DrawChatEditHandles(
                    viewport,
                    ref rect,
                    workPosition.X,
                    workPosition.Y,
                    workSize.X,
                    workSize.Y);
                _editedChatRect = rect;
            }
        }
        finally
        {
            ImGui.End();
        }
    }

    private void DrawSettingsMenu()
    {
        var configuration = _getConfiguration();
        var viewport = ImGui.GetMainViewport();
        var workPosition = viewport.WorkPos;
        var workSize = viewport.WorkSize;
        var width = Math.Min(OverlayUiScale.Scale(900.0f), Math.Max(1.0f, workSize.X - OverlayUiScale.Scale(48.0f)));
        var height = Math.Min(OverlayUiScale.Scale(610.0f), Math.Max(1.0f, workSize.Y - OverlayUiScale.Scale(48.0f)));
        using var position = CreateVector2(
            workPosition.X + Math.Max(0.0f, (workSize.X - width) * 0.5f),
            workPosition.Y + Math.Max(0.0f, (workSize.Y - height) * 0.5f));
        using var size = CreateVector2(width, height);
        using var pivot = CreateVector2(0.0f, 0.0f);
        ImGui.SetNextWindowPos(position, (int)ImGuiCond.FirstUseEver, pivot);
        ImGui.SetNextWindowSize(size, (int)ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.96f);

        var flags = ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoResize;
        var settingsBinding = string.IsNullOrWhiteSpace(configuration.SettingsMenuKeyboardBinding)
            ? T("未绑定", "Unbound")
            : configuration.SettingsMenuKeyboardBinding;
        var began = ImGui.Begin(
            $"{T("GBFR 聊天与语音设置", "GBFR Chat & Voice Settings")}  [{settingsBinding}]##GBFRSettings",
            ref _settingsWindowOpen,
            (int)flags);
        try
        {
            if (!began)
                return;

            ImGui.BeginDisabled(!_mouseInteractionGate.IsArmed);
            try
            {
                if (ImGui.BeginTabBar("##GBFRSettingsTabs", 0))
                {
                    try
                    {
                        DrawSettingsTab(T("00 通用设置", "00 General"), DrawGeneralSettingsTab);
                        DrawSettingsTab(T("01 语音", "01 Voice"), DrawVoiceSettingsTab);
                        DrawSettingsTab(T("02 玩家禁言", "02 Player Mute"), DrawPlayerMuteSettingsTab);
                        DrawSettingsTab(T("03 快捷动作", "03 Quick Actions"), DrawQuickActionSettingsTab);
                    }
                    finally
                    {
                        ImGui.EndTabBar();
                    }
                }
            }
            finally
            {
                ImGui.EndDisabled();
            }
        }
        finally
        {
            ImGui.End();
        }
    }

    private static unsafe void DrawSettingsTab(string label, Action draw)
    {
        if (!ImGui.__Internal.BeginTabItem(label, null, 0))
            return;
        try
        {
            draw();
        }
        finally
        {
            ImGui.EndTabItem();
        }
    }

    private void DrawGeneralSettingsTab()
    {
        DrawLanguageSetting();
        ImGui.Separator();
        ImGui.Text(T("聊天框", "Chat Overlay"));
        DrawChatLayoutSettingsSection();
        ImGui.Separator();
        ImGui.Text(T("按键", "Hotkeys"));
        DrawHotkeySettingsSection();
    }

    private void DrawLanguageSetting()
    {
        var current = CurrentLanguage;
        if (!ImGui.BeginCombo(
                "语言 / Language##GBFRInterfaceLanguage",
                UiLocalization.LanguageName(current),
                0))
        {
            return;
        }

        try
        {
            using var zero = CreateVector2(0.0f, 0.0f);
            foreach (var language in new[] { UiLanguage.SimplifiedChinese, UiLanguage.English })
            {
                if (!ImGui.SelectableBool(
                        $"{UiLocalization.LanguageName(language)}##GBFRLanguage{language}",
                        language == current,
                        0,
                        zero))
                {
                    continue;
                }
                UpdateConfigurationSafely(configuration => configuration.InterfaceLanguage = language);
                RefreshSelectedQuickActionBuffers();
            }
        }
        finally
        {
            ImGui.EndCombo();
        }
    }

    private void DrawVoiceSettingsTab()
    {
        var configuration = _getConfiguration();
        DrawConfigurationCheckbox(
            T("启用语音聊天", "Enable Voice Chat"),
            configuration.EnableVoiceInput,
            (value, enabled) => value.EnableVoiceInput = enabled);
        DrawConfigurationCheckbox(
            T("显示队伍语音状态", "Show Party Voice Status"),
            configuration.EnableVoiceIndicators,
            (value, enabled) => value.EnableVoiceIndicators = enabled);
        ImGui.Separator();

        if (_audioSettings is null)
        {
            ImGui.TextWrapped(T("麦克风测试不可用。", "Microphone test unavailable."));
            return;
        }

        var snapshot = _audioSettings.GetSnapshot();
        if (DrawEndpointCombo(
                $"{T("麦克风", "Microphone")}##GBFRMicrophone",
                snapshot.MicrophoneDeviceId,
                snapshot.Microphones,
                out var microphoneId))
        {
            _setLocalSelfTestRequested(false);
            _audioSettings.SelectMicrophone(microphoneId);
        }

        if (DrawEndpointCombo(
                $"{T("扬声器", "Speaker")}##GBFRSpeaker",
                snapshot.SpeakerDeviceId,
                snapshot.Speakers,
                out var speakerId))
        {
            _setLocalSelfTestRequested(false);
            _audioSettings.SelectSpeaker(speakerId);
        }

        var inputGainPercent = snapshot.MicrophoneInputGain * 100.0f;
        if (ImGui.SliderFloat(
                $"{T("麦克风音量", "Microphone Volume")}##GBFRMicGain",
                ref inputGainPercent,
                0.0f,
                200.0f,
                "%.0f%%",
                0))
        {
            _audioSettings.SetMicrophoneInputGain(inputGainPercent / 100.0f);
        }

        var speakerVolumePercent = snapshot.SpeakerVolume * 100.0f;
        if (ImGui.SliderFloat(
                $"{T("监听音量", "Monitor Volume")}##GBFRSpeakerVolume",
                ref speakerVolumePercent,
                0.0f,
                50.0f,
                "%.0f%%",
                0))
        {
            _audioSettings.SetSpeakerVolume(speakerVolumePercent / 100.0f);
        }

        using var testButtonSize = CreateVector2(OverlayUiScale.Scale(190.0f), OverlayUiScale.Scale(42.0f));
        var selfTesting = snapshot.IsSelfTestRequested &&
                          snapshot.SelfTestState is not LocalMicrophoneMonitorState.Faulted;
        if (ImGui.Button(
                selfTesting
                    ? T("停止测试", "Stop Test")
                    : T("测试麦克风", "Test Microphone"),
                testButtonSize))
        {
            _setLocalSelfTestRequested(!selfTesting);
        }

        ImGui.TextWrapped(DescribeSelfTest(snapshot.SelfTestState));
        using var meterSize = CreateVector2(-1.0f, OverlayUiScale.Scale(26.0f));
        ImGui.ProgressBar(
            Math.Clamp(snapshot.PeakLevel, 0.0f, 1.0f),
            meterSize,
            $"{T("输入电平", "Input Level")}  {snapshot.PeakLevel:P0}");
        ImGui.TextWrapped(
            T(
                "设备选择和测试立即生效。Party 语音设备将在重启 Mod 后应用。",
                "Device and test changes apply now. Party voice devices apply after restarting the mod."));
    }

    private void DrawPlayerMuteSettingsTab()
    {
        ImGui.TextWrapped(T("按玩家 2、3、4 显示。", "Shows players 2, 3 and 4."));
        if (!string.IsNullOrWhiteSpace(_playerMuteStatusText))
            ImGui.TextWrapped(LocalizeLegacyText(_playerMuteStatusText));
        ImGui.Separator();
        var slots = _getPlayerMuteSlots();
        using var buttonSize = CreateVector2(OverlayUiScale.Scale(190.0f), OverlayUiScale.Scale(36.0f));
        for (var player = 2; player <= 4; player++)
        {
            var status = slots.FirstOrDefault(candidate => candidate.PlayerNumber == player);
            if (status.PlayerNumber != player)
            {
                status = new PartyPlayerMuteSlotStatus(
                    player,
                    false,
                    false,
                    T("玩家状态不可用。", "Player status unavailable."));
            }

            ImGui.Text(T($"玩家 {player}", $"Player {player}"));
            ImGui.SameLine(OverlayUiScale.Scale(280.0f), OverlayUiScale.Scale(12.0f));
            ImGui.BeginDisabled(!status.IsAvailable);
            try
            {
                var label = status.IsAvailable
                    ? status.IsMuted
                        ? $"{T("取消禁言", "Unmute")}##MutePlayer{player}"
                        : $"{T("禁言", "Mute")}##MutePlayer{player}"
                    : $"{T("不可用", "Unavailable")}##MutePlayer{player}";
                if (ImGui.Button(label, buttonSize))
                {
                    var operation = _setPlayerMuted(player, !status.IsMuted);
                    _playerMuteStatusText = operation.Message;
                }
            }
            finally
            {
                ImGui.EndDisabled();
            }
            ImGui.TextWrapped(LocalizeLegacyText(status.Detail));
            ImGui.Separator();
        }
    }

    private void DrawHotkeySettingsSection()
    {
        ImGui.TextWrapped(T(
            "点击按钮后按下按键。键盘支持组合键，手柄最多两个按键。",
            "Select a binding, then press it. Keyboard chords and up to two controller buttons are supported."));
        ImGui.TextWrapped(T("Ctrl+F10 始终可打开设置。", "Ctrl+F10 always opens Settings."));
        ImGui.TextWrapped(Volatile.Read(ref _controllerInputAvailable) != 0
            ? T("手柄：已连接", "Controller: connected")
            : T("未检测到手柄", "No controller detected"));
        ImGui.Separator();
        DrawBindingRow(T("设置菜单", "Settings Menu"), BindingTarget.SettingsMenu);
        DrawBindingRow(T("打开聊天", "Open Chat"), BindingTarget.OpenChat);
        DrawBindingRow(T("按住说话", "Push-to-Talk"), BindingTarget.PushToTalk);
        DrawBindingRow(T("快捷动作面板", "Quick Actions Panel"), BindingTarget.QuickActionsPanel);
        DrawBindingRow(T("全局禁言", "Global Mute"), BindingTarget.GlobalMute);
        DrawBindingCapturePanel();
    }

    private unsafe void DrawQuickActionSettingsTab()
    {
        var configuration = _getConfiguration();
        var actions = configuration.QuickActions ?? [];
        ImGui.TextWrapped(T(
            "为常用表情、短语、动作或文字设置快捷键。",
            "Create shortcuts for stickers, phrases, emotes or custom text."));
        using var addSize = CreateVector2(OverlayUiScale.Scale(190.0f), OverlayUiScale.Scale(34.0f));
        if (ImGui.Button(T("＋ 新建快捷动作", "＋ Add Action"), addSize))
            AddQuickAction();
        ImGui.Separator();

        using var listSize = CreateVector2(OverlayUiScale.Scale(270.0f), -1.0f);
        var beganList = ImGui.BeginChildStr("##GBFRQuickActionList", listSize, true, 0);
        try
        {
            if (beganList)
            {
                using var zero = CreateVector2(0.0f, 0.0f);
                foreach (var action in actions)
                {
                    var display = string.IsNullOrWhiteSpace(action.Name)
                        ? T("未命名", "Untitled")
                        : LocalizeQuickActionName(action.Name);
                    if (ImGui.SelectableBool(
                            $"{display}##QuickActionSelect{action.Id}",
                            string.Equals(action.Id, _selectedQuickActionId, StringComparison.Ordinal),
                            0,
                            zero))
                    {
                        SelectQuickAction(action);
                    }
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        ImGui.SameLine(0.0f, OverlayUiScale.Scale(14.0f));
        ImGui.BeginGroup();
        try
        {
            var selected = actions.FirstOrDefault(action =>
                string.Equals(action.Id, _selectedQuickActionId, StringComparison.Ordinal));
            if (selected is null)
            {
                ImGui.TextWrapped(T("请从左侧选择快捷动作。", "Select a quick action from the list."));
                return;
            }

            var enabled = selected.Enabled;
            if (ImGui.Checkbox(T("启用", "Enabled"), ref enabled))
                UpdateQuickAction(selected.Id, action => action.Enabled = enabled);

            ImGui.SetNextItemWidth(-1.0f);
            fixed (byte* nameBuffer = _quickActionNameBuffer)
            {
                if (ImGui.InputText(
                        $"{T("名称", "Name")}##QuickActionName{selected.Id}",
                        (sbyte*)nameBuffer,
                        (nint)_quickActionNameBuffer.Length,
                        0,
                        null!,
                        nint.Zero))
                {
                    var value = ReadUtf8Buffer(_quickActionNameBuffer);
                    UpdateQuickAction(selected.Id, action => action.Name = value);
                }
            }

            if (DrawQuickActionKindCombo(selected.Kind, selected.Id, out var selectedKind))
            {
                UpdateQuickAction(selected.Id, action =>
                {
                    action.Kind = selectedKind;
                    action.OfficialId = selectedKind == QuickActionKind.CustomText
                        ? -1
                        : CommunicationCatalog.GetEntries(selectedKind).FirstOrDefault().Id;
                });
            }

            if (selected.Kind == QuickActionKind.CustomText)
            {
                using var textSize = CreateVector2(-1.0f, OverlayUiScale.Scale(110.0f));
                fixed (byte* textBuffer = _quickActionTextBuffer)
                {
                    if (ImGui.InputTextMultiline(
                            $"{T("自定义文字", "Custom Text")}##QuickActionText{selected.Id}",
                            (sbyte*)textBuffer,
                            (nint)_quickActionTextBuffer.Length,
                            textSize,
                            0,
                            null!,
                            nint.Zero))
                    {
                        var value = ReadUtf8Buffer(_quickActionTextBuffer);
                        UpdateQuickAction(selected.Id, action => action.Text = value);
                    }
                }
            }
            else if (DrawOfficialCommunicationCombo(
                         selected.Kind,
                         selected.OfficialId,
                         selected.Id,
                         out var officialId))
            {
                UpdateQuickAction(selected.Id, action => action.OfficialId = officialId);
            }

            DrawBindingRow(T("此动作", "This Action"), BindingTarget.QuickAction, selected.Id);
            using var moveSize = CreateVector2(OverlayUiScale.Scale(150.0f), OverlayUiScale.Scale(34.0f));
            using var deleteSize = CreateVector2(OverlayUiScale.Scale(170.0f), OverlayUiScale.Scale(34.0f));
            if (ImGui.Button($"{T("上移", "Move Up")}##QuickActionUp{selected.Id}", moveSize))
                MoveQuickAction(selected.Id, -1);
            ImGui.SameLine(0.0f, OverlayUiScale.Scale(8.0f));
            if (ImGui.Button($"{T("下移", "Move Down")}##QuickActionDown{selected.Id}", moveSize))
                MoveQuickAction(selected.Id, 1);
            ImGui.SameLine(0.0f, OverlayUiScale.Scale(8.0f));
            if (ImGui.Button($"{T("删除", "Delete")}##QuickActionDelete{selected.Id}", deleteSize))
                DeleteQuickAction(selected.Id);
            DrawBindingCapturePanel();
        }
        finally
        {
            ImGui.EndGroup();
        }
    }

    private void DrawChatLayoutSettingsSection()
    {
        var configuration = _getConfiguration();
        DrawConfigurationCheckbox(
            T("启用聊天框", "Enable Chat Overlay"),
            configuration.EnableOverlay,
            (value, enabled) => value.EnableOverlay = enabled);
        DrawConfigurationCheckbox(
            T("输入法候选框兼容", "IME Candidate Fallback"),
            configuration.EnableImeCandidateFallback,
            (value, enabled) => value.EnableImeCandidateFallback = enabled);
        var opacity = (float)configuration.BackgroundOpacity;
        if (ImGui.SliderFloat(
                T("背景透明度", "Background Opacity"),
                ref opacity,
                0.0f,
                1.0f,
                "%.2f",
                0))
        {
            UpdateConfigurationSafely(value => value.BackgroundOpacity = opacity);
        }
        ImGui.Separator();
        ImGui.TextWrapped(T(
            "拖动聊天框顶部移动，拖动右下角缩放。",
            "Drag the chat header to move it and the lower-right corner to resize it."));
    }

    private void DrawConfigurationCheckbox(
        string label,
        bool current,
        Action<Config, bool> apply)
    {
        var value = current;
        if (ImGui.Checkbox(label, ref value))
            UpdateConfigurationSafely(configuration => apply(configuration, value));
    }

    private void DrawBindingRow(
        string label,
        BindingTarget target,
        string? quickActionId = null,
        int playerNumber = 0)
    {
        var requestKeyboard = new BindingCaptureRequest(
            target,
            BindingCaptureDevice.Keyboard,
            quickActionId,
            playerNumber);
        var requestController = new BindingCaptureRequest(
            target,
            BindingCaptureDevice.Controller,
            quickActionId,
            playerNumber);
        var configuration = _getConfiguration();
        var keyboard = GetBindingValue(configuration, requestKeyboard);
        var controller = GetBindingValue(configuration, requestController);

        ImGui.Text(label);
        using var keyboardSize = CreateVector2(OverlayUiScale.Scale(230.0f), OverlayUiScale.Scale(34.0f));
        using var controllerSize = CreateVector2(OverlayUiScale.Scale(230.0f), OverlayUiScale.Scale(34.0f));
        if (ImGui.Button(
                $"{T("键盘", "Keyboard")}: {DescribeBinding(keyboard)}##Keyboard{target}{quickActionId}{playerNumber}",
                keyboardSize))
        {
            BeginBindingCapture(requestKeyboard);
        }
        ImGui.SameLine(0.0f, OverlayUiScale.Scale(10.0f));
        ImGui.BeginDisabled(Volatile.Read(ref _controllerInputAvailable) == 0);
        try
        {
            if (ImGui.Button(
                    $"{T("手柄", "Controller")}: {DescribeBinding(controller)}##Controller{target}{quickActionId}{playerNumber}",
                    controllerSize))
            {
                BeginBindingCapture(requestController);
            }
        }
        finally
        {
            ImGui.EndDisabled();
        }
        ImGui.Separator();
    }

    private void DrawBindingCapturePanel()
    {
        PendingBindingConflict? pendingConflict;
        BindingCaptureRequest? activeCapture;
        string? captureStatusText;
        lock (_bindingCaptureSync)
        {
            pendingConflict = _bindingConflictPending;
            activeCapture = _bindingCapture;
            captureStatusText = _captureStatusText;
        }

        if (pendingConflict is { } conflict)
        {
            ImGui.TextWrapped(
                T(
                    $"与“{conflict.Description}”冲突，仍要保存吗？",
                    $"Conflicts with “{conflict.Description}”. Save anyway?"));
            using var saveSize = CreateVector2(OverlayUiScale.Scale(180.0f), OverlayUiScale.Scale(34.0f));
            using var cancelSize = CreateVector2(OverlayUiScale.Scale(150.0f), OverlayUiScale.Scale(34.0f));
            if (ImGui.Button(T("仍然保存", "Save Anyway"), saveSize))
            {
                PersistBinding(conflict.Request, conflict.Value);
                lock (_bindingCaptureSync)
                {
                    if (_bindingConflictPending == conflict)
                        _bindingConflictPending = null;
                }
            }
            ImGui.SameLine(0.0f, OverlayUiScale.Scale(10.0f));
            if (ImGui.Button($"{T("取消", "Cancel")}##Conflict", cancelSize))
            {
                lock (_bindingCaptureSync)
                {
                    if (_bindingConflictPending == conflict)
                        _bindingConflictPending = null;
                }
            }
            return;
        }

        if (activeCapture is not { } capture)
            return;
        ImGui.TextWrapped(
            T(
                $"正在设置“{DescribeTarget(capture)}”。{captureStatusText} Esc 取消，Backspace 清除。",
                $"Setting “{DescribeTarget(capture)}”. {captureStatusText} Esc cancels; Backspace clears."));
        using var cancelButtonSize = CreateVector2(OverlayUiScale.Scale(150.0f), OverlayUiScale.Scale(34.0f));
        if (ImGui.Button(T("取消设置", "Cancel Binding"), cancelButtonSize))
            CancelBindingCapture();
    }

    internal void BeginBindingCapture(BindingCaptureRequest request)
    {
        lock (_bindingCaptureSync)
        {
            _bindingConflictPending = null;
            _bindingCapture = request;
            _keyboardCaptureCandidate = default;
            _controllerCaptureCandidate = ControllerButtons.None;
            _captureWaitingForRelease = request.Device == BindingCaptureDevice.Controller &&
                                        Volatile.Read(ref _latestControllerButtonsMask) != 0;
            _captureStatusText = _captureWaitingForRelease
                ? T("请先松开所有按键。", "Release all buttons first.")
                : request.Device == BindingCaptureDevice.Keyboard
                    ? T("请按下键盘按键，松开后确认。", "Press a keyboard key, then release to confirm.")
                    : T("请按下一个或两个手柄按键，松开后确认。", "Press one or two controller buttons, then release to confirm.");
        }
        ResetManagedControllerHotkeys();
        _forceReleaseVoiceInputs();
    }

    private void CancelBindingCapture()
    {
        lock (_bindingCaptureSync)
        {
            _bindingCapture = null;
            _keyboardCaptureCandidate = default;
            _controllerCaptureCandidate = ControllerButtons.None;
            _captureWaitingForRelease = false;
            _captureStatusText = null;
        }
    }

    private void CompleteBindingCapture(string value)
    {
        lock (_bindingCaptureSync)
        {
            if (_bindingCapture is not { } request)
                return;
            if (request.Target == BindingTarget.SettingsMenu &&
                string.IsNullOrWhiteSpace(value))
            {
                var configuration = _getConfiguration();
                var alternate = request.Device == BindingCaptureDevice.Keyboard
                    ? configuration.SettingsMenuControllerBinding
                    : configuration.SettingsMenuKeyboardBinding;
                if (string.IsNullOrWhiteSpace(alternate))
                {
                    _captureStatusText = T(
                        "请先设置另一种打开菜单的按键。",
                        "Set another Settings binding before clearing this one.");
                    return;
                }
            }
            var conflict = FindBindingConflict(request, value);
            CancelBindingCapture();
            if (conflict is not null)
            {
                _bindingConflictPending = new PendingBindingConflict(request, value, conflict);
                return;
            }
            PersistBinding(request, value);
        }
    }

    private void PersistBinding(BindingCaptureRequest request, string value)
    {
        UpdateConfigurationSafely(configuration =>
        {
            switch (request.Target)
            {
                case BindingTarget.SettingsMenu when request.Device == BindingCaptureDevice.Keyboard:
                    configuration.SettingsMenuKeyboardBinding = value;
                    break;
                case BindingTarget.SettingsMenu:
                    configuration.SettingsMenuControllerBinding = value;
                    break;
                case BindingTarget.OpenChat when request.Device == BindingCaptureDevice.Keyboard:
                    configuration.OpenChatKeyboardBinding = value;
                    break;
                case BindingTarget.OpenChat:
                    configuration.OpenChatControllerBinding = value;
                    break;
                case BindingTarget.PushToTalk when request.Device == BindingCaptureDevice.Keyboard:
                    configuration.PushToTalkKeyboardBinding = value;
                    break;
                case BindingTarget.PushToTalk:
                    configuration.PushToTalkControllerBinding = value;
                    break;
                case BindingTarget.QuickActionsPanel when request.Device == BindingCaptureDevice.Keyboard:
                    configuration.QuickActionsKeyboardBinding = value;
                    break;
                case BindingTarget.QuickActionsPanel:
                    configuration.QuickActionsControllerBinding = value;
                    break;
                case BindingTarget.GlobalMute when request.Device == BindingCaptureDevice.Keyboard:
                    configuration.GlobalMuteKeyboardBinding = value;
                    break;
                case BindingTarget.GlobalMute:
                    configuration.GlobalMuteControllerBinding = value;
                    break;
                case BindingTarget.QuickAction:
                    var action = configuration.QuickActions.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, request.QuickActionId, StringComparison.Ordinal));
                    if (action is null)
                        return;
                    if (request.Device == BindingCaptureDevice.Keyboard)
                        action.KeyboardBinding = value;
                    else
                        action.ControllerBinding = value;
                    break;
            }
        });
    }

    private string? FindBindingConflict(BindingCaptureRequest request, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var matches = EnumerateBindings(_getConfiguration())
            .Where(item => item.Request.Device == request.Device &&
                           item.Request != request &&
                           string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
            .Select(item => DescribeTarget(item.Request))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return matches.Length == 0 ? null : string.Join("、", matches);
    }

    private static IEnumerable<(BindingCaptureRequest Request, string Value)> EnumerateBindings(
        Config configuration)
    {
        yield return (new(BindingTarget.SettingsMenu, BindingCaptureDevice.Keyboard, null), configuration.SettingsMenuKeyboardBinding);
        yield return (new(BindingTarget.SettingsMenu, BindingCaptureDevice.Controller, null), configuration.SettingsMenuControllerBinding);
        yield return (new(BindingTarget.OpenChat, BindingCaptureDevice.Keyboard, null), configuration.OpenChatKeyboardBinding);
        yield return (new(BindingTarget.OpenChat, BindingCaptureDevice.Controller, null), configuration.OpenChatControllerBinding);
        yield return (new(BindingTarget.PushToTalk, BindingCaptureDevice.Keyboard, null), configuration.PushToTalkKeyboardBinding);
        yield return (new(BindingTarget.PushToTalk, BindingCaptureDevice.Controller, null), configuration.PushToTalkControllerBinding);
        yield return (new(BindingTarget.QuickActionsPanel, BindingCaptureDevice.Keyboard, null), configuration.QuickActionsKeyboardBinding);
        yield return (new(BindingTarget.QuickActionsPanel, BindingCaptureDevice.Controller, null), configuration.QuickActionsControllerBinding);
        yield return (new(BindingTarget.GlobalMute, BindingCaptureDevice.Keyboard, null), configuration.GlobalMuteKeyboardBinding);
        yield return (new(BindingTarget.GlobalMute, BindingCaptureDevice.Controller, null), configuration.GlobalMuteControllerBinding);
        foreach (var action in configuration.QuickActions ?? [])
        {
            yield return (new(BindingTarget.QuickAction, BindingCaptureDevice.Keyboard, action.Id), action.KeyboardBinding);
            yield return (new(BindingTarget.QuickAction, BindingCaptureDevice.Controller, action.Id), action.ControllerBinding);
        }
    }

    private static string GetBindingValue(Config configuration, BindingCaptureRequest request)
    {
        return EnumerateBindings(configuration)
            .FirstOrDefault(item => item.Request == request).Value ?? string.Empty;
    }

    private string DescribeBinding(string value) =>
        string.IsNullOrWhiteSpace(value) ? T("未绑定", "Unbound") : value;

    private string DescribeTarget(BindingCaptureRequest request) => request.Target switch
    {
        BindingTarget.SettingsMenu => T("设置菜单", "Settings Menu"),
        BindingTarget.OpenChat => T("打开聊天", "Open Chat"),
        BindingTarget.PushToTalk => T("按住说话", "Push-to-Talk"),
        BindingTarget.QuickActionsPanel => T("快捷动作面板", "Quick Actions Panel"),
        BindingTarget.GlobalMute => T("全局禁言", "Global Mute"),
        _ => T("快捷动作", "Quick Action"),
    };

    private bool DrawQuickActionKindCombo(
        QuickActionKind current,
        string actionId,
        out QuickActionKind selected)
    {
        selected = current;
        if (!ImGui.BeginCombo(
                $"{T("类型", "Type")}##QuickActionKind{actionId}",
                GetQuickActionKindLabel(current),
                0))
        {
            return false;
        }

        try
        {
            using var zero = CreateVector2(0.0f, 0.0f);
            foreach (var kind in new[]
                     {
                         QuickActionKind.Stamp,
                         QuickActionKind.FixedPhrase,
                         QuickActionKind.Emotion,
                         QuickActionKind.CustomText,
                     })
            {
                if (!ImGui.SelectableBool(
                        $"{GetQuickActionKindLabel(kind)}##QuickActionKindChoice{actionId}{kind}",
                        current == kind,
                        0,
                        zero))
                {
                    continue;
                }

                selected = kind;
                return selected != current;
            }
        }
        finally
        {
            ImGui.EndCombo();
        }

        return false;
    }

    private bool DrawOfficialCommunicationCombo(
        QuickActionKind kind,
        int currentId,
        string actionId,
        out int selectedId)
    {
        selectedId = currentId;
        var entries = CommunicationCatalog.GetEntries(kind);
        var preview = CommunicationCatalog.TryGetEntry(kind, currentId, out var current)
            ? current.GetDisplayName(CurrentLanguage)
            : T("请选择", "Select");
        if (!ImGui.BeginCombo(
                $"{GetQuickActionKindLabel(kind)}##QuickActionPayload{actionId}",
                preview,
                0))
        {
            return false;
        }

        try
        {
            using var zero = CreateVector2(0.0f, 0.0f);
            foreach (var entry in entries)
            {
                if (!ImGui.SelectableBool(
                        $"{entry.GetDisplayName(CurrentLanguage)}##QuickActionPayloadChoice{actionId}{entry.Id}",
                        currentId == entry.Id,
                        0,
                        zero))
                {
                    continue;
                }

                selectedId = entry.Id;
                return selectedId != currentId;
            }
        }
        finally
        {
            ImGui.EndCombo();
        }

        return false;
    }

    private string GetQuickActionKindLabel(QuickActionKind kind) => kind switch
    {
        QuickActionKind.Stamp => T("表情", "Stamp"),
        QuickActionKind.FixedPhrase => T("模板文", "Template"),
        QuickActionKind.Emotion => T("动作", "Action"),
        QuickActionKind.CustomText => T("自定义文", "Custom Text"),
        _ => T("未知", "Unknown"),
    };

    private void AddQuickAction()
    {
        var firstStamp = CommunicationCatalog.GetEntries(QuickActionKind.Stamp)[0];
        var created = new QuickActionConfiguration
        {
            Kind = QuickActionKind.Stamp,
            OfficialId = firstStamp.Id,
        };
        UpdateConfigurationSafely(configuration =>
        {
            configuration.QuickActions ??= [];
            var number = configuration.QuickActions.Count + 1;
            created.Name = $"快捷动作 {number} / Quick Action {number}";
            configuration.QuickActions.Add(created);
        });
        SelectQuickAction(created);
    }

    private void DeleteQuickAction(string actionId)
    {
        UpdateConfigurationSafely(configuration =>
        {
            configuration.QuickActions.RemoveAll(action =>
                string.Equals(action.Id, actionId, StringComparison.Ordinal));
        });
        _selectedQuickActionId = null;
        Array.Clear(_quickActionNameBuffer);
        Array.Clear(_quickActionTextBuffer);
        lock (_bindingCaptureSync)
        {
            if (_bindingCapture?.QuickActionId == actionId)
                CancelBindingCapture();
        }
    }

    private void MoveQuickAction(string actionId, int direction)
    {
        UpdateConfigurationSafely(configuration =>
        {
            var index = configuration.QuickActions.FindIndex(action =>
                string.Equals(action.Id, actionId, StringComparison.Ordinal));
            if (index < 0)
                return;
            var destination = Math.Clamp(index + direction, 0, configuration.QuickActions.Count - 1);
            if (destination == index)
                return;
            var action = configuration.QuickActions[index];
            configuration.QuickActions.RemoveAt(index);
            configuration.QuickActions.Insert(destination, action);
        });
    }

    private void SelectQuickAction(QuickActionConfiguration action)
    {
        _selectedQuickActionId = action.Id;
        WriteUtf8Buffer(_quickActionNameBuffer, LocalizeQuickActionName(action.Name));
        WriteUtf8Buffer(_quickActionTextBuffer, action.Text);
    }

    private void RefreshSelectedQuickActionBuffers()
    {
        if (string.IsNullOrWhiteSpace(_selectedQuickActionId))
            return;
        var selected = (_getConfiguration().QuickActions ?? []).FirstOrDefault(action =>
            string.Equals(action.Id, _selectedQuickActionId, StringComparison.Ordinal));
        if (selected is not null)
            SelectQuickAction(selected);
    }

    private string LocalizeQuickActionName(string? name) =>
        LocalizeQuickActionName(CurrentLanguage, name);

    internal static string LocalizeQuickActionName(UiLanguage language, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        const string chinesePrefix = "快捷动作 ";
        const string englishPrefix = "Quick Action ";
        const string separator = " / Quick Action ";
        var separatorIndex = name.IndexOf(separator, StringComparison.Ordinal);
        if (name.StartsWith(chinesePrefix, StringComparison.Ordinal) &&
            separatorIndex < 0 &&
            int.TryParse(name.AsSpan(chinesePrefix.Length), out var chineseOnlyNumber))
        {
            return UiLocalization.Select(language, name, $"{englishPrefix}{chineseOnlyNumber}");
        }
        if (name.StartsWith(englishPrefix, StringComparison.Ordinal) &&
            int.TryParse(name.AsSpan(englishPrefix.Length), out var englishOnlyNumber))
        {
            return UiLocalization.Select(language, $"{chinesePrefix}{englishOnlyNumber}", name);
        }
        if (!name.StartsWith(chinesePrefix, StringComparison.Ordinal) ||
            separatorIndex < 0)
        {
            return name;
        }

        var chineseNumber = name[chinesePrefix.Length..separatorIndex];
        var englishNumber = name[(separatorIndex + separator.Length)..];
        return int.TryParse(chineseNumber, out var parsedChineseNumber) &&
               int.TryParse(englishNumber, out var parsedEnglishNumber) &&
               parsedChineseNumber == parsedEnglishNumber
            ? UiLocalization.Select(
                language,
                $"快捷动作 {chineseNumber}",
                $"Quick Action {englishNumber}")
            : name;
    }

    private void UpdateQuickAction(string actionId, Action<QuickActionConfiguration> update)
    {
        UpdateConfigurationSafely(configuration =>
        {
            var action = configuration.QuickActions.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, actionId, StringComparison.Ordinal));
            if (action is not null)
                update(action);
        });
    }

    private void UpdateConfigurationSafely(Action<Config> update)
    {
        try
        {
            _updateConfiguration(update);
        }
        catch (Exception exception)
        {
            LogSafely($"Configuration update failed: {exception.Message}");
        }
    }

    private static void WriteUtf8Buffer(byte[] buffer, string? value)
    {
        Array.Clear(buffer);
        Encoding.UTF8.GetEncoder().Convert(
            (value ?? string.Empty).AsSpan(),
            buffer.AsSpan(0, buffer.Length - 1),
            true,
            out _,
            out _,
            out _);
    }

    private static string ReadUtf8Buffer(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
            length = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private bool DrawEndpointCombo(
        string label,
        string selectedId,
        IReadOnlyList<AudioEndpointInfo> endpoints,
        out string newSelection)
    {
        newSelection = selectedId;
        var preview = AudioEndpointSelectionValues.IsSystemDefault(selectedId)
            ? T("系统默认", "System Default")
            : endpoints.FirstOrDefault(endpoint => string.Equals(
                endpoint.Id,
                selectedId,
                StringComparison.Ordinal))?.FriendlyName ?? T("已保存的设备不可用", "Saved device unavailable");
        if (!ImGui.BeginCombo(label, preview, 0))
            return false;

        try
        {
            using var zero = CreateVector2(0.0f, 0.0f);
            var defaultSelected = AudioEndpointSelectionValues.IsSystemDefault(selectedId);
            if (ImGui.SelectableBool(
                    T("系统默认", "System Default"),
                    defaultSelected,
                    0,
                    zero))
            {
                newSelection = AudioEndpointSelectionValues.SystemDefault;
                return true;
            }

            foreach (var endpoint in endpoints)
            {
                var suffix = endpoint.IsDefaultCommunicationsDevice
                    ? T("  [Windows 通信默认]", "  [Windows Communications Default]")
                    : string.Empty;
                if (ImGui.SelectableBool(
                        endpoint.FriendlyName + suffix + "##" + endpoint.Id,
                        string.Equals(endpoint.Id, selectedId, StringComparison.Ordinal),
                        0,
                        zero))
                {
                    newSelection = endpoint.Id;
                    return true;
                }
            }
        }
        finally
        {
            ImGui.EndCombo();
        }

        return false;
    }

    private void DrawQuickActionsPanel(Config configuration)
    {
        var viewport = ImGui.GetMainViewport();
        var workPosition = viewport.WorkPos;
        var workSize = viewport.WorkSize;
        using var position = CreateVector2(
            workPosition.X + Math.Max(OverlayUiScale.Scale(16.0f), workSize.X - OverlayUiScale.Scale(410.0f)),
            workPosition.Y + Math.Max(OverlayUiScale.Scale(16.0f), workSize.Y * 0.18f));
        using var size = CreateVector2(
            OverlayUiScale.Scale(380.0f),
            Math.Min(
                OverlayUiScale.Scale(520.0f),
                Math.Max(OverlayUiScale.Scale(180.0f), workSize.Y - OverlayUiScale.Scale(80.0f))));
        using var pivot = CreateVector2(0.0f, 0.0f);
        ImGui.SetNextWindowPos(position, (int)ImGuiCond.FirstUseEver, pivot);
        ImGui.SetNextWindowSize(size, (int)ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.94f);
        var open = true;
        var began = ImGui.Begin(
            $"{T("快捷动作", "Quick Actions")}##GBFRQuickActionsPanel",
            ref open,
            (int)(ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings));
        try
        {
            if (!began)
                return;
            var actions = (configuration.QuickActions ?? [])
                .Where(action =>
                    action.Enabled &&
                    action.IsConfigured)
                .ToArray();
            if (actions.Length == 0)
            {
                ImGui.TextWrapped(
                    T(
                        "暂无快捷动作，请先在设置中添加。",
                        "No quick actions yet. Add one in Settings."));
                return;
            }

            foreach (var action in actions)
            {
                var payload = DescribeQuickActionPayload(action);
                var label = string.IsNullOrWhiteSpace(action.Name)
                    ? payload
                    : LocalizeQuickActionName(action.Name);
                using var buttonSize = CreateVector2(-1.0f, OverlayUiScale.Scale(42.0f));
                if (ImGui.Button($"{label}##QuickActionRun{action.Id}", buttonSize))
                    SendQuickAction(action.Id);
                if (!string.Equals(label, payload, StringComparison.Ordinal))
                    ImGui.TextWrapped(payload);
            }
        }
        finally
        {
            ImGui.End();
            if (!open)
            {
                SetQuickActionsPanelOpen(false);
            }
        }
    }

    private void SendQuickAction(string actionId)
    {
        var action = (_getConfiguration().QuickActions ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, actionId, StringComparison.Ordinal));
        if (action is null || !action.Enabled)
            return;
        if (!action.IsConfigured)
        {
            _statusText = T("快捷动作内容为空。", "Quick action content is empty.");
            LogSafely($"Quick action ignored because it is not configured: id={action.Id}, kind={action.Kind}.");
            return;
        }
        LogSafely(
            $"Quick action dispatch: id={action.Id}, kind={action.Kind}, official_id={action.OfficialId}.");
        var result = action.Kind == QuickActionKind.CustomText
            ? _session.SendText(action.Text)
            : _sendOfficialQuickAction(action.Kind, action.OfficialId);
        if (!result.Succeeded)
        {
            _statusText = result.Error ?? result.Status.ToString();
            LogSafely(
                $"Quick action failed: id={action.Id}, kind={action.Kind}, status={result.Status}, " +
                $"error={result.Error ?? "none"}.");
        }
        else
        {
            _statusText = null;
            LogSafely(
                $"Quick action native call completed: id={action.Id}, kind={action.Kind}, " +
                $"official_id={action.OfficialId}.");
        }
    }

    private string DescribeQuickActionPayload(QuickActionConfiguration action)
    {
        if (action.Kind == QuickActionKind.CustomText)
            return action.Text;
        return CommunicationCatalog.TryGetEntry(action.Kind, action.OfficialId, out var entry)
            ? $"{GetQuickActionKindLabel(action.Kind)} · {entry.GetDisplayName(CurrentLanguage)}"
            : T("未选择内容", "No content selected");
    }

    private void DrawChatEditHandles(
        ImGuiViewport viewport,
        ref ChatOverlayRect rect,
        float workX,
        float workY,
        float workWidth,
        float workHeight)
    {
        ImGui.BeginDisabled(!_mouseInteractionGate.IsArmed);
        try
        {
            using var movePosition = CreateVector2(rect.X + 4.0f, rect.Y + 3.0f);
            using var moveSize = CreateVector2(Math.Max(1.0f, rect.Width - 38.0f), 22.0f);
            ImGui.SetCursorScreenPos(movePosition);
            _ = ImGui.InvisibleButton("##GBFRMoveChat", moveSize, 0);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(0, 0.0f))
            {
                using var delta = CreateVector2(0.0f, 0.0f);
                ImGui.GetMouseDragDelta(delta, 0, 0.0f);
                rect = ChatOverlayLayout.Move(
                    rect,
                    delta.X,
                    delta.Y,
                    workX,
                    workY,
                    workWidth,
                    workHeight);
                ImGui.ResetMouseDragDelta(0);
            }

            using var resizePosition = CreateVector2(
                rect.X + rect.Width - 30.0f,
                rect.Y + rect.Height - 30.0f);
            using var resizeSize = CreateVector2(30.0f, 30.0f);
            ImGui.SetCursorScreenPos(resizePosition);
            _ = ImGui.InvisibleButton("##GBFRResizeChat", resizeSize, 0);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(0, 0.0f))
            {
                using var delta = CreateVector2(0.0f, 0.0f);
                ImGui.GetMouseDragDelta(delta, 0, 0.0f);
                rect = ChatOverlayLayout.Resize(
                    rect,
                    delta.X,
                    delta.Y,
                    workX,
                    workY,
                    workWidth,
                    workHeight);
                ImGui.ResetMouseDragDelta(0);
            }
        }
        finally
        {
            ImGui.EndDisabled();
        }

        var drawList = ImGui.GetForegroundDrawListViewportPtr(viewport);
        using var topLeft = CreateVector2(rect.X + 5.0f, rect.Y + 4.0f);
        using var topRight = CreateVector2(rect.X + rect.Width - 34.0f, rect.Y + 4.0f);
        ImGui.ImDrawListAddLine(drawList, topLeft, topRight, PackColor(105, 224, 255, 0.75f), 2.0f);
        using var triangleTop = CreateVector2(rect.X + rect.Width - 5.0f, rect.Y + rect.Height - 25.0f);
        using var triangleCorner = CreateVector2(rect.X + rect.Width - 5.0f, rect.Y + rect.Height - 5.0f);
        using var triangleLeft = CreateVector2(rect.X + rect.Width - 25.0f, rect.Y + rect.Height - 5.0f);
        ImGui.ImDrawListAddTriangleFilled(
            drawList,
            triangleTop,
            triangleCorner,
            triangleLeft,
            PackColor(105, 224, 255, 0.92f));
    }

    private string DescribeSelfTest(LocalMicrophoneMonitorState state) => state switch
    {
        LocalMicrophoneMonitorState.Starting => T("正在启动音频设备……", "Starting audio devices..."),
        LocalMicrophoneMonitorState.Monitoring => T("正在监听，请对着麦克风说话。", "Listening. Speak into the microphone."),
        LocalMicrophoneMonitorState.SignalDetected => T("已检测到麦克风输入。", "Microphone input detected."),
        LocalMicrophoneMonitorState.Faulted => T("测试失败，请重新选择设备。", "Test failed. Select another device."),
        LocalMicrophoneMonitorState.Suspended => T("Mod 已暂停，测试不可用。", "The mod is suspended; testing is unavailable."),
        _ => T("点击“测试麦克风”查看输入电平。", "Select “Test Microphone” to view the input level."),
    };

    private static uint PackColor(byte red, byte green, byte blue, float alpha)
    {
        var a = (uint)Math.Clamp((int)MathF.Round(Math.Clamp(alpha, 0.0f, 1.0f) * 255.0f), 0, 255);
        return (uint)red | ((uint)green << 8) | ((uint)blue << 16) | (a << 24);
    }

    private void DrawVoiceStatus(PartyVoiceUiStatus voiceUiStatus)
    {
        var presentation = VoiceOverlayPresenter.Create(voiceUiStatus, CurrentLanguage);
        if (!presentation.IsVisible)
            return;

        ImGui.TextWrapped(presentation.Text);
        ImGui.Separator();
    }

    private void DrawHistory(bool composerOpen, string? imeCandidateText)
    {
        var candidateHeight = MeasureWrappedTextItemHeight(imeCandidateText);
        var childHeight = CalculateHistoryChildHeight(composerOpen, candidateHeight);
        using var childSize = CreateVector2(0.0f, childHeight);
        var began = ImGui.BeginChildStr(
            "##GBFRChatHistory",
            childSize,
            false,
            (int)ImGuiWindowFlags.NoBackground);
        try
        {
            if (!began)
                return;

            var wasNearBottom = IsHistoryNearBottom(ImGui.GetScrollY(), ImGui.GetScrollMaxY());
            var snapshot = _session.History.Snapshot();
            foreach (var message in snapshot)
            {
                var prefix = $"[{message.Timestamp:HH:mm}] {message.Sender}: ";
                ImGui.TextWrapped(prefix + message.Text);
            }

            if (snapshot.Count > 0 && snapshot[^1].Sequence != _lastRenderedSequence)
            {
                var shouldScroll = _lastRenderedSequence == 0 || wasNearBottom;
                _lastRenderedSequence = snapshot[^1].Sequence;
                if (shouldScroll)
                    ImGui.SetScrollHereY(1.0f);
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    internal static bool IsHistoryNearBottom(float scrollY, float scrollMaxY) =>
        float.IsFinite(scrollY) &&
        float.IsFinite(scrollMaxY) &&
        scrollY >= Math.Max(0.0f, scrollMaxY - 4.0f);

    internal static float CalculateHistoryChildHeight(bool composerOpen, float candidateHeight)
    {
        if (!composerOpen)
            return 0.0f;

        var safeCandidateHeight = float.IsFinite(candidateHeight) && candidateHeight > 0.0f
            ? candidateHeight
            : 0.0f;
        return -(ComposerReservedHeight + safeCandidateHeight);
    }

    private static float MeasureWrappedTextItemHeight(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0.0f;

        using var available = CreateVector2(0.0f, 0.0f);
        ImGui.GetContentRegionAvail(available);
        using var textSize = CreateVector2(0.0f, 0.0f);
        ImGui.CalcTextSize(
            textSize,
            text,
            null!,
            false,
            Math.Max(1.0f, available.X));
        var itemSpacing = Math.Max(
            0.0f,
            ImGui.GetTextLineHeightWithSpacing() - ImGui.GetTextLineHeight());
        return Math.Max(0.0f, textSize.Y) + itemSpacing;
    }

    private unsafe void DrawComposer(bool openedThisFrame, string? imeCandidateText)
    {
        ImGui.Separator();
        DrawImeCandidateFallback(imeCandidateText);
        if (_focusInputNextFrame && !openedThisFrame)
        {
            ImGui.SetKeyboardFocusHere(0);
            _focusInputNextFrame = false;
        }

        ImGui.SetNextItemWidth(-1.0f);
        bool submitRequested;
        fixed (byte* buffer = _inputBuffer)
        {
            submitRequested = ImGui.InputText(
                "##GBFRChatInput",
                (sbyte*)buffer,
                (nint)_inputBuffer.Length,
                (int)ImGuiInputTextFlags.EnterReturnsTrue,
                null!,
                nint.Zero);
        }

        _session.Composer.SetDraft(ReadInputBuffer());
        if (submitRequested)
        {
            var result = _session.SendDraft();
            if (result.Succeeded)
            {
                Array.Clear(_inputBuffer);
                _releaseCaptureFrames = 2;
                Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
                ClearImeCandidateSnapshot();
                _statusText = null;
            }
            else
            {
                _statusText = result.Error ?? result.Status.ToString();
                _focusInputNextFrame = true;
            }
        }

        if (!string.IsNullOrEmpty(_statusText))
            ImGui.TextWrapped(_statusText);
    }

    public OverlayWindowMessageResult ObserveWindowMessage(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam)
    {
        Volatile.Write(ref _windowHandle, windowHandle);
        MouseButtonStateTracker.ObserveWindowMessage(message, wParam);
        ObserveKeyboardFocusTransition(message, wParam);
        if (ShouldIgnoreUnactivateBeforeBackend(message, wParam))
            return OverlayWindowMessageResult.HandledWith(nint.Zero);
        if (TryHandleImeCharacter(windowHandle, message, wParam))
            return OverlayWindowMessageResult.HandledWith(nint.Zero);
        if (ShouldRouteImeUiToDefault(message))
        {
            ObserveImeUiMessage(windowHandle, message, wParam, lParam);
            var forwardedLParam = Win32ImeCompatibility.PrepareImeUiLParam(
                message,
                wParam,
                lParam);
            if (message == Win32ImeCompatibility.WmImeSetContext && wParam != nint.Zero)
                LogImeCandidateUi(lParam, forwardedLParam);
            return OverlayWindowMessageResult.HandledWith(
                Win32ImeCompatibility.CallDefaultWindowProc(
                    windowHandle,
                    message,
                    wParam,
                    forwardedLParam));
        }
        return TryHandleHotkeyWindowMessage(message, wParam)
            ? OverlayWindowMessageResult.HandledWith(nint.Zero)
            : OverlayWindowMessageResult.Continue;
    }

    private bool TryHandleHotkeyWindowMessage(uint message, nint wParam)
    {
        var isDown = message is WmKeyDown or WmSysKeyDown;
        var isUp = message is WmKeyUp or WmSysKeyUp;
        if (!isDown && !isUp)
            return false;
        var virtualKey = unchecked((ushort)(nuint)wParam);
        if (TryHandleBindingCaptureWindowMessage(virtualKey, isDown, isUp))
            return true;

        var configuration = _getConfiguration();
        var emergencySettings = HotkeyConfigurationSnapshot.EmergencySettingsKeyboard.Format();
        if ((isDown &&
             (MatchesWindowBinding(configuration.SettingsMenuKeyboardBinding, virtualKey) ||
              MatchesWindowBinding(emergencySettings, virtualKey))) ||
            (isUp &&
             (MatchesWindowBindingPrimary(configuration.SettingsMenuKeyboardBinding, virtualKey) ||
              MatchesWindowBindingPrimary(emergencySettings, virtualKey))))
        {
            ObserveSettingsMenuKey(isDown, WindowHotkeySource);
            return true;
        }

        // Settings owns the complete keyboard while it is open. The settings
        // binding above remains available as the intentional close/emergency key.
        if (Volatile.Read(ref _settingsMenuOpen) != 0)
            return true;

        // The quick-action panel captures keyboard input so Escape and its own
        // toggle never leak into the game or trigger an action behind the panel.
        if (Volatile.Read(ref _quickActionsPanelOpen) != 0)
        {
            if ((isDown && virtualKey == VirtualKeyEscape) ||
                (isDown && MatchesWindowBinding(
                    configuration.QuickActionsKeyboardBinding,
                    virtualKey)))
            {
                SetQuickActionsPanelOpen(false);
            }
            return true;
        }

        var onlineRoomActive = IsOnlineRoomActive();
        if (configuration.EnableOverlay && onlineRoomActive)
        {
            var composerOpen = _session.Composer.IsOpen;
            if (isUp &&
                MatchesWindowBindingPrimary(configuration.OpenChatKeyboardBinding, virtualKey) &&
                Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 0) != 0)
            {
                return true;
            }
            if (!composerOpen &&
                isDown &&
                MatchesWindowBinding(configuration.OpenChatKeyboardBinding, virtualKey) &&
                TryRequestOpen())
            {
                Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 1);
                return true;
            }
        }

        var globalMuteBinding = configuration.GlobalMuteKeyboardBinding;
        if ((isDown && onlineRoomActive && MatchesWindowBinding(globalMuteBinding, virtualKey)) ||
            (isUp && MatchesWindowBindingPrimary(globalMuteBinding, virtualKey)))
        {
            ObserveGlobalMuteKey(isDown, WindowHotkeySource);
            return true;
        }

        if (!configuration.EnableOverlay)
            return false;

        if ((isDown &&
             MatchesWindowBinding(configuration.QuickActionsKeyboardBinding, virtualKey)) ||
            (isUp &&
             MatchesWindowBindingPrimary(configuration.QuickActionsKeyboardBinding, virtualKey)))
        {
            ObserveQuickActionsMenuKey(isDown, WindowHotkeySource);
            return true;
        }

        foreach (var action in configuration.QuickActions ?? [])
        {
            if (!action.Enabled || !action.IsConfigured ||
                !(isDown
                    ? MatchesWindowBinding(action.KeyboardBinding, virtualKey)
                    : MatchesWindowBindingPrimary(action.KeyboardBinding, virtualKey)))
            {
                continue;
            }
            ObserveQuickActionKey(action.Id, isDown, WindowHotkeySource);
            return true;
        }

        return false;
    }

    private void ObserveKeyboardFocusTransition(uint message, nint wParam)
    {
        var deactivated = message == WmKillFocus ||
            (message == WmActivate && ((nuint)wParam & 0xFFFF) == 0) ||
            (message == WmActivateApp && wParam == nint.Zero);
        if (!deactivated)
            return;

        lock (_bindingCaptureSync)
        {
            if (_bindingCapture is not { Device: BindingCaptureDevice.Keyboard })
                return;

            _keyboardCaptureCandidate = default;
            _captureWaitingForRelease = false;
            _captureStatusText = T(
                "窗口失去焦点，请重新按键。",
                "The window lost focus. Press the binding again.");
        }
    }

    private bool TryHandleBindingCaptureWindowMessage(
        ushort virtualKey,
        bool isDown,
        bool isUp)
    {
        lock (_bindingCaptureSync)
        {
            if (_bindingCapture is not { Device: BindingCaptureDevice.Keyboard })
                return false;

            if (_captureWaitingForRelease)
                return true;
            if (isDown && virtualKey == VirtualKeyEscape)
            {
                CancelBindingCapture();
                return true;
            }
            if (isDown && virtualKey == VirtualKeyBackspace)
            {
                CompleteBindingCapture(string.Empty);
                return true;
            }
            if (IsModifierVirtualKey(virtualKey))
                return true;

            if (isDown && !_keyboardCaptureCandidate.IsBound)
            {
                _keyboardCaptureCandidate = new KeyboardBinding(
                    virtualKey,
                    ReadWindowModifiers());
                _captureStatusText = T(
                    $"已捕获 {_keyboardCaptureCandidate.Format()}，松开确认。",
                    $"Captured {_keyboardCaptureCandidate.Format()}. Release to confirm.");
                return true;
            }
            if (isUp &&
                _keyboardCaptureCandidate.IsBound &&
                _keyboardCaptureCandidate.VirtualKey == virtualKey)
            {
                CompleteBindingCapture(_keyboardCaptureCandidate.Format());
            }
            return true;
        }
    }

    private bool HasActiveBindingCapture()
    {
        lock (_bindingCaptureSync)
            return _bindingCapture is not null;
    }

    private static bool MatchesWindowBinding(string? value, ushort virtualKey)
    {
        if (!KeyboardBinding.TryParse(value, out var binding) ||
            !binding.IsBound ||
            binding.VirtualKey != virtualKey)
        {
            return false;
        }
        var modifiers = ReadWindowModifiers();
        return (modifiers & binding.Modifiers) == binding.Modifiers;
    }

    private static bool MatchesWindowBindingPrimary(string? value, ushort virtualKey) =>
        KeyboardBinding.TryParse(value, out var binding) &&
        binding.IsBound &&
        binding.VirtualKey == virtualKey;

    private static KeyboardModifiers ReadWindowModifiers()
    {
        var modifiers = KeyboardModifiers.None;
        if ((GetKeyState(VirtualKeyControl) & 0x8000) != 0)
            modifiers |= KeyboardModifiers.Control;
        if ((GetKeyState(VirtualKeyShift) & 0x8000) != 0)
            modifiers |= KeyboardModifiers.Shift;
        if ((GetKeyState(VirtualKeyAlt) & 0x8000) != 0)
            modifiers |= KeyboardModifiers.Alt;
        return modifiers;
    }

    private static bool IsModifierVirtualKey(ushort virtualKey) => virtualKey is
        VirtualKeyShift or VirtualKeyControl or VirtualKeyAlt or
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private bool IsCapturingTextInput() =>
        _getConfiguration().EnableOverlay &&
        IsOnlineRoomActive() &&
        Volatile.Read(ref _captureKeyboard) != 0 &&
        _session.Composer.IsOpen;

    private bool ShouldIgnoreUnactivateBeforeBackend(uint message, nint wParam) =>
        IsCapturingTextInput() &&
        (message == WmKillFocus ||
         ((message is WmActivate or WmActivateApp) && wParam == nint.Zero));

    private bool TryHandleImeCharacter(nint windowHandle, uint message, nint wParam)
    {
        if (!IsCapturingTextInput())
        {
            Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
            return false;
        }

        var unicodeWindow = Win32ImeCompatibility.IsUnicodeWindow(windowHandle);
        if (message == Win32ImeCompatibility.WmImeChar)
        {
            Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
            ClearImeCandidateSnapshot();
            if (unicodeWindow)
            {
                ImGui.ImGuiIO_AddInputCharacterUTF16(
                    ImGui.GetIO(),
                    unchecked((ushort)wParam));
                LogImeCompatibility(windowHandle, 0);
                return true;
            }

            var codePage = Win32ImeCompatibility.GetActiveInputCodePage();
            if (Win32ImeCompatibility.TryDecodePackedAnsiCharacter(
                    unchecked((uint)wParam),
                    codePage,
                    out var committedText))
            {
                AddUtf8Input(committedText);
                LogImeCompatibility(windowHandle, codePage);
            }
            else
            {
                LogImeDecodeFailure(unchecked((uint)wParam), codePage);
            }

            // Do not let DefWindowProcA split a DBCS WM_IME_CHAR into two
            // Latin-1-looking WM_CHAR messages (for example CE D2 -> ÎÒ).
            return true;
        }

        if (message != WmChar || unicodeWindow)
            return false;

        var activeCodePage = Win32ImeCompatibility.GetActiveInputCodePage();
        var pendingLeadByte = Volatile.Read(ref _pendingAnsiLeadByte);
        if (Win32ImeCompatibility.TryConsumeAnsiWindowCharacter(
                unchecked((uint)wParam),
                activeCodePage,
                ref pendingLeadByte,
                out var text))
        {
            Volatile.Write(ref _pendingAnsiLeadByte, pendingLeadByte);
            AddUtf8Input(text);
            LogImeCompatibility(windowHandle, activeCodePage);
        }
        else
        {
            Volatile.Write(ref _pendingAnsiLeadByte, -1);
            LogImeDecodeFailure(unchecked((uint)wParam), activeCodePage);
        }

        // The ANSI message has either been queued as UTF-8 or retained as a
        // DBCS lead byte. In both cases the stock ImGui WM_CHAR path must not run.
        return true;
    }

    private bool ShouldRouteImeUiToDefault(uint message)
    {
        if (!IsCapturingTextInput() || !Win32ImeCompatibility.IsImeUiMessage(message))
            return false;

        if (message == Win32ImeCompatibility.WmImeEndComposition)
            Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
        return true;
    }

    private void ObserveImeUiMessage(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        if (!_getConfiguration().EnableImeCandidateFallback)
        {
            ClearImeCandidateSnapshot();
            return;
        }

        try
        {
            if (message == Win32ImeCompatibility.WmImeStartComposition)
            {
                ClearImeCandidateSnapshot();
                Interlocked.Exchange(ref _imeCompositionObserved, 1);
                Interlocked.Exchange(ref _imeCandidateCapturedInComposition, 0);
                return;
            }

            if (message == Win32ImeCompatibility.WmImeEndComposition)
            {
                var compositionObserved = Interlocked.Exchange(ref _imeCompositionObserved, 0) != 0;
                var candidateCaptured = Interlocked.Exchange(
                    ref _imeCandidateCapturedInComposition,
                    0) != 0;
                ClearImeCandidateSnapshot();
                if (compositionObserved &&
                    !candidateCaptured &&
                    Interlocked.Exchange(ref _imeCompositionWithoutCandidatesLogged, 1) == 0)
                {
                    LogSafely(
                        "Win32 IME composition ended without an IMM32 candidate list. " +
                        "This input method may expose candidates only through its external TSF/Qt UI.");
                }

                return;
            }

            uint candidateMask;
            var reportReadFailure = false;
            if (message == Win32ImeCompatibility.WmImeNotify)
            {
                var notification = unchecked((uint)(nuint)wParam);
                if (notification == Win32ImeCandidateReader.ImnCloseCandidate)
                {
                    ClearImeCandidateSnapshot();
                    return;
                }

                if (!Win32ImeCandidateReader.IsRefreshNotification(notification))
                    return;

                candidateMask = unchecked((uint)(nuint)lParam);
                reportReadFailure = notification is
                    Win32ImeCandidateReader.ImnOpenCandidate or
                    Win32ImeCandidateReader.ImnChangeCandidate;
            }
            else if (message == Win32ImeCompatibility.WmImeComposition)
            {
                Interlocked.Exchange(ref _imeCompositionObserved, 1);
                candidateMask = 1;
            }
            else
            {
                return;
            }

            if (Win32ImeCandidateReader.TryReadFirstCandidateList(
                    windowHandle,
                    candidateMask,
                    out var snapshot,
                    out var failure) &&
                snapshot is not null)
            {
                Volatile.Write(ref _imeCandidateSnapshot, snapshot);
                Interlocked.Exchange(ref _imeCandidateCapturedInComposition, 1);
                if (Interlocked.Exchange(ref _imeCandidateCaptureLogged, 1) == 0)
                {
                    LogSafely(
                        $"Win32 IME candidate fallback captured list {snapshot.ListIndex}: " +
                        $"count={snapshot.Count}, selection={snapshot.SelectedIndex}, " +
                        $"pageStart={snapshot.PageStart}, pageSize={snapshot.PageSize}; " +
                        "candidates are now drawn inside the Overlay.");
                }

                return;
            }

            if (reportReadFailure &&
                Interlocked.Exchange(ref _imeCandidateReadFailureLogged, 1) == 0)
            {
                LogSafely(
                    $"Win32 IME candidate notification did not expose a readable IMM32 list: {failure}; " +
                    "further read failures are suppressed.");
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _imeCandidateReadFailureLogged, 1) == 0)
            {
                LogSafely(
                    "Win32 IME candidate fallback recovered from an exception; " +
                    $"further failures are suppressed: {exception.GetType().Name}: {exception.Message}.");
            }
        }
    }

    private string? GetImeCandidateFallbackText(bool enabled)
    {
        if (!enabled)
            return null;

        var snapshot = Volatile.Read(ref _imeCandidateSnapshot);
        if (snapshot is null)
            return null;

        var displayText = snapshot.BuildDisplayText();
        return string.IsNullOrEmpty(displayText) ? null : displayText;
    }

    private static void DrawImeCandidateFallback(string? displayText)
    {
        if (!string.IsNullOrEmpty(displayText))
            ImGui.TextWrapped(displayText);
    }

    private void ClearImeCandidateSnapshot() =>
        Volatile.Write(ref _imeCandidateSnapshot, null);

    private static void AddUtf8Input(string text)
    {
        if (!string.IsNullOrEmpty(text))
            ImGui.ImGuiIO_AddInputCharactersUTF8(ImGui.GetIO(), text);
    }

    private void BindPlatformImeWindow()
    {
        var windowHandle = Volatile.Read(ref _windowHandle);
        if (windowHandle == nint.Zero)
            return;

        var viewport = ImGui.GetMainViewport();
        if (viewport.PlatformHandleRaw != windowHandle)
            viewport.PlatformHandleRaw = windowHandle;

        if (Interlocked.Exchange(ref _platformImeBridgeLogged, 1) == 0)
        {
            var platformCallbackAvailable = ImGui.GetIO().SetPlatformImeDataFn is not null;
            LogSafely(
                $"Dear ImGui platform IME bridge bound to game window 0x{unchecked((nuint)windowHandle):X}; " +
                $"platform callback available={platformCallbackAvailable}; " +
                "IMM32 composition and candidate positioning follow the active text caret.");
        }
    }

    private bool IsOnlineRoomActive()
    {
        try
        {
            return _isOnlineRoomActive();
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _onlineRoomGateFailureLogged, 1) == 0)
            {
                LogSafely(
                    $"Overlay online-room gate failed closed; further failures are suppressed: " +
                    $"{exception.GetType().Name}: {exception.Message}.");
            }

            return false;
        }
    }

    internal void SetSettingsMenuOpen(bool open)
    {
        var newValue = open ? 1 : 0;
        if (Interlocked.Exchange(ref _settingsMenuOpen, newValue) == newValue)
            return;

        if (open)
        {
            ResetChatInteractionState();
            SetQuickActionsPanelOpen(false);
            _settingsWindowOpen = true;
            _editedChatRect = null;
            _mouseInteractionGate.Open();
            _forceReleaseVoiceInputs();
            _audioSettings?.RefreshEndpointsAsync();
            UpdateInputCapture();
            LogSafely(
                "Settings opened; the game cursor lock/recenter path is suspended and " +
                "Win32, Raw Input, DirectInput keyboard and mouse are captured.");
            return;
        }

        UpdateInputCapture();
        _setLocalSelfTestRequested(false);
        _audioSettings?.FlushPendingLevelSave();
        _mouseInteractionGate.Close();
        PersistEditedChatLayout();
        _editedChatRect = null;
        CancelBindingCapture();
        lock (_bindingCaptureSync)
            _bindingConflictPending = null;
        LogSafely("Settings closed; held DirectInput keys and mouse buttons will drain before release.");
    }

    internal void SetQuickActionsPanelOpen(bool open)
    {
        Interlocked.Exchange(ref _quickActionsPanelOpen, open ? 1 : 0);
        UpdateInputCapture();
    }

    private void UpdateInputCapture()
    {
        var devices = OverlayInputDevices.None;
        if (Volatile.Read(ref _initialized) && Volatile.Read(ref _suspended) == 0)
        {
            if (Volatile.Read(ref _settingsMenuOpen) != 0)
            {
                devices = OverlayInputDevices.Keyboard |
                          OverlayInputDevices.Mouse |
                          OverlayInputDevices.Text;
            }
            else if (Volatile.Read(ref _quickActionsPanelOpen) != 0)
            {
                devices = OverlayInputDevices.Keyboard | OverlayInputDevices.Mouse;
            }
            else if (Volatile.Read(ref _captureKeyboard) != 0)
            {
                devices = OverlayInputDevices.Keyboard | OverlayInputDevices.Text;
            }
        }
        _registration?.SetInputCapture(devices);
    }

    private void PersistEditedChatLayout()
    {
        if (_editedChatRect is not { } rect || _editWorkWidth <= 0.0f || _editWorkHeight <= 0.0f)
            return;

        var ratios = ChatOverlayLayout.ToRatios(
            rect,
            _editWorkX,
            _editWorkY,
            _editWorkWidth,
            _editWorkHeight);
        try
        {
            _updateConfiguration(configuration =>
            {
                configuration.OverlayWidth = (int)MathF.Round(rect.Width);
                configuration.OverlayHeight = (int)MathF.Round(rect.Height);
                configuration.OverlayPositionXRatio = ratios.XRatio;
                configuration.OverlayPositionYRatio = ratios.YRatio;
            });
        }
        catch (Exception exception)
        {
            LogSafely($"Chat layout could not be persisted: {exception.Message}");
        }
    }

    private void ResetChatInteractionState()
    {
        _session.Composer.Cancel();
        Interlocked.Exchange(ref _openRequested, 0);
        Interlocked.Exchange(ref _captureKeyboard, 0);
        Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 0);
        Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
        ClearImeCandidateSnapshot();
        Interlocked.Exchange(ref _imeCompositionObserved, 0);
        Interlocked.Exchange(ref _imeCandidateCapturedInComposition, 0);
        _releaseCaptureFrames = 0;
        Interlocked.Exchange(ref _quickActionsPanelOpen, 0);
        Interlocked.Exchange(ref _quickActionsToggleRequested, 0);
        Interlocked.Exchange(ref _quickActionsToggleKeyDown, 0);
        _focusInputNextFrame = false;
        _statusText = null;
        UpdateInputCapture();
    }

    private void ResetInteractionState()
    {
        SetSettingsMenuOpen(false);
        ResetChatInteractionState();
        Interlocked.Exchange(ref _settingsToggleRequested, 0);
        Interlocked.Exchange(ref _settingsToggleKeyDown, 0);
        MouseButtonStateTracker.Reset();
    }

    private void NotifyOnlineRoomUnavailable()
    {
        try
        {
            _onOnlineRoomUnavailable();
        }
        catch (Exception exception)
        {
            LogSafely(
                $"Overlay online-room release callback failed closed: " +
                $"{exception.GetType().Name}: {exception.Message}.");
        }
    }

    public void OnHostUnavailable(string reason)
    {
        Volatile.Write(ref _initialized, false);
        ResetInteractionState();
        NotifyOnlineRoomUnavailable();
        LogSafely(
            $"Chat peer was isolated from the Overlay Broker; Extra Sigil and other healthy peers " +
            $"remain active. Reason: {reason}.");
        if (!reason.StartsWith("peer-local failure", StringComparison.Ordinal))
            _requestOverlayBrokerRecovery(reason);
    }

    public void Dispose()
    {
        Suspend();
        Volatile.Write(ref _initialized, false);
        Interlocked.Exchange(ref _registration, null)?.Dispose();
    }

    private void LogImeCompatibility(nint windowHandle, uint codePage)
    {
        if (Interlocked.Exchange(ref _imeCompatibilityLogged, 1) != 0)
            return;

        var windowKind = Win32ImeCompatibility.IsUnicodeWindow(windowHandle)
            ? "Unicode"
            : $"ANSI/code page {codePage}";
        LogSafely(
            $"Win32 IME compatibility active for the {windowKind} game window; " +
            "committed text is normalized to UTF-8 and candidate placement follows the chat input.");
    }

    private void LogImeCandidateUi(nint originalLParam, nint forwardedLParam)
    {
        if (Interlocked.Exchange(ref _imeCandidateUiLogged, 1) != 0)
            return;

        LogSafely(
            "Win32 IME candidate UI enabled for the active chat context: " +
            $"WM_IME_SETCONTEXT lParam 0x{unchecked((nuint)originalLParam):X} -> " +
            $"0x{unchecked((nuint)forwardedLParam):X}.");
    }

    private void LogImeDecodeFailure(uint rawCharacter, uint codePage)
    {
        if (Interlocked.Exchange(ref _imeDecodeFailureLogged, 1) != 0)
            return;

        LogSafely(
            $"Win32 IME discarded an undecodable ANSI character 0x{rawCharacter:X4} " +
            $"from code page {codePage}; further decode failures are suppressed.");
    }

    private void SyncInputBufferFromDraft() =>
        WriteUtf8Buffer(_inputBuffer, _session.Composer.Draft);

    private string ReadInputBuffer() => ReadUtf8Buffer(_inputBuffer);

    private static ImVec2 CreateVector2(float x, float y)
    {
        var vector = new ImVec2();
        vector.X = x;
        vector.Y = y;
        return vector;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    private void LogSafely(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // A logger must never let an exception cross a native render callback.
        }
    }

}

internal enum BindingTarget
{
    SettingsMenu,
    OpenChat,
    PushToTalk,
    QuickActionsPanel,
    GlobalMute,
    QuickAction,
}

internal enum BindingCaptureDevice
{
    Keyboard,
    Controller,
}

internal readonly record struct BindingCaptureRequest(
    BindingTarget Target,
    BindingCaptureDevice Device,
    string? QuickActionId,
    int PlayerNumber = 0);

internal readonly record struct PendingBindingConflict(
    BindingCaptureRequest Request,
    string Value,
    string Description);
