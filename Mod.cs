using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using GBFR.ChatOverlay.Runtime;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Overlay;
using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Audio;
using GBFR.OverlayHub.Contracts;

namespace GBFR.ChatOverlay;

/// <summary>
/// Composes the native chat, Party voice, input and overlay modules for the Reloaded-II runtime.
/// </summary>
public sealed class Mod : IDisposable
{
    /// <summary>
    /// Provides access to the Reloaded.Hooks API.
    /// </summary>
    /// <remarks>This is null if you remove dependency on Reloaded.SharedLib.Hooks in your mod.</remarks>
    private readonly IReloadedHooks? _hooks;

    /// <summary>
    /// Provides access to the Reloaded logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Provides access to this mod's configuration.
    /// </summary>
    private Config _configuration;
    private long _configurationRevision;

    /// <summary>
    /// The configuration of the currently executing mod.
    /// </summary>
    private readonly IModConfig _modConfig;
    private readonly Action<Action<Config>> _persistConfigurationUpdate;
    private readonly Action<string> _requestOverlayBrokerRecovery;

    private readonly ChatSession _chatSession;
    private readonly ChatBlacklist _chatBlacklist = new();
    private readonly ChatOverlayPeer _overlay;
    private DirectInputKeyboardHook? _directInputKeyboard;
    private readonly RelinkChatBridge? _nativeChatBridge;
    private readonly PartyLifecycleProbe? _partyLifecycleProbe;
    private readonly RelinkPartyHudTracker? _partyHudTracker;
    private readonly InGameAudioSettingsController? _audioSettings;
    private readonly RelinkGameContextProbe? _gameContextProbe;
    private readonly IGbfrOverlayHub _overlayHub;
    private bool _ownsOverlayBroker;
    private int _disposed;

