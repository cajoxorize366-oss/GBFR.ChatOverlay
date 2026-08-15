using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using GBFR.ChatOverlay.Runtime.Configuration;
using GBFR.ChatOverlay.Runtime.Diagnostics;
using GBFR.ChatOverlay.Configuration;
using GBFR.OverlayHub.Contracts;
using GBFR.OverlayHub.Runtime;
using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Runtime;

public sealed class Startup : IMod, IExports, IDisposable
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
    /// Stores the live configuration used by the in-process runtime.
    /// </summary>
    private Config _configuration = null!;
    private readonly object _configurationSync = new();
    private DebugFileLog? _debugFileLog;
    private Action<string> _moduleLog = _ => { };

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
    /// Encapsulates the running mod instance after <see cref="StartEx"/> completes.
    /// </summary>
    private Mod? _mod;
    private bool _overlayHubControllerRegistered;
    private OverlayBrokerHost? _overlayBrokerHost;
    private IGbfrOverlayHub? _overlayHub;
    private readonly object _brokerRecoverySync = new();
    private int _brokerRecoveryInProgress;
    private int _disposing;

    /// <summary>
    /// Entry point for your mod.
    /// </summary>
    public void StartEx(IModLoaderV1 loader, IModConfigV1 config)
    {
        _modLoader = (IModLoader)loader;
        _modConfig = (IModConfig)config;
        _logger = (ILogger)_modLoader.GetLogger();
        _modLoader.GetController<IReloadedHooks>()?.TryGetTarget(out _hooks!);
        _debugFileLog = CreateDebugFileLog();

        var configurator = new Configurator(_modLoader.GetModConfigDirectory(_modConfig.ModId));
        configurator.SetContext(new() { Application = _modLoader.GetAppConfig() });

        _configuration = configurator.GetConfiguration<Config>(0);
        _configuration.ConfigurationUpdated += OnConfigurationUpdated;

        ApplyDebugLogging(_configuration);
        _moduleLog = CreateModuleLog();
        Action<string> moduleLog = _moduleLog;
        var election = OverlayBrokerElectionService.Elect(
            _modLoader,
            this,
            _modConfig.ModId,
            moduleLog);
        _overlayHub = election.Hub;
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
                        getNativeInputCapture:
                            DirectInputBrokerBridge.Instance.GetEffectiveInputDevices,
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
            Hooks = _hooks,
            Configuration = _configuration,
            UpdateConfiguration = UpdateConfiguration,
            OverlayHub = election.Hub,
            OwnsOverlayBroker = election.IsHost,
            RequestOverlayBrokerRecovery = RequestOverlayBrokerRecovery,
            Log = moduleLog,
        });
        if (election.IsHost)
            _overlayBrokerHost?.SetCarrierUpkeep(_mod.BrokerCarrierUpkeep);
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

    private void RequestOverlayBrokerRecovery(string reason)
    {
        lock (_brokerRecoverySync)
        {
            if (_hooks is null || Volatile.Read(ref _disposing) != 0)
                return;
            if (Interlocked.CompareExchange(ref _brokerRecoveryInProgress, 1, 0) != 0)
                return;

            Action<string> moduleLog = _moduleLog;
            IOverlayBrokerHostControl? claimedHost = null;
            try
            {
                moduleLog($"Overlay Broker recovery requested: {reason}.");
                var election = OverlayBrokerElectionService.Elect(
                    _modLoader,
                    this,
                    _modConfig.ModId,
                    moduleLog);
                _overlayHub = election.Hub;
                if (!election.IsHost)
                {
                    Interlocked.Exchange(ref _brokerRecoveryInProgress, 0);
                    return;
                }

                _overlayHubControllerRegistered = true;
                claimedHost = election.HostControl;

                DxgiPresentBridge.Configure(_modLoader.GetDirectoryForModId(_modConfig.ModId));
                _ = DxgiPresentBridge.SetCursorReleaseActive(false);
                var recoveredHost = new OverlayBrokerHost(
                    claimedHost!,
                    moduleLog,
                    getNativeInputCapture:
                        DirectInputBrokerBridge.Instance.GetEffectiveInputDevices,
                    setNativeCursorRelease: capture =>
                    {
                        var installed = DxgiPresentBridge.SetCursorReleaseActive(capture);
                        if (capture && installed != DxgiPresentBridge.CursorReleaseHook.All)
                            moduleLog($"Overlay Broker cursor release installed only {installed}.");
                    });
                claimedHost = null;
                Interlocked.Exchange(ref _overlayBrokerHost, recoveredHost)?.Dispose();
                _ = InitializeRecoveredBrokerAsync(recoveredHost, _hooks, moduleLog);
            }
            catch (Exception exception)
            {
                claimedHost?.MarkHostUnavailable(
                    $"recovery bootstrap failed: {exception.GetType().Name}");
                Interlocked.Exchange(ref _brokerRecoveryInProgress, 0);
                moduleLog($"Overlay Broker recovery failed closed: {exception}");
            }
        }
    }

    private async Task InitializeRecoveredBrokerAsync(
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
            lock (_brokerRecoverySync)
            {
                if (Volatile.Read(ref _disposing) != 0 ||
                    !ReferenceEquals(Volatile.Read(ref _overlayBrokerHost), host))
                {
                    host.Dispose();
                    return;
                }
                var mod = _mod;
                if (mod is not null)
                {
                    mod.BecomeOverlayBrokerCarrier();
                    host.SetCarrierUpkeep(mod.BrokerCarrierUpkeep);
                }
            }
            log("Overlay Broker recovery completed with one coordinated graphics writer.");
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _overlayBrokerHost, null, host);
            log($"Overlay Broker recovery initialization failed closed: {exception}");
        }
        finally
        {
            Interlocked.Exchange(ref _brokerRecoveryInProgress, 0);
        }
    }

    private void UpdateConfiguration(Action<Config> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_configurationSync)
        {
            update(_configuration);
            ApplyDebugLogging(_configuration);
            _configuration.Save?.Invoke();
        }
    }

    private void OnConfigurationUpdated(IConfigurable obj)
    {
        var configuration = (Config)obj;
        lock (_configurationSync)
        {
            if (Volatile.Read(ref _disposing) != 0)
            {
                configuration.DisposeEvents();
                return;
            }

            _configuration = configuration;
            ApplyDebugLogging(configuration);
            _mod?.ConfigurationUpdated(_configuration);
        }
    }

    public void Suspend() => _mod?.Suspend();
    public void Resume() => _mod?.Resume();
    public void Unload() { }
    public bool CanUnload() => false;
    public bool CanSuspend() => Volatile.Read(ref _disposing) == 0 && _mod is not null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
            return;

        Mod? mod;
        OverlayBrokerHost? overlayBrokerHost;
        lock (_brokerRecoverySync)
        {
            mod = Interlocked.Exchange(ref _mod, null);
            overlayBrokerHost = Interlocked.Exchange(ref _overlayBrokerHost, null);
        }

        RunDisposeStep("configuration watcher", () =>
        {
            Config configuration;
            lock (_configurationSync)
                configuration = _configuration;

            configuration.DisposeEvents();
        });
        RunDisposeStep("mod runtime", () => mod?.Dispose());
        RunDisposeStep("OverlayHub host", () => overlayBrokerHost?.Dispose());
        RunDisposeStep("debug file log", () => _debugFileLog?.Dispose());

        lock (_brokerRecoverySync)
        {
            var recoveredElsewhere = _overlayHub is IRecoverableGbfrOverlayHub recoverable &&
                                     recoverable.IsHostAvailable;
            if (_overlayHubControllerRegistered && !recoveredElsewhere)
            {
                RunDisposeStep(
                    "OverlayHub controller registration",
                    () => _modLoader.RemoveController<IGbfrOverlayHub>());
                _overlayHubControllerRegistered = false;
            }
        }
    }

    public Action Disposing => Dispose;

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
                _moduleLog(
                    $"Disposal of {component} failed; continuing teardown: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
            catch
            {
                // Reloaded teardown must continue even if its logger is no longer available.
            }
        }
    }

    private DebugFileLog CreateDebugFileLog()
    {
        string? logFilePath = null;
        try
        {
            logFilePath = Path.Combine(
                _modLoader.GetDirectoryForModId(_modConfig.ModId),
                DebugFileLog.FileName);
        }
        catch
        {
            // The sink reports once when it is enabled and the path is unavailable.
        }

        return new DebugFileLog(logFilePath, _modConfig.ModId, ReportDebugFileLogFailure);
    }

    private Action<string> CreateModuleLog() =>
        CreateLogFanout(
            message => _logger.WriteLine(message),
            _debugFileLog,
            _modConfig.ModId);

    internal static Action<string> CreateLogFanout(
        Action<string> reloadedSink,
        DebugFileLog? debugLog,
        string modId) =>
        message =>
        {
            var prefixedMessage = $"[{modId}] {message}";
            try
            {
                reloadedSink(prefixedMessage);
            }
            catch
            {
                // Reloaded logger failures must not prevent the debug file sink.
            }

            try
            {
                debugLog?.Write(message);
            }
            catch
            {
                // Debug file sink failures must not propagate to the game thread.
            }
        };

    private void ApplyDebugLogging(Config configuration) =>
        _debugFileLog?.ApplyEnabled(configuration.EnableDebugLogging);

    private void ReportDebugFileLogFailure(string failure)
    {
        try
        {
            _logger.WriteLine($"[{_modConfig.ModId}] {failure}");
        }
        catch
        {
            // Debug logging must not interrupt the game thread.
        }
    }

    public Type[] GetTypes() =>
    [
        typeof(IGbfrOverlayHub),
    ];
}
