using Reloaded.Mod.Interfaces;
using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Template.Configuration;

public class Configurator : IConfiguratorV3
{
    private const string ModId = "gbfr.qol.chatoverlay";
    private static ConfiguratorMixin _configuratorMixin = new ConfiguratorMixin();

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
        var configFolder = ResolveConfigurationDirectory(
            ConfigFolder,
            Context.ModConfigPath,
            AppContext.BaseDirectory);
        Directory.CreateDirectory(configFolder);
        _configurations = _configuratorMixin.MakeConfigurations(configFolder);

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

    public Configurator() { }
    public Configurator(string configDirectory) : this()
    {
        ConfigFolder = configDirectory;
    }

    /* Configurator V2 */

    /// <summary>
    /// Migrates from the old config location to the newer config location.
    /// </summary>
    /// <param name="oldDirectory">Old directory containing the mod configs.</param>
    /// <param name="newDirectory">New directory pointing to user config folder.</param>
    public void Migrate(string oldDirectory, string newDirectory) => _configuratorMixin.Migrate(oldDirectory, newDirectory);

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

    /// <summary>
    /// Allows for custom launcher/configurator implementation.
    /// If you have your own configuration program/code, run that code here and return true, else return false.
    /// </summary>
    public bool TryRunCustomConfiguration() => _configuratorMixin.TryRunCustomConfiguration(this);

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
