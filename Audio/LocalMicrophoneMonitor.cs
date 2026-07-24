namespace GBFR.ChatOverlay.Audio;

internal enum LocalMicrophoneMonitorState
{
    Idle,
    Starting,
    Monitoring,
    SignalDetected,
    Faulted,
    Suspended,
}

internal interface ILocalAudioMonitorBackend : IDisposable
{
    event Action<float>? PeakLevelChanged;

    event Action<Exception>? Faulted;

    void Start();

    /// <summary>Stops accepting/playing samples without waiting for endpoint teardown.</summary>
    void SilenceImmediately();

    void Stop();
}

internal interface ILocalAudioMonitorBackendFactory
{
    ILocalAudioMonitorBackend Create(
        ResolvedAudioEndpointSelection inputSelection,
        ResolvedAudioEndpointSelection outputSelection,
        float volume);
}

/// <summary>
/// Owns one hold-to-monitor session. All endpoint activation and teardown is serialized on a
/// background worker so DirectInput and its watchdog never block on Core Audio.
/// </summary>
internal sealed class LocalMicrophoneMonitor : IDisposable
{
    private const float SignalThreshold = 0.01f;

    private readonly ILocalAudioMonitorBackendFactory _backendFactory;
    private readonly ResolvedAudioEndpointSelection _inputSelection;
    private readonly ResolvedAudioEndpointSelection _outputSelection;
    private readonly float _volume;
    private readonly Action<string> _log;
    private readonly Action<Action> _schedule;
    private readonly object _sync = new();

    private ILocalAudioMonitorBackend? _backend;
    private LocalMicrophoneMonitorState _state;
    private long _generation;
    private bool _desiredPressed;
    private bool _holdStarted;
    private bool _signalDetected;
    private bool _workScheduled;
    private bool _suspended;
    private bool _disposed;

