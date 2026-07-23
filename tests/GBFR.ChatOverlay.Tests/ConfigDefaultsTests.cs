using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Template.Configuration;

namespace GBFR.ChatOverlay.Tests;

public sealed class ConfigDefaultsTests
{
    [Fact]
    public void PartyLifecycleProbe_IsEnabledForValidationBuilds()
    {
        Assert.True(new Config().EnablePartyLifecycleProbe);
    }

    [Fact]
    public void MutedPartyChatControlCanary_IsEnabledForStage2ValidationBuilds()
    {
        Assert.True(new Config().EnableMutedPartyChatControlCanary);
    }

    [Fact]
    public void Configurator_UsesExplicitConfigurationDirectory()
    {
        var configured = Path.Combine(Path.GetTempPath(), "gbfr-explicit-config");

        var resolved = Configurator.ResolveConfigurationDirectory(
            configured,
            modConfigPath: null,
            launcherBaseDirectory: Path.GetTempPath());

        Assert.Equal(Path.GetFullPath(configured), resolved);
    }

    [Fact]
    public void Configurator_RecoversPortableUserDirectoryWhenReloadedOmitsConfigFolder()
    {
        var reloadedRoot = Path.Combine(Path.GetTempPath(), "Reloaded-II-Probe-Test");
        var modConfigPath = Path.Combine(
            reloadedRoot,
            "Mods",
            "arbitrary-install-folder",
            "ModConfig.json");

        var resolved = Configurator.ResolveConfigurationDirectory(
            configuredDirectory: null,
            modConfigPath,
            launcherBaseDirectory: Path.Combine(Path.GetTempPath(), "unused"));

        Assert.Equal(
            Path.Combine(reloadedRoot, "User", "Mods", "gbfr.qol.chatoverlay"),
            resolved);
    }

    [Fact]
    public void Configurator_FallsBackToLauncherDirectoryWithoutContext()
    {
        var reloadedRoot = Path.Combine(Path.GetTempPath(), "Reloaded-II-Fallback-Test");

        var resolved = Configurator.ResolveConfigurationDirectory(
            configuredDirectory: null,
            modConfigPath: null,
            launcherBaseDirectory: reloadedRoot);

        Assert.Equal(
            Path.Combine(reloadedRoot, "User", "Mods", "gbfr.qol.chatoverlay"),
            resolved);
    }
}