    internal Mod(ModContext context)
    {
        _hooks = context.Hooks;
        _logger = context.Logger;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;
        _persistConfigurationUpdate = context.UpdateConfiguration;
        _requestOverlayBrokerRecovery = context.RequestOverlayBrokerRecovery;
        _overlayHub = context.OverlayHub;
        _ownsOverlayBroker = context.OwnsOverlayBroker;

        Action<string> moduleLog =
            message => _logger.WriteLine($"[{_modConfig.ModId}] {message}");

        if (_hooks is not null)
        {
            try
            {
                _gameContextProbe = RelinkGameContextProbe.CreateForCurrentProcess(moduleLog);
            }
            catch (Exception exception)
            {
                _logger.WriteLine(
                    $"[{_modConfig.ModId}] Relink native chat-manager probe unavailable; " +
                    $"native sends will fail closed: {exception.Message}");
            }
        }

        var audioInputSelection = ResolvedAudioEndpointSelection.SystemDefault();
        var audioOutputSelection = ResolvedAudioEndpointSelection.SystemDefault();
        if (_configuration.EnableVoiceInput)
        {
            var audioCatalog = new WindowsAudioEndpointCatalog();
            audioInputSelection = AudioEndpointSelectionResolver.Resolve(
                _configuration.VoiceMicrophoneDeviceId,
                AudioEndpointFlow.Capture,
                audioCatalog,
                moduleLog);
            audioOutputSelection = AudioEndpointSelectionResolver.Resolve(
                _configuration.VoicePlaybackDeviceId,
                AudioEndpointFlow.Render,
                audioCatalog,
                moduleLog);
        }

        if (_hooks is not null)
        {
            PartyLifecycleProbe? partyLifecycleProbe = null;
            try
            {
                partyLifecycleProbe = new PartyLifecycleProbe(
                    _hooks,
                    moduleLog,
                    enableLifecycleLogging: _configuration.EffectivePartyLifecycleDiagnostics,
                    enableVoice: _configuration.EnableVoiceInput,
                    audioInputSelection: audioInputSelection,
                    audioOutputSelection: audioOutputSelection,
                    invalidateRoomIdentity: ResetLobbyOwner,
                    roomIdentityReader: GetRoomIdentitySnapshot);
                StartupPhaseDiagnostic.Run(
                    "party-lifecycle-hooks",
                    moduleLog,
                    partyLifecycleProbe.Initialize);
                _partyLifecycleProbe = partyLifecycleProbe;
            }
            catch (Exception exception)
            {
                partyLifecycleProbe?.Dispose();
                _logger.WriteLine(
                    $"[{_modConfig.ModId}] Online Party-room gate unavailable; the Overlay will remain " +
                    $"hidden (fail-closed): {exception}");
            }
        }

        if (_hooks is not null)
        {
            RelinkPartyHudTracker? partyHudTracker = null;
            try
            {
                partyHudTracker = new RelinkPartyHudTracker(_hooks, moduleLog);
                partyHudTracker.Initialize();
                _partyHudTracker = partyHudTracker;
            }
            catch (Exception exception)
            {
                _logger.WriteLine(
                    $"[{_modConfig.ModId}] Native party-HUD anchor tracking unavailable; " +
                    $"voice indicators will remain hidden (fail-closed): {exception}");
            }
        }

        if (_hooks is not null && _configuration.EnableVoiceInput)
        {
            try
            {
                _audioSettings = new InGameAudioSettingsController(
                    _configuration,
                    UpdateConfiguration,
                    moduleLog);
            }
            catch (Exception exception)
            {
                _logger.WriteLine(
                    $"[{_modConfig.ModId}] Local microphone monitor unavailable: {exception}");
            }
        }

        var historyCapacity = Math.Clamp(_configuration.HistoryCapacity, 10, 5_000);
        var history = new ChatHistory(historyCapacity);
        history.Add(
            "System",
            "GBFR Chat Overlay loaded. Press Y to open chat.",
            ChatMessageKind.System);
        if (_audioSettings is not null)
        {
            history.Add(
                "System",
                "VOICE: press F10 for microphone/speaker selection and the local self-test. " +
                "Hold U for Party voice when another Mod client is ready. Use headphones for testing.",
                ChatMessageKind.System);
        }

        IChatTransport transport = new UnavailableChatTransport();
        IIncomingChatSource? incoming = null;
        var transportStatus = "Native Relink chat is unavailable.";
        if (_hooks is not null && _configuration.EnableNativeChatBridge)
        {
            RelinkChatBridge? nativeChatBridge = null;
            try
            {
                nativeChatBridge = new RelinkChatBridge(
                    _hooks,
                    message => _logger.WriteLine($"[{_modConfig.ModId}] {message}"),
                    _gameContextProbe,
                    _chatBlacklist,
                    GetLocalNetworkRole);
                StartupPhaseDiagnostic.Run(
                    "native-chat-hooks",
                    moduleLog,
                    nativeChatBridge.Initialize);
                _nativeChatBridge = nativeChatBridge;
                _gameContextProbe ??= nativeChatBridge.GameContext;
                transport = nativeChatBridge;
                incoming = nativeChatBridge;
                transportStatus = "Native Relink chat connected (2.0.4).";
                history.Add(
                    "System",
                    "Native Relink chat send/receive bridge connected for game version 2.0.4.",
                    ChatMessageKind.System);
            }
            catch (Exception exception)
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Native chat bridge unavailable: {exception}");
                history.Add(
                    "System",
                    "Native chat bridge validation failed; chat sending is unavailable.",
                    ChatMessageKind.System);
            }
        }
        else
        {
            history.Add(
                "System",
                "Native chat bridge is disabled or Reloaded.Hooks is unavailable; chat sending is unavailable.",
                ChatMessageKind.System);
        }

        _chatSession = new ChatSession(
            history,
            new ChatComposer(),
            transport,
            incoming: incoming,
            transportStatusText: transportStatus,
            getLocalIdentity: () =>
                _nativeChatBridge?.GetLocalIdentity() ?? new LocalChatIdentity("Local", 0));

