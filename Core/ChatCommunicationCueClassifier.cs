using System.Globalization;

namespace GBFR.ChatOverlay.Core;

internal static class ChatCommunicationCueClassifier
{
    private const string MachineCuePrefix = "vo_CMM_";

    internal static bool TryClassifySenderLabel(
        string? senderLabel,
        out ChatCommunicationCue communicationCue)
    {
        communicationCue = ChatCommunicationCue.None;
        if (string.IsNullOrEmpty(senderLabel))
            return false;

        var normalized = TrimProtocolPadding(senderLabel.AsSpan());
        if (!normalized.StartsWith(MachineCuePrefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (normalized.Equals("vo_CMM_chance".AsSpan(), StringComparison.OrdinalIgnoreCase))
            communicationCue = ChatCommunicationCue.LinkAttack;
        else if (normalized.Equals("vo_CMM_thanks".AsSpan(), StringComparison.OrdinalIgnoreCase))
            communicationCue = ChatCommunicationCue.Thanks;
        else if (normalized.StartsWith("vo_CMM_win_".AsSpan(), StringComparison.OrdinalIgnoreCase))
            communicationCue = ChatCommunicationCue.Victory;

        return true;
    }

    private static ReadOnlySpan<char> TrimProtocolPadding(ReadOnlySpan<char> value)
    {
        var start = 0;
        while (start < value.Length && IsProtocolPadding(value[start]))
            start++;

        var end = value.Length;
        while (end > start && IsProtocolPadding(value[end - 1]))
            end--;

        return value[start..end];
    }

    private static bool IsProtocolPadding(char value) =>
        char.IsWhiteSpace(value) ||
        char.IsControl(value) ||
        CharUnicodeInfo.GetUnicodeCategory(value) == UnicodeCategory.Format;
}
