using Reloaded.Mod.Interfaces;
using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Runtime.Configuration;

public sealed class Configurator : IConfiguratorV3
{
    private const string ModId = "gbfr.qol.chatoverlay";
    private const string ConfiguratorUiAssemblyName = "GBFR.ChatOverlay.ConfiguratorUI";
    private const string ConfiguratorUiFileName = ConfiguratorUiAssemblyName + ".dll";

    /// <summary>
    /// The folder where the modification files are stored.
    /// </summary>
    public string? ModFolder { get; private set; }

    /// <summary>
    /// Full path to the config folder.
    /// </summary>
    public string? ConfigFolder { get; private set; }

    /// <summary>
    /// Specifies additional information for the configurator.
    /// </summary>
    public ConfiguratorContext Context { get; private set; }

    /// <summary>
    /// Returns a list of configurations.
    /// </summary> 
    public IUpdatableConfigurable[] Configurations => _configurations ?? MakeConfigurations();
    private IUpdatableConfigurable[]? _configurations;

    private IUpdatableConfigurable[] MakeConfigurations()
    {
        LoadConfiguratorUiForLauncher();
        var configFolder = ResolveConfigurationDirectory(
            ConfigFolder,
            Context.ModConfigPath,
            AppContext.BaseDirectory);
        Directory.CreateDirectory(configFolder);
        _configurations =
        [
            Configurable<Config>.FromFile(Path.Combine(configFolder, "Config.json"), "Default Config"),
        ];

        // Add self-updating to configurations.
        for (int x = 0; x < Configurations.Length; x++)
        {
            var xCopy = x;
            Configurations[x].ConfigurationUpdated += configurable =>
            {
                Configurations[xCopy] = configurable;
            };
        }

        return _configurations;
    }

    private void LoadConfiguratorUiForLauncher()
    {
        // Startup constructs this Configurator with only a config directory inside the game.
        // Reloaded-II's launcher supplies the Mod folder/context; only that process may load WPF.
        var launcherModFolder = !string.IsNullOrWhiteSpace(ModFolder)
            ? ModFolder
            : string.IsNullOrWhiteSpace(Context.ModConfigPath)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(Context.ModConfigPath));
        if (string.IsNullOrWhiteSpace(launcherModFolder))
            return;

        if (System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies.Any(assembly =>
                string.Equals(
                    assembly.GetName().Name,
                    ConfiguratorUiAssemblyName,
                    StringComparison.Ordinal)))
        {
            return;
        }

        var editorPath = Path.GetFullPath(Path.Combine(launcherModFolder, ConfiguratorUiFileName));
        if (!File.Exists(editorPath))
        {
            throw new FileNotFoundException(
                $"The GBFR audio-device configuration UI is missing from the Mod package: {editorPath}",
                editorPath);
        }

        try
        {
            _ = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(editorPath);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Failed to load the GBFR audio-device configuration UI. Keep both GBFR.ChatOverlay DLLs " +
                "from the same ZIP beside each other and use Reloaded-II 1.30.2 or a compatible build.",
                exception);
        }
    }

    public Configurator() { }
    public Configurator(string configDirectory) : this()
    {
        ConfigFolder = configDirectory;
    }

    public void Migrate(string oldDirectory, string newDirectory)
    {
        // No legacy configuration location is supported by this release.
    }

    /* Configurator */

    /// <summary>
    /// Gets an individual user configuration.
    /// </summary>
    public TType GetConfiguration<TType>(int index) => (TType)Configurations[index];

    /* IConfigurator. */

    /// <summary>
    /// Sets the config directory for the Configurator.
    /// </summary>
    public void SetConfigDirectory(string configDirectory)
    {
        ConfigFolder = configDirectory;
        _configurations = null;
    }

    /// <summary>
    /// Specifies additional context for the configurator.
    /// </summary>
    public void SetContext(in ConfiguratorContext context)
    {
        Context = context;
        if (string.IsNullOrWhiteSpace(ConfigFolder))
            _configurations = null;
    }

    /// <summary>
    /// Returns a list of user configurations.
    /// </summary>
    public IConfigurable[] GetConfigurations() => Configurations;

    public bool TryRunCustomConfiguration() => false;

    /// <summary>
    /// Sets the mod directory for the Configurator.
    /// </summary>
    public void SetModDirectory(string modDirectory) { ModFolder = modDirectory; }

    public static string ResolveConfigurationDirectory(
        string? configuredDirectory,
        string? modConfigPath,
        string launcherBaseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
            return Path.GetFullPath(configuredDirectory);

        if (!string.IsNullOrWhiteSpace(modConfigPath))
        {
            var modDirectory = Path.GetDirectoryName(Path.GetFullPath(modConfigPath));
            var modsDirectory = modDirectory is null ? null : Directory.GetParent(modDirectory);
            if (modsDirectory?.Parent is not null &&
                string.Equals(modsDirectory.Name, "Mods", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(modsDirectory.Parent.FullName, "User", "Mods", ModId);
            }
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(launcherBaseDirectory);
        return Path.Combine(Path.GetFullPath(launcherBaseDirectory), "User", "Mods", ModId);
    }
}
