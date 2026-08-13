using System.Collections.Concurrent;
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
    private const int VirtualKeyLeftShift = 0xA0;
    private const int VirtualKeyRightShift = 0xA1;
    private const int VirtualKeyLeftControl = 0xA2;
    private const int VirtualKeyRightControl = 0xA3;
    private const int VirtualKeyLeftAlt = 0xA4;
    private const int VirtualKeyRightAlt = 0xA5;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmKillFocus = 0x0008;
    private const uint WmActivate = 0x0006;
    private const uint WmActivateApp = 0x001C;
    private const int InputBufferSize = 2_048;
    private const float ComposerReservedHeight = ChatOverlayLayout.ComposerReservedHeight;
    private static readonly TimeSpan RoomTransitionNoticeDuration = TimeSpan.FromSeconds(5);
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
    private readonly ChatBlacklist _chatBlacklist;
    private readonly Func<int?> _getHostPlayerNumber;
    private readonly Func<int, string?> _getRemotePlayerName;
    private readonly Func<string?> _getLocalPlayerName;
    private readonly Func<PartyVoiceIndicatorSnapshot> _getVoiceIndicatorSnapshot;
    private readonly Func<PartyRoomTransition?> _readRoomTransition;
    private readonly Func<PartyMemberTransition?> _readMemberTransition;
    private readonly Func<int> _getEstablishedVoiceParticipantCount;
    private readonly Func<DateTimeOffset> _getCurrentTime;
    private readonly XInputControllerPoller _controllerInputPoller = new();
    private readonly FlydigiExtendedControllerPoller _flydigiControllerInputPoller = new();
    private readonly VoicePushToTalkSafetyGate _windowVoicePushToTalkGate;
    private readonly Func<int, bool> _isWindowKeyDown;
    private readonly MouseInteractionGate _mouseInteractionGate = new();
    private readonly byte[] _inputBuffer = new byte[InputBufferSize];
    private readonly byte[] _quickActionNameBuffer = new byte[256];
    private readonly byte[] _quickActionTextBuffer = new byte[InputBufferSize];
    private readonly Dictionary<string, int> _quickActionKeyDown = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _pendingQuickActions = new();
    private readonly object _memberNameSync = new();
    private readonly Dictionary<string, string> _memberNamesByEntityId = new(StringComparer.Ordinal);
    private int _voicePushToTalkKeyDown;
    private int _windowVoicePushToTalkPhysicalDown;
    private int _globalMuteKeyDown;
    private bool _controllerGlobalMuteWasDown;
    private readonly int[] _remotePlayerChatMuteKeyDown = new int[3];
    private readonly bool[] _controllerRemotePlayerChatMuteWasDown = new bool[3];
    private readonly HashSet<int> _talkingRemotePlayers = [];
    private PartyVoiceIndicatorSnapshot _voiceIndicatorSnapshot = PartyVoiceIndicatorSnapshot.Unavailable;
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
    private string? _composerStatusText;
    private string? _transientNoticeText;
    private DateTimeOffset _transientNoticeExpiresAt;
    private string? _selectedQuickActionId;
    private BindingCaptureRequest? _bindingCapture;
    private BindingCaptureRequest? _bindingResultRequest;
    private string? _bindingResultText;
    private KeyboardBinding _keyboardCaptureCandidate;
    private ControllerBinding _controllerCaptureCandidate;
    private int _latestControllerButtonsMask;
    private int _latestExtendedControllerButtonsMask;
    private int _controllerInputAvailable;
    private int _nativeControllerInputAvailable;
    private int _managedControllerApiAvailable;
    private int _managedControllerConnected;
    private int _flydigiControllerConnected;
    private int _flydigiControllerReady;
    private int _flydigiControllerStatus;
    private ulong _lastManagedControllerSequence = ulong.MaxValue;
    private ulong _lastFlydigiControllerSequence = ulong.MaxValue;
    private bool _controllerSettingsWasDown;
    private bool _controllerOpenChatWasDown;
    private bool _controllerPushToTalkWasDown;
    private bool _controllerPushToTalkPhysicalWasDown;
    private bool _controllerQuickActionsWasDown;
    private bool _managedControllerReleasePending = true;
    private bool _captureWaitingForRelease;
    private string? _captureStatusText;
    private string? _playerMuteStatusText;
    private string? _voiceMuteStatusText;
    private string? _lastVoiceIndicatorSnapshotFingerprint;
    private int _voiceIndicatorSnapshotFailureLogged;

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
        Action<string> log,
        ChatBlacklist? chatBlacklist = null,
        Func<int?>? getHostPlayerNumber = null,
        Func<int, string?>? getRemotePlayerName = null,
        Func<PartyVoiceIndicatorSnapshot>? getVoiceIndicatorSnapshot = null,
        Func<PartyRoomTransition?>? readRoomTransition = null,
        Func<int>? getEstablishedVoiceParticipantCount = null,
        Func<DateTimeOffset>? getCurrentTime = null,
        Func<Action<bool>, VoicePushToTalkSafetyGate>? createWindowVoicePushToTalkGate = null,
        Func<int, bool>? isWindowKeyDown = null,
        Func<string?>? getLocalPlayerName = null,
        Func<PartyMemberTransition?>? readMemberTransition = null)
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
        _chatBlacklist = chatBlacklist ?? new ChatBlacklist();
        _getHostPlayerNumber = getHostPlayerNumber ?? (() => null);
        _getRemotePlayerName = getRemotePlayerName ?? (_ => null);
        _getLocalPlayerName = getLocalPlayerName ?? (() => null);
        _getVoiceIndicatorSnapshot = getVoiceIndicatorSnapshot ??
            (() => PartyVoiceIndicatorSnapshot.Unavailable);
        _readRoomTransition = readRoomTransition ?? (() => null);
        _readMemberTransition = readMemberTransition ?? (() => null);
        _getEstablishedVoiceParticipantCount = getEstablishedVoiceParticipantCount ?? (() => 0);
        _getCurrentTime = getCurrentTime ?? (() => DateTimeOffset.UtcNow);
        _isWindowKeyDown = isWindowKeyDown ?? (virtualKey =>
            OperatingSystem.IsWindows() && (GetAsyncKeyState(virtualKey) & 0x8000) != 0);
        Action<bool> reportWindowVoicePushToTalk = pressed =>
            ObserveVoicePushToTalkKey(pressed, WindowHotkeySource);
        _windowVoicePushToTalkGate = createWindowVoicePushToTalkGate?.Invoke(reportWindowVoicePushToTalk) ??
            new VoicePushToTalkSafetyGate(
                reportWindowVoicePushToTalk,
                _log,
                operationName: "window push-to-talk");
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
            _pendingQuickActions.Enqueue(actionId);
    }

    public void ObserveVoicePushToTalkKey(bool pressed) =>
        ObserveVoicePushToTalkKey(pressed, NativeHotkeySource);

    private void ObserveVoicePushToTalkKey(bool pressed, int source)
    {
        if (pressed && (!Volatile.Read(ref _initialized) || Volatile.Read(ref _suspended) != 0))
            return;

        var previous = UpdateSourceMask(ref _voicePushToTalkKeyDown, source, pressed);
        var current = pressed ? previous | source : previous & ~source;
        if (previous == 0 && current != 0)
        {
            _setVoicePushToTalkPressed(true);
            _statusText = T("[语音] 正在通话中", "[Voice] Transmitting");
        }
        else if (previous != 0 && current == 0)
        {
            _setVoicePushToTalkPressed(false);
            var activeStatus = T("[语音] 正在通话中", "[Voice] Transmitting");
            if (string.Equals(_statusText, activeStatus, StringComparison.Ordinal))
                _statusText = null;
        }
    }

    public void ObserveGlobalMuteKey(bool pressed) =>
        ObserveGlobalMuteKey(pressed, NativeHotkeySource);

    public void ObserveRemotePlayerChatMuteKey(int remotePlayerNumber, bool pressed) =>
        ObserveRemotePlayerChatMuteKey(remotePlayerNumber, pressed, NativeHotkeySource);

    private void ObserveRemotePlayerChatMuteKey(int remotePlayerNumber, bool pressed, int source)
    {
        if (remotePlayerNumber is < 1 or > 3)
            return;
        var previous = UpdateSourceMask(
            ref _remotePlayerChatMuteKeyDown[remotePlayerNumber - 1],
            source,
            pressed);
        if (pressed && previous == 0)
            TogglePlayerMute(remotePlayerNumber + 1);
    }

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
            var targetMuted = _chatBlacklist.ToggleAllRemotePlayers();
            var message = targetMuted
                ? T("已全局禁言。", "All chat muted.")
                : T("已解除全局禁言。", "Global chat mute cleared.");
            _playerMuteStatusText = message;
            _statusText = message;
            LogSafely($"Global chat blacklist toggled: blocked={targetMuted}.");
        }
        catch (Exception exception)
        {
            var message = T("全局聊天禁言失败。", "Could not toggle the chat blacklist.");
            _playerMuteStatusText = message;
            _statusText = message;
            LogSafely(
                $"Global chat blacklist hotkey recovered from an exception: " +
                $"{exception.GetType().Name}: {exception.Message}.");
        }
    }

    private void TogglePlayerMute(int playerNumber)
    {
        try
        {
            var muted = _chatBlacklist.Toggle(playerNumber);
            var remotePlayerNumber = playerNumber - 1;
            var playerName = ResolveRemotePlayerName(remotePlayerNumber, playerNumber);
            var message = muted
                ? T($"已禁言 {playerName}。", $"Muted {playerName}.")
                : T($"已解除禁言 {playerName}。", $"Unmuted {playerName}.");
            _playerMuteStatusText = message;
            _statusText = message;
        }
        catch (Exception exception)
        {
            var message = T("切换聊天禁言失败。", "Could not toggle the chat blacklist.");
            _playerMuteStatusText = message;
            _statusText = message;
            LogSafely(
                $"Player {playerNumber} mute hotkey recovered from an exception: " +
                $"{exception.GetType().Name}: {exception.Message}.");
        }
    }

    private string ResolveRemotePlayerName(
        int remotePlayerNumber,
        int? fallbackPlayerNumber = null,
        string? fallbackLabel = null)
    {
        try
        {
            var playerName = _getRemotePlayerName(remotePlayerNumber)?.Trim();
            if (!string.IsNullOrWhiteSpace(playerName))
                return playerName;
        }
        catch (Exception exception)
        {
            LogSafely(
                $"Remote player {remotePlayerNumber} name lookup failed; the stable slot label was kept: " +
                $"{exception.GetType().Name}: {exception.Message}.");
        }

        if (!string.IsNullOrWhiteSpace(fallbackLabel))
            return fallbackLabel;

        var displayedPlayerNumber = fallbackPlayerNumber ?? remotePlayerNumber;
        return T($"玩家 {displayedPlayerNumber}", $"Player {displayedPlayerNumber}");
    }

    private void ObserveRemoteVoiceActivity(IReadOnlyList<int> talkingPlayers)
    {
        var current = talkingPlayers
            .Where(static playerNumber => playerNumber is >= 1 and <= 3)
            .Distinct()
            .ToHashSet();
        _talkingRemotePlayers.Clear();
        _talkingRemotePlayers.UnionWith(current);
    }

    private string ResolveLocalPlayerName()
    {
        try
        {
            var playerName = _getLocalPlayerName()?.Trim();
            if (!string.IsNullOrWhiteSpace(playerName))
                return playerName;
        }
        catch (Exception exception)
        {
            LogSafely(
                $"Local player name lookup failed; using the localized self label: " +
                $"{exception.GetType().Name}: {exception.Message}.");
        }

        return T("你", "You");
    }

    private IReadOnlyList<string> ResolveVoiceTalkerNames(
        PartyVoiceUiStatus voiceUiStatus,
        PartyVoiceIndicatorSnapshot snapshot)
    {
        if (voiceUiStatus.State is not (PartyVoiceUiState.Ready or PartyVoiceUiState.Speaking))
        {
            return Array.Empty<string>();
        }

        var remoteTalkers = snapshot.IsValid
            ? NormalizeVoiceIndicatorPlayers(snapshot.TalkingRemotePlayers)
            : Array.Empty<int>();
        var talkerNames = new List<string>(remoteTalkers.Count + 1);
        if (voiceUiStatus.State == PartyVoiceUiState.Speaking)
            talkerNames.Add(ResolveLocalPlayerName());

        foreach (var remotePlayerNumber in remoteTalkers)
            talkerNames.Add(ResolveRemotePlayerName(
                remotePlayerNumber,
                remotePlayerNumber + 1));

        return talkerNames.Count == 0
            ? Array.Empty<string>()
            : Array.AsReadOnly(talkerNames.ToArray());
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
        {
            ObserveControllerBindingCapture(
                snapshot.ControllerButtons,
                (ExtendedControllerButtons)Volatile.Read(ref _latestExtendedControllerButtonsMask));
        }
    }

    private void PollManagedControllerInput()
    {
        var snapshot = _controllerInputPoller.Poll();
        var flydigiSnapshot = _flydigiControllerInputPoller.Poll();
        Volatile.Write(ref _managedControllerApiAvailable, snapshot.ApiAvailable ? 1 : 0);
        Volatile.Write(ref _managedControllerConnected, snapshot.IsConnected ? 1 : 0);
        Volatile.Write(ref _flydigiControllerConnected, flydigiSnapshot.IsConnected ? 1 : 0);
        Volatile.Write(
            ref _flydigiControllerReady,
            flydigiSnapshot.IsReady ? 1 : 0);
        ObserveFlydigiControllerStatus(flydigiSnapshot);
        UpdateControllerInputAvailability();

        var xinputChanged = snapshot.Sequence != _lastManagedControllerSequence;
        var flydigiChanged = flydigiSnapshot.Sequence != _lastFlydigiControllerSequence;
        if (!snapshot.ApiAvailable && !flydigiSnapshot.IsConnected)
        {
            ResetManagedControllerHotkeys();
            return;
        }
        if (!xinputChanged && !flydigiChanged)
            return;

        _lastManagedControllerSequence = snapshot.Sequence;
        _lastFlydigiControllerSequence = flydigiSnapshot.Sequence;
        var standardButtons = snapshot.ApiAvailable
            ? snapshot.Buttons
            : ControllerButtons.None;
        var extendedButtons = flydigiSnapshot.IsReady
            ? flydigiSnapshot.Buttons
            : ExtendedControllerButtons.None;
        if (snapshot.ApiAvailable)
            Volatile.Write(ref _latestControllerButtonsMask, (int)standardButtons);
        Volatile.Write(ref _latestExtendedControllerButtonsMask, (int)extendedButtons);
        var captureWasActive = IsControllerBindingCaptureActive();
        var captureStandardButtons = snapshot.ApiAvailable
            ? standardButtons
            : (ControllerButtons)Volatile.Read(ref _latestControllerButtonsMask);
        ObserveControllerBindingCapture(captureStandardButtons, extendedButtons);
        if (captureWasActive || IsControllerBindingCaptureActive())
        {
            ResetManagedControllerHotkeys();
            return;
        }
        if (_managedControllerReleasePending)
        {
            ResetManagedControllerHotkeys();
            if (standardButtons == ControllerButtons.None &&
                extendedButtons == ExtendedControllerButtons.None)
            {
                _managedControllerReleasePending = false;
            }
            return;
        }
        ProcessManagedControllerHotkeys(standardButtons, extendedButtons);
    }

    private void ObserveControllerBindingCapture(
        ControllerButtons buttons,
        ExtendedControllerButtons extendedButtons)
    {
        lock (_bindingCaptureSync)
        {
            if (_bindingCapture is not { Device: BindingCaptureDevice.Controller })
                return;
            var allReleased = buttons == ControllerButtons.None &&
                              extendedButtons == ExtendedControllerButtons.None;
            if (_captureWaitingForRelease)
            {
                if (allReleased)
                {
                    _captureWaitingForRelease = false;
                    _captureStatusText = GetControllerCapturePrompt();
                }
                return;
            }
            if (!_controllerCaptureCandidate.IsBound)
            {
                if (allReleased)
                    return;
                if ((buttons & ControllerButtons.DPadDown) != 0)
                {
                    _captureStatusText = T(
                        "DPadDown 是游戏官方快捷短语保留键，不能绑定到模组功能。",
                        "DPadDown is reserved for the game's official quick phrase and cannot be bound to a mod action.");
                    return;
                }
                var candidate = new ControllerBinding(buttons, extendedButtons);
                var buttonCount = BitOperations.PopCount((uint)buttons) +
                                  BitOperations.PopCount((uint)extendedButtons);
                if (buttonCount > 2)
                {
                    _captureStatusText = T(
                        "最多两个按键。",
                        "Two buttons maximum.");
                    return;
                }
                _controllerCaptureCandidate = candidate;
                _captureStatusText = T(
                    $"{candidate.Format()}，松开确认。",
                    $"{candidate.Format()}; release to confirm.");
                return;
            }
            if (allReleased)
                CompleteBindingCapture(_controllerCaptureCandidate.Format());
        }
    }

    private bool IsControllerBindingCaptureActive()
    {
        lock (_bindingCaptureSync)
            return _bindingCapture is { Device: BindingCaptureDevice.Controller };
    }

    private void ProcessManagedControllerHotkeys(
        ControllerButtons buttons,
        ExtendedControllerButtons extendedButtons)
    {
        var configuration = _getConfiguration();
        var settingsDown = IsControllerBindingPressed(
            configuration.SettingsMenuControllerBinding,
            buttons,
            extendedButtons);
        if (settingsDown != _controllerSettingsWasDown)
            ObserveSettingsMenuKey(settingsDown, ControllerHotkeySource);
        _controllerSettingsWasDown = settingsDown;

        var inputCaptured = ShouldCaptureKeyboard();
        var openChatDown = !inputCaptured &&
            IsControllerBindingPressed(
                configuration.OpenChatControllerBinding,
                buttons,
                extendedButtons);
        if (openChatDown && !_controllerOpenChatWasDown)
            TryRequestOpen();
        _controllerOpenChatWasDown = openChatDown;

        var pushToTalkPhysicalDown = !inputCaptured &&
            IsControllerBindingPressed(
                configuration.PushToTalkControllerBinding,
                buttons,
                extendedButtons);
        if (!pushToTalkPhysicalDown)
        {
            if (_controllerPushToTalkWasDown)
                ObserveVoicePushToTalkKey(false, ControllerHotkeySource);
            _controllerPushToTalkWasDown = false;
        }
        else if (!_controllerPushToTalkPhysicalWasDown && _canUseVoicePushToTalk())
        {
            ObserveVoicePushToTalkKey(true, ControllerHotkeySource);
            _controllerPushToTalkWasDown = true;
        }
        else if (_controllerPushToTalkWasDown && !_canUseVoicePushToTalk())
        {
            ObserveVoicePushToTalkKey(false, ControllerHotkeySource);
            _controllerPushToTalkWasDown = false;
        }
        _controllerPushToTalkPhysicalWasDown = pushToTalkPhysicalDown;

        var officialActionsAvailable = configuration.EnableOverlay;
        var customActionsAvailable = configuration.EnableOverlay;
        var quickActionsPanelAvailable = officialActionsAvailable || customActionsAvailable;
        var quickActionsDown = !inputCaptured &&
            quickActionsPanelAvailable &&
            IsControllerBindingPressed(
                configuration.QuickActionsControllerBinding,
                buttons,
                extendedButtons);
        if (quickActionsDown != _controllerQuickActionsWasDown)
            ObserveQuickActionsMenuKey(quickActionsDown, ControllerHotkeySource);
        _controllerQuickActionsWasDown = quickActionsDown;

        var globalMuteDown = !inputCaptured &&
            IsOnlineRoomActive() &&
            IsControllerBindingPressed(
                configuration.GlobalMuteControllerBinding,
                buttons,
                extendedButtons);
        if (globalMuteDown != _controllerGlobalMuteWasDown)
            ObserveGlobalMuteKey(globalMuteDown, ControllerHotkeySource);
        _controllerGlobalMuteWasDown = globalMuteDown;

        for (var remotePlayerNumber = 1; remotePlayerNumber <= 3; remotePlayerNumber++)
        {
            var index = remotePlayerNumber - 1;
            var playerMuteDown = !inputCaptured &&
                IsOnlineRoomActive() &&
                IsControllerBindingPressed(
                    GetRemotePlayerChatMuteControllerBinding(configuration, remotePlayerNumber),
                    buttons,
                    extendedButtons);
            if (playerMuteDown != _controllerRemotePlayerChatMuteWasDown[index])
            {
                ObserveRemotePlayerChatMuteKey(
                    remotePlayerNumber,
                    playerMuteDown,
                    ControllerHotkeySource);
            }
            _controllerRemotePlayerChatMuteWasDown[index] = playerMuteDown;
        }

    }

    private void ResetManagedControllerHotkeys()
    {
        if (_controllerSettingsWasDown)
            ObserveSettingsMenuKey(false, ControllerHotkeySource);
        if (_controllerPushToTalkWasDown)
            ObserveVoicePushToTalkKey(false, ControllerHotkeySource);
        if (_controllerQuickActionsWasDown)
            ObserveQuickActionsMenuKey(false, ControllerHotkeySource);
        if (_controllerGlobalMuteWasDown)
            ObserveGlobalMuteKey(false, ControllerHotkeySource);
        for (var index = 0; index < _controllerRemotePlayerChatMuteWasDown.Length; index++)
        {
            if (_controllerRemotePlayerChatMuteWasDown[index])
                ObserveRemotePlayerChatMuteKey(index + 1, false, ControllerHotkeySource);
            _controllerRemotePlayerChatMuteWasDown[index] = false;
        }
        _controllerSettingsWasDown = false;
        _controllerOpenChatWasDown = false;
        _controllerPushToTalkWasDown = false;
        _controllerPushToTalkPhysicalWasDown = false;
        _controllerQuickActionsWasDown = false;
        _controllerGlobalMuteWasDown = false;
    }

    private static bool IsControllerBindingPressed(
        string? value,
        ControllerButtons buttons,
        ExtendedControllerButtons extendedButtons) =>
        ControllerBinding.TryParse(value, out var binding) &&
        binding.IsPressed(buttons, extendedButtons);

    private static string GetRemotePlayerChatMuteKeyboardBinding(
        Config configuration,
        int remotePlayerNumber) => remotePlayerNumber switch
        {
            1 => configuration.RemotePlayer1ChatMuteKeyboardBinding,
            2 => configuration.RemotePlayer2ChatMuteKeyboardBinding,
            3 => configuration.RemotePlayer3ChatMuteKeyboardBinding,
            _ => string.Empty,
        };

    private static string GetRemotePlayerChatMuteControllerBinding(
        Config configuration,
        int remotePlayerNumber) => remotePlayerNumber switch
        {
            1 => configuration.RemotePlayer1ChatMuteControllerBinding,
            2 => configuration.RemotePlayer2ChatMuteControllerBinding,
            3 => configuration.RemotePlayer3ChatMuteControllerBinding,
            _ => string.Empty,
        };

    private static void SetRemotePlayerChatMuteKeyboardBinding(
        Config configuration,
        int remotePlayerNumber,
        string value)
    {
        switch (remotePlayerNumber)
        {
            case 1:
                configuration.RemotePlayer1ChatMuteKeyboardBinding = value;
                break;
            case 2:
                configuration.RemotePlayer2ChatMuteKeyboardBinding = value;
                break;
            case 3:
                configuration.RemotePlayer3ChatMuteKeyboardBinding = value;
                break;
        }
    }

    private static void SetRemotePlayerChatMuteControllerBinding(
        Config configuration,
        int remotePlayerNumber,
        string value)
    {
        switch (remotePlayerNumber)
        {
            case 1:
                configuration.RemotePlayer1ChatMuteControllerBinding = value;
                break;
            case 2:
                configuration.RemotePlayer2ChatMuteControllerBinding = value;
                break;
            case 3:
                configuration.RemotePlayer3ChatMuteControllerBinding = value;
                break;
        }
    }

    private void UpdateControllerInputAvailability() =>
        Volatile.Write(
            ref _controllerInputAvailable,
            Volatile.Read(ref _managedControllerConnected) != 0 ||
            (Volatile.Read(ref _flydigiControllerConnected) != 0 &&
             Volatile.Read(ref _flydigiControllerReady) != 0) ||
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

    private void RefreshWindowVoicePushToTalkHeartbeat()
    {
        if (Volatile.Read(ref _windowVoicePushToTalkPhysicalDown) == 0)
            return;

        var configuration = _getConfiguration();
        if (!KeyboardBinding.TryParse(configuration.PushToTalkKeyboardBinding, out var binding) ||
            !binding.IsBound ||
            !_isWindowKeyDown(binding.VirtualKey) ||
            !AreWindowVoicePushToTalkModifiersDown(binding.Modifiers))
        {
            Interlocked.Exchange(ref _windowVoicePushToTalkPhysicalDown, 0);
            _windowVoicePushToTalkGate.Report(false);
            return;
        }

        if (!_canUseVoicePushToTalk())
        {
            if ((Volatile.Read(ref _voicePushToTalkKeyDown) & WindowHotkeySource) != 0)
            {
                LogSafely(
                    "Party voice window push-to-talk hold was revoked because Party voice is no longer ready; " +
                    "release the physical key before retrying.");
            }
            _windowVoicePushToTalkGate.Report(false);
            return;
        }

        if ((Volatile.Read(ref _voicePushToTalkKeyDown) & WindowHotkeySource) != 0)
            _windowVoicePushToTalkGate.Report(true);
    }

    private bool AreWindowVoicePushToTalkModifiersDown(KeyboardModifiers modifiers) =>
        (!modifiers.HasFlag(KeyboardModifiers.Control) ||
         _isWindowKeyDown(VirtualKeyControl) || _isWindowKeyDown(VirtualKeyLeftControl) ||
         _isWindowKeyDown(VirtualKeyRightControl)) &&
        (!modifiers.HasFlag(KeyboardModifiers.Shift) ||
         _isWindowKeyDown(VirtualKeyShift) || _isWindowKeyDown(VirtualKeyLeftShift) ||
         _isWindowKeyDown(VirtualKeyRightShift)) &&
        (!modifiers.HasFlag(KeyboardModifiers.Alt) ||
         _isWindowKeyDown(VirtualKeyAlt) || _isWindowKeyDown(VirtualKeyLeftAlt) ||
         _isWindowKeyDown(VirtualKeyRightAlt));

    private void ForceReleaseVoicePushToTalkSources()
    {
        Interlocked.Exchange(ref _windowVoicePushToTalkPhysicalDown, 0);
        _windowVoicePushToTalkGate.ForceMute();
        if (Interlocked.Exchange(ref _voicePushToTalkKeyDown, 0) != 0)
            _setVoicePushToTalkPressed(false);
    }

    private void ObserveFlydigiControllerStatus(FlydigiExtendedControllerSnapshot snapshot)
    {
        var current = snapshot.AccessBlocked
            ? 5
            : !snapshot.IsConnected
            ? 0
            : !snapshot.TakeoverStatusKnown
                ? 1
                : !snapshot.TakeoverAllowed
                    ? 3
                    : snapshot.AcquisitionStatusKnown && !snapshot.AcquisitionSucceeded
                        ? 4
                        : snapshot.IsReady ? 2 : 1;
        var previous = Interlocked.Exchange(ref _flydigiControllerStatus, current);
        if (current == previous)
            return;

        if (current == 2)
        {
            _managedControllerReleasePending = true;
            LogSafely("Flydigi Vader 5 Pro extended buttons are ready.");
        }
        else if (current == 3)
        {
            lock (_bindingCaptureSync)
            {
                if (_bindingCapture is { Device: BindingCaptureDevice.Controller } &&
                    !_controllerCaptureCandidate.IsBound)
                {
                    _captureStatusText = GetControllerCapturePrompt();
                }
            }
            LogSafely("Flydigi Vader 5 Pro is connected, but third-party mapping takeover is disabled.");
        }
        else if (current == 4)
        {
            lock (_bindingCaptureSync)
            {
                if (_bindingCapture is { Device: BindingCaptureDevice.Controller } &&
                    !_controllerCaptureCandidate.IsBound)
                {
                    _captureStatusText = GetControllerCapturePrompt();
                }
            }
            LogSafely("Flydigi Vader 5 Pro acquisition was rejected; Steam Input or another mapping client may already own it.");
        }
        else if (current == 5)
        {
            lock (_bindingCaptureSync)
            {
                if (_bindingCapture is { Device: BindingCaptureDevice.Controller } &&
                    !_controllerCaptureCandidate.IsBound)
                {
                    _captureStatusText = GetControllerCapturePrompt();
                }
            }
            LogSafely("Flydigi Vader 5 Pro was detected, but its HID interface could not be opened.");
        }
    }

    private string GetControllerCapturePrompt() => Volatile.Read(ref _flydigiControllerStatus) switch
    {
        1 => T(
            "正在等待飞智接管回应。若一直无响应，Steam Input、飞智空间站或其他程序可能正在占用；也可把扩展键映射到 F13–F21，再点“键盘”绑定。",
            "Waiting for the Flydigi takeover response. If this persists, Steam Input, Space Station, or another program may own it; you can also map extras to F13–F21 and bind them as Keyboard keys."),
        3 => T(
            "请按 1–2 个手柄键。飞智扩展键可关闭本游戏的 Steam Input 后启用第三方接管；若必须保留 Steam Input，请在空间站把扩展键映射到 F13–F21，再点“键盘”绑定。",
            "Press 1–2 controller buttons. For Flydigi extras, disable Steam Input for this game and enable third-party takeover; to keep Steam Input, map extras to F13–F21 in Space Station and bind them as Keyboard keys."),
        4 or 5 => T(
            "飞智扩展接口正被 Steam Input、飞智空间站或其他程序占用。请关闭其中一个接管方后重连；或把扩展键映射到 F13–F21，再点“键盘”绑定。",
            "The Flydigi extra-button interface is occupied by Steam Input, Space Station, or another program. Close one mapping client and reconnect, or map extras to F13–F21 and bind them as Keyboard keys."),
        _ => T("请按 1–2 个手柄键。", "Press 1–2 controller buttons."),
    };

    private void RefreshVoiceIndicatorSnapshot()
    {
        PartyVoiceIndicatorSnapshot snapshot;
        try
        {
            snapshot = _getVoiceIndicatorSnapshot() ?? PartyVoiceIndicatorSnapshot.Unavailable;
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _voiceIndicatorSnapshotFailureLogged, 1) == 0)
            {
                LogSafely(
                    $"Voice indicator membership snapshot lookup failed; formal icons are hidden " +
                    $"until the lookup recovers: {exception.GetType().Name}: {exception.Message}.");
            }
            PublishVoiceIndicatorSnapshotUnavailable();
            return;
        }

        if (Interlocked.Exchange(ref _voiceIndicatorSnapshotFailureLogged, 0) != 0)
            LogSafely("Voice indicator membership snapshot lookup recovered.");

        var normalizedSnapshot = NormalizeVoiceIndicatorSnapshot(snapshot);
        LogVoiceIndicatorSnapshotTransition(normalizedSnapshot);
        Volatile.Write(ref _voiceIndicatorSnapshot, normalizedSnapshot);
        if (normalizedSnapshot.IsValid)
            ObserveRemoteVoiceActivity(normalizedSnapshot.TalkingRemotePlayers);
        else
            _talkingRemotePlayers.Clear();
    }

    private static PartyVoiceIndicatorSnapshot NormalizeVoiceIndicatorSnapshot(
        PartyVoiceIndicatorSnapshot snapshot)
    {
        if (!snapshot.IsValid)
            return PartyVoiceIndicatorSnapshot.Unavailable;

        return new PartyVoiceIndicatorSnapshot(
            true,
            NormalizeVoiceIndicatorPlayers(snapshot.EstablishedRemotePlayers),
            NormalizeVoiceIndicatorPlayers(snapshot.OccupiedRemotePlayers),
            NormalizeVoiceIndicatorPlayers(snapshot.TalkingRemotePlayers));
    }

    private static IReadOnlyList<int> NormalizeVoiceIndicatorPlayers(
        IReadOnlyList<int>? players)
    {
        if (players is null || players.Count == 0)
            return Array.Empty<int>();

        var normalized = players
            .Where(static playerNumber => playerNumber is >= 1 and <= 3)
            .Distinct()
            .OrderBy(static playerNumber => playerNumber)
            .ToArray();
        return normalized.Length == 0 ? Array.Empty<int>() : Array.AsReadOnly(normalized);
    }

    private void LogVoiceIndicatorSnapshotTransition(PartyVoiceIndicatorSnapshot snapshot)
    {
        var fingerprint = snapshot.IsValid
            ? $"valid|{FormatVoiceIndicatorPlayers(snapshot.EstablishedRemotePlayers)}|" +
              $"{FormatVoiceIndicatorPlayers(snapshot.OccupiedRemotePlayers)}|" +
              $"{FormatVoiceIndicatorPlayers(snapshot.TalkingRemotePlayers)}"
            : "unavailable";
        if (string.Equals(
                _lastVoiceIndicatorSnapshotFingerprint,
                fingerprint,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastVoiceIndicatorSnapshotFingerprint = fingerprint;
        LogSafely(
            $"Voice indicator membership snapshot changed: valid={snapshot.IsValid}, " +
            $"established=[{FormatVoiceIndicatorPlayers(snapshot.EstablishedRemotePlayers)}], " +
            $"occupied=[{FormatVoiceIndicatorPlayers(snapshot.OccupiedRemotePlayers)}], " +
            $"talking=[{FormatVoiceIndicatorPlayers(snapshot.TalkingRemotePlayers)}].");
    }

    private static string FormatVoiceIndicatorPlayers(IReadOnlyList<int> players) =>
        players.Count == 0 ? string.Empty : string.Join(',', players);

    private void PublishVoiceIndicatorSnapshotUnavailable()
    {
        Volatile.Write(
            ref _voiceIndicatorSnapshot,
            PartyVoiceIndicatorSnapshot.Unavailable);
        _talkingRemotePlayers.Clear();
        _lastVoiceIndicatorSnapshotFingerprint = null;
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
                   (configuration.EnableVoiceIndicators &&
                    (configuration.EffectiveShowAllVoiceIndicatorSlots || IsOnlineRoomActive())) ||
                   (configuration.EnableOverlay &&
                    (IsOnlineRoomActive() || HasActiveTransientNotice()));
        }
    }

    public void Tick()
    {
        if (!Volatile.Read(ref _initialized) || Volatile.Read(ref _suspended) != 0)
            return;
        RefreshWindowVoicePushToTalkHeartbeat();
        PollManagedControllerInput();
        var configuration = _getConfiguration();
        DrainMemberTransitions();
        var roomTransitionExited = DrainRoomTransitions();
        if (!configuration.EnableImeCandidateFallback)
            ClearImeCandidateSnapshot();
        var onlineRoomActive = IsOnlineRoomActive();
        var previousOnlineRoomInactive = Interlocked.Exchange(
            ref _onlineRoomWasInactive,
            onlineRoomActive ? 0 : 1);
        if (roomTransitionExited || (!onlineRoomActive && previousOnlineRoomInactive == 0))
            ClearMemberNameCache();
        if (!onlineRoomActive && previousOnlineRoomInactive == 0)
        {
            _chatBlacklist.Clear();
            _session.Composer.Cancel(clearDraft: true);
            _session.InputHistory.ResetNavigation();
            Array.Clear(_inputBuffer);
            _playerMuteStatusText = null;
            _voiceMuteStatusText = null;
            PublishVoiceIndicatorSnapshotUnavailable();
            NotifyOnlineRoomUnavailable();
            ResetChatInteractionState();
        }
        else if (onlineRoomActive && previousOnlineRoomInactive != 0)
        {
            _chatBlacklist.Clear();
            _session.Composer.Cancel(clearDraft: true);
            _session.InputHistory.ResetNavigation();
            Array.Clear(_inputBuffer);
            _playerMuteStatusText = null;
            _voiceMuteStatusText = null;
            _talkingRemotePlayers.Clear();
            LogSafely(
                "Relink online Party room became active; configured chat, voice and quick-action hotkeys are enabled. " +
                "The configured settings binding remains available in every scene.");
        }
        if (onlineRoomActive)
        {
            RefreshVoiceIndicatorSnapshot();
            _session.DrainIncoming();
        }
        else
        {
            PublishVoiceIndicatorSnapshotUnavailable();
        }
    }

    private bool DrainRoomTransitions()
    {
        var hasExitedTransition = false;
        while (true)
        {
            PartyRoomTransition? pending;
            try
            {
                pending = _readRoomTransition();
            }
            catch (Exception exception)
            {
                LogSafely(
                    $"Room transition reader failed; remaining transitions were deferred: " +
                    $"{exception.GetType().Name}: {exception.Message}.");
                return hasExitedTransition;
            }

            if (pending is not { } transition)
                return hasExitedTransition;

            var language = CurrentLanguage;
            var establishedVoiceParticipantCount = transition.Kind == PartyRoomTransitionKind.Entered
                ? ReadEstablishedVoiceParticipantCount()
                : 0;
            var notice = FormatRoomTransitionNotice(
                transition,
                language,
                establishedVoiceParticipantCount);
            var now = ReadCurrentTime();
            _session.History.Add(
                UiLocalization.Select(language, "系统", "System"),
                notice,
                ChatMessageKind.System,
                now);
            _transientNoticeText = notice;
            _transientNoticeExpiresAt = now + RoomTransitionNoticeDuration;
            if (transition.Kind == PartyRoomTransitionKind.Exited)
                hasExitedTransition = true;
        }
    }

    private void DrainMemberTransitions()
    {
        while (true)
        {
            PartyMemberTransition? pending;
            try
            {
                pending = _readMemberTransition();
            }
            catch (Exception exception)
            {
                LogSafely(
                    $"Member transition reader failed; remaining transitions were deferred: " +
                    $"{exception.GetType().Name}: {exception.Message}.");
                return;
            }

            if (pending is not { } transition)
                return;

            var language = CurrentLanguage;
            var memberName = ResolveMemberTransitionName(transition);
            if (transition.Kind == PartyMemberTransitionKind.Baseline)
                continue;

            _session.History.Add(
                UiLocalization.Select(language, "系统", "System"),
                FormatMemberTransitionNotice(transition, memberName, language),
                ChatMessageKind.System,
                ReadCurrentTime());
        }
    }

    private string ResolveMemberTransitionName(PartyMemberTransition transition)
    {
        var entityId = NormalizeEntityId(transition.EntityId);
        if (transition.Kind == PartyMemberTransitionKind.Left &&
            entityId is not null &&
            TryTakeCachedMemberName(entityId, out var cachedName))
        {
            return cachedName;
        }

        var memberName = ResolveMemberNameByOrdinal(
            transition.RemotePlayerOrdinal,
            out var resolvedActualName);
        if (transition.Kind is PartyMemberTransitionKind.Baseline or PartyMemberTransitionKind.Joined &&
            entityId is not null &&
            resolvedActualName)
        {
            lock (_memberNameSync)
                _memberNamesByEntityId[entityId] = memberName;
        }
        return memberName;
    }

    private string ResolveMemberNameByOrdinal(
        int remotePlayerOrdinal,
        out bool resolvedActualName)
    {
        resolvedActualName = false;
        if (remotePlayerOrdinal is < 1 or > 3)
            return T("未知玩家", "Unknown player");

        var fallbackPlayerNumber = remotePlayerOrdinal + 1;
        var fallbackLabel = T($"玩家 {fallbackPlayerNumber}", $"Player {fallbackPlayerNumber}");

        try
        {
            var playerName = _getRemotePlayerName(remotePlayerOrdinal)?.Trim();
            if (!string.IsNullOrWhiteSpace(playerName))
            {
                resolvedActualName = true;
                return playerName;
            }
        }
        catch (Exception exception)
        {
            LogSafely(
                $"Remote member {remotePlayerOrdinal} name lookup failed; the stable slot label was kept: " +
                $"{exception.GetType().Name}: {exception.Message}.");
        }

        return fallbackLabel;
    }

    private static string? NormalizeEntityId(string? entityId)
    {
        var normalized = entityId?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private bool TryTakeCachedMemberName(string entityId, out string memberName)
    {
        lock (_memberNameSync)
        {
            return _memberNamesByEntityId.Remove(entityId, out memberName!) &&
                   !string.IsNullOrWhiteSpace(memberName);
        }
    }

    private void ClearMemberNameCache()
    {
        lock (_memberNameSync)
            _memberNamesByEntityId.Clear();
    }

    internal static string FormatMemberTransitionNotice(
        PartyMemberTransition transition,
        string memberName,
        UiLanguage language) =>
        transition.Kind switch
        {
            PartyMemberTransitionKind.Baseline => string.Empty,
            PartyMemberTransitionKind.Joined => UiLocalization.Select(
                language,
                $"{memberName} 加入了房间。",
                $"{memberName} joined the room."),
            PartyMemberTransitionKind.Left => UiLocalization.Select(
                language,
                $"{memberName} 离开了房间，原因：{FormatMemberLeaveReason(transition.LeaveReason, language)}。",
                $"{memberName} left the room. Reason: {FormatMemberLeaveReason(transition.LeaveReason, language)}."),
            _ => UiLocalization.Select(language, "房间成员状态已更新。", "Room member status updated."),
        };

    internal static string FormatMemberLeaveReason(
        PartyMemberLeaveReason reason,
        UiLanguage language) =>
        reason switch
        {
            PartyMemberLeaveReason.Unknown => UiLocalization.Select(language, "原因未知", "Unknown"),
            PartyMemberLeaveReason.Requested => UiLocalization.Select(language, "主动离开", "Left voluntarily"),
            PartyMemberLeaveReason.Disconnected => UiLocalization.Select(language, "连接中断", "Connection lost"),
            PartyMemberLeaveReason.Kicked => UiLocalization.Select(language, "被踢出房间", "Kicked"),
            PartyMemberLeaveReason.DeviceLostAuthentication => UiLocalization.Select(
                language,
                "认证失效",
                "Authentication lost"),
            PartyMemberLeaveReason.CreationFailed => UiLocalization.Select(
                language,
                "联机端点创建失败",
                "Online endpoint creation failed"),
            _ => UiLocalization.Select(language, "原因未知", "Unknown"),
        };

    private int ReadEstablishedVoiceParticipantCount()
    {
        try
        {
            return Math.Max(0, _getEstablishedVoiceParticipantCount());
        }
        catch (Exception exception)
        {
            LogSafely(
                $"Established voice participant count lookup failed; using zero: " +
                $"{exception.GetType().Name}: {exception.Message}.");
            return 0;
        }
    }

    private DateTimeOffset ReadCurrentTime()
    {
        try
        {
            return _getCurrentTime();
        }
        catch (Exception exception)
        {
            LogSafely(
                $"Transient notice time lookup failed; using UTC now: " +
                $"{exception.GetType().Name}: {exception.Message}.");
            return DateTimeOffset.UtcNow;
        }
    }

    private bool HasActiveTransientNotice() =>
        IsTransientNoticeActive(
            _transientNoticeText,
            ReadCurrentTime(),
            _transientNoticeExpiresAt);

    internal static bool IsTransientNoticeActive(
        string? notice,
        DateTimeOffset now,
        DateTimeOffset expiresAt) =>
        !string.IsNullOrWhiteSpace(notice) && now < expiresAt;

    internal static string FormatRoomTransitionNotice(
        PartyRoomTransition transition,
        UiLanguage language,
        int establishedVoiceParticipantCount = 0)
    {
        var roomName = string.IsNullOrWhiteSpace(transition.RoomName)
            ? null
            : transition.RoomName.Trim();
        return transition.Kind switch
        {
            PartyRoomTransitionKind.Entered =>
                roomName is null
                    ? UiLocalization.Select(
                        language,
                        $"已进入当前房间，{GetEnteredVoiceParticipantCount(transition.VoiceParticipantCount, establishedVoiceParticipantCount)}人成功建立语音通道",
                        $"Entered the current room; {GetEnteredVoiceParticipantCount(transition.VoiceParticipantCount, establishedVoiceParticipantCount)} people established voice channels.")
                    : UiLocalization.Select(
                        language,
                        $"已进入{roomName}的房间，{GetEnteredVoiceParticipantCount(transition.VoiceParticipantCount, establishedVoiceParticipantCount)}人成功建立语音通道",
                        $"Entered {roomName}'s room; {GetEnteredVoiceParticipantCount(transition.VoiceParticipantCount, establishedVoiceParticipantCount)} people established voice channels."),
            PartyRoomTransitionKind.Exited =>
                roomName is null
                    ? UiLocalization.Select(
                        language,
                        $"你已退出当前房间，原因是：{FormatRoomExitReason(transition.ExitReason, language)}",
                        $"You left the current room. Reason: {FormatRoomExitReason(transition.ExitReason, language)}")
                    : UiLocalization.Select(
                        language,
                        $"你已退出{roomName}的房间，原因是：{FormatRoomExitReason(transition.ExitReason, language)}",
                        $"You left {roomName}'s room. Reason: {FormatRoomExitReason(transition.ExitReason, language)}"),
            _ => UiLocalization.Select(language, "房间状态已更新。", "Room status updated."),
        };
    }

    internal static int GetEnteredVoiceParticipantCount(
        int transitionVoiceParticipantCount,
        int establishedVoiceParticipantCount) =>
        Math.Max(0, Math.Max(transitionVoiceParticipantCount, establishedVoiceParticipantCount));

    internal static string FormatRoomExitReason(
        PartyRoomExitReason reason,
        UiLanguage language) =>
        reason switch
        {
            PartyRoomExitReason.SelfLeft => UiLocalization.Select(language, "自行退房", "Left voluntarily"),
            PartyRoomExitReason.HostDisconnected => UiLocalization.Select(language, "房主掉线", "Host disconnected"),
            PartyRoomExitReason.Kicked => UiLocalization.Select(language, "你已被踢除房间", "You were kicked from the room"),
            PartyRoomExitReason.NetworkInterrupted or PartyRoomExitReason.None => UiLocalization.Select(
                language,
                "网络波动已退出房间",
                "Network interruption caused you to leave"),
            _ => UiLocalization.Select(language, "网络波动已退出房间", "Network interruption caused you to leave"),
        };

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
        ClearMemberNameCache();
        _windowVoicePushToTalkGate.Suspend();
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
        ClearPendingQuickActions();
        ForceReleaseVoicePushToTalkSources();
        ResetManagedControllerHotkeys();
        Interlocked.Exchange(ref _globalMuteKeyDown, 0);
        Array.Clear(_remotePlayerChatMuteKeyDown);
        PublishVoiceIndicatorSnapshotUnavailable();
        CancelBindingCapture();
        lock (_bindingCaptureSync)
        {
            _bindingResultRequest = null;
            _bindingResultText = null;
        }
        Interlocked.Exchange(ref _captureKeyboard, 0);
        Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 0);
        Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
        ClearImeCandidateSnapshot();
        _releaseCaptureFrames = 0;
        _focusInputNextFrame = false;
        _statusText = null;
        _composerStatusText = null;
        _playerMuteStatusText = null;
        _voiceMuteStatusText = null;
        _registration?.SetInputCapture(OverlayInputDevices.None);
        _registration?.SetEnabled(false);
    }

    public void Resume()
    {
        Interlocked.Exchange(ref _suspended, 0);
        _windowVoicePushToTalkGate.Resume();
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
            var voiceIndicatorSnapshot = Volatile.Read(ref _voiceIndicatorSnapshot);
            var voiceTalkerNames = ResolveVoiceTalkerNames(voiceUiStatus, voiceIndicatorSnapshot);
            if (configuration.EnableOverlay)
                DrawTransientRoomNotice(configuration);
            if (configuration.EnableVoiceIndicators &&
                (onlineRoomActive || configuration.EffectiveShowAllVoiceIndicatorSlots))
                VoiceIndicatorOverlay.Draw(
                    configuration,
                    voiceUiStatus,
                    _getPartyHudAnchors,
                    voiceIndicatorSnapshot.EstablishedRemotePlayers,
                    voiceIndicatorSnapshot.OccupiedRemotePlayers,
                    voiceIndicatorSnapshot.TalkingRemotePlayers,
                    voiceIndicatorSnapshot.IsValid);

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
                    DrawChatWindow(
                        configuration,
                        openedThisFrame: false,
                        voiceUiStatus,
                        voiceTalkerNames,
                        editMode: true);
                return;
            }

            if (settingsOpen)
            {
                ResetChatInteractionState();
                DrawChatWindow(
                    configuration,
                    openedThisFrame: false,
                    voiceUiStatus,
                    voiceTalkerNames,
                    editMode: true);
                return;
            }

            DrainQuickActionRequests();

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
                _composerStatusText = null;
                _statusText = configuration.CompactMode
                    ? null
                    : _session.TransportStatusText;
            }

            if (_session.Composer.IsOpen && ImGui.IsKeyPressed((int)ImGuiKey.Escape, false))
            {
                _session.Composer.Cancel();
                _releaseCaptureFrames = 2;
                Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
                ClearImeCandidateSnapshot();
                _statusText = null;
                _composerStatusText = null;
            }

            DrawChatWindow(
                configuration,
                openedThisFrame,
                voiceUiStatus,
                voiceTalkerNames,
                editMode: false);
        }
        catch (Exception exception)
        {
            ResetInteractionState();
            LogSafely($"Render callback recovered from an exception: {exception}");
        }
    }

    private void DrawTransientRoomNotice(Config configuration)
    {
        var notice = _transientNoticeText;
        if (!IsTransientNoticeActive(notice, ReadCurrentTime(), _transientNoticeExpiresAt))
            return;

        var viewport = ImGui.GetMainViewport();
        var workPosition = viewport.WorkPos;
        var workSize = viewport.WorkSize;
        var width = Math.Min(
            OverlayUiScale.Scale(760.0f),
            Math.Max(1.0f, workSize.X - OverlayUiScale.Scale(48.0f)));
        using var position = CreateVector2(
            workPosition.X + Math.Max(0.0f, (workSize.X - width) * 0.5f),
            workPosition.Y + OverlayUiScale.Scale(72.0f));
        using var size = CreateVector2(width, 0.0f);
        using var pivot = CreateVector2(0.0f, 0.0f);
        ImGui.SetNextWindowPos(position, (int)ImGuiCond.Always, pivot);
        ImGui.SetNextWindowSize(size, (int)ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.72f);

        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoInputs;
        var open = true;
        var began = ImGui.Begin("##GBFRRoomTransitionNotice", ref open, (int)flags);
        try
        {
            if (!began)
                return;
            ImGui.SetWindowFontScale(Math.Clamp((float)configuration.ChatFontSize / 18.0f, 0.67f, 1.67f));
            ImGui.TextWrapped(notice);
        }
        finally
        {
            ImGui.End();
        }
    }

    private void DrawChatWindow(
        Config configuration,
        bool openedThisFrame,
        PartyVoiceUiStatus voiceUiStatus,
        IReadOnlyList<string> voiceTalkerNames,
        bool editMode)
    {
        var viewport = ImGui.GetMainViewport();
        var workPosition = viewport.WorkPos;
        var workSize = viewport.WorkSize;
        var fullRect = editMode && _editedChatRect is { } edited
            ? edited
            : ChatOverlayLayout.Resolve(
                configuration,
                workPosition.X,
                workPosition.Y,
                workSize.X,
                workSize.Y);
        var composerOpen = _session.Composer.IsOpen;
        var presentation = ChatOverlayLayout.ResolvePresentation(
            configuration.CompactMode,
            composerOpen,
            editMode);
        if (presentation == ChatOverlayPresentationMode.Hidden)
            return;

        var voicePresentation = VoiceOverlayPresenter.Create(
            voiceUiStatus,
            CurrentLanguage,
            voiceTalkerNames);
        var voiceText = voicePresentation.IsVisible ? voicePresentation.Text : null;
        var imeCandidateText = composerOpen
            ? GetImeCandidateFallbackText(configuration.EnableImeCandidateFallback)
            : null;
        var fontScale = Math.Clamp((float)configuration.ChatFontSize / 18.0f, 0.67f, 1.67f);
        var compactContentWidth = Math.Max(
            1.0f,
            fullRect.Width - OverlayUiScale.Scale(16.0f));
        var rect = presentation == ChatOverlayPresentationMode.Compact
            ? ChatOverlayLayout.ResolveCompactInputRect(
                fullRect,
                MeasureCompactTextItemHeight(voiceText, compactContentWidth, fontScale),
                MeasureCompactTextItemHeight(imeCandidateText, compactContentWidth, fontScale),
                MeasureCompactTextItemHeight(_composerStatusText, compactContentWidth, fontScale),
                fontScale,
                workPosition.Y)
            : fullRect;
        var rectBeforeEditHandles = rect;
        if (editMode)
        {
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
        if (presentation == ChatOverlayPresentationMode.Full && !composerOpen && !editMode)
            flags |= ImGuiWindowFlags.NoInputs;

        var began = ImGui.Begin("GBFR Chat##GBFRChatOverlay", ref _windowOpen, (int)flags);
        try
        {
            if (!began)
                return;

            ImGui.SetWindowFontScale(fontScale);
            if (presentation == ChatOverlayPresentationMode.Full)
            {
                DrawVoiceStatus(voiceUiStatus, voiceTalkerNames);
                DrawHistory(configuration, composerOpen, imeCandidateText);
                if (composerOpen)
                    DrawComposer(
                        openedThisFrame,
                        imeCandidateText,
                        showTopSeparator: true,
                        compactStatusOnly: false,
                        readOnly: false);
                else if (!string.IsNullOrEmpty(_statusText))
                    ImGui.TextWrapped(_statusText);
            }
            else
            {
                if (voicePresentation.IsVisible)
                    DrawVoiceStatus(voiceUiStatus, voiceTalkerNames);
                DrawComposer(
                    openedThisFrame,
                    imeCandidateText,
                    showTopSeparator: false,
                    compactStatusOnly: true,
                    readOnly: editMode);
            }
            if (editMode)
            {
                DrawChatEditHandles(
                    viewport,
                    ref rect,
                    workPosition.X,
                    workPosition.Y,
                    workSize.X,
                    workSize.Y,
                    compactPresentation: presentation == ChatOverlayPresentationMode.Compact);
                _editedChatRect = presentation == ChatOverlayPresentationMode.Compact
                    ? ChatOverlayLayout.ApplyCompactEditToFullRect(
                        fullRect,
                        rectBeforeEditHandles,
                        rect,
                        workPosition.X,
                        workPosition.Y,
                        workSize.X,
                        workSize.Y)
                    : rect;
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
                        DrawSettingsTab(T("02 快捷动作", "02 Quick Actions"), DrawQuickActionSettingsTab);
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
        ImGui.Separator();
        DrawVoiceMuteSettingsSection();
    }

    private void DrawVoiceMuteSettingsSection()
    {
        ImGui.Text(T("玩家语音禁言", "Player Voice Mute"));
        if (!string.IsNullOrWhiteSpace(_voiceMuteStatusText))
            ImGui.TextWrapped(LocalizeLegacyText(_voiceMuteStatusText));

        var slots = _getPlayerMuteSlots();
        using var buttonSize = CreateVector2(
            OverlayUiScale.Scale(190.0f),
            OverlayUiScale.Scale(36.0f));
        for (var playerNumber = 2; playerNumber <= 4; playerNumber++)
        {
            var status = slots.FirstOrDefault(candidate => candidate.PlayerNumber == playerNumber);
            if (status.PlayerNumber != playerNumber)
            {
                status = new PartyPlayerMuteSlotStatus(
                    playerNumber,
                    false,
                    false,
                    string.Empty);
            }

            var remotePlayerNumber = playerNumber - 1;
            var playerName = ResolveRemotePlayerName(remotePlayerNumber, playerNumber);
            ImGui.Text($"{T($"玩家 {playerNumber}", $"Player {playerNumber}")}：{playerName}");
            ImGui.SameLine(OverlayUiScale.Scale(360.0f), OverlayUiScale.Scale(12.0f));
            ImGui.BeginDisabled(!status.IsAvailable);
            try
            {
                var label = status.IsAvailable
                    ? status.IsMuted
                        ? $"{T("解除语音禁言", "Unmute Voice")}##VoiceMutePlayer{playerNumber}"
                        : $"{T("语音禁言", "Mute Voice")}##VoiceMutePlayer{playerNumber}"
                    : $"{T("不可用", "Unavailable")}##VoiceMutePlayer{playerNumber}";
                if (ImGui.Button(label, buttonSize))
                {
                    var operation = _setPlayerMuted(playerNumber, !status.IsMuted);
                    _voiceMuteStatusText = operation.Message;
                }
            }
            finally
            {
                ImGui.EndDisabled();
            }
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
        DrawBindingRow(T("全局聊天禁言", "Block All Chat"), BindingTarget.GlobalMute);
        DrawBindingRow(T("玩家 1 聊天禁言", "Player 1 Chat Mute"), BindingTarget.RemotePlayerChatMute, playerNumber: 1);
        DrawBindingRow(T("玩家 2 聊天禁言", "Player 2 Chat Mute"), BindingTarget.RemotePlayerChatMute, playerNumber: 2);
        DrawBindingRow(T("玩家 3 聊天禁言", "Player 3 Chat Mute"), BindingTarget.RemotePlayerChatMute, playerNumber: 3);
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
                        : CommunicationCatalog.GetDefaultId(selectedKind);
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

            DrawKeyboardBindingRow(T("此动作", "This Action"), BindingTarget.QuickAction, selected.Id);
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
            T("精简模式", "Compact Mode"),
            configuration.CompactMode,
            (value, enabled) => value.CompactMode = enabled);
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

        var fontSize = Math.Clamp((float)configuration.ChatFontSize, 12.0f, 30.0f);
        if (ImGui.SliderFloat(T("字体大小", "Font Size"), ref fontSize, 12.0f, 30.0f, "%.0f", 0))
            UpdateConfigurationSafely(value => value.ChatFontSize = fontSize);
        DrawConfigurationCheckbox(
            T("显示时间戳", "Show Timestamps"),
            configuration.ShowTimestamps,
            (value, enabled) => value.ShowTimestamps = enabled);

        var historyCapacity = Math.Clamp(configuration.HistoryCapacity, 10, 5_000);
        if (ImGui.SliderInt(
                T("聊天记录上限", "Chat History Limit"),
                ref historyCapacity,
                10,
                5_000,
                "%d",
                0))
        {
            UpdateConfigurationSafely(value => value.HistoryCapacity = historyCapacity);
        }

        ImGui.Separator();
        ImGui.Text(T("玩家名字", "Player Names"));
        var nameSize = Math.Clamp((float)configuration.PlayerNameFontSize, 12.0f, 30.0f);
        if (ImGui.SliderFloat(T("名字大小", "Name Size"), ref nameSize, 12.0f, 30.0f, "%.0f", 0))
            UpdateConfigurationSafely(value => value.PlayerNameFontSize = nameSize);
        var nameWeight = Math.Clamp(configuration.PlayerNameWeight, 1, 3);
        if (ImGui.SliderInt(T("名字粗细", "Name Weight"), ref nameWeight, 1, 3, "%d", 0))
            UpdateConfigurationSafely(value => value.PlayerNameWeight = nameWeight);
        DrawPlayerColorSetting(1, configuration.Player1NameColor);
        DrawPlayerColorSetting(2, configuration.Player2NameColor);
        DrawPlayerColorSetting(3, configuration.Player3NameColor);
        DrawPlayerColorSetting(4, configuration.Player4NameColor);
        ImGui.Separator();
        ImGui.TextWrapped(T(
            "拖动聊天框顶部移动，拖动右下角缩放。",
            "Drag the chat header to move it and the lower-right corner to resize it."));
    }

    private void DrawPlayerColorSetting(int playerNumber, string configured)
    {
        if (!ChatColor.TryParseRgb(configured, out var color))
            color = [0.85f, 0.85f, 0.85f, 1.0f];
        if (!ImGui.ColorEdit4(
                T($"玩家 {playerNumber} 颜色", $"Player {playerNumber} Color"),
                color,
                (int)ImGuiColorEditFlags.NoAlpha))
        {
            return;
        }

        var hex = ChatColor.ToHex(color);
        UpdateConfigurationSafely(configuration =>
        {
            switch (playerNumber)
            {
                case 1:
                    configuration.Player1NameColor = hex;
                    break;
                case 2:
                    configuration.Player2NameColor = hex;
                    break;
                case 3:
                    configuration.Player3NameColor = hex;
                    break;
                case 4:
                    configuration.Player4NameColor = hex;
                    break;
            }
        });
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
        using var bindingSize = CreateVector2(OverlayUiScale.Scale(185.0f), OverlayUiScale.Scale(34.0f));
        using var clearSize = CreateVector2(OverlayUiScale.Scale(64.0f), OverlayUiScale.Scale(34.0f));
        if (ImGui.Button(
                $"{T("键盘", "Keyboard")}: {DescribeBinding(keyboard)}##Keyboard{target}{quickActionId}{playerNumber}",
                bindingSize))
        {
            BeginBindingCapture(requestKeyboard);
        }
        ImGui.SameLine(0.0f, OverlayUiScale.Scale(6.0f));
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(keyboard));
        try
        {
            if (ImGui.Button(
                    $"{T("清除", "Clear")}##ClearKeyboard{target}{quickActionId}{playerNumber}",
                    clearSize))
            {
                ClearBinding(requestKeyboard);
            }
        }
        finally
        {
            ImGui.EndDisabled();
        }
        ImGui.SameLine(0.0f, OverlayUiScale.Scale(10.0f));
        ImGui.BeginDisabled(Volatile.Read(ref _controllerInputAvailable) == 0);
        try
        {
            if (ImGui.Button(
                    $"{T("手柄", "Controller")}: {DescribeControllerBinding(controller)}##Controller{target}{quickActionId}{playerNumber}",
                    bindingSize))
            {
                BeginBindingCapture(requestController);
            }
        }
        finally
        {
            ImGui.EndDisabled();
        }
        ImGui.SameLine(0.0f, OverlayUiScale.Scale(6.0f));
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(controller));
        try
        {
            if (ImGui.Button(
                    $"{T("清除", "Clear")}##ClearController{target}{quickActionId}{playerNumber}",
                    clearSize))
            {
                ClearBinding(requestController);
            }
        }
        finally
        {
            ImGui.EndDisabled();
        }
        DrawBindingRowFeedback(requestKeyboard, requestController);
        ImGui.Separator();
    }

    private void DrawKeyboardBindingRow(
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
        var keyboard = GetBindingValue(_getConfiguration(), requestKeyboard);

        ImGui.Text(label);
        using var bindingSize = CreateVector2(OverlayUiScale.Scale(185.0f), OverlayUiScale.Scale(34.0f));
        using var clearSize = CreateVector2(OverlayUiScale.Scale(64.0f), OverlayUiScale.Scale(34.0f));
        if (ImGui.Button(
                $"{T("键盘", "Keyboard")}: {DescribeBinding(keyboard)}##KeyboardOnly{target}{quickActionId}{playerNumber}",
                bindingSize))
        {
            BeginBindingCapture(requestKeyboard);
        }
        ImGui.SameLine(0.0f, OverlayUiScale.Scale(6.0f));
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(keyboard));
        try
        {
            if (ImGui.Button(
                    $"{T("清除", "Clear")}##ClearKeyboardOnly{target}{quickActionId}{playerNumber}",
                    clearSize))
            {
                ClearBinding(requestKeyboard);
            }
        }
        finally
        {
            ImGui.EndDisabled();
        }
        DrawBindingRowFeedback(requestKeyboard, null);
        ImGui.Separator();
    }

    private void DrawBindingRowFeedback(
        BindingCaptureRequest keyboardRequest,
        BindingCaptureRequest? controllerRequest)
    {
        BindingCaptureRequest? activeCapture;
        BindingCaptureRequest? resultRequest;
        string? feedback;
        lock (_bindingCaptureSync)
        {
            activeCapture = _bindingCapture;
            resultRequest = _bindingResultRequest;
            feedback = activeCapture is not null ? _captureStatusText : _bindingResultText;
        }

        var request = activeCapture ?? resultRequest;
        if (request is not { } resolvedRequest)
            return;
        var matchesRequest = resolvedRequest == keyboardRequest ||
                             (controllerRequest is { } controller && resolvedRequest == controller);
        if (!matchesRequest ||
            string.IsNullOrWhiteSpace(feedback))
        {
            return;
        }

        ImGui.SameLine(0.0f, OverlayUiScale.Scale(10.0f));
        var deviceLabel = resolvedRequest.Device == BindingCaptureDevice.Keyboard
            ? T("键盘", "Keyboard")
            : T("手柄", "Controller");
        ImGui.TextUnformatted($"{deviceLabel}：{feedback}", null!);
        if (activeCapture is null)
            return;

        ImGui.SameLine(0.0f, OverlayUiScale.Scale(8.0f));
        using var cancelSize = CreateVector2(OverlayUiScale.Scale(72.0f), OverlayUiScale.Scale(34.0f));
        if (ImGui.Button(
                $"{T("取消", "Cancel")}##CancelBinding{resolvedRequest.Target}{resolvedRequest.Device}{resolvedRequest.QuickActionId}{resolvedRequest.PlayerNumber}",
                cancelSize))
        {
            CancelBindingCapture();
        }
    }

    internal void BeginBindingCapture(BindingCaptureRequest request)
    {
        lock (_bindingCaptureSync)
        {
            _bindingResultRequest = null;
            _bindingResultText = null;
            _bindingCapture = request;
            _keyboardCaptureCandidate = default;
            _controllerCaptureCandidate = default;
            _captureWaitingForRelease = request.Device == BindingCaptureDevice.Controller &&
                                        (Volatile.Read(ref _latestControllerButtonsMask) != 0 ||
                                         Volatile.Read(ref _latestExtendedControllerButtonsMask) != 0);
            _captureStatusText = _captureWaitingForRelease
                ? T("请先松开所有按键。", "Release all buttons first.")
                : request.Device == BindingCaptureDevice.Keyboard
                    ? T("请按键。", "Press a key.")
                    : GetControllerCapturePrompt();
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
            _controllerCaptureCandidate = default;
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
            var conflicts = FindBindingConflicts(request, value);
            var conflictDescription = string.Join(
                "、",
                conflicts
                    .Select(DescribeTarget)
                    .Distinct(StringComparer.Ordinal));
            CancelBindingCapture();
            PersistBindingReplacingConflicts(request, value, conflicts);
            _bindingResultRequest = request;
            _bindingResultText = conflicts.Length == 0
                ? T("已保存。", "Saved.")
                : T(
                    $"冲突：已清除 {conflictDescription}。",
                    $"Conflict cleared: {conflictDescription}.");
        }
    }

    internal bool ClearBinding(BindingCaptureRequest request)
    {
        lock (_bindingCaptureSync)
        {
            var configuration = _getConfiguration();
            if (request.Target == BindingTarget.SettingsMenu)
            {
                var alternate = request.Device == BindingCaptureDevice.Keyboard
                    ? configuration.SettingsMenuControllerBinding
                    : configuration.SettingsMenuKeyboardBinding;
                if (string.IsNullOrWhiteSpace(alternate))
                {
                    _bindingResultRequest = request;
                    _bindingResultText = T(
                        "请先设置另一种菜单按键。",
                        "Set the other Settings binding first.");
                    return false;
                }
            }

            if (_bindingCapture == request)
                CancelBindingCapture();
            PersistBinding(request, string.Empty);
            _bindingResultRequest = request;
            _bindingResultText = T("已清除。", "Cleared.");
            return true;
        }
    }

    private void PersistBinding(BindingCaptureRequest request, string value)
    {
        UpdateConfigurationSafely(configuration => SetBindingValue(configuration, request, value));
    }

    private void PersistBindingReplacingConflicts(
        BindingCaptureRequest request,
        string value,
        IReadOnlyList<BindingCaptureRequest> conflicts)
    {
        UpdateConfigurationSafely(configuration =>
        {
            foreach (var conflict in conflicts)
                SetBindingValue(configuration, conflict, string.Empty);
            SetBindingValue(configuration, request, value);
        });
    }

    private static void SetBindingValue(
        Config configuration,
        BindingCaptureRequest request,
        string value)
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
            case BindingTarget.RemotePlayerChatMute when request.Device == BindingCaptureDevice.Keyboard:
                SetRemotePlayerChatMuteKeyboardBinding(configuration, request.PlayerNumber, value);
                break;
            case BindingTarget.RemotePlayerChatMute:
                SetRemotePlayerChatMuteControllerBinding(configuration, request.PlayerNumber, value);
                break;
            case BindingTarget.QuickAction when request.Device == BindingCaptureDevice.Keyboard:
                var action = configuration.QuickActions.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, request.QuickActionId, StringComparison.Ordinal));
                if (action is null)
                    return;
                action.KeyboardBinding = value;
                break;
        }
    }

    private BindingCaptureRequest[] FindBindingConflicts(BindingCaptureRequest request, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        return EnumerateBindings(_getConfiguration())
            .Where(item => item.Request.Device == request.Device &&
                           item.Request != request &&
                           string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Request)
            .Distinct()
            .ToArray();
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
        for (var remotePlayerNumber = 1; remotePlayerNumber <= 3; remotePlayerNumber++)
        {
            yield return (
                new(BindingTarget.RemotePlayerChatMute, BindingCaptureDevice.Keyboard, null, remotePlayerNumber),
                GetRemotePlayerChatMuteKeyboardBinding(configuration, remotePlayerNumber));
            yield return (
                new(BindingTarget.RemotePlayerChatMute, BindingCaptureDevice.Controller, null, remotePlayerNumber),
                GetRemotePlayerChatMuteControllerBinding(configuration, remotePlayerNumber));
        }
        foreach (var action in configuration.QuickActions ?? [])
            yield return (new(BindingTarget.QuickAction, BindingCaptureDevice.Keyboard, action.Id), action.KeyboardBinding);
    }

    private static string GetBindingValue(Config configuration, BindingCaptureRequest request)
    {
        return EnumerateBindings(configuration)
            .FirstOrDefault(item => item.Request == request).Value ?? string.Empty;
    }

    private string DescribeBinding(string value) =>
        string.IsNullOrWhiteSpace(value) ? T("未绑定", "Unbound") : value;

    private string DescribeControllerBinding(string value) =>
        ControllerBinding.ContainsReservedDPadDown(value)
            ? T("不可用：DPadDown 为游戏保留键", "Unavailable: DPadDown is reserved by the game")
            : DescribeBinding(value);

    private string DescribeTarget(BindingCaptureRequest request) => request.Target switch
    {
        BindingTarget.SettingsMenu => T("设置菜单", "Settings Menu"),
        BindingTarget.OpenChat => T("打开聊天", "Open Chat"),
        BindingTarget.PushToTalk => T("按住说话", "Push-to-Talk"),
        BindingTarget.QuickActionsPanel => T("快捷动作面板", "Quick Actions Panel"),
        BindingTarget.GlobalMute => T("全局聊天禁言", "Block All Chat"),
        BindingTarget.RemotePlayerChatMute => T(
            $"玩家 {request.PlayerNumber} 聊天禁言",
            $"Player {request.PlayerNumber} Chat Mute"),
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

    internal int DrainQuickActionRequests()
    {
        var count = 0;
        while (_pendingQuickActions.TryDequeue(out var actionId))
        {
            SendQuickAction(actionId);
            count++;
        }
        return count;
    }

    private void ClearPendingQuickActions()
    {
        while (_pendingQuickActions.TryDequeue(out _))
        {
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
        float workHeight,
        bool compactPresentation)
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
                rect = compactPresentation
                    ? ChatOverlayLayout.ResizeWidth(
                        rect,
                        delta.X,
                        workX,
                        workWidth)
                    : ChatOverlayLayout.Resize(
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
        _ => T("点击测试麦克风按钮查看输入电平。", "Click Test Microphone to view the input level."),
    };

    private static uint PackColor(byte red, byte green, byte blue, float alpha)
    {
        var a = (uint)Math.Clamp((int)MathF.Round(Math.Clamp(alpha, 0.0f, 1.0f) * 255.0f), 0, 255);
        return (uint)red | ((uint)green << 8) | ((uint)blue << 16) | (a << 24);
    }

    private void DrawVoiceStatus(
        PartyVoiceUiStatus voiceUiStatus,
        IReadOnlyList<string> voiceTalkerNames)
    {
        var presentation = VoiceOverlayPresenter.Create(
            voiceUiStatus,
            CurrentLanguage,
            voiceTalkerNames);
        if (!presentation.IsVisible)
            return;

        ImGui.TextWrapped(presentation.Text);
        ImGui.Separator();
    }

    private void DrawHistory(Config configuration, bool composerOpen, string? imeCandidateText)
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
            var hostPlayerNumber = _getHostPlayerNumber();
            foreach (var message in snapshot)
                DrawHistoryMessage(configuration, message, hostPlayerNumber);

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

    private void DrawHistoryMessage(Config configuration, ChatMessage message, int? hostPlayerNumber)
    {
        var baseScale = Math.Clamp((float)configuration.ChatFontSize / 18.0f, 0.67f, 1.67f);
        if (configuration.ShowTimestamps)
        {
            ImGui.TextUnformatted($"[{message.Timestamp.ToLocalTime():HH:mm}]", null!);
            ImGui.SameLine(0.0f, OverlayUiScale.Scale(4.0f));
        }

        var playerNumber = ResolveHistoryPlayerNumber(message);
        var color = GetPlayerNameColor(configuration, playerNumber);
        var communicationCue = GetEffectiveCommunicationCue(message);
        var name = FormatHistorySenderLabel(
            ResolveHistorySender(message),
            IsHistoryMessageHostedByPlayer(message, hostPlayerNumber),
            configuration.InterfaceLanguage,
            communicationCue);
        var nameScale = playerNumber > 0
            ? Math.Clamp((float)configuration.PlayerNameFontSize / 18.0f, 0.67f, 1.67f)
            : baseScale;
        ImGui.SetWindowFontScale(nameScale);
        using var namePosition = CreateVector2(0.0f, 0.0f);
        ImGui.GetCursorScreenPos(namePosition);
        ImGui.PushStyleColorU32((int)ImGuiCol.Text, color);
        try
        {
            ImGui.TextUnformatted(name, null!);
        }
        finally
        {
            ImGui.PopStyleColor(1);
        }

        var weight = playerNumber > 0
            ? Math.Clamp(configuration.PlayerNameWeight, 1, 3)
            : 1;
        var drawList = ImGui.GetWindowDrawList();
        for (var pass = 1; pass < weight; pass++)
        {
            using var extraPosition = CreateVector2(
                namePosition.X + OverlayUiScale.Scale(pass * 0.55f),
                namePosition.Y);
            ImGui.ImDrawListAddTextVec2(drawList, extraPosition, color, name, null!);
        }

        ImGui.SameLine(0.0f, OverlayUiScale.Scale(5.0f));
        ImGui.SetWindowFontScale(baseScale);
        ImGui.TextWrapped(message.Text);
        DrawMessageContextMenu(message, playerNumber);
    }

    internal string ResolveHistorySender(ChatMessage message) => message.Sender;

    internal static int ResolveHistoryPlayerNumber(ChatMessage message)
    {
        if (message.Kind == ChatMessageKind.Self)
            return 1;

        return message.Kind == ChatMessageKind.Party && message.PlayerNumber is >= 2 and <= 4
            ? message.PlayerNumber
            : 0;
    }

    internal static bool IsHistoryMessageHostedByPlayer(
        ChatMessage message,
        int? authoritativeHostPlayerNumber)
    {
        if (authoritativeHostPlayerNumber is not { } hostPlayerNumber ||
            hostPlayerNumber is < 1 or > 4)
        {
            return false;
        }

        return ResolveHistoryPlayerNumber(message) == hostPlayerNumber;
    }

    internal static ChatCommunicationCue GetEffectiveCommunicationCue(ChatMessage message) =>
        message.CommunicationCue;

    internal static string FormatHistorySenderLabel(
        string sender,
        bool isHost,
        UiLanguage language,
        ChatCommunicationCue communicationCue = ChatCommunicationCue.None)
    {
        var hostPrefix = isHost
            ? $"[{UiLocalization.Select(language, "房主", "Host")}] "
            : string.Empty;
        if (communicationCue == ChatCommunicationCue.None)
            return $"{hostPrefix}{sender}:";

        var cueLabel = FormatCommunicationCueLabel(communicationCue, language);
        return language == UiLanguage.English
            ? $"{hostPrefix}{sender} ({cueLabel}):"
            : $"{hostPrefix}{sender}（{cueLabel}）:";
    }

    internal static string FormatCommunicationCueLabel(
        ChatCommunicationCue communicationCue,
        UiLanguage language) =>
        communicationCue switch
        {
            ChatCommunicationCue.Victory => UiLocalization.Select(language, "胜利", "Victory"),
            ChatCommunicationCue.LinkAttack => UiLocalization.Select(language, "连携攻击", "Link Attack"),
            ChatCommunicationCue.Thanks => UiLocalization.Select(language, "感谢", "Thanks"),
            _ => string.Empty,
        };

    private void DrawMessageContextMenu(ChatMessage message, int playerNumber)
    {
        if (!ImGui.BeginPopupContextItem($"##GBFRChatMessage{message.Sequence}", 1))
            return;

        try
        {
            if (ImGui.MenuItemBool(T("复制消息", "Copy Message"), string.Empty, false, true))
                ImGui.SetClipboardText(message.Text);
            if (ImGui.MenuItemBool(T("复制玩家名", "Copy Player Name"), string.Empty, false, true))
                ImGui.SetClipboardText(message.Sender);
            if (playerNumber is >= 2 and <= 4)
            {
                ImGui.Separator();
                var muted = _chatBlacklist.IsMuted(playerNumber);
                if (ImGui.MenuItemBool(
                        muted
                            ? T("解除聊天禁言", "Unblock Chat")
                            : T("聊天禁言", "Block Chat"),
                        string.Empty,
                        muted,
                        true))
                {
                    TogglePlayerMute(playerNumber);
                }
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private static uint GetPlayerNameColor(Config configuration, int playerNumber)
    {
        var configured = playerNumber switch
        {
            1 => configuration.Player1NameColor,
            2 => configuration.Player2NameColor,
            3 => configuration.Player3NameColor,
            4 => configuration.Player4NameColor,
            _ => "#D8D8D8",
        };
        return ChatColor.TryParseImGuiColor(configured, out var color)
            ? color
            : 0xFFD8D8D8;
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

    private static float MeasureWrappedTextItemHeight(
        string? text,
        float? availableWidth = null,
        float fontScale = 1.0f)
    {
        if (string.IsNullOrEmpty(text))
            return 0.0f;

        var safeFontScale = float.IsFinite(fontScale) && fontScale > 0.0f
            ? fontScale
            : 1.0f;
        var width = availableWidth;
        if (width is null)
        {
            using var available = CreateVector2(0.0f, 0.0f);
            ImGui.GetContentRegionAvail(available);
            width = available.X;
        }
        using var textSize = CreateVector2(0.0f, 0.0f);
        ImGui.CalcTextSize(
            textSize,
            text,
            null!,
            false,
            Math.Max(1.0f, width.Value / safeFontScale));
        var itemSpacing = Math.Max(
            0.0f,
            ImGui.GetTextLineHeightWithSpacing() - ImGui.GetTextLineHeight());
        return (Math.Max(0.0f, textSize.Y) + itemSpacing) * safeFontScale;
    }

    private static float MeasureCompactTextItemHeight(
        string? text,
        float availableWidth,
        float fontScale)
    {
        var safeFontScale = float.IsFinite(fontScale) && fontScale > 0.0f
            ? fontScale
            : 1.0f;
        var renderedHeight = MeasureWrappedTextItemHeight(text, availableWidth, safeFontScale);
        return renderedHeight / safeFontScale;
    }

    private unsafe void DrawComposer(
        bool openedThisFrame,
        string? imeCandidateText,
        bool showTopSeparator,
        bool compactStatusOnly,
        bool readOnly)
    {
        if (showTopSeparator)
            ImGui.Separator();
        if (!compactStatusOnly)
            DrawImeCandidateFallback(imeCandidateText);
        if (!readOnly && _focusInputNextFrame && !openedThisFrame)
        {
            ImGui.SetKeyboardFocusHere(0);
            _focusInputNextFrame = false;
        }

        ImGui.SetNextItemWidth(-1.0f);
        var submitRequested = false;
        if (readOnly)
            ImGui.BeginDisabled(true);
        try
        {
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
        }
        finally
        {
            if (readOnly)
                ImGui.EndDisabled();
        }

        if (compactStatusOnly)
            DrawImeCandidateFallback(imeCandidateText);
        if (readOnly)
        {
            if (!string.IsNullOrEmpty(_composerStatusText))
                ImGui.TextWrapped(_composerStatusText);
            return;
        }

        var currentDraft = ReadInputBuffer();
        if (ImGui.IsItemActive() && ImGui.IsItemHovered(0))
        {
            var wheel = ImGui.GetIO().MouseWheel;
            var recalled = wheel > 0.0f
                ? _session.InputHistory.MovePrevious(currentDraft)
                : wheel < 0.0f
                    ? _session.InputHistory.MoveNext(currentDraft)
                    : null;
            if (recalled is not null)
            {
                WriteUtf8Buffer(_inputBuffer, recalled);
                currentDraft = recalled;
            }
        }

        _session.Composer.SetDraft(currentDraft);
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
                _composerStatusText = null;
            }
            else
            {
                _composerStatusText = result.Error ?? result.Status.ToString();
                _statusText = _composerStatusText;
                _focusInputNextFrame = true;
            }
        }

        var visibleStatusText = compactStatusOnly ? _composerStatusText : _statusText;
        if (!string.IsNullOrEmpty(visibleStatusText))
            ImGui.TextWrapped(visibleStatusText);
    }

    public unsafe OverlayWindowMessageResult ObserveWindowMessage(
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

            // The peer returns a completed DefWindowProc result below, so the Broker will not
            // invoke Dear ImGui's backend for this message. Feed the IME lifecycle into ImGui
            // here first; otherwise system IMEs can switch layouts but never establish a live
            // composition on Relink's ANSI game window.
            ImGui.ImplWin32_WndProcHandler(
                (void*)windowHandle,
                message,
                wParam,
                forwardedLParam);
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
        var pushToTalkBinding = configuration.PushToTalkKeyboardBinding;
        if (isUp && MatchesWindowBindingPrimary(pushToTalkBinding, virtualKey))
        {
            Interlocked.Exchange(ref _windowVoicePushToTalkPhysicalDown, 0);
            _windowVoicePushToTalkGate.Report(false);
            return true;
        }
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

        // Let ImGui receive normal editing keys while settings are open, but
        // keep configured mod hotkeys from firing behind the settings window.
        if (Volatile.Read(ref _settingsMenuOpen) != 0)
            return MatchesConfiguredWindowHotkey(configuration, virtualKey, isDown);

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
        if (isDown &&
            configuration.EnableVoiceInput &&
            onlineRoomActive &&
            MatchesWindowBinding(pushToTalkBinding, virtualKey))
        {
            var firstPhysicalDown = Interlocked.Exchange(
                ref _windowVoicePushToTalkPhysicalDown,
                1) == 0;
            if (firstPhysicalDown && _canUseVoicePushToTalk())
            {
                LogSafely(
                    "Party voice window push-to-talk physical press reached the Chat input route and entered the safety gate.");
                _windowVoicePushToTalkGate.Report(true);
            }
            else if (firstPhysicalDown)
            {
                LogSafely(
                    "Party voice window push-to-talk physical press reached the Chat input route, but Party voice " +
                    "is not ready; no unmute request was sent.");
                _statusText = T(
                    "[语音] 尚未就绪，正在等待另一位安装相同版本模组的队友。",
                    "[Voice] Not ready; waiting for another player with the same mod version.");
            }
            return true;
        }
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

        for (var remotePlayerNumber = 1; remotePlayerNumber <= 3; remotePlayerNumber++)
        {
            var playerMuteBinding = GetRemotePlayerChatMuteKeyboardBinding(
                configuration,
                remotePlayerNumber);
            if ((isDown && onlineRoomActive && MatchesWindowBinding(playerMuteBinding, virtualKey)) ||
                (isUp && MatchesWindowBindingPrimary(playerMuteBinding, virtualKey)))
            {
                ObserveRemotePlayerChatMuteKey(
                    remotePlayerNumber,
                    isDown,
                    WindowHotkeySource);
                return true;
            }
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

        Interlocked.Exchange(ref _windowVoicePushToTalkPhysicalDown, 0);
        _windowVoicePushToTalkGate.ForceMute();

        lock (_bindingCaptureSync)
        {
            if (_bindingCapture is not { Device: BindingCaptureDevice.Keyboard })
                return;

            _keyboardCaptureCandidate = default;
            _captureWaitingForRelease = false;
            _captureStatusText = T(
                "窗口失焦，请重试。",
                "Focus lost; try again.");
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
                    $"{_keyboardCaptureCandidate.Format()}，松开确认。",
                    $"{_keyboardCaptureCandidate.Format()}; release to confirm.");
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

    private static bool MatchesConfiguredWindowHotkey(
        Config configuration,
        ushort virtualKey,
        bool isDown)
    {
        bool Matches(string? value) => isDown
            ? MatchesWindowBinding(value, virtualKey)
            : MatchesWindowBindingPrimary(value, virtualKey);

        if (Matches(configuration.PushToTalkKeyboardBinding) ||
            Matches(configuration.OpenChatKeyboardBinding) ||
            Matches(configuration.SettingsMenuKeyboardBinding) ||
            Matches(configuration.QuickActionsKeyboardBinding) ||
            Matches(configuration.GlobalMuteKeyboardBinding) ||
            Matches(configuration.RemotePlayer1ChatMuteKeyboardBinding) ||
            Matches(configuration.RemotePlayer2ChatMuteKeyboardBinding) ||
            Matches(configuration.RemotePlayer3ChatMuteKeyboardBinding))
        {
            return true;
        }

        foreach (var action in configuration.QuickActions ?? [])
        {
            if (action.Enabled && action.IsConfigured && Matches(action.KeyboardBinding))
                return true;
        }

        return false;
    }

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

    internal static bool ShouldCaptureImeTextInput(
        bool settingsMenuOpen,
        bool overlayEnabled,
        bool onlineRoomActive,
        bool captureKeyboard,
        bool composerOpen) =>
        settingsMenuOpen ||
        (overlayEnabled && onlineRoomActive && captureKeyboard && composerOpen);

    private bool IsImeTextContextActive() => ShouldCaptureImeTextInput(
        Volatile.Read(ref _settingsMenuOpen) != 0,
        _getConfiguration().EnableOverlay,
        IsOnlineRoomActive(),
        Volatile.Read(ref _captureKeyboard) != 0,
        _session.Composer.IsOpen);

    private bool ShouldIgnoreUnactivateBeforeBackend(uint message, nint wParam) =>
        IsImeTextContextActive() &&
        (message == WmKillFocus ||
         ((message is WmActivate or WmActivateApp) && wParam == nint.Zero));

    private bool TryHandleImeCharacter(nint windowHandle, uint message, nint wParam)
    {
        if (!IsImeTextContextActive())
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
        if (!IsImeTextContextActive() || !Win32ImeCompatibility.IsImeUiMessage(message))
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
        {
            _bindingResultRequest = null;
            _bindingResultText = null;
        }
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
                devices = OverlayInputDevices.Keyboard |
                          OverlayInputDevices.Mouse |
                          OverlayInputDevices.Text;
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
        ClearPendingQuickActions();
        _focusInputNextFrame = false;
        _statusText = null;
        _composerStatusText = null;
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
        ClearMemberNameCache();
        ForceReleaseVoicePushToTalkSources();
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
        _flydigiControllerInputPoller.Dispose();
        _windowVoicePushToTalkGate.Dispose();
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

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
    RemotePlayerChatMute,
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
