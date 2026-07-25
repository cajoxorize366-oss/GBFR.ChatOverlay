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

    [DisplayName("Experimental Voice (U Party / I Local Test)")]
    [Description("PREVIEW: hold U to unmute PlayFab Party's native selected microphone path for another Mod client. Hold I for local-only Windows microphone monitoring. Release, focus loss, input timeout and session exit force both paths off. Restart required.")]
    [DefaultValue(true)]
    public bool EnableVoiceInput { get; set; } = true;

    [DisplayName("Voice Microphone")]
    [Description("Windows recording endpoint selected for Party voice while U is held and captured locally while I is held. Defaults to Default (Windows system default). Restart required.")]
    [Editor(
        "GBFR.ChatOverlay.ConfiguratorUI.VoiceMicrophonePropertyEditor, GBFR.ChatOverlay.ConfiguratorUI",
        "HandyControl.Controls.PropertyEditorBase, HandyControl")]
    [TypeConverter(typeof(VoiceMicrophoneDeviceIdConverter))]
    [DefaultValue(AudioEndpointSelectionValues.SystemDefault)]
    public string VoiceMicrophoneDeviceId { get; set; } = AudioEndpointSelectionValues.SystemDefault;

    [DisplayName("Voice Playback Device")]
    [Description("Playback endpoint used for Party voice. Defaults to Default (Windows system default); active Windows speakers and headsets are listed as manual choices. Restart required.")]
    [Editor(
        "GBFR.ChatOverlay.ConfiguratorUI.VoicePlaybackPropertyEditor, GBFR.ChatOverlay.ConfiguratorUI",
        "HandyControl.Controls.PropertyEditorBase, HandyControl")]
    [TypeConverter(typeof(VoicePlaybackDeviceIdConverter))]
    [DefaultValue(AudioEndpointSelectionValues.SystemDefault)]
    public string VoicePlaybackDeviceId { get; set; } = AudioEndpointSelectionValues.SystemDefault;

    [DisplayName("Microphone Self-Monitor Volume")]
    [Description("Local playback volume used only while holding I. Start with headphones to avoid acoustic feedback. Capped at 50%. Restart required.")]
    [DefaultValue(0.35)]
    [SliderControlParams(minimum: 0.0, maximum: 0.5)]
    public double MicrophoneSelfMonitorVolume { get; set; } = 0.35;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
    // 
}
