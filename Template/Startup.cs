/*
 * This file and other files in the `Template` folder are intended to be left unedited (if possible),
 * to make it easier to upgrade to newer versions of the template.
*/

using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using GBFR.ChatOverlay.Template.Configuration;
using GBFR.ChatOverlay.Configuration;
using GBFR.OverlayHub.Contracts;
using GBFR.OverlayHub.Runtime;
using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Template;

public class Startup : IMod, IExports
{
    /// <summary>
    /// Used for writing text to the Reloaded log.
    /// </summary>
    private ILogger _logger = null!;

    /// <summary>
    /// Provides access to the mod loader API.
    /// </summary>
    private IModLoader _modLoader = null!;

    /// <summary>
    /// Stores the contents of your mod's configuration. Automatically updated by template.
    /// </summary>
    private Config _configuration = null!;
    private readonly object _configurationSync = new();

    /// <summary>
    /// An interface to Reloaded's the function hooks/detours library.
    /// See: https://github.com/Reloaded-Project/Reloaded.Hooks
    ///      for documentation and samples. 
    /// </summary>
    private IReloadedHooks? _hooks;

    /// <summary>
    /// Configuration of the current mod.
    /// </summary>
    private IModConfig _modConfig = null!;

    /// <summary>
    /// Encapsulates your mod logic.
    /// </summary>
    private ModBase _mod = new Mod();
    private bool _overlayHubControllerRegistered;
    private OverlayBrokerHost? _overlayBrokerHost;

    /// <summary>
    /// Entry point for your mod.
    /// </summary>
    public void StartEx(IModLoaderV1 loaderApi, IModConfigV1 modConfig)
    {
        _modLoader = (IModLoader)loaderApi;
        _modConfig = (IModConfig)modConfig;
        _logger = (ILogger)_modLoader.GetLogger();
        _modLoader.GetController<IReloadedHooks>()?.TryGetTarget(out _hooks!);

        // Your config file is in Config.json.
        // Need a different name, format or more configurations? Modify the `Configurator`.
        // If you do not want a config, remove Configuration folder and Config class.
        var configurator = new Configurator(_modLoader.GetModConfigDirectory(_modConfig.ModId));
        configurator.SetContext(new() { Application = _modLoader.GetAppConfig() });

        _configuration = configurator.GetConfiguration<Config>(0);
        _configuration.ConfigurationUpdated += OnConfigurationUpdated;

        // Please put your mod code in the class below,
        // use this class for only interfacing with mod loader.
        Action<string> moduleLog = message => _logger.WriteLine($"[{_modConfig.ModId}] {message}");
        var election = OverlayBrokerElectionService.Elect(
            _modLoader,
            this,
            _modConfig.ModId,
            moduleLog);
        if (election.IsHost)
        {
            _overlayHubControllerRegistered = true;
            if (_hooks is null)
            {
                election.HostControl!.MarkHostUnavailable("Reloaded.Hooks is unavailable");
            }
            else
            {
                try
                {
                    DxgiPresentBridge.Configure(_modLoader.GetDirectoryForModId(_modConfig.ModId));
                    _ = DxgiPresentBridge.SetCursorReleaseActive(false);
                    _overlayBrokerHost = new OverlayBrokerHost(
                        election.HostControl!,
                        moduleLog,
                        setNativeCursorRelease: capture =>
                        {
                            var installed = DxgiPresentBridge.SetCursorReleaseActive(capture);
                            if (capture && installed != DxgiPresentBridge.CursorReleaseHook.All)
                                moduleLog($"Overlay Broker cursor release installed only {installed}.");
                        });
                    _ = InitializeBrokerAsync(_overlayBrokerHost, _hooks, moduleLog);
                }
                catch (Exception exception)
                {
                    election.HostControl!.MarkHostUnavailable(
                        $"native graphics bridge initialization failed: {exception.GetType().Name}");
                    moduleLog($"Overlay Broker bootstrap failed closed: {exception}");
                }
            }
        }

        _mod = new Mod(new ModContext()
        {
            Logger = _logger,
            Hooks = _hooks,
            ModLoader = _modLoader,
            ModConfig = _modConfig,
            Owner = this,
            Configuration = _configuration,
            UpdateConfiguration = UpdateConfiguration,
            OverlayHub = election.Hub,
            OwnsOverlayBroker = election.IsHost,
        });
        if (election.IsHost && _mod is Mod concreteMod)
            _overlayBrokerHost?.SetCarrierUpkeep(concreteMod.BrokerCarrierUpkeep);
    }

    private static async Task InitializeBrokerAsync(
        OverlayBrokerHost host,
        IReloadedHooks hooks,
        Action<string> log)
    {
        try
        {
            await host.InitializeAsync(
                    hooks,
                    (tick, shouldRender, permanentFailure) =>
                        new CjkConfiguredDx11Hook(
                            tick,
                            shouldRender,
                            log,
                            permanentFailure))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log($"Overlay Broker graphics initialization failed: {exception}");
        }
    }

    private void UpdateConfiguration(Action<Config> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_configurationSync)
        {
            update(_configuration);
            _configuration.Save?.Invoke();
        }
    }

    private void OnConfigurationUpdated(IConfigurable obj)
    {
        /*
            This is executed when the configuration file gets 
            updated by the user at runtime.
        */

        // Replace configuration with new.
        lock (_configurationSync)
        {
            _configuration = (Config)obj;
            _mod.ConfigurationUpdated(_configuration);
        }
    }

    /* Mod loader actions. */
    public void Suspend() => _mod.Suspend();
    public void Resume() => _mod.Resume();
    public void Unload() => _mod.Unload();

    /*  If CanSuspend == false, suspend and resume button are disabled in Launcher and Suspend()/Resume() will never be called.
        If CanUnload == false, unload button is disabled in Launcher and Unload() will never be called.
    */
    public bool CanUnload() => _mod.CanUnload();
    public bool CanSuspend() => _mod.CanSuspend();

    /* Automatically called by the mod loader when the mod is about to be unloaded. */
    public Action Disposing => () =>
    {
        _mod.Disposing();
        _overlayBrokerHost?.Dispose();
        _overlayBrokerHost = null;
        if (_overlayHubControllerRegistered)
        {
            _modLoader.RemoveController<IGbfrOverlayHub>();
            _overlayHubControllerRegistered = false;
        }
    };

    public Type[] GetTypes() =>
    [
        typeof(IGbfrOverlayHub),
    ];
}
