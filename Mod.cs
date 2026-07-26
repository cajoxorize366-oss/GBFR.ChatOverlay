using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using GBFR.ChatOverlay.Template;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Overlay;
using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Audio;

namespace GBFR.ChatOverlay;

/// <summary>
/// Your mod logic goes here.
/// </summary>
public class Mod : ModBase // <= Do not Remove.
{
    /// <summary>
    /// Provides access to the mod loader API.
    /// </summary>
    private readonly IModLoader _modLoader;

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
    /// Entry point into the mod, instance that created this class.
    /// </summary>
    private readonly IMod _owner;

    /// <summary>
    /// Provides access to this mod's configuration.
    /// </summary>
    private Config _configuration;

    /// <summary>
    /// The configuration of the currently executing mod.
    /// </summary>
    private readonly IModConfig _modConfig;

    private readonly ChatSession _chatSession;
    private readonly ChatOverlayHost? _overlay;
    private readonly DirectInputKeyboardHook? _directInputKeyboard;
    private readonly RelinkChatBridge? _nativeChatBridge;
    private readonly PartyLifecycleProbe? _partyLifecycleProbe;
    private readonly RelinkPartyHudTracker? _partyHudTracker;
    private readonly InGameAudioSettingsController? _audioSettings;
    private readonly RelinkGameContextProbe? _gameContextProbe;

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

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
        if (_configuration.EnableMutedPartyChatControlCanary || _configuration.EnableVoiceInput)
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
            try
            {
                _partyLifecycleProbe = new PartyLifecycleProbe(
                    _hooks,
                    moduleLog,
                    enableLifecycleLogging: _configuration.EnablePartyLifecycleProbe,
                    enableMutedChatControlCanary:
                        _configuration.EnableMutedPartyChatControlCanary ||
                        _configuration.EnableVoiceInput,
                    enableVoiceTest: _configuration.EnableVoiceInput,
                    audioInputSelection: audioInputSelection,
                    audioOutputSelection: audioOutputSelection);
                _partyLifecycleProbe.Initialize();
            }
            catch (Exception exception)
            {
                _logger.WriteLine(
                    $"[{_modConfig.ModId}] Online Party-room gate unavailable; the Overlay will remain " +
                    $"hidden (fail-closed): {exception}");
            }
        }

        if (_hooks is not null)
        {
            try
            {
                _partyHudTracker = new RelinkPartyHudTracker(_hooks, moduleLog);
                _partyHudTracker.Initialize();
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
                    context.UpdateConfiguration,
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
                "VOICE PREVIEW: press F10 for microphone/speaker selection and the local self-test. " +
                "Hold U for Party voice when another Mod client is ready. Use headphones for testing.",
                ChatMessageKind.System);
        }

        IChatTransport transport = new LocalPreviewChatTransport();
        IIncomingChatSource? incoming = null;
        var transportStatus = "Local preview: the Relink chat bridge is not attached.";
        if (_hooks is not null && _configuration.EnableNativeChatBridge)
        {
            try
            {
                _nativeChatBridge = new RelinkChatBridge(
                    _hooks,
                    message => _logger.WriteLine($"[{_modConfig.ModId}] {message}"),
                    _gameContextProbe);
                _nativeChatBridge.Initialize();
                _gameContextProbe ??= _nativeChatBridge.GameContext;
                transport = _nativeChatBridge;
                incoming = _nativeChatBridge;
                transportStatus = "Native Relink chat connected (2.0.2).";
                history.Add(
                    "System",
                    "Native Relink chat send/receive bridge connected for game version 2.0.2.",
                    ChatMessageKind.System);
            }
            catch (Exception exception)
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Native chat bridge unavailable: {exception}");
                history.Add(
                    "System",
                    "Native chat bridge validation failed; messages remain in local preview.",
                    ChatMessageKind.System);
            }
        }
        else
        {
            history.Add(
                "System",
                "Native chat bridge is disabled or Reloaded.Hooks is unavailable; messages remain local.",
                ChatMessageKind.System);
        }

        _chatSession = new ChatSession(
            history,
            new ChatComposer(),
            transport,
            incoming: incoming,
            transportStatusText: transportStatus);

        if (_hooks is null)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Reloaded.Hooks is unavailable; overlay disabled.");
            return;
        }

        try
        {
            DxgiPresentBridge.Configure(_modLoader.GetDirectoryForModId(_modConfig.ModId));
        }
        catch (Exception exception)
        {
            _logger.WriteLine(
                $"[{_modConfig.ModId}] Native Present compatibility bridge unavailable; " +
                $"overlay and input interception are disabled (fail-closed): {exception}");
            return;
        }

        _overlay = new ChatOverlayHost(
            _chatSession,
            () => _configuration,
            IsOnlineRoomActive,
            ReleaseRoomScopedInputs,
            GetVoiceUiStatus,
            GetPartyHudAnchors,
            _audioSettings,
            context.UpdateConfiguration,
            SetLocalMicrophoneSelfTestRequested,
            ForceReleaseVoiceInputs,
            message => _logger.WriteLine($"[{_modConfig.ModId}] {message}"));

        _directInputKeyboard = new DirectInputKeyboardHook(
            _hooks,
            _overlay.TryRequestOpen,
            _overlay.ShouldCaptureKeyboard,
            () => IsOnlineRoomActive() &&
                  _configuration.EnableVoiceInput &&
                  _partyLifecycleProbe?.IsVoicePushToTalkReady == true,
            pressed => _partyLifecycleProbe?.SetPushToTalkPressed(pressed),
            () => _partyLifecycleProbe?.RequestVoiceDiagnosticSample(),
            () => _overlay.IsInitialized,
            _overlay.ObserveSettingsMenuKey,
            pressed => _audioSettings?.SetSelfTestPressed(pressed),
            message => _logger.WriteLine($"[{_modConfig.ModId}] {message}"));
        try
        {
            _directInputKeyboard.Initialize();
        }
        catch (Exception exception)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] DirectInput interception unavailable: {exception}");
        }

        _ = InitializeOverlayAsync();
    }

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        _audioSettings?.ApplyConfiguration(configuration);
        if (!configuration.EnableVoiceInput)
            SetLocalMicrophoneSelfTestRequested(false);
        _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
    }

    public override bool CanSuspend() => _overlay is not null;

    public override void Suspend()
    {
        _directInputKeyboard?.Suspend();
        _audioSettings?.Suspend();
        _partyLifecycleProbe?.Suspend();
        _partyHudTracker?.Suspend();
        _nativeChatBridge?.Suspend();
        _overlay?.Suspend();
    }

    public override void Resume()
    {
        _overlay?.Resume();
        _audioSettings?.Resume();
        _partyLifecycleProbe?.Resume();
        _partyHudTracker?.Resume();
        _nativeChatBridge?.Resume();
        _directInputKeyboard?.Resume();
    }
    #endregion

    private async Task InitializeOverlayAsync()
    {
        try
        {
            await _overlay!.InitializeAsync(_hooks!).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Failed to initialize overlay: {exception}");
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

    private void ReleaseRoomScopedInputs()
    {
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

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
