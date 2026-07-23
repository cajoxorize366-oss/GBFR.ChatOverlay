using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using GBFR.ChatOverlay.Template;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Overlay;
using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Stt;

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
    private readonly VoiceInputCoordinator? _voiceInput;

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;
        MigrateVoiceLanguageDefault();

        var historyCapacity = Math.Clamp(_configuration.HistoryCapacity, 10, 5_000);
        var history = new ChatHistory(historyCapacity);
        history.Add(
            "System",
            "GBFR Chat Overlay loaded. Press Y to open chat.",
            ChatMessageKind.System);

        IChatTransport transport = new LocalPreviewChatTransport();
        IIncomingChatSource? incoming = null;
        var transportStatus = "Local preview: the Relink chat bridge is not attached.";
        if (_hooks is not null && _configuration.EnableNativeChatBridge)
        {
            try
            {
                _nativeChatBridge = new RelinkChatBridge(
                    _hooks,
                    message => _logger.WriteLine($"[{_modConfig.ModId}] {message}"));
                _nativeChatBridge.Initialize();
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

        if (_configuration.EnableVoiceInput)
        {
            var assemblyDirectory = Path.GetDirectoryName(typeof(Mod).Assembly.Location)
                                    ?? AppContext.BaseDirectory;
            var worker = SttWorkerProcessClient.Create(
                assemblyDirectory,
                _configuration.VoiceLanguageCode,
                _configuration.VoiceCpuThreads,
                _configuration.VoiceMaximumSeconds,
                message => _logger.WriteLine($"[{_modConfig.ModId}] {message}"));
            _voiceInput = new VoiceInputCoordinator(_chatSession.Composer, worker);
            if (_voiceInput.State is VoiceRecognitionState.Unavailable)
            {
                history.Add(
                    "System",
                    $"Voice input unavailable: {_voiceInput.StatusText}",
                    ChatMessageKind.System);
            }
            else
            {
                history.Add(
                    "System",
                    "Local Whisper base voice input ready. Hold U or LB + R3, then review and press Enter.",
                    ChatMessageKind.System);
            }
        }

        _overlay = new ChatOverlayHost(
            _chatSession,
            () => _configuration,
            message => _logger.WriteLine($"[{_modConfig.ModId}] {message}"),
            _voiceInput);

        _directInputKeyboard = new DirectInputKeyboardHook(
            _hooks,
            _overlay.TryRequestOpen,
            _overlay.ShouldCaptureKeyboard,
            _overlay.TryRequestVoiceCapture,
            _overlay.RequestVoiceCaptureEnd,
            _overlay.IsVoiceInputEnabled,
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
        MigrateVoiceLanguageDefault();
        _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
    }

    public override bool CanSuspend() => _overlay is not null;

    public override void Suspend()
    {
        _nativeChatBridge?.Suspend();
        _directInputKeyboard?.Suspend();
        _overlay?.Suspend();
    }

    public override void Resume()
    {
        _overlay?.Resume();
        _directInputKeyboard?.Resume();
        _nativeChatBridge?.Resume();
    }

    public override void Disposing()
    {
        _nativeChatBridge?.Suspend();
        _directInputKeyboard?.Suspend();
        _overlay?.Shutdown();
        _voiceInput?.Dispose();
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

    private void MigrateVoiceLanguageDefault()
    {
        var previousLanguage = _configuration.VoiceLanguageCode;
        if (!_configuration.ApplyVoiceLanguageDefaultMigration())
            return;

        try
        {
            _configuration.Save?.Invoke();
            if (string.Equals(previousLanguage?.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            {
                _logger.WriteLine(
                    $"[{_modConfig.ModId}] Migrated the previous automatic voice-language default to Chinese (zh).");
            }
        }
        catch (Exception exception)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Failed to persist voice-language migration: {exception.Message}");
        }
    }

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
