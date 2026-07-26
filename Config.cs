using System.ComponentModel;
using GBFR.ChatOverlay.Audio;
using GBFR.ChatOverlay.Template.Configuration;
using Reloaded.Mod.Interfaces.Structs;

namespace GBFR.ChatOverlay.Configuration;

public class Config : Configurable<Config>
{
    [DisplayName("Enable Overlay")]
    [Description("Show the chat overlay only while an authenticated Relink online Party room is active.")]
    [DefaultValue(true)]
    public bool EnableOverlay { get; set; } = true;

    [DisplayName("Overlay IME Candidate Fallback")]
    [Description("Draw the current IMM32 candidate list inside the chat box when a third-party IME's external candidate window is invisible. Number-key selection remains owned by the IME.")]
    [DefaultValue(true)]
    public bool EnableImeCandidateFallback { get; set; } = true;

    [DisplayName("Enable Native Chat Bridge")]
    [Description("Connect directly to supported Relink chat functions. Changing this setting requires restarting the mod.")]
    [DefaultValue(true)]
    public bool EnableNativeChatBridge { get; set; } = true;

    [DisplayName("Log Party Lifecycle Diagnostics")]
    [Description("Log observed PlayFab Party lifecycle events. The observation-only online-room gate remains active regardless; it sends no voice or network data. Restart required.")]
    [DefaultValue(true)]
    public bool EnablePartyLifecycleProbe { get; set; } = true;

    [DisplayName("Enable Muted Party ChatControl Canary")]
    [Description("Required Stage 2 lifecycle foundation: create one initially muted ChatControl on the existing Party session. Both clients need the mod. Changing this setting requires restarting the mod.")]
    [DefaultValue(true)]
    public bool EnableMutedPartyChatControlCanary { get; set; } = true;

    [DisplayName("History Capacity")]
    [Description("Maximum number of messages kept in memory. Applied after restarting the mod.")]
    [DefaultValue(200)]
    public int HistoryCapacity { get; set; } = 200;

    [DisplayName("Overlay Width")]
    [Description("Chat window width in pixels.")]
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

    [DisplayName("Overlay Height")]
    [Description("Chat window height in pixels.")]
    [DefaultValue(260)]
    [SliderControlParams(minimum: 160.0, maximum: 800.0)]
    public int OverlayHeight { get; set; } = 260;

    [DisplayName("Background Opacity")]
    [Description("Background opacity while the input box is open.")]
    [DefaultValue(0.55)]
    [SliderControlParams(minimum: 0.0, maximum: 1.0)]
    public double BackgroundOpacity { get; set; } = 0.55;

    [DisplayName("Experimental Voice (U Party / F10 Settings)")]
    [Description("PREVIEW: hold U to unmute PlayFab Party's native selected microphone path for another Mod client. Use the F10 settings menu for the local microphone self-test. Release, focus loss, input timeout and session exit force both paths off. Restart required for Party device changes.")]
    [DefaultValue(true)]
    public bool EnableVoiceInput { get; set; } = true;

    [DisplayName("Enable Party Voice Indicators")]
    [Description("Draw microphone indicators from Relink's live party-HUD node transforms. Normal mode remains online-room gated; the explicit Show All position test also works with a CPU party.")]
    [DefaultValue(true)]
    public bool EnableVoiceIndicators { get; set; } = true;

    [DisplayName("Voice Indicator Debug: Show All Slots")]
    [Description("POSITION TEST: show every live lobby or battle HUD row, including a CPU party, without other Mod clients. Disable after both native HUD placements are confirmed.")]
    [DefaultValue(true)]
    public bool ShowAllVoiceIndicatorSlots { get; set; } = true;

    [DisplayName("Voice Microphone")]
    [Description("Windows recording endpoint selected for Party voice while U is held and for the F10 local self-test. Defaults to Default (Windows system default). Party voice changes require a restart; local self-test changes apply immediately.")]
    [Editor(
        "GBFR.ChatOverlay.ConfiguratorUI.VoiceMicrophonePropertyEditor, GBFR.ChatOverlay.ConfiguratorUI",
        "HandyControl.Controls.PropertyEditorBase, HandyControl")]
    [TypeConverter(typeof(VoiceMicrophoneDeviceIdConverter))]
    [DefaultValue(AudioEndpointSelectionValues.SystemDefault)]
    public string VoiceMicrophoneDeviceId { get; set; } = AudioEndpointSelectionValues.SystemDefault;

    [DisplayName("Voice Playback Device")]
    [Description("Playback endpoint used for Party voice and the F10 local self-test. Defaults to Default (Windows system default). Party voice changes require a restart; local self-test changes apply immediately.")]
    [Editor(
        "GBFR.ChatOverlay.ConfiguratorUI.VoicePlaybackPropertyEditor, GBFR.ChatOverlay.ConfiguratorUI",
        "HandyControl.Controls.PropertyEditorBase, HandyControl")]
    [TypeConverter(typeof(VoicePlaybackDeviceIdConverter))]
    [DefaultValue(AudioEndpointSelectionValues.SystemDefault)]
    public string VoicePlaybackDeviceId { get; set; } = AudioEndpointSelectionValues.SystemDefault;

    [DisplayName("Microphone Self-Test Playback Volume")]
    [Description("Local playback volume used only by the F10 microphone self-test. Start with headphones to avoid acoustic feedback. Capped at 50% and applied immediately.")]
    [DefaultValue(0.35)]
    [SliderControlParams(minimum: 0.0, maximum: 0.5)]
    public double MicrophoneSelfMonitorVolume { get; set; } = 0.35;

    [DisplayName("Microphone Self-Test Input Gain")]
    [Description("Software input gain used only by the F10 microphone self-test and its level meter. Party transmission is not modified.")]
    [DefaultValue(1.0)]
    [SliderControlParams(minimum: 0.0, maximum: 2.0)]
    public double MicrophoneSelfTestInputGain { get; set; } = 1.0;

    [Browsable(false)]
    [DefaultValue(-1.0)]
    public double OverlayPositionXRatio { get; set; } = -1.0;

    [Browsable(false)]
    [DefaultValue(-1.0)]
    public double OverlayPositionYRatio { get; set; } = -1.0;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
    // 
}
