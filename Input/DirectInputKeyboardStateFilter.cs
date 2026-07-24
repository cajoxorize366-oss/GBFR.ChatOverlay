namespace GBFR.ChatOverlay.Input;

/// <summary>
/// Testable policy for DirectInput's 256-byte keyboard state buffer.
/// </summary>
public sealed class DirectInputKeyboardStateFilter
{
    internal const int ActivationScanCode = 0x15; // DIK_Y
    internal const int VoicePushToTalkScanCode = 0x16; // DIK_U
    private bool _activationWasDown;
    private bool _drainPressedKeys;

    public bool Process(
        Span<byte> keyboardState,
        Func<bool> tryActivate,
        Func<bool> shouldCapture,
        Func<bool>? isVoicePushToTalkEnabled = null,
        Action<bool>? reportVoicePushToTalk = null)
    {
        ArgumentNullException.ThrowIfNull(tryActivate);
        ArgumentNullException.ThrowIfNull(shouldCapture);

        var activationIsDown = keyboardState.Length > ActivationScanCode &&
                               (keyboardState[ActivationScanCode] & 0x80) != 0;
        if (activationIsDown && !_activationWasDown)
            tryActivate();
        _activationWasDown = activationIsDown;

        var capture = shouldCapture();
        var voicePushToTalkEnabled = isVoicePushToTalkEnabled?.Invoke() == true;
        var voicePushToTalkIsDown = keyboardState.Length > VoicePushToTalkScanCode &&
                                    (keyboardState[VoicePushToTalkScanCode] & 0x80) != 0;
        reportVoicePushToTalk?.Invoke(
            voicePushToTalkEnabled && !capture && voicePushToTalkIsDown);

        var voiceKeyWasFiltered = false;
        if (voicePushToTalkEnabled && keyboardState.Length > VoicePushToTalkScanCode)
        {
            voiceKeyWasFiltered = keyboardState[VoicePushToTalkScanCode] != 0;
            keyboardState[VoicePushToTalkScanCode] = 0;
        }

        if (capture)
            _drainPressedKeys = true;

        if (!capture && !_drainPressedKeys)
            return voiceKeyWasFiltered;

        var anyKeyIsDown = keyboardState.ContainsAnyExcept((byte)0);
        keyboardState.Clear();
        if (!capture && !anyKeyIsDown)
            _drainPressedKeys = false;
        return true;
    }
}
