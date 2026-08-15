using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;
using GBFR.ChatOverlay.Configuration;
using GBFR.OverlayHub.Contracts;

namespace GBFR.ChatOverlay.Runtime;

/// <summary>
/// Carries the dependencies required to compose the in-process mod runtime.
/// </summary>
internal sealed class ModContext
{
    /// <summary>
    /// Provides access to the Reloaded.Hooks API.
    /// </summary>
    public IReloadedHooks? Hooks { get; set; } = null!;

    /// <summary>
    /// Provides access to this mod's configuration.
    /// </summary>
    public Config Configuration { get; set; } = null!;

    /// <summary>
    /// Applies and persists an in-game configuration edit through the runtime-owned instance.
    /// </summary>
    public Action<Action<Config>> UpdateConfiguration { get; set; } = null!;

    public IGbfrOverlayHub OverlayHub { get; set; } = null!;

    public bool OwnsOverlayBroker { get; set; }

    public Action<string> RequestOverlayBrokerRecovery { get; set; } = _ => { };

    /// <summary>
    /// Receives diagnostic lines after Startup applies the mod id and debug file fan-out.
    /// </summary>
    public Action<string> Log { get; set; } = _ => { };
}
