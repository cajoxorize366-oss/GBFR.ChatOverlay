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
        tickPlacement:SliderControlTickPlacement.BottomRight,
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

    [DisplayName("Voice Input")]
    [Description("Reserved for the hold-to-talk speech recognition milestone.")]
    [DefaultValue(false)]
    public bool EnableVoiceInput { get; set; }
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
    // 
}
