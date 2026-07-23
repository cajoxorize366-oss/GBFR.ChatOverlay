namespace GBFR.ChatOverlay.Input;

/// <summary>
/// Testable policy for DirectInput's 256-byte keyboard state buffer.
/// </summary>
public sealed class DirectInputKeyboardStateFilter
{
    internal const int ActivationScanCode = 0x15; // DIK_Y
    private bool _activationWasDown;
    private bool _drainPressedKeys;

    public bool Process(
        Span<byte> keyboardState,
        Func<bool> tryActivate,
        Func<bool> shouldCapture)
    {
        ArgumentNullException.ThrowIfNull(tryActivate);
        ArgumentNullException.ThrowIfNull(shouldCapture);

        var activationIsDown = keyboardState.Length > ActivationScanCode &&
                               (keyboardState[ActivationScanCode] & 0x80) != 0;
        if (activationIsDown && !_activationWasDown)
            tryActivate();
        _activationWasDown = activationIsDown;

        var capture = shouldCapture();
        if (capture)
            _drainPressedKeys = true;

        if (!capture && !_drainPressedKeys)
            return false;

        var anyKeyIsDown = keyboardState.ContainsAnyExcept((byte)0);
        keyboardState.Clear();
        if (!capture && !anyKeyIsDown)
            _drainPressedKeys = false;
        return true;
    }
}
