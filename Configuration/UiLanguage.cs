using System.ComponentModel;

namespace GBFR.ChatOverlay.Configuration;

public enum UiLanguage
{
    [Description("简体中文")]
    SimplifiedChinese = 0,

    [Description("English")]
    English = 1,
}

public static class UiLocalization
{
    public static string Select(UiLanguage language, string chinese, string english) =>
        language == UiLanguage.English ? english : chinese;

    public static string LanguageName(UiLanguage language) => language switch
    {
        UiLanguage.English => "English",
        _ => "简体中文",
    };

    public static string FromLegacyBilingual(UiLanguage language, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        const string separator = " / ";
        var separatorIndex = value.IndexOf(separator, StringComparison.Ordinal);
        if (separatorIndex < 0)
            return value;
        return language == UiLanguage.English
            ? value[(separatorIndex + separator.Length)..]
            : value[..separatorIndex];
    }
}
