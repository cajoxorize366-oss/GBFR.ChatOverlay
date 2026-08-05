namespace GBFR.ChatOverlay.Input;

/// <summary>
/// Testable policy for DirectInput's 256-byte keyboard state buffer.
/// </summary>
public sealed class DirectInputKeyboardStateFilter
{
    internal const int ActivationScanCode = 0x15; // DIK_Y
    internal const int VoicePushToTalkScanCode = 0x16; // DIK_U
    internal const int SettingsMenuScanCode = 0x44; // DIK_F10
    private bool _activationWasDown;
    private bool _voicePushToTalkWasDown;
    private bool _voicePushToTalkAccepted;
    private bool _voicePushToTalkConsumed;
    private bool _settingsMenuWasDown;

    public bool Process(
        Span<byte> keyboardState,
        Func<bool> tryActivate,
        Func<bool> shouldCapture,
        Func<bool>? isVoicePushToTalkEnabled = null,
        Action<bool>? reportVoicePushToTalk = null,
        Func<bool>? isSettingsMenuAvailable = null,
        Action<bool>? reportSettingsMenuKey = null)
    {
        ArgumentNullException.ThrowIfNull(tryActivate);
        ArgumentNullException.ThrowIfNull(shouldCapture);

        var activationIsDown = keyboardState.Length > ActivationScanCode &&
                               (keyboardState[ActivationScanCode] & 0x80) != 0;
        if (activationIsDown && !_activationWasDown)
            tryActivate();
        _activationWasDown = activationIsDown;

        var settingsMenuAvailable = isSettingsMenuAvailable?.Invoke() == true;
        var settingsMenuIsDown = keyboardState.Length > SettingsMenuScanCode &&
                                 (keyboardState[SettingsMenuScanCode] & 0x80) != 0;
        if (settingsMenuAvailable)
        {
            if (settingsMenuIsDown != _settingsMenuWasDown)
                reportSettingsMenuKey?.Invoke(settingsMenuIsDown);
            _settingsMenuWasDown = settingsMenuIsDown;
        }
        else
        {
            _settingsMenuWasDown = false;
        }

        var settingsKeyWasFiltered = false;
        if (settingsMenuAvailable && keyboardState.Length > SettingsMenuScanCode)
        {
            settingsKeyWasFiltered = keyboardState[SettingsMenuScanCode] != 0;
            keyboardState[SettingsMenuScanCode] = 0;
        }

        var capture = shouldCapture();
        var voicePushToTalkEnabled = isVoicePushToTalkEnabled?.Invoke() == true;
        var voicePushToTalkIsDown = keyboardState.Length > VoicePushToTalkScanCode &&
                                    (keyboardState[VoicePushToTalkScanCode] & 0x80) != 0;
        var voicePushToTalkAccepted = UpdateAcceptedKeyState(
            voicePushToTalkIsDown,
            voicePushToTalkEnabled && !capture,
            ref _voicePushToTalkWasDown,
            ref _voicePushToTalkAccepted,
            ref _voicePushToTalkConsumed);
        reportVoicePushToTalk?.Invoke(voicePushToTalkAccepted);

        var voiceKeyWasFiltered = false;
        if ((voicePushToTalkEnabled || _voicePushToTalkConsumed) &&
            keyboardState.Length > VoicePushToTalkScanCode)
        {
            voiceKeyWasFiltered = keyboardState[VoicePushToTalkScanCode] != 0;
            keyboardState[VoicePushToTalkScanCode] = 0;
        }

        if (!capture)
            return voiceKeyWasFiltered || settingsKeyWasFiltered;

        keyboardState.Clear();
        return true;
    }

    private static bool UpdateAcceptedKeyState(
        bool physicalDown,
        bool eligible,
        ref bool physicalWasDown,
        ref bool accepted,
        ref bool consumedUntilRelease)
    {
        if (!physicalDown)
        {
            accepted = false;
            consumedUntilRelease = false;
        }
        else if (!physicalWasDown)
        {
            accepted = eligible;
            consumedUntilRelease = eligible;
        }
        else if (!eligible)
        {
            // Once an active hold loses eligibility (focus capture, disabled channel, etc.), it
            // cannot reopen until the physical key is released and pressed again.
            accepted = false;
        }

        physicalWasDown = physicalDown;
        return accepted;
    }
}