        _overlay = new ChatOverlayPeer(
            _chatSession,
            () => Volatile.Read(ref _configuration),
            IsOnlineRoomActive,
            ReleaseRoomScopedInputs,
            GetVoiceUiStatus,
            GetPartyHudAnchors,
            GetPlayerMuteSlots,
            SetPlayerMuted,
            (kind, id) => _nativeChatBridge?.SendOfficialQuickAction(kind, id) ??
                ChatSendResult.Unavailable("Relink's native communication bridge is unavailable."),
            _audioSettings,
            UpdateConfiguration,
            SetLocalMicrophoneSelfTestRequested,
            () => IsOnlineRoomActive() &&
                  _configuration.EnableVoiceInput &&
                  _partyLifecycleProbe?.IsVoicePushToTalkReady == true,
            pressed => _partyLifecycleProbe?.SetPushToTalkPressed(pressed),
            ForceReleaseVoiceInputs,
            HandleOverlayBrokerUnavailable,
            message => _logger.WriteLine($"[{_modConfig.ModId}] {message}"),
            chatBlacklist: _chatBlacklist,
            getHostPlayerNumber: GetHostPlayerNumber,
            getRemotePlayerName: GetRemotePlayerName,
            getVoiceIndicatorSnapshot: GetVoiceIndicatorSnapshot,
            readRoomTransition: ReadRoomTransition,
            getEstablishedVoiceParticipantCount: GetEstablishedVoiceParticipantCount,
            getLocalPlayerName: GetLocalPlayerName,
            readMemberTransition: ReadMemberTransition);

        try
        {
            var registration = _overlayHub.Register(_overlay);
            _overlay.AttachRegistration(registration);
            moduleLog(
                $"Chat registered as a normal Overlay Broker peer; " +
                $"bootstrap='{_overlayHub.HostModId}', local_bootstrap={_ownsOverlayBroker}.");
        }
        catch (Exception exception)
        {
            _overlay.OnHostUnavailable($"registration failed: {exception.GetType().Name}");
            moduleLog($"Chat peer registration failed closed: {exception}");
        }

        if (_ownsOverlayBroker && _hooks is not null)
            BecomeOverlayBrokerCarrier();
        else if (!_ownsOverlayBroker)
        {
            moduleLog(
                "Chat is a Broker guest; it did not install a second DirectInput hook. " +
                "WndProc hotkeys and the bootstrap peer's input writer remain authoritative.");
        }

