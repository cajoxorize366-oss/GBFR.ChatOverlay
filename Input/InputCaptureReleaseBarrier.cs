namespace GBFR.ChatOverlay.Input;

internal readonly record struct InputCaptureTransition(
    InputCaptureDevices Previous,
    InputCaptureDevices Current);

internal sealed class InputCaptureReleaseBarrier
{
    private const int RequiredNeutralFrames = 2;
    private readonly object _sync = new();
    private int _requested;
    private int _effective;
    private int _neutralFrames;

    internal InputCaptureDevices Requested =>
        (InputCaptureDevices)Volatile.Read(ref _requested);

    internal InputCaptureDevices Effective =>
        (InputCaptureDevices)Volatile.Read(ref _effective);

    internal InputCaptureTransition SetRequested(InputCaptureDevices requested)
    {
        requested = Normalize(requested);
        lock (_sync)
        {
            var previous = (InputCaptureDevices)_effective;
            if ((InputCaptureDevices)_requested == requested)
                return new InputCaptureTransition(previous, previous);

            var effective = requested;
            if ((requested & InputCaptureDevices.Mouse) == 0)
                effective |= previous & InputCaptureDevices.Mouse;
            if ((requested & (InputCaptureDevices.Keyboard | InputCaptureDevices.Text)) == 0)
            {
                effective |= previous &
                             (InputCaptureDevices.Keyboard | InputCaptureDevices.Text);
            }

            Volatile.Write(ref _requested, (int)requested);
            Volatile.Write(ref _effective, (int)effective);
            _neutralFrames = 0;
            return new InputCaptureTransition(previous, effective);
        }
    }

    internal InputCaptureTransition Tick(bool keyboardNeutral, bool mouseNeutral)
    {
        lock (_sync)
        {
            var requested = (InputCaptureDevices)_requested;
            var previous = (InputCaptureDevices)_effective;
            var effective = previous | requested;
            var pendingRelease = effective & ~requested;
            if (pendingRelease == InputCaptureDevices.None)
            {
                _neutralFrames = 0;
                Volatile.Write(ref _effective, (int)effective);
                return new InputCaptureTransition(previous, effective);
            }

            var keyboardPending =
                (pendingRelease & (InputCaptureDevices.Keyboard | InputCaptureDevices.Text)) != 0;
            var mousePending = (pendingRelease & InputCaptureDevices.Mouse) != 0;
            if ((keyboardPending && !keyboardNeutral) || (mousePending && !mouseNeutral))
            {
                _neutralFrames = 0;
                return new InputCaptureTransition(previous, previous);
            }

            _neutralFrames++;
            if (_neutralFrames < RequiredNeutralFrames)
                return new InputCaptureTransition(previous, previous);

            _neutralFrames = 0;
            Volatile.Write(ref _effective, (int)requested);
            return new InputCaptureTransition(previous, requested);
        }
    }

    internal InputCaptureTransition ForceRelease()
    {
        lock (_sync)
        {
            var previous = (InputCaptureDevices)_effective;
            Volatile.Write(ref _requested, (int)InputCaptureDevices.None);
            Volatile.Write(ref _effective, (int)InputCaptureDevices.None);
            _neutralFrames = 0;
            return new InputCaptureTransition(previous, InputCaptureDevices.None);
        }
    }

    private static InputCaptureDevices Normalize(InputCaptureDevices devices)
    {
        devices &= InputCaptureDevices.All;
        if ((devices & InputCaptureDevices.Text) != 0)
            devices |= InputCaptureDevices.Keyboard;
        return devices;
    }
}
