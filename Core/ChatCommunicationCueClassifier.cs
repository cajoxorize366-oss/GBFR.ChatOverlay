using System.Globalization;

namespace GBFR.ChatOverlay.Core;

internal static class ChatCommunicationCueClassifier
{
    private const string MachineCuePrefix = "vo_CMM_";
    private const string RelinkCharacterVoiceSeparator = "_VO_CMM_";

    internal static bool TryClassifySenderLabel(
        string? senderLabel,
        out ChatCommunicationCue communicationCue)
    {
        communicationCue = ChatCommunicationCue.None;
        if (string.IsNullOrEmpty(senderLabel))
            return false;

        var normalized = TrimProtocolPadding(senderLabel.AsSpan());
        var action = ExtractMachineAction(normalized);
        if (action.IsEmpty)
            return false;

        communicationCue = ChatCommunicationCue.Official;
        if (StartsWithAction(action, "chance"))
            communicationCue = ChatCommunicationCue.LinkAttack;
        else if (StartsWithAction(action, "thanks"))
            communicationCue = ChatCommunicationCue.Thanks;
        else if (StartsWithAction(action, "win"))
            communicationCue = ChatCommunicationCue.Victory;

        return true;
    }

    private static ReadOnlySpan<char> ExtractMachineAction(ReadOnlySpan<char> value)
    {
        if (value.StartsWith(MachineCuePrefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return value.Slice(MachineCuePrefix.Length);

        if (!value.StartsWith("PL".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return default;

        var separatorIndex = 2;
        var digitStart = separatorIndex;
        while (separatorIndex < value.Length &&
               value[separatorIndex] is >= '0' and <= '9')
        {
            separatorIndex++;
        }

        if (separatorIndex == digitStart ||
            !value.Slice(separatorIndex).StartsWith(
                RelinkCharacterVoiceSeparator.AsSpan(),
                StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        return value.Slice(separatorIndex + RelinkCharacterVoiceSeparator.Length);
    }

    private static bool StartsWithAction(ReadOnlySpan<char> action, string prefix)
    {
        var prefixSpan = prefix.AsSpan();
        return action.StartsWith(prefixSpan, StringComparison.OrdinalIgnoreCase) &&
               (action.Length == prefixSpan.Length || action[prefixSpan.Length] == '_');
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
