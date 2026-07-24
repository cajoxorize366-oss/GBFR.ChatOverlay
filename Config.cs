using System.ComponentModel;
using GBFR.ChatOverlay.Template.Configuration;
using Reloaded.Mod.Interfaces.Structs;

namespace GBFR.ChatOverlay.Configuration;

public class Config : Configurable<Config>
{
    [DisplayName("Enable Overlay")]
    [Description("Show the chat overlay when the rendering bridge is available.")]
    [DefaultValue(true)]
    public bool EnableOverlay { get; set; } = true;

    [DisplayName("Enable Native Chat Bridge")]
    [Description("Connect directly to supported Relink chat functions. Changing this setting requires restarting the mod.")]
    [DefaultValue(true)]
    public bool EnableNativeChatBridge { get; set; } = true;

    [DisplayName("Enable Party Lifecycle Probe")]
    [Description("Attach an observation-only PlayFab Party lifecycle probe. No voice or network data is sent. Changing this setting requires restarting the mod.")]
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

    [DisplayName("Experimental Party Voice Test (Hold U)")]
    [Description("PREVIEW: grant microphone-only send/receive permissions to remote Mod ChatControls on the same PartyNetwork. Hold U to talk; release, focus loss, input timeout and session exit force mute. Both clients need this package. Restart required.")]
    [DefaultValue(true)]
    public bool EnableVoiceInput { get; set; } = true;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
    // 
}
