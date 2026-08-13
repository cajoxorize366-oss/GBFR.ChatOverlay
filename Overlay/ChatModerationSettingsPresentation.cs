using System.Text;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Overlay;

internal static class ChatModerationSettingsPresentation
{
    internal const int IdentityDisplayLength = 12;

    internal static string FormatNotification(
        string? template,
        string? playerName,
        int playerNumber,
        int count,
        int threshold,
        int maximumLength,
        UiLanguage language = UiLanguage.SimplifiedChinese)
    {
        var normalizedLength = Math.Max(1, maximumLength);
        var player = string.IsNullOrWhiteSpace(playerName)
            ? playerNumber > 0
                ? UiLocalization.Select(language, $"玩家 {playerNumber}", $"Player {playerNumber}")
                : UiLocalization.Select(language, "玩家", "Player")
            : playerName.Trim();
        var selectedTemplate = SanitizeSingleLine(template, int.MaxValue);
        if (string.IsNullOrWhiteSpace(selectedTemplate))
            selectedTemplate = ChatFilterConfiguration.DefaultNotificationTemplate;

        var formatted = selectedTemplate
            .Replace("{player}", player, StringComparison.Ordinal)
            .Replace("{count}", Math.Max(0, count).ToString(), StringComparison.Ordinal)
            .Replace("{threshold}", Math.Max(0, threshold).ToString(), StringComparison.Ordinal);
        return SanitizeSingleLine(formatted, normalizedLength).Trim();
    }

    internal static string FormatThresholdReason(int count, int threshold, UiLanguage language)
    {
        var safeCount = Math.Max(0, count);
        var safeThreshold = Math.Max(0, threshold);
        return UiLocalization.Select(
            language,
            $"过滤命中 {safeCount}/{safeThreshold}",
            $"Filter threshold {safeCount}/{safeThreshold}");
    }

    internal static string ShortenIdentity(string? identity)
    {
        var normalized = identity?.Trim() ?? string.Empty;
        if (normalized.Length <= IdentityDisplayLength)
            return normalized;
        return "…" + normalized[^8..];
    }

    internal static string ParticipantLabel(ChatModerationParticipant participant, UiLanguage language)
    {
        if (!string.IsNullOrWhiteSpace(participant.DisplayName))
            return participant.DisplayName.Trim();
        return participant.PlayerNumber > 0
            ? UiLocalization.Select(
                language,
                $"玩家 {participant.PlayerNumber}",
                $"Player {participant.PlayerNumber}")
            : UiLocalization.Select(language, "未知玩家", "Unknown player");
    }

    internal static string SanitizeSingleLine(string? value, int maximumLength)
    {
        var normalizedLength = Math.Max(1, maximumLength);
        var source = value ?? string.Empty;
        var builder = new StringBuilder(Math.Min(source.Length, normalizedLength));
        var previousWasCarriageReturn = false;
        var runeCount = 0;

        foreach (var rune in source.EnumerateRunes())
        {
            if (rune.Value == 0)
                continue;
            if (runeCount >= normalizedLength)
                break;

            if (rune.Value == '\r')
            {
                builder.Append(' ');
                previousWasCarriageReturn = true;
                runeCount++;
                continue;
            }

            if (rune.Value == '\n')
            {
                if (!previousWasCarriageReturn)
                {
                    builder.Append(' ');
                    runeCount++;
                }
                previousWasCarriageReturn = false;
                continue;
            }

            builder.Append(rune.ToString());
            previousWasCarriageReturn = false;
            runeCount++;
        }

        return builder.ToString();
    }
}
