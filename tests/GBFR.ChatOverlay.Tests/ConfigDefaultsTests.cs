using System.IO;
using System.ComponentModel;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Template.Configuration;

namespace GBFR.ChatOverlay.Tests;

public sealed class ConfigDefaultsTests
{
    [Fact]
    public void InterfaceLanguage_DefaultsToSimplifiedChinese()
    {
        Assert.Equal(UiLanguage.SimplifiedChinese, new Config().InterfaceLanguage);
    }

    [Theory]
    [InlineData(nameof(Config.InterfaceLanguage), "语言 / Language")]
    [InlineData(nameof(Config.PushToTalkControllerBinding), "语音键（手柄） / PTT (Controller)")]
    [InlineData(nameof(Config.PushToTalkKeyboardBinding), "语音键（键盘） / PTT (Keyboard)")]
    [InlineData(nameof(Config.OpenChatControllerBinding), "聊天键（手柄） / Chat (Controller)")]
    [InlineData(nameof(Config.OpenChatKeyboardBinding), "聊天键（键盘） / Chat (Keyboard)")]
    [InlineData(nameof(Config.SettingsMenuControllerBinding), "菜单键（手柄） / Menu (Controller)")]
    [InlineData(nameof(Config.SettingsMenuKeyboardBinding), "菜单键（键盘） / Menu (Keyboard)")]
    [InlineData(nameof(Config.QuickActionsKeyboardBinding), "快捷菜单 / Quick Action Menu")]
    [InlineData(nameof(Config.BackgroundOpacity), "聊天背景透明度 / Chat Background Opacity")]
    [InlineData(nameof(Config.HistoryCapacity), "聊天记录上限 / Chat History Limit")]
    [InlineData(nameof(Config.EnableImeCandidateFallback), "输入法兼容 / IME Compatibility")]
    [InlineData(nameof(Config.EnableOverlay), "启用聊天界面 / Enable Chat Overlay")]
    [InlineData(nameof(Config.QuickActionsControllerBinding), "快捷菜单（手柄） / Quick Action (Controller)")]
    [InlineData(nameof(Config.GlobalMuteControllerBinding), "全局禁言（手柄） / Global Mute (Controller)")]
    [InlineData(nameof(Config.GlobalMuteKeyboardBinding), "全局禁言（键盘） / Global Mute (Keyboard)")]
    [InlineData(nameof(Config.VoicePlaybackDeviceId), "播放设备 / Playback Device")]
    [InlineData(nameof(Config.EnableVoiceIndicators), "语音状态指示 / Show Voice Indicator")]
    [InlineData(nameof(Config.EnableVoiceInput), "启用语音聊天 / Enable Voice Chat")]
    [InlineData(nameof(Config.VoiceMicrophoneDeviceId), "麦克风 / Microphone")]
    public void ReloadedConfigurator_UsesConciseUserFacingNames(
        string propertyName,
        string expectedDisplayName)
    {
        var property = TypeDescriptor.GetProperties(typeof(Config))[propertyName]!;

        Assert.Equal(expectedDisplayName, property.DisplayName);
    }

    [Fact]
    public void GeneralSettings_GroupLanguageChatLayoutAndHotkeys()
    {
        var properties = TypeDescriptor.GetProperties(typeof(Config));

        foreach (var propertyName in new[]
                 {
                     nameof(Config.InterfaceLanguage),
                     nameof(Config.EnableOverlay),
                     nameof(Config.EnableImeCandidateFallback),
                     nameof(Config.BackgroundOpacity),
                     nameof(Config.SettingsMenuKeyboardBinding),
                     nameof(Config.SettingsMenuControllerBinding),
                     nameof(Config.GlobalMuteKeyboardBinding),
                     nameof(Config.GlobalMuteControllerBinding),
                 })
        {
            Assert.Equal(
                "00 通用设置 / General",
                properties[propertyName]!.Attributes[typeof(CategoryAttribute)] is CategoryAttribute category
                    ? category.Category
                    : null);
        }
    }

    [Fact]
    public void ImeCandidateFallback_IsEnabledForThirdPartyInputMethods()
    {
        Assert.True(new Config().EnableImeCandidateFallback);
    }

    [Fact]
    public void PartyLifecycleDiagnostics_FollowBuildVisibility()
    {
#if DEBUG
        Assert.True(new Config().EnablePartyLifecycleProbe);
        Assert.True(new Config().EffectivePartyLifecycleDiagnostics);
#else
        Assert.False(new Config().EnablePartyLifecycleProbe);
        Assert.False(new Config().EffectivePartyLifecycleDiagnostics);
#endif
    }

    [Fact]
    public void ReloadedConfigurator_ShowsDiagnosticsOnlyInDebugBuilds()
    {
        var nativeBridge = TypeDescriptor.GetProperties(typeof(Config))[
            nameof(Config.EnableNativeChatBridge)]!;
        var lifecycle = TypeDescriptor.GetProperties(typeof(Config))[
            nameof(Config.EnablePartyLifecycleProbe)]!;

#if DEBUG
        Assert.True(nativeBridge.IsBrowsable);
        Assert.True(lifecycle.IsBrowsable);
#else
        Assert.False(nativeBridge.IsBrowsable);
        Assert.False(lifecycle.IsBrowsable);
#endif
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
    public void VoiceIndicatorPositionPreview_IsDisabledByDefault()
    {
        var configuration = new Config();

        Assert.True(configuration.EnableVoiceIndicators);
        Assert.False(configuration.ShowAllVoiceIndicatorSlots);
        Assert.False(configuration.EffectiveShowAllVoiceIndicatorSlots);
    }

    [Fact]
    public void UserHotkeys_UseSafeKeyboardDefaultsAndNoControllerDefaults()
    {
        var configuration = new Config();

        Assert.Equal("F10", configuration.SettingsMenuKeyboardBinding);
        Assert.Equal("Y", configuration.OpenChatKeyboardBinding);
        Assert.Equal("U", configuration.PushToTalkKeyboardBinding);
        Assert.Equal(string.Empty, configuration.QuickActionsKeyboardBinding);
        Assert.Equal(string.Empty, configuration.SettingsMenuControllerBinding);
        Assert.Equal(string.Empty, configuration.OpenChatControllerBinding);
        Assert.Equal(string.Empty, configuration.PushToTalkControllerBinding);
        Assert.Equal(string.Empty, configuration.QuickActionsControllerBinding);
        Assert.Equal(string.Empty, configuration.GlobalMuteKeyboardBinding);
        Assert.Equal(string.Empty, configuration.GlobalMuteControllerBinding);
        Assert.Empty(configuration.QuickActions);
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
