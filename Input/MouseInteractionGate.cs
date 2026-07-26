namespace GBFR.ChatOverlay.Input;

internal sealed class MouseInteractionGate
{
    private const int RequiredReleasedFrames = 2;
    private bool _open;
    private int _releasedFrames;

    internal bool IsArmed { get; private set; }

    internal void Open()
    {
        _open = true;
        _releasedFrames = 0;
        IsArmed = false;
    }

    internal void Close()
    {
        _open = false;
        _releasedFrames = 0;
        IsArmed = false;
    }

    internal void Observe(bool anyButtonPressed)
    {
        if (!_open || IsArmed)
            return;
        if (anyButtonPressed)
        {
            _releasedFrames = 0;
            return;
        }

        if (++_releasedFrames >= RequiredReleasedFrames)
            IsArmed = true;
    }
}
