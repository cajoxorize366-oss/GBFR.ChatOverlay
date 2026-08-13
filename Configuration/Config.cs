using System.ComponentModel;
using System.Text.Json.Serialization;
using GBFR.ChatOverlay.Audio;
using GBFR.ChatOverlay.Runtime.Configuration;
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
    [DisplayName("聊天背景透明度 / Chat Background Opacity")]
    [Description("调整聊天界面的背景透明度。 / Adjust the chat background opacity.")]
    [DefaultValue(0.55)]
    [SliderControlParams(minimum: 0.0, maximum: 1.0)]
    public double BackgroundOpacity { get; set; } = 0.55;

    [Category("00 通用设置 / General")]
    [DisplayName("字体大小 / Font Size")]
    [Description("聊天消息的字体大小。 / Chat message font size.")]
    [DefaultValue(18.0)]
    [SliderControlParams(minimum: 12.0, maximum: 30.0)]
    public double ChatFontSize { get; set; } = 18.0;

    [Category("00 通用设置 / General")]
    [DisplayName("显示时间戳 / Show Timestamps")]
    [Description("在消息前显示小时和分钟。 / Show hours and minutes before messages.")]
    [DefaultValue(false)]
    public bool ShowTimestamps { get; set; }

    [Category("00 通用设置 / General")]
    [DisplayName("聊天记录上限 / Chat History Limit")]
    [Description("最多保留的聊天消息数量。 / Maximum number of chat messages kept.")]
    [DefaultValue(200)]
    [SliderControlParams(minimum: 10.0, maximum: 5000.0)]
    public int HistoryCapacity { get; set; } = 200;

    [Category("00 通用设置 / General")]
    [DisplayName("玩家名字大小 / Player Name Size")]
    [Description("玩家名字的字体大小。 / Player name font size.")]
    [DefaultValue(18.0)]
    [SliderControlParams(minimum: 12.0, maximum: 30.0)]
    public double PlayerNameFontSize { get; set; } = 18.0;

    [Category("00 通用设置 / General")]
    [DisplayName("玩家名字粗细 / Player Name Weight")]
    [Description("玩家名字的加粗程度。 / Player name weight.")]
    [DefaultValue(2)]
    [SliderControlParams(minimum: 1.0, maximum: 3.0)]
    public int PlayerNameWeight { get; set; } = 2;

    [Category("00 通用设置 / General")]
    [DisplayName("玩家 1 颜色 / Player 1 Color")]
    [Description("玩家 1 的名字颜色（#RRGGBB）。 / Player 1 name color (#RRGGBB).")]
    [DefaultValue("#5ED9FF")]
    public string Player1NameColor { get; set; } = "#5ED9FF";

    [Category("00 通用设置 / General")]
    [DisplayName("玩家 2 颜色 / Player 2 Color")]
    [Description("玩家 2 的名字颜色（#RRGGBB）。 / Player 2 name color (#RRGGBB).")]
    [DefaultValue("#FFAD5E")]
    public string Player2NameColor { get; set; } = "#FFAD5E";

    [Category("00 通用设置 / General")]
    [DisplayName("玩家 3 颜色 / Player 3 Color")]
    [Description("玩家 3 的名字颜色（#RRGGBB）。 / Player 3 name color (#RRGGBB).")]
    [DefaultValue("#71DF8A")]
    public string Player3NameColor { get; set; } = "#71DF8A";

    [Category("00 通用设置 / General")]
    [DisplayName("玩家 4 颜色 / Player 4 Color")]
    [Description("玩家 4 的名字颜色（#RRGGBB）。 / Player 4 name color (#RRGGBB).")]
    [DefaultValue("#C69CFF")]
    public string Player4NameColor { get; set; } = "#C69CFF";

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
    [DisplayName("精简模式 / Compact Mode")]
    [Description("平时隐藏聊天框，按聊天键时显示语音状态和输入框。 / Hide the chat window until the chat key is pressed, then show voice status and the input box.")]
    [DefaultValue(false)]
    public bool CompactMode { get; set; }

    [Category("00 通用设置 / General")]
    [DisplayName("全局聊天禁言（手柄） / Block All Chat (Controller)")]
    [Description("切换所有玩家的聊天黑名单。 / Toggle the chat blacklist for all players.")]
    [DefaultValue("")]
    public string GlobalMuteControllerBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("全局聊天禁言（键盘） / Block All Chat (Keyboard)")]
    [Description("切换所有玩家的聊天黑名单。 / Toggle the chat blacklist for all players.")]
    [DefaultValue("")]
    public string GlobalMuteKeyboardBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("玩家 1 聊天禁言（手柄） / Player 1 Chat Mute (Controller)")]
    [Description("切换远端玩家 1 的聊天黑名单。 / Toggle remote Player 1 in the chat blacklist.")]
    [DefaultValue("")]
    public string RemotePlayer1ChatMuteControllerBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("玩家 1 聊天禁言（键盘） / Player 1 Chat Mute (Keyboard)")]
    [Description("切换远端玩家 1 的聊天黑名单。 / Toggle remote Player 1 in the chat blacklist.")]
    [DefaultValue("")]
    public string RemotePlayer1ChatMuteKeyboardBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("玩家 2 聊天禁言（手柄） / Player 2 Chat Mute (Controller)")]
    [Description("切换远端玩家 2 的聊天黑名单。 / Toggle remote Player 2 in the chat blacklist.")]
    [DefaultValue("")]
    public string RemotePlayer2ChatMuteControllerBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("玩家 2 聊天禁言（键盘） / Player 2 Chat Mute (Keyboard)")]
    [Description("切换远端玩家 2 的聊天黑名单。 / Toggle remote Player 2 in the chat blacklist.")]
    [DefaultValue("")]
    public string RemotePlayer2ChatMuteKeyboardBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("玩家 3 聊天禁言（手柄） / Player 3 Chat Mute (Controller)")]
    [Description("切换远端玩家 3 的聊天黑名单。 / Toggle remote Player 3 in the chat blacklist.")]
    [DefaultValue("")]
    public string RemotePlayer3ChatMuteControllerBinding { get; set; } = string.Empty;

    [Category("00 通用设置 / General")]
    [DisplayName("玩家 3 聊天禁言（键盘） / Player 3 Chat Mute (Keyboard)")]
    [Description("切换远端玩家 3 的聊天黑名单。 / Toggle remote Player 3 in the chat blacklist.")]
    [DefaultValue("")]
    public string RemotePlayer3ChatMuteKeyboardBinding { get; set; } = string.Empty;

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
    public ChatFilterConfiguration ChatFilter { get; set; } = new();

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