        LogInjectionSource(moduleLog);
        StartDeferredFileHashDiagnostics(moduleLog);
    }

    public void ConfigurationUpdated(Config configuration)
    {
        Interlocked.Increment(ref _configurationRevision);
        try
        {
            Volatile.Write(ref _configuration, configuration);
            _chatSession.History.Resize(Math.Clamp(configuration.HistoryCapacity, 10, 5_000));
            _audioSettings?.ApplyConfiguration(configuration);
            if (!configuration.EnableVoiceInput)
                SetLocalMicrophoneSelfTestRequested(false);
            _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
        }
        finally
        {
            Interlocked.Increment(ref _configurationRevision);
        }
    }

    public void Suspend()
    {
        // The process-wide Broker and its input writer outlive this peer's suspended state.
        _audioSettings?.Suspend();
        _partyLifecycleProbe?.Suspend();
        _partyHudTracker?.Suspend();
        _nativeChatBridge?.Suspend();
        _overlay.Suspend();
    }

    public void Resume()
    {
        _overlay.Resume();
        _audioSettings?.Resume();
        _partyLifecycleProbe?.Resume();
        _partyHudTracker?.Resume();
        _nativeChatBridge?.Resume();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        RunDisposeStep("voice input release", ForceReleaseVoiceInputs);
        RunDisposeStep("DirectInput carrier", () =>
        {
            _directInputKeyboard?.Dispose();
            _directInputKeyboard = null;
        });
        RunDisposeStep("OverlayHub peer", _overlay.Dispose);
        RunDisposeStep("local audio settings", () => _audioSettings?.Dispose());
        RunDisposeStep("Party lifecycle and voice", () => _partyLifecycleProbe?.Dispose());
        RunDisposeStep("native party HUD", () => _partyHudTracker?.Suspend());
        RunDisposeStep("native chat", () => _nativeChatBridge?.Suspend());
    }

    internal void BrokerCarrierUpkeep() => _directInputKeyboard?.Poll();

    internal void BecomeOverlayBrokerCarrier()
    {
        _ownsOverlayBroker = true;
        if (_hooks is null || _directInputKeyboard is not null)
            return;

        DirectInputKeyboardHook? directInputKeyboard = null;
        try
        {
            directInputKeyboard = new DirectInputKeyboardHook(
                DirectInputBrokerBridge.Instance,
                _overlay.CanRequestOpen,
                _overlay.TryRequestOpen,
                () => (_overlayHub.CapturedInputDevices &
                       (OverlayInputDevices.Keyboard | OverlayInputDevices.Text)) != 0,
                () => (_overlayHub.CapturedInputDevices & OverlayInputDevices.Mouse) != 0,
                () => IsOnlineRoomActive() &&
                      _configuration.EnableVoiceInput &&
                      _partyLifecycleProbe?.IsVoicePushToTalkReady == true,
                _overlay.ObserveVoicePushToTalkKey,
                () => _partyLifecycleProbe?.RequestVoiceDiagnosticSample(),
                () => _overlay.IsInitialized && !_overlay.IsSuspended,
                _overlay.ObserveSettingsMenuKey,
                pressed => _audioSettings?.SetSelfTestPressed(pressed),
                message => _logger.WriteLine($"[{_modConfig.ModId}] {message}"),
                () => Volatile.Read(ref _configuration),
                () => Volatile.Read(ref _configurationRevision),
                _overlay.ObserveNativeInputSnapshot,
                _overlay.ObserveQuickActionKey,
                _overlay.ObserveGlobalMuteKey,
                _overlay.ObserveRemotePlayerChatMuteKey);
            StartupPhaseDiagnostic.Run(
                "directinput-broker-hooks",
                message => _logger.WriteLine($"[{_modConfig.ModId}] {message}"),
                directInputKeyboard.Initialize);
            _directInputKeyboard = directInputKeyboard;
        }
        catch (Exception exception)
        {
            directInputKeyboard?.Dispose();
            _logger.WriteLine(
                $"[{_modConfig.ModId}] DirectInput interception unavailable: {exception}");
        }
    }

    private void HandleOverlayBrokerUnavailable(string reason)
    {
        _directInputKeyboard?.Dispose();
        _directInputKeyboard = null;
        _ownsOverlayBroker = false;
        _requestOverlayBrokerRecovery(reason);
    }

    private void UpdateConfiguration(Action<Config> update)
    {
        Interlocked.Increment(ref _configurationRevision);
        try
        {
            _persistConfigurationUpdate(update);
            _chatSession.History.Resize(Math.Clamp(_configuration.HistoryCapacity, 10, 5_000));
        }
        finally
        {
            Interlocked.Increment(ref _configurationRevision);
        }
    }

    private void LogInjectionSource(Action<string> log)
    {
        try
        {
            log(ReloadedInjectionSourceDetector.FormatLogMessage(
                ReloadedInjectionSourceDetector.Detect()));
        }
        catch (Exception exception)
        {
            log(
                $"Reloaded-II load source=unknown; detector failed with " +
                $"{exception.GetType().Name}: {exception.Message}.");
        }
    }

    private void StartDeferredFileHashDiagnostics(Action<string> log)
    {
        string? executablePath = null;
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            executablePath = process.MainModule?.FileName;
        }
        catch (Exception exception)
        {
            log(
                $"Startup phase=relink-executable-sha256 state=failed " +
                $"reason=path-unavailable error={exception.GetType().Name} diagnostic_only=true.");
        }

        _ = DeferredFileHashDiagnostic.Start(
            "relink-executable",
            executablePath,
            RelinkBuildLocator.SupportedSha256,
            log);
        if (_partyLifecycleProbe?.ModulePath is { Length: > 0 } partyModulePath)
        {
            _ = DeferredFileHashDiagnostic.Start(
                "partywin",
                partyModulePath,
                PartyLifecycleProbe.SupportedPartySha256,
                log);
        }
    }

    private PartyVoiceUiStatus GetVoiceUiStatus()
    {
        if (!_configuration.EnableVoiceInput)
            return PartyVoiceUiStatus.Disabled;

        var localMonitorState = _audioSettings?.GetSnapshot().SelfTestState;
        if (localMonitorState == LocalMicrophoneMonitorState.Starting ||
            localMonitorState == LocalMicrophoneMonitorState.Monitoring)
        {
            return new PartyVoiceUiStatus(PartyVoiceUiState.LocalSelfTesting);
        }
        if (localMonitorState == LocalMicrophoneMonitorState.SignalDetected)
            return new PartyVoiceUiStatus(PartyVoiceUiState.LocalSelfTestSignalDetected);
        if (localMonitorState == LocalMicrophoneMonitorState.Faulted)
            return new PartyVoiceUiStatus(PartyVoiceUiState.LocalSelfTestFailed);

        return _partyLifecycleProbe?.VoiceUiStatus ?? PartyVoiceUiStatus.Unavailable;
    }

    private bool IsOnlineRoomActive() =>
        _partyLifecycleProbe?.IsOnlineRoomActive == true;

    private PartyNetworkLocalRole GetLocalNetworkRole() =>
        _partyLifecycleProbe?.LocalNetworkRole ?? PartyNetworkLocalRole.Unknown;

    private int? GetHostPlayerNumber()
    {
        if (!IsOnlineRoomActive())
            return null;
        return _nativeChatBridge?.TryGetHostPlayerNumber(out var playerNumber) == true
            ? playerNumber
            : null;
    }

    private string? GetRemotePlayerName(int remotePlayerNumber) =>
        _nativeChatBridge?.TryGetRemotePlayerName(remotePlayerNumber, out var playerName) == true
            ? playerName
            : null;

    private string? GetLocalPlayerName() =>
        _nativeChatBridge?.TryGetLocalPlayerName(out var playerName) == true
            ? playerName
            : null;

    private PartyVoiceIndicatorSnapshot GetVoiceIndicatorSnapshot() =>
        _partyLifecycleProbe?.GetVoiceIndicatorSnapshot() ?? PartyVoiceIndicatorSnapshot.Unavailable;

    private PartyRoomIdentitySnapshot GetRoomIdentitySnapshot() =>
        _nativeChatBridge?.TryGetRoomIdentitySnapshot(out var snapshot) == true
            ? snapshot
            : default;

    private PartyRoomTransition? ReadRoomTransition() =>
        _partyLifecycleProbe?.TryReadRoomTransition(out var transition) == true
            ? transition
            : null;

    private PartyMemberTransition? ReadMemberTransition() =>
        _partyLifecycleProbe?.TryReadMemberTransition(out var transition) == true
            ? transition
            : null;

    private int GetEstablishedVoiceParticipantCount() =>
        Math.Max(0, _partyLifecycleProbe?.EstablishedVoiceParticipantCount ?? 0);

    private void ResetLobbyOwner() => _nativeChatBridge?.ResetLobbyOwner();

    private IReadOnlyList<PartyHudAnchor> GetPartyHudAnchors(
        float viewportX,
        float viewportY,
        float viewportWidth,
        float viewportHeight) =>
        _partyHudTracker?.GetAnchors(
            viewportX,
            viewportY,
            viewportWidth,
            viewportHeight) ?? Array.Empty<PartyHudAnchor>();

    private IReadOnlyList<PartyPlayerMuteSlotStatus> GetPlayerMuteSlots() =>
        _partyLifecycleProbe?.GetPlayerMuteSlots() ??
        PartyPlayerMuteSlotStatus.Unavailable(
            "玩家禁言服务不可用。 / Player mute service is unavailable.");

    private PartyPlayerMuteOperationResult SetPlayerMuted(int playerNumber, bool muted) =>
        _partyLifecycleProbe?.SetPlayerMuted(playerNumber, muted) ??
        new PartyPlayerMuteOperationResult(
            false,
            "玩家禁言服务不可用。 / Player mute service is unavailable.");

    private void ReleaseRoomScopedInputs()
    {
        ResetLobbyOwner();
        _partyLifecycleProbe?.SetPushToTalkPressed(false);
        SetLocalMicrophoneSelfTestRequested(false);
    }

    private void SetLocalMicrophoneSelfTestRequested(bool pressed)
    {
        if (pressed && !_configuration.EnableVoiceInput)
            pressed = false;
        _directInputKeyboard?.SetLocalMicrophoneMonitorPressed(pressed);
        if (_directInputKeyboard is null)
            _audioSettings?.SetSelfTestPressed(pressed);
    }

    private void ForceReleaseVoiceInputs()
    {
        _directInputKeyboard?.ForceReleaseVoiceInputs();
        _partyLifecycleProbe?.SetPushToTalkPressed(false);
        _audioSettings?.SetSelfTestPressed(false);
    }

    private void RunDisposeStep(string component, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            try
            {
                _logger.WriteLine(
                    $"[{_modConfig.ModId}] Disposal of {component} failed; continuing teardown: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
            catch
            {
                // Teardown must continue even when the host logger is unavailable.
            }
        }
    }

}
