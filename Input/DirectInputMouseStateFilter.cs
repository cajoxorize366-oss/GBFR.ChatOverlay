namespace GBFR.ChatOverlay.Input;

internal sealed class DirectInputMouseStateFilter
{
    internal bool Process(Span<byte> mouseState, bool capture)
    {
        if (!capture)
            return false;

        mouseState.Clear();
        return true;
    }
}