    public LocalMicrophoneMonitor(
        ILocalAudioMonitorBackendFactory backendFactory,
        ResolvedAudioEndpointSelection inputSelection,
        ResolvedAudioEndpointSelection outputSelection,
        float volume,
        Action<string> log,
        Action<Action>? schedule = null)
    {
        _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
        _inputSelection = inputSelection;
        _outputSelection = outputSelection;
        _volume = Math.Clamp(volume, 0f, 0.5f);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _schedule = schedule ?? QueueBackground;
    }

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
                return !_disposed && !_suspended;
        }
    }

    public LocalMicrophoneMonitorState State
    {
        get
        {
            lock (_sync)
                return _state;
        }
    }

    public void SetPressed(bool pressed)
    {
        string? resultLog = null;
        ILocalAudioMonitorBackend? silenceBackend = null;
        var scheduleReconcile = false;
        lock (_sync)
        {
            if (_disposed)
                return;

            if (_suspended)
                pressed = false;
            if (pressed == _desiredPressed)
            {
                if (!pressed && _state == LocalMicrophoneMonitorState.Faulted)
                    _state = LocalMicrophoneMonitorState.Idle;
                return;
            }

            if (!pressed && _desiredPressed)
            {
                resultLog = !_holdStarted
                    ? "Local microphone monitor release acknowledged before audio activation completed; " +
                      "the pending start was cancelled."
                    : _signalDetected
                        ? "Local microphone monitor result: PASS — microphone signal was detected and sent to the selected local playback path."
                        : "Local microphone monitor result: no microphone signal was observed during this hold.";
            }

            _desiredPressed = pressed;
            _generation++;
            if (pressed)
            {
                _holdStarted = false;
                _signalDetected = false;
                _state = LocalMicrophoneMonitorState.Starting;
            }
            else if (!_suspended)
            {
                _state = LocalMicrophoneMonitorState.Idle;
                silenceBackend = _backend;
            }

            scheduleReconcile = MarkReconcileScheduledLocked();
        }

        var playbackWasGated = SilenceImmediately(silenceBackend);
        if (playbackWasGated)
        {
            SafeLog(
                "Local microphone monitor release acknowledged; local playback was gated off " +
                "and endpoint cleanup continues in the background.");
        }
        if (resultLog is not null)
            SafeLog(resultLog);
        if (scheduleReconcile)
            ScheduleReconcile();
    }

    public void Suspend()
    {
        ILocalAudioMonitorBackend? silenceBackend = null;
        var scheduleReconcile = false;
        lock (_sync)
        {
            if (_disposed || _suspended)
                return;

            _suspended = true;
            _desiredPressed = false;
            _generation++;
            _state = LocalMicrophoneMonitorState.Suspended;
            silenceBackend = _backend;
            scheduleReconcile = MarkReconcileScheduledLocked();
        }

        _ = SilenceImmediately(silenceBackend);
        if (scheduleReconcile)
            ScheduleReconcile();
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_disposed || !_suspended)
                return;

            _suspended = false;
            _state = LocalMicrophoneMonitorState.Idle;
        }
    }

    public void Dispose()
    {
        ILocalAudioMonitorBackend? silenceBackend = null;
        var scheduleReconcile = false;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _desiredPressed = false;
            _generation++;
            _state = LocalMicrophoneMonitorState.Idle;
            silenceBackend = _backend;
            scheduleReconcile = MarkReconcileScheduledLocked();
        }

        _ = SilenceImmediately(silenceBackend);
        if (scheduleReconcile)
            ScheduleReconcile();
    }

    private void Reconcile()
    {
        while (true)
        {
            ILocalAudioMonitorBackend? stopBackend = null;
            long startGeneration = 0;
            var shouldCreate = false;
            lock (_sync)
            {
                var shouldRun = _desiredPressed && !_suspended && !_disposed;
                if (!shouldRun && _backend is not null)
                {
                    stopBackend = _backend;
                    _backend = null;
                }
                else if (shouldRun && _backend is null)
                {
                    shouldCreate = true;
                    startGeneration = _generation;
                }
                else
                {
                    _workScheduled = false;
                    return;
                }
            }

            if (stopBackend is not null)
            {
                StopAndDispose(stopBackend);
                continue;
            }

            if (!shouldCreate)
                continue;

            ILocalAudioMonitorBackend? candidate = null;
            try
            {
                candidate = _backendFactory.Create(
                    _inputSelection,
                    _outputSelection,
                    _volume);
                candidate.PeakLevelChanged += peak => OnPeakLevelChanged(candidate, startGeneration, peak);
                candidate.Faulted += exception => OnBackendFaulted(candidate, startGeneration, exception);

                var canStart = false;
                lock (_sync)
                {
                    canStart = CanStartLocked(startGeneration);
                    if (canStart)
                        _backend = candidate;
                }

                if (!canStart)
                {
                    StopAndDispose(candidate);
                    continue;
                }

                candidate.Start();

                var remainsCurrent = false;
                lock (_sync)
                {
                    remainsCurrent = ReferenceEquals(_backend, candidate) &&
                                     CanStartLocked(startGeneration);
                    if (remainsCurrent)
                    {
                        _holdStarted = true;
                        _state = _signalDetected
                            ? LocalMicrophoneMonitorState.SignalDetected
                            : LocalMicrophoneMonitorState.Monitoring;
                    }
                    else if (ReferenceEquals(_backend, candidate))
                    {
                        _backend = null;
                    }
                }

                if (!remainsCurrent)
                {
                    StopAndDispose(candidate);
                    continue;
                }

                SafeLog(
                    $"Local microphone monitor started: input=\"{_inputSelection.DisplayName}\", " +
                    $"output=\"{_outputSelection.DisplayName}\", volume={_volume:P0}. " +
                    "Audio remains on this PC and is not sent through Party.");
            }
            catch (Exception exception)
            {
                if (candidate is not null)
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_backend, candidate))
                            _backend = null;
                    }
                    StopAndDispose(candidate);
                }

                FailStart(startGeneration, exception);
            }
        }
    }

    private bool CanStartLocked(long generation) =>
        generation == _generation &&
        _desiredPressed &&
        !_suspended &&
        !_disposed;

    private void OnPeakLevelChanged(
        ILocalAudioMonitorBackend backend,
        long generation,
        float peak)
    {
        var detected = false;
        lock (_sync)
        {
            if (!ReferenceEquals(_backend, backend) ||
                generation != _generation ||
                !_desiredPressed ||
                _signalDetected ||
                !float.IsFinite(peak) ||
                peak < SignalThreshold)
            {
                return;
            }

            _signalDetected = true;
            _state = LocalMicrophoneMonitorState.SignalDetected;
            detected = true;
        }

        if (detected)
            SafeLog($"Local microphone monitor detected input signal (peak {peak:P0}).");
    }

    private void OnBackendFaulted(
        ILocalAudioMonitorBackend backend,
        long generation,
        Exception exception)
    {
        var accepted = false;
        ILocalAudioMonitorBackend? silenceBackend = null;
        var scheduleReconcile = false;
        lock (_sync)
        {
            if (!ReferenceEquals(_backend, backend) || generation != _generation || _disposed)
                return;

            _desiredPressed = false;
            _generation++;
            _state = LocalMicrophoneMonitorState.Faulted;
            silenceBackend = _backend;
            scheduleReconcile = MarkReconcileScheduledLocked();
            accepted = true;
        }

        if (accepted)
        {
            SafeLog(
                $"Local microphone monitor failed closed with {exception.GetType().Name}: " +
                $"{exception.Message}");
        }
        _ = SilenceImmediately(silenceBackend);
        if (scheduleReconcile)
            ScheduleReconcile();
    }

    private void FailStart(long generation, Exception exception)
    {
        var accepted = false;
        lock (_sync)
        {
            if (generation == _generation && !_disposed)
            {
                _desiredPressed = false;
                _generation++;
                _state = LocalMicrophoneMonitorState.Faulted;
                accepted = true;
            }
        }

        if (accepted)
        {
            SafeLog(
                $"Local microphone monitor could not start and remained off: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private bool MarkReconcileScheduledLocked()
    {
        if (_workScheduled)
            return false;

        _workScheduled = true;
        return true;
    }

    private void ScheduleReconcile()
    {
        try
        {
            _schedule(Reconcile);
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _workScheduled = false;
                _desiredPressed = false;
                _generation++;
                _state = LocalMicrophoneMonitorState.Faulted;
            }
            SafeLog($"Local microphone monitor worker could not be scheduled: {exception.Message}");
        }
    }

    private static void StopAndDispose(ILocalAudioMonitorBackend backend)
    {
        try
        {
            backend.Stop();
        }
        catch
        {
            // Teardown is best-effort; Dispose is still required to release Core Audio handles.
        }

        try
        {
            backend.Dispose();
        }
        catch
        {
            // Never allow endpoint teardown to escape the serialized worker.
        }
    }

    private bool SilenceImmediately(ILocalAudioMonitorBackend? backend)
    {
        if (backend is null)
            return false;

        try
        {
            backend.SilenceImmediately();
            return true;
        }
        catch (Exception exception)
        {
            SafeLog(
                $"Local microphone monitor immediate silence request failed: {exception.Message}; " +
                "the background endpoint cleanup remains queued.");
            return false;
        }
    }

    private void SafeLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Logging must not destabilize an input or audio callback.
        }
    }

    private static void QueueBackground(Action work)
    {
        if (!ThreadPool.QueueUserWorkItem(static state => ((Action)state!).Invoke(), work))
            throw new InvalidOperationException("The local microphone monitor worker queue rejected work.");
    }
}
