namespace GBFR.ChatOverlay.Input;

internal sealed class DirectInputMouseStateFilter
{
    private const int ButtonOffset = 12;
    private bool _drainPressedButtons;

    internal bool IsSuppressing => _drainPressedButtons;

    internal bool Process(Span<byte> mouseState, bool capture)
    {
        if (capture)
            _drainPressedButtons = true;
        if (!capture && !_drainPressedButtons)
            return false;

        var buttonsDown = mouseState.Length > ButtonOffset &&
                          mouseState[ButtonOffset..].ContainsAnyExcept((byte)0);
        mouseState.Clear();
        if (!capture && !buttonsDown)
            _drainPressedButtons = false;
        return true;
    }
}
