using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using GBFR.ChatOverlay.Template;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Overlay;

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

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

        var historyCapacity = Math.Clamp(_configuration.HistoryCapacity, 10, 5_000);
        var history = new ChatHistory(historyCapacity);
        history.Add(
            "System",
            "GBFR Chat Overlay loaded. Press Y to open local preview chat.",
            ChatMessageKind.System);
        history.Add(
            "System",
            "Relink chat send/receive is not connected yet; preview messages stay on this PC.",
            ChatMessageKind.System);

        _chatSession = new ChatSession(
            history,
            new ChatComposer(),
            new LocalPreviewChatTransport());

        if (_hooks is null)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Reloaded.Hooks is unavailable; overlay disabled.");
            return;
        }

        _overlay = new ChatOverlayHost(
            _chatSession,
            () => _configuration,
            message => _logger.WriteLine($"[{_modConfig.ModId}] {message}"));
        _ = InitializeOverlayAsync();
    }

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
    }

    public override bool CanSuspend() => _overlay is not null;

    public override void Suspend() => _overlay?.Suspend();

    public override void Resume() => _overlay?.Resume();
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

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
