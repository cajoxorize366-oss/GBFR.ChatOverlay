using System.ComponentModel;
using System.Text.Json.Serialization;

namespace GBFR.ChatOverlay.Configuration;

public enum QuickActionKind
{
    // Keep CustomText at zero so configurations created by the earlier text-only
    // implementation deserialize without a migration step.
    CustomText = 0,
    Stamp = 1,
    FixedPhrase = 2,
    Emotion = 3,
}

public sealed class QuickActionConfiguration
{
    [Browsable(false)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Browsable(false)]
    public bool Enabled { get; set; } = true;

    [Browsable(false)]
    public string Name { get; set; } = string.Empty;

    [Browsable(false)]
    public QuickActionKind Kind { get; set; } = QuickActionKind.CustomText;

    [Browsable(false)]
    public int OfficialId { get; set; } = -1;

    [Browsable(false)]
    public string Text { get; set; } = string.Empty;

    [Browsable(false)]
    public string KeyboardBinding { get; set; } = string.Empty;

    [Browsable(false)]
    public string ControllerBinding { get; set; } = string.Empty;

    [Browsable(false)]
    [JsonIgnore]
    public bool IsConfigured => Kind == QuickActionKind.CustomText
        ? !string.IsNullOrWhiteSpace(Text)
        : OfficialId >= 0 && CommunicationCatalog.TryGetEntry(Kind, OfficialId, out _);
}
