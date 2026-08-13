using System.IO;
using System.ComponentModel;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Runtime.Configuration;

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
    [InlineData(nameof(Config.BackgroundOpacity), "聊天背景透明度 / Chat Background Opacity")]
    [InlineData(nameof(Config.ChatFontSize), "字体大小 / Font Size")]
    [InlineData(nameof(Config.ShowTimestamps), "显示时间戳 / Show Timestamps")]
    [InlineData(nameof(Config.HistoryCapacity), "聊天记录上限 / Chat History Limit")]
    [InlineData(nameof(Config.PlayerNameFontSize), "玩家名字大小 / Player Name Size")]
    [InlineData(nameof(Config.PlayerNameWeight), "玩家名字粗细 / Player Name Weight")]
    [InlineData(nameof(Config.Player1NameColor), "玩家 1 颜色 / Player 1 Color")]
    [InlineData(nameof(Config.Player2NameColor), "玩家 2 颜色 / Player 2 Color")]
    [InlineData(nameof(Config.Player3NameColor), "玩家 3 颜色 / Player 3 Color")]
    [InlineData(nameof(Config.Player4NameColor), "玩家 4 颜色 / Player 4 Color")]
    [InlineData(nameof(Config.EnableImeCandidateFallback), "输入法兼容 / IME Compatibility")]
    [InlineData(nameof(Config.EnableOverlay), "启用聊天界面 / Enable Chat Overlay")]
    [InlineData(nameof(Config.CompactMode), "精简模式 / Compact Mode")]
    [InlineData(nameof(Config.GlobalMuteControllerBinding), "全局聊天禁言（手柄） / Block All Chat (Controller)")]
    [InlineData(nameof(Config.GlobalMuteKeyboardBinding), "全局聊天禁言（键盘） / Block All Chat (Keyboard)")]
    [InlineData(nameof(Config.RemotePlayer1ChatMuteControllerBinding), "玩家 1 聊天禁言（手柄） / Player 1 Chat Mute (Controller)")]
    [InlineData(nameof(Config.RemotePlayer1ChatMuteKeyboardBinding), "玩家 1 聊天禁言（键盘） / Player 1 Chat Mute (Keyboard)")]
    [InlineData(nameof(Config.RemotePlayer2ChatMuteControllerBinding), "玩家 2 聊天禁言（手柄） / Player 2 Chat Mute (Controller)")]
    [InlineData(nameof(Config.RemotePlayer2ChatMuteKeyboardBinding), "玩家 2 聊天禁言（键盘） / Player 2 Chat Mute (Keyboard)")]
    [InlineData(nameof(Config.RemotePlayer3ChatMuteControllerBinding), "玩家 3 聊天禁言（手柄） / Player 3 Chat Mute (Controller)")]
    [InlineData(nameof(Config.RemotePlayer3ChatMuteKeyboardBinding), "玩家 3 聊天禁言（键盘） / Player 3 Chat Mute (Keyboard)")]
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
                     nameof(Config.CompactMode),
                     nameof(Config.EnableImeCandidateFallback),
                     nameof(Config.BackgroundOpacity),
                     nameof(Config.ChatFontSize),
                     nameof(Config.ShowTimestamps),
                     nameof(Config.HistoryCapacity),
                     nameof(Config.PlayerNameFontSize),
                     nameof(Config.PlayerNameWeight),
                     nameof(Config.Player1NameColor),
                     nameof(Config.SettingsMenuKeyboardBinding),
                     nameof(Config.SettingsMenuControllerBinding),
                     nameof(Config.GlobalMuteKeyboardBinding),
                     nameof(Config.GlobalMuteControllerBinding),
                     nameof(Config.RemotePlayer1ChatMuteKeyboardBinding),
                     nameof(Config.RemotePlayer1ChatMuteControllerBinding),
                     nameof(Config.RemotePlayer2ChatMuteKeyboardBinding),
                     nameof(Config.RemotePlayer2ChatMuteControllerBinding),
                     nameof(Config.RemotePlayer3ChatMuteKeyboardBinding),
                     nameof(Config.RemotePlayer3ChatMuteControllerBinding),
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
    public void CompactMode_IsDisabledByDefault()
    {
        Assert.False(new Config().CompactMode);
    }

    [Fact]
    public void CompactMode_DescriptionMatchesVoiceRowAndInputPresentation()
    {
        var property = TypeDescriptor.GetProperties(typeof(Config))[nameof(Config.CompactMode)]!;

        Assert.Contains("语音状态和输入框", property.Description, StringComparison.Ordinal);
        Assert.Contains("voice status and the input box", property.Description, StringComparison.Ordinal);
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
    public void PartyVoice_IsEnabledByDefault()
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
        Assert.Equal(string.Empty, configuration.SettingsMenuControllerBinding);
        Assert.Equal(string.Empty, configuration.OpenChatControllerBinding);
        Assert.Equal(string.Empty, configuration.PushToTalkControllerBinding);
        Assert.Equal(string.Empty, configuration.GlobalMuteKeyboardBinding);
        Assert.Equal(string.Empty, configuration.GlobalMuteControllerBinding);
        Assert.Equal(string.Empty, configuration.RemotePlayer1ChatMuteKeyboardBinding);
        Assert.Equal(string.Empty, configuration.RemotePlayer1ChatMuteControllerBinding);
        Assert.Equal(string.Empty, configuration.RemotePlayer2ChatMuteKeyboardBinding);
        Assert.Equal(string.Empty, configuration.RemotePlayer2ChatMuteControllerBinding);
        Assert.Equal(string.Empty, configuration.RemotePlayer3ChatMuteKeyboardBinding);
        Assert.Equal(string.Empty, configuration.RemotePlayer3ChatMuteControllerBinding);
        Assert.Empty(configuration.QuickActions);
    }

    [Fact]
    public void ChatFilter_UsesOptInFilteringAndConservativeAutoBlockDefaults()
    {
        var filter = new Config().ChatFilter;

        Assert.False(filter.Enabled);
        Assert.True(filter.UseSteamTextFilter);
        Assert.Equal(ChatFilterAction.MaskMatchedWords, filter.Action);
        Assert.False(filter.AutoBlockEnabled);
        Assert.Equal(3, filter.AutoBlockThreshold);
        Assert.Equal(10, filter.AutoBlockWindowMinutes);
        Assert.Equal(ChatFilterNotificationMode.LocalOnly, filter.NotificationMode);
        Assert.Equal(ChatFilterConfiguration.DefaultNotificationTemplate, filter.NotificationTemplate);
        Assert.Empty(filter.Rules);
        Assert.Empty(filter.BlockedPlayers);
    }

    [Fact]
    public void ChatPresentation_UsesReadableDefaultsAndHidesTimestamps()
    {
        var configuration = new Config();

        Assert.Equal(18.0, configuration.ChatFontSize);
        Assert.False(configuration.ShowTimestamps);
        Assert.Equal(18.0, configuration.PlayerNameFontSize);
        Assert.Equal(2, configuration.PlayerNameWeight);
        Assert.Equal("#5ED9FF", configuration.Player1NameColor);
        Assert.Equal("#FFAD5E", configuration.Player2NameColor);
        Assert.Equal("#71DF8A", configuration.Player3NameColor);
        Assert.Equal("#C69CFF", configuration.Player4NameColor);
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
