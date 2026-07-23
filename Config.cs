using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using GBFR.ChatOverlay.Template.Configuration;
using Reloaded.Mod.Interfaces.Structs;

namespace GBFR.ChatOverlay.Configuration;

public enum VoiceLanguageOption
{
    [Display(Name = "中文 (zh)")]
    Chinese,

    [Display(Name = "日语 (ja)")]
    Japanese,

    [Display(Name = "英语 (en)")]
    English,

    [Display(Name = "韩语 (ko)")]
    Korean,

    [Display(Name = "自动检测 (auto)")]
    Automatic,
}

public static class VoiceLanguageOptionExtensions
{
    public static string ToWhisperCode(this VoiceLanguageOption language) => language switch
    {
        VoiceLanguageOption.Chinese => "zh",
        VoiceLanguageOption.Japanese => "ja",
        VoiceLanguageOption.English => "en",
        VoiceLanguageOption.Korean => "ko",
        VoiceLanguageOption.Automatic => "auto",
        _ => "zh",
    };

    public static VoiceLanguageOption FromWhisperCode(string? language) =>
        language?.Trim().ToLowerInvariant() switch
        {
            "ja" => VoiceLanguageOption.Japanese,
            "en" => VoiceLanguageOption.English,
            "ko" => VoiceLanguageOption.Korean,
            "auto" => VoiceLanguageOption.Automatic,
            _ => VoiceLanguageOption.Chinese,
        };
}

public class Config : Configurable<Config>
{
    private const int CurrentVoiceLanguageDefaultVersion = 1;

    [DisplayName("Enable Overlay")]
    [Description("Show the chat overlay when the rendering bridge is available.")]
    [DefaultValue(true)]
    public bool EnableOverlay { get; set; } = true;

    [DisplayName("Enable Native Chat Bridge")]
    [Description("Connect directly to supported Relink chat functions. Changing this setting requires restarting the mod.")]
    [DefaultValue(true)]
    public bool EnableNativeChatBridge { get; set; } = true;

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
    [Description("Enable local hold-to-talk using U or the controller LB + R3 chord. Restart the mod after changing this setting.")]
    [DefaultValue(true)]
    public bool EnableVoiceInput { get; set; } = true;

    [DisplayName("Voice Language")]
    [Description("Recognition language. Chinese is the default; restart the mod after changing this setting.")]
    [DefaultValue(VoiceLanguageOption.Chinese)]
    [JsonIgnore]
    public VoiceLanguageOption VoiceLanguage
    {
        get => VoiceLanguageOptionExtensions.FromWhisperCode(VoiceLanguageCode);
        set => VoiceLanguageCode = value.ToWhisperCode();
    }

    [Browsable(false)]
    [JsonPropertyName("VoiceLanguage")]
    public string VoiceLanguageCode { get; set; } = "zh";

    [Browsable(false)]
    public int VoiceLanguageDefaultVersion { get; set; }

    [DisplayName("Voice CPU Threads")]
    [Description("CPU threads used by the isolated Whisper worker. Restart the mod after changing this setting.")]
    [DefaultValue(4)]
    [SliderControlParams(minimum: 1.0, maximum: 16.0)]
    public int VoiceCpuThreads { get; set; } = 4;

    [DisplayName("Maximum Voice Seconds")]
    [Description("Maximum duration of one hold-to-talk recording.")]
    [DefaultValue(15)]
    [SliderControlParams(minimum: 3.0, maximum: 30.0)]
    public int VoiceMaximumSeconds { get; set; } = 15;

    public bool ApplyVoiceLanguageDefaultMigration()
    {
        if (VoiceLanguageDefaultVersion >= CurrentVoiceLanguageDefaultVersion)
            return false;

        if (string.Equals(VoiceLanguageCode?.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            VoiceLanguageCode = VoiceLanguageOption.Chinese.ToWhisperCode();

        VoiceLanguageDefaultVersion = CurrentVoiceLanguageDefaultVersion;
        return true;
    }
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
    // 
}
