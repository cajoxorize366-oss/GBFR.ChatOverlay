namespace GBFR.ChatOverlay.Input;

/// <summary>
/// Testable policy for DirectInput's 256-byte keyboard state buffer.
/// </summary>
public sealed class DirectInputKeyboardStateFilter
{
    internal const int ActivationScanCode = 0x15; // DIK_Y
    internal const int VoicePushToTalkScanCode = 0x16; // DIK_U
    internal const int LocalMicrophoneMonitorScanCode = 0x17; // DIK_I
    private bool _activationWasDown;
    private bool _drainPressedKeys;
    private bool _voicePushToTalkWasDown;
    private bool _voicePushToTalkAccepted;
    private bool _voicePushToTalkConsumed;
    private bool _localMonitorWasDown;
    private bool _localMonitorAccepted;
    private bool _localMonitorConsumed;

    public bool Process(
        Span<byte> keyboardState,
        Func<bool> tryActivate,
        Func<bool> shouldCapture,
        Func<bool>? isVoicePushToTalkEnabled = null,
        Action<bool>? reportVoicePushToTalk = null,
        Func<bool>? isLocalMicrophoneMonitorEnabled = null,
        Action<bool>? reportLocalMicrophoneMonitor = null)
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

        var localMonitorEnabled = isLocalMicrophoneMonitorEnabled?.Invoke() == true;
        var localMonitorIsDown = keyboardState.Length > LocalMicrophoneMonitorScanCode &&
                                 (keyboardState[LocalMicrophoneMonitorScanCode] & 0x80) != 0;
        var localMonitorAccepted = UpdateAcceptedKeyState(
            localMonitorIsDown,
            localMonitorEnabled && !capture,
            ref _localMonitorWasDown,
            ref _localMonitorAccepted,
            ref _localMonitorConsumed);
        reportLocalMicrophoneMonitor?.Invoke(localMonitorAccepted);

        var monitorKeyWasFiltered = false;
        if ((localMonitorEnabled || _localMonitorConsumed) &&
            keyboardState.Length > LocalMicrophoneMonitorScanCode)
        {
            monitorKeyWasFiltered = keyboardState[LocalMicrophoneMonitorScanCode] != 0;
            keyboardState[LocalMicrophoneMonitorScanCode] = 0;
        }

        if (capture)
            _drainPressedKeys = true;

        if (!capture && !_drainPressedKeys)
            return voiceKeyWasFiltered || monitorKeyWasFiltered;

        var anyKeyIsDown = keyboardState.ContainsAnyExcept((byte)0);
        keyboardState.Clear();
        if (!capture && !anyKeyIsDown)
            _drainPressedKeys = false;
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
