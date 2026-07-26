using System.IO;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Template.Configuration;

namespace GBFR.ChatOverlay.Tests;

public sealed class ConfigDefaultsTests
{
    [Fact]
    public void ImeCandidateFallback_IsEnabledForThirdPartyInputMethods()
    {
        Assert.True(new Config().EnableImeCandidateFallback);
    }

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
    public void ExperimentalPartyVoiceTest_IsEnabledForPreviewPackage()
    {
        Assert.True(new Config().EnableVoiceInput);
    }

    [Fact]
    public void VoiceIndicatorPositionPreview_ShowsAllNativeHudSlotsByDefault()
    {
        var configuration = new Config();

        Assert.True(configuration.EnableVoiceIndicators);
        Assert.True(configuration.ShowAllVoiceIndicatorSlots);
    }

    [Fact]
    public void MicrophoneSelfMonitor_UsesConservativeDefaultVolume()
    {
        var configuration = new Config();

        Assert.Equal(0.35, configuration.MicrophoneSelfMonitorVolume);
        Assert.Equal(1.0, configuration.MicrophoneSelfTestInputGain);
        Assert.Equal(-1.0, configuration.OverlayPositionXRatio);
        Assert.Equal(-1.0, configuration.OverlayPositionYRatio);
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
