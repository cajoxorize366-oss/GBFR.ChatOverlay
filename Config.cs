using System.ComponentModel;
using System.Text.Json.Serialization;
using GBFR.ChatOverlay.Audio;
using GBFR.ChatOverlay.Template.Configuration;
using Reloaded.Mod.Interfaces.Structs;

namespace GBFR.ChatOverlay.Configuration;

public class Config : Configurable<Config>
{
#if DEBUG
    private const bool ShowDiagnosticsInConfigurator = true;
    private const bool DiagnosticsDefault = true;
#else
    private const bool ShowDiagnosticsInConfigurator = false;
    private const bool DiagnosticsDefault = false;
#endif

    [Category("00 通用设置 / General")]
    [DisplayName("语言 / Language")]
    [Description("选择游戏内界面语言。 / Select the in-game interface language.")]
    [DefaultValue(UiLanguage.SimplifiedChinese)]
    public UiLanguage InterfaceLanguage { get; set; } = UiLanguage.SimplifiedChinese;

    [Category("00 通用设置 / General")]
    [DisplayName("语音键（手柄） / PTT (Controller)")]
    [Description("按住说话的手柄按键。 / Controller button used for push-to-talk.")]
    [DefaultValue("")]
    public string PushToTalkControllerBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("语音键（键盘） / PTT (Keyboard)")]
    [Description("按住说话的键盘按键。 / Keyboard key used for push-to-talk.")]
    [DefaultValue("U")]
    public string PushToTalkKeyboardBinding { get; set; } = "U";

    [Category("00 通用设置 / General")]
    [DisplayName("聊天键（手柄） / Chat (Controller)")]
    [Description("打开聊天的手柄按键。 / Controller button used to open chat.")]
    [DefaultValue("")]
    public string OpenChatControllerBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("聊天键（键盘） / Chat (Keyboard)")]
    [Description("打开聊天的键盘按键。 / Keyboard key used to open chat.")]
    [DefaultValue("Y")]
    public string OpenChatKeyboardBinding { get; set; } = "Y";

    [Category("00 通用设置 / General")]
    [DisplayName("菜单键（手柄） / Menu (Controller)")]
    [Description("打开设置菜单的手柄按键。 / Controller button used to open Settings.")]
    [DefaultValue("")]
    public string SettingsMenuControllerBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("菜单键（键盘） / Menu (Keyboard)")]
    [Description("打开设置菜单的键盘按键。 / Keyboard key used to open Settings.")]
    [DefaultValue("F10")]
    public string SettingsMenuKeyboardBinding { get; set; } = "F10";

    [Category("00 通用设置 / General")]
    [DisplayName("快捷菜单 / Quick Action Menu")]
    [Description("打开快捷菜单的键盘按键。 / Keyboard key used to open the quick action menu.")]
    [DefaultValue("")]
    public string QuickActionsKeyboardBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("聊天背景透明度 / Chat Background Opacity")]
    [Description("调整聊天界面的背景透明度。 / Adjust the chat background opacity.")]
    [DefaultValue(0.55)]
    [SliderControlParams(minimum: 0.0, maximum: 1.0)]
    public double BackgroundOpacity { get; set; } = 0.55;

    [Category("00 通用设置 / General")]
    [DisplayName("聊天记录上限 / Chat History Limit")]
    [Description("最多保留的聊天消息数量。 / Maximum number of chat messages kept.")]
    [DefaultValue(200)]
    public int HistoryCapacity { get; set; } = 200;

    [Category("00 通用设置 / General")]
    [DisplayName("输入法兼容 / IME Compatibility")]
    [Description("兼容部分输入法候选框。 / Improves compatibility with some IME candidate windows.")]
    [DefaultValue(true)]
    public bool EnableImeCandidateFallback { get; set; } = true;

    [Category("00 通用设置 / General")]
    [DisplayName("启用聊天界面 / Enable Chat Overlay")]
    [Description("显示游戏内聊天界面。 / Show the in-game chat overlay.")]
    [DefaultValue(true)]
    public bool EnableOverlay { get; set; } = true;

    [Category("00 通用设置 / General")]
    [DisplayName("快捷菜单（手柄） / Quick Action (Controller)")]
    [Description("打开快捷菜单的手柄按键。 / Controller button used to open the quick action menu.")]
    [DefaultValue("")]
    public string QuickActionsControllerBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("全局禁言（手柄） / Global Mute (Controller)")]
    [Description("切换所有玩家的禁言状态。 / Toggle mute for all players.")]
    [DefaultValue("")]
    public string GlobalMuteControllerBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("全局禁言（键盘） / Global Mute (Keyboard)")]
    [Description("切换所有玩家的禁言状态。 / Toggle mute for all players.")]
    [DefaultValue("")]
    public string GlobalMuteKeyboardBinding { get; set; } = string.Empty;

    [Category("03 语音 / Voice")]
    [DisplayName("播放设备 / Playback Device")]
    [Description("语音聊天的播放设备。 / Playback device for voice chat.")]
    [Editor(
        "GBFR.ChatOverlay.ConfiguratorUI.VoicePlaybackPropertyEditor, GBFR.ChatOverlay.ConfiguratorUI",
        "HandyControl.Controls.PropertyEditorBase, HandyControl")]
    [TypeConverter(typeof(VoicePlaybackDeviceIdConverter))]
    [DefaultValue(AudioEndpointSelectionValues.SystemDefault)]
    public string VoicePlaybackDeviceId { get; set; } = AudioEndpointSelectionValues.SystemDefault;

    [Category("03 语音 / Voice")]
    [DisplayName("语音状态指示 / Show Voice Indicator")]
    [Description("显示队伍语音状态。 / Show party voice status.")]
    [DefaultValue(true)]
    public bool EnableVoiceIndicators { get; set; } = true;

    [Category("03 语音 / Voice")]
    [DisplayName("启用语音聊天 / Enable Voice Chat")]
    [Description("开启队伍语音聊天。 / Enable party voice chat.")]
    [DefaultValue(true)]
    public bool EnableVoiceInput { get; set; } = true;

    [Category("03 语音 / Voice")]
    [DisplayName("麦克风 / Microphone")]
    [Description("语音聊天使用的麦克风。 / Microphone used for voice chat.")]
    [Editor(
        "GBFR.ChatOverlay.ConfiguratorUI.VoiceMicrophonePropertyEditor, GBFR.ChatOverlay.ConfiguratorUI",
        "HandyControl.Controls.PropertyEditorBase, HandyControl")]
    [TypeConverter(typeof(VoiceMicrophoneDeviceIdConverter))]
    [DefaultValue(AudioEndpointSelectionValues.SystemDefault)]
    public string VoiceMicrophoneDeviceId { get; set; } = AudioEndpointSelectionValues.SystemDefault;

    [Category("99 调试 / Debug")]
    [DisplayName("原生聊天桥 / Native Chat Bridge")]
    [Description("启用原生聊天桥。 / Enable the native chat bridge.")]
    [Browsable(ShowDiagnosticsInConfigurator)]
    [DefaultValue(true)]
    public bool EnableNativeChatBridge { get; set; } = true;

    [Category("99 调试 / Debug")]
    [DisplayName("记录 Party 生命周期 / Log Party Lifecycle Diagnostics")]
    [Description("记录 Party 生命周期事件。 / Log Party lifecycle events.")]
    [Browsable(ShowDiagnosticsInConfigurator)]
    [DefaultValue(DiagnosticsDefault)]
    public bool EnablePartyLifecycleProbe { get; set; } = DiagnosticsDefault;

    [Browsable(false)]
    [DefaultValue(true)]
    public bool EnableMutedPartyChatControlCanary { get; set; } = true;

    [Category("99 调试 / Debug")]
    [DisplayName("显示全部语音槽位 / Show All Voice Indicator Slots")]
    [Description("显示全部语音状态槽位。 / Show every voice indicator slot.")]
    [Browsable(ShowDiagnosticsInConfigurator)]
    [DefaultValue(false)]
    public bool ShowAllVoiceIndicatorSlots { get; set; }

    [Browsable(false)]
    [DefaultValue(560)]
    [SliderControlParams(
        minimum: 320.0,
        maximum: 1200.0,
        smallChange: 10.0,
        largeChange: 50.0,
        tickFrequency: 100,
        isSnapToTickEnabled: false,
        tickPlacement: SliderControlTickPlacement.BottomRight,
        showTextField: true,
        isTextFieldEditable: true,
        textValidationRegex: "\\d{3,4}")]
    public int OverlayWidth { get; set; } = 560;

    [Browsable(false)]
    [DefaultValue(260)]
    [SliderControlParams(minimum: 160.0, maximum: 800.0)]
    public int OverlayHeight { get; set; } = 260;

    [Browsable(false)]
    [DefaultValue(0.35)]
    [SliderControlParams(minimum: 0.0, maximum: 0.5)]
    public double MicrophoneSelfMonitorVolume { get; set; } = 0.35;

    [Browsable(false)]
    [DefaultValue(1.0)]
    [SliderControlParams(minimum: 0.0, maximum: 2.0)]
    public double MicrophoneSelfTestInputGain { get; set; } = 1.0;

    [Browsable(false)]
    public List<QuickActionConfiguration> QuickActions { get; set; } = [];

    [Browsable(false)]
    [DefaultValue(-1.0)]
    public double OverlayPositionXRatio { get; set; } = -1.0;

    [Browsable(false)]
    [DefaultValue(-1.0)]
    public double OverlayPositionYRatio { get; set; } = -1.0;

    [JsonIgnore]
    [Browsable(false)]
    public bool EffectivePartyLifecycleDiagnostics =>
        ShowDiagnosticsInConfigurator && EnablePartyLifecycleProbe;

    [JsonIgnore]
    [Browsable(false)]
    public bool EffectiveShowAllVoiceIndicatorSlots =>
        ShowDiagnosticsInConfigurator && ShowAllVoiceIndicatorSlots;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
    // 
}
