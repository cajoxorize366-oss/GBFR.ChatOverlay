using System.Diagnostics;

namespace GBFR.ChatOverlay.Input;

/// <summary>
/// Converts DirectInput key-state heartbeats into edge notifications and forces a release when
/// polling stops. This prevents a lost key-up, focus loss or suspended input hook from leaving
/// Party's microphone input unmuted.
/// </summary>
public sealed class VoicePushToTalkSafetyGate : IDisposable
{
    private static readonly TimeSpan DefaultHeartbeatTimeout = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan DefaultWatchdogPeriod = TimeSpan.FromMilliseconds(100);

    private readonly Action<bool> _setPressed;
    private readonly Action<string>? _log;
    private readonly Func<long> _getTimestamp;
    private readonly long _heartbeatTimeoutTicks;
    private readonly object _sync = new();
    private readonly Timer? _watchdog;

    private long _lastHeartbeat;
    private bool _reportedPressed;
    private bool _acceptReports = true;
    private bool _disposed;

    public VoicePushToTalkSafetyGate(Action<bool> setPressed, Action<string>? log = null)
        : this(
            setPressed,
            log,
            DefaultHeartbeatTimeout,
            Stopwatch.GetTimestamp,
            startWatchdog: true)
    {
    }

    internal VoicePushToTalkSafetyGate(
        Action<bool> setPressed,
        Action<string>? log,
        TimeSpan heartbeatTimeout,
        Func<long> getTimestamp,
        bool startWatchdog)
    {
        _setPressed = setPressed ?? throw new ArgumentNullException(nameof(setPressed));
        _log = log;
        _getTimestamp = getTimestamp ?? throw new ArgumentNullException(nameof(getTimestamp));
        if (heartbeatTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(heartbeatTimeout));

        _heartbeatTimeoutTicks = Math.Max(
            1,
            checked((long)(heartbeatTimeout.TotalSeconds * Stopwatch.Frequency)));
        _lastHeartbeat = _getTimestamp();
        if (startWatchdog)
        {
            _watchdog = new Timer(
                static state => ((VoicePushToTalkSafetyGate)state!).CheckForTimeout(),
                this,
                DefaultWatchdogPeriod,
                DefaultWatchdogPeriod);
        }
    }

    public void Report(bool pressed)
    {
        lock (_sync)
        {
            if (_disposed || !_acceptReports)
                return;

            _lastHeartbeat = _getTimestamp();
            var notify = pressed != _reportedPressed;
            _reportedPressed = pressed;
            if (notify)
                SafeSetPressed(pressed);
        }
    }

    public void ForceMute()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            var notify = _reportedPressed;
            _reportedPressed = false;
            _lastHeartbeat = _getTimestamp();
            if (notify)
                SafeSetPressed(false);
        }
    }

    public void Suspend()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _acceptReports = false;
            var notify = _reportedPressed;
            _reportedPressed = false;
            _lastHeartbeat = _getTimestamp();
            if (notify)
                SafeSetPressed(false);
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _reportedPressed = false;
            _lastHeartbeat = _getTimestamp();
            _acceptReports = true;
        }
    }

    internal void CheckForTimeout()
    {
        var timedOut = false;
        lock (_sync)
        {
            if (_disposed || !_acceptReports || !_reportedPressed)
                return;

            timedOut = _getTimestamp() - _lastHeartbeat >= _heartbeatTimeoutTicks;
            if (timedOut)
            {
                _reportedPressed = false;
                SafeSetPressed(false);
            }
        }

        if (timedOut)
            SafeLog("Stage 3 push-to-talk heartbeat timed out; microphone mute was forced.");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _acceptReports = false;
            var notify = _reportedPressed;
            _reportedPressed = false;
            if (notify)
                SafeSetPressed(false);
        }

        _watchdog?.Dispose();
    }

    private void SafeSetPressed(bool pressed)
    {
        try
        {
            _setPressed(pressed);
        }
        catch (Exception exception)
        {
            SafeLog($"Stage 3 push-to-talk state callback failed: {exception.Message}");
        }
    }

    private void SafeLog(string message)
    {
        try
        {
            _log?.Invoke(message);
        }
        catch
        {
            // A logger failure must never escape a watchdog or native input callback.
        }
    }
}
