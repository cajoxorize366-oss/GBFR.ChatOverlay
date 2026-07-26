namespace GBFR.ChatOverlay.Input;

/// <summary>
/// Keeps remote Party push-to-talk and the F10 menu's local microphone self-test mutually exclusive.
/// Remote U has priority; reopening the local test always requires an explicit menu action.
/// </summary>
internal sealed class VoiceInputModeCoordinator
{
    private readonly Action<bool> _setRemotePushToTalkPressed;
    private readonly Action<bool> _setLocalMonitorPressed;
    private readonly object _sync = new();

    private bool _remoteKeyPressed;
    private bool _monitorKeyPressed;
    private bool _monitorSuppressedUntilRelease;
    private VoiceInputMode _effectiveMode;

    public VoiceInputModeCoordinator(
        Action<bool> setRemotePushToTalkPressed,
        Action<bool> setLocalMonitorPressed)
    {
        _setRemotePushToTalkPressed = setRemotePushToTalkPressed ??
            throw new ArgumentNullException(nameof(setRemotePushToTalkPressed));
        _setLocalMonitorPressed = setLocalMonitorPressed ??
            throw new ArgumentNullException(nameof(setLocalMonitorPressed));
    }

    public void ReportRemotePushToTalk(bool pressed)
    {
        lock (_sync)
        {
            _remoteKeyPressed = pressed;
            if (pressed && _monitorKeyPressed)
                _monitorSuppressedUntilRelease = true;
            var previous = _effectiveMode;
            var next = GetEffectiveModeLocked();
            _effectiveMode = next;
            ApplyTransition(previous, next);
        }
    }

    public void ReportLocalMonitor(bool pressed)
    {
        lock (_sync)
        {
            _monitorKeyPressed = pressed;
            if (!pressed)
                _monitorSuppressedUntilRelease = false;
            else if (_remoteKeyPressed)
                _monitorSuppressedUntilRelease = true;
            var previous = _effectiveMode;
            var next = GetEffectiveModeLocked();
            _effectiveMode = next;
            ApplyTransition(previous, next);
        }
    }

    private VoiceInputMode GetEffectiveModeLocked()
    {
        if (_remoteKeyPressed)
            return VoiceInputMode.RemotePushToTalk;
        if (_monitorKeyPressed && !_monitorSuppressedUntilRelease)
            return VoiceInputMode.LocalMonitor;
        return VoiceInputMode.None;
    }

    private void ApplyTransition(VoiceInputMode previous, VoiceInputMode next)
    {
        if (previous == next)
            return;

        // Always close the previous audio path before opening the next one.
        if (previous == VoiceInputMode.LocalMonitor)
            _setLocalMonitorPressed(false);
        else if (previous == VoiceInputMode.RemotePushToTalk)
            _setRemotePushToTalkPressed(false);

        if (next == VoiceInputMode.RemotePushToTalk)
            _setRemotePushToTalkPressed(true);
        else if (next == VoiceInputMode.LocalMonitor)
            _setLocalMonitorPressed(true);
    }

    private enum VoiceInputMode
    {
        None,
        LocalMonitor,
        RemotePushToTalk,
    }
}
