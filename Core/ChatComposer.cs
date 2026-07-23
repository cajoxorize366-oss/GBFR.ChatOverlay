using System.Text;

namespace GBFR.ChatOverlay.Core;

public enum ChatInputMode
{
    Closed,
    Keyboard,
    VoiceRecording,
    VoiceReview,
}

/// <summary>
/// UI-independent state machine for keyboard and future hold-to-talk input.
/// </summary>
public sealed class ChatComposer
{
    public ChatComposer(int maximumDraftLength = 512)
    {
        if (maximumDraftLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDraftLength));

        MaximumDraftLength = maximumDraftLength;
    }

    public int MaximumDraftLength { get; }
    public ChatInputMode Mode { get; private set; }
    public string Draft { get; private set; } = string.Empty;
    public bool IsOpen => Mode is not ChatInputMode.Closed;

    public bool OpenKeyboard()
    {
        if (Mode is ChatInputMode.VoiceRecording)
            return false;

        Mode = ChatInputMode.Keyboard;
        return true;
    }

    public bool BeginVoiceCapture()
    {
        if (Mode is ChatInputMode.Keyboard or ChatInputMode.VoiceRecording)
            return false;

        Mode = ChatInputMode.VoiceRecording;
        return true;
    }

    public bool CompleteVoiceCapture(string? transcript)
    {
        if (Mode is not ChatInputMode.VoiceRecording)
            return false;

        SetDraft(transcript);
        Mode = ChatInputMode.VoiceReview;
        return !string.IsNullOrWhiteSpace(Draft);
    }

    public void SetDraft(string? value)
    {
        Draft = NormalizeSingleLine(value ?? string.Empty, MaximumDraftLength);
    }

    public bool TryGetSubmittableText(out string text)
    {
        text = Draft.Trim();
        return text.Length > 0;
    }

    public void MarkSubmitted()
    {
        Draft = string.Empty;
        Mode = ChatInputMode.Closed;
    }

    public void Cancel(bool clearDraft = false)
    {
        Mode = ChatInputMode.Closed;
        if (clearDraft)
            Draft = string.Empty;
    }

    private static string NormalizeSingleLine(string value, int maximumLength)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        var previousWasCarriageReturn = false;
        var runeCount = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            if (runeCount >= maximumLength)
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
