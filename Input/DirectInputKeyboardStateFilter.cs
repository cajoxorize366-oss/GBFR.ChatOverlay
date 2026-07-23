namespace GBFR.ChatOverlay.Input;

/// <summary>
/// Testable policy for DirectInput's 256-byte keyboard state buffer.
/// </summary>
public sealed class DirectInputKeyboardStateFilter
{
    internal const int ActivationScanCode = 0x15; // DIK_Y
    internal const int VoiceScanCode = 0x16; // DIK_U
    private bool _activationWasDown;
    private bool _voiceWasDown;
    private bool _voiceCaptureAccepted;
    private bool _drainPressedKeys;

    public bool Process(
        Span<byte> keyboardState,
        Func<bool> tryActivate,
        Func<bool> shouldCapture)
    {
        return Process(
            keyboardState,
            tryActivate,
            shouldCapture,
            () => false,
            () => { },
            () => false);
    }

    public bool Process(
        Span<byte> keyboardState,
        Func<bool> tryActivate,
        Func<bool> shouldCapture,
        Func<bool> tryBeginVoiceCapture,
        Action endVoiceCapture,
        Func<bool> isVoiceInputEnabled)
    {
        ArgumentNullException.ThrowIfNull(tryActivate);
        ArgumentNullException.ThrowIfNull(shouldCapture);
        ArgumentNullException.ThrowIfNull(tryBeginVoiceCapture);
        ArgumentNullException.ThrowIfNull(endVoiceCapture);
        ArgumentNullException.ThrowIfNull(isVoiceInputEnabled);

        var activationIsDown = keyboardState.Length > ActivationScanCode &&
                               (keyboardState[ActivationScanCode] & 0x80) != 0;
        if (activationIsDown && !_activationWasDown)
            tryActivate();
        _activationWasDown = activationIsDown;

        var capture = shouldCapture();
        if (capture)
            _drainPressedKeys = true;

        var voiceIsDown = keyboardState.Length > VoiceScanCode &&
                          (keyboardState[VoiceScanCode] & 0x80) != 0;
        var voiceEnabled = isVoiceInputEnabled();
        if (voiceEnabled && !capture && voiceIsDown && !_voiceWasDown)
            _voiceCaptureAccepted = tryBeginVoiceCapture();

        if ((!voiceIsDown || !voiceEnabled) && _voiceCaptureAccepted)
        {
            endVoiceCapture();
            _voiceCaptureAccepted = false;
        }

        var suppressVoiceKey = _voiceCaptureAccepted && voiceIsDown;
        _voiceWasDown = voiceIsDown;

        if (!capture && !_drainPressedKeys && !suppressVoiceKey)
            return false;

        if (!capture && !_drainPressedKeys)
        {
            keyboardState[VoiceScanCode] = 0;
            return true;
        }

        var anyKeyIsDown = keyboardState.ContainsAnyExcept((byte)0);
        keyboardState.Clear();
        if (!capture && !anyKeyIsDown)
            _drainPressedKeys = false;
        return true;
    }
}
