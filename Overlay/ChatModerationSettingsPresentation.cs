using System.Text;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Overlay;

internal static class ChatModerationSettingsPresentation
{
    internal const int IdentityDisplayLength = 12;

    internal static void EnsureMutableCollections(ChatFilterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Rules ??= [];
        configuration.BlockedPlayers ??= [];
    }

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

        var formatted = ExpandTemplate(
            selectedTemplate,
            player,
            Math.Max(0, count).ToString(),
            Math.Max(0, threshold).ToString());
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
        var utf8ByteCount = 0;

        foreach (var rune in source.EnumerateRunes())
        {
            if (rune.Value == 0)
                continue;

            if (rune.Value == '\r')
            {
                if (utf8ByteCount >= normalizedLength)
                    break;
                builder.Append(' ');
                previousWasCarriageReturn = true;
                utf8ByteCount++;
                continue;
            }

            if (rune.Value == '\n')
            {
                if (!previousWasCarriageReturn)
                {
                    if (utf8ByteCount >= normalizedLength)
                        break;
                    builder.Append(' ');
                    utf8ByteCount++;
                }
                previousWasCarriageReturn = false;
                continue;
            }

            if (utf8ByteCount + rune.Utf8SequenceLength > normalizedLength)
                break;
            builder.Append(rune.ToString());
            previousWasCarriageReturn = false;
            utf8ByteCount += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }

    private static string ExpandTemplate(
        string template,
        string player,
        string count,
        string threshold)
    {
        var builder = new StringBuilder(template.Length + player.Length);
        for (var index = 0; index < template.Length;)
        {
            if (template.AsSpan(index).StartsWith("{player}"))
            {
                builder.Append(player);
                index += "{player}".Length;
            }
            else if (template.AsSpan(index).StartsWith("{count}"))
            {
                builder.Append(count);
                index += "{count}".Length;
            }
            else if (template.AsSpan(index).StartsWith("{threshold}"))
            {
                builder.Append(threshold);
                index += "{threshold}".Length;
            }
            else
            {
                builder.Append(template[index]);
                index++;
            }
        }

        return builder.ToString();
    }
}
