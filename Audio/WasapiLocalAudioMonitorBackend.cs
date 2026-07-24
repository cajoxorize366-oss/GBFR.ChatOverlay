using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GBFR.ChatOverlay.Audio;

internal sealed class WasapiLocalAudioMonitorBackendFactory : ILocalAudioMonitorBackendFactory
{
    public ILocalAudioMonitorBackend Create(
        ResolvedAudioEndpointSelection inputSelection,
        ResolvedAudioEndpointSelection outputSelection,
        float volume) =>
        new WasapiLocalAudioMonitorBackend(inputSelection, outputSelection, volume);
}

/// <summary>
/// One-shot shared-mode WASAPI capture-to-render path. The Party ChatControl is deliberately not
/// involved: this backend exists only to let the local user hear the configured microphone.
/// </summary>
internal sealed class WasapiLocalAudioMonitorBackend : ILocalAudioMonitorBackend
{
    private static readonly TimeSpan CaptureStopWait = TimeSpan.FromMilliseconds(750);

    private readonly ResolvedAudioEndpointSelection _inputSelection;
    private readonly ResolvedAudioEndpointSelection _outputSelection;
    private readonly float _volume;
    private readonly object _sync = new();
    private readonly object _callbackSync = new();
    private readonly ManualResetEventSlim _recordingStopped = new(initialState: false);
    private readonly ManualResetEventSlim _callbacksDrained = new(initialState: true);

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _captureDevice;
    private MMDevice? _renderDevice;
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private bool _started;
    private bool _stopping;
    private bool _silenced;
    private bool _disposed;
    private bool _callbacksClosed;
    private int _callbacksInFlight;
    private int _faultSignaled;

    public WasapiLocalAudioMonitorBackend(
        ResolvedAudioEndpointSelection inputSelection,
        ResolvedAudioEndpointSelection outputSelection,
        float volume)
    {
        _inputSelection = inputSelection;
        _outputSelection = outputSelection;
        _volume = Math.Clamp(volume, 0f, 0.5f);
    }

    public event Action<float>? PeakLevelChanged;

    public event Action<Exception>? Faulted;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping)
                throw new InvalidOperationException("A stopped local monitor backend cannot be restarted.");
            if (_started)
                return;

            _enumerator = new MMDeviceEnumerator();
            _captureDevice = ResolveDevice(_enumerator, _inputSelection, DataFlow.Capture);
            _renderDevice = ResolveDevice(_enumerator, _outputSelection, DataFlow.Render);
            EnsureActive(_captureDevice, "microphone");
            EnsureActive(_renderDevice, "playback");

            _capture = new WasapiCapture(_captureDevice, useEventSync: true, audioBufferMillisecondsLength: 40);
            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(250),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            _output = new WasapiOut(
                _renderDevice,
                AudioClientShareMode.Shared,
                useEventSync: true,
                latency: 80);

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Init(_buffer);
            _output.Volume = _volume;

            try
            {
                // Start silence first, then capture. Teardown reverses this order so feedback ends
                // before the microphone client is stopped.
                _output.Play();
                _capture.StartRecording();
                _started = true;
            }
            catch
            {
                _stopping = true;
                DisposeResourcesLocked(requestCaptureStop: true);
                throw;
            }
        }
    }

    public void SilenceImmediately()
    {
        Volatile.Write(ref _silenced, true);
        try
        {
            _buffer?.ClearBuffer();
        }
        catch
        {
            // The background worker will still perform bounded Stop/Dispose.
        }
    }

    public void Stop()
    {
        WasapiCapture? capture;
        WasapiOut? output;
        BufferedWaveProvider? buffer;
        lock (_sync)
        {
            if (_disposed || _stopping)
                return;

            _stopping = true;
            capture = _capture;
            output = _output;
            buffer = _buffer;
        }

        // Stop local playback first so no buffered microphone audio can continue after I release.
        try
        {
            SilenceImmediately();
            output?.Stop();
        }
        catch
        {
            // Continue with capture shutdown and resource disposal.
        }

        buffer?.ClearBuffer();
        if (capture is not null)
        {
            try
            {
                capture.StopRecording();
                _recordingStopped.Wait(CaptureStopWait);
            }
            catch
            {
                // Dispose remains the final fail-closed boundary.
            }
        }

        lock (_sync)
            DisposeResourcesLocked(requestCaptureStop: false);
    }

    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            DisposeResourcesLocked(requestCaptureStop: false);
        }

        // No callback can enter after callbacksClosed. If an already-running callback exceeded the
        // bounded drain wait, leave these tiny managed wait objects for GC instead of racing a late
        // Set() against Dispose().
        lock (_callbackSync)
        {
            if (_callbacksInFlight == 0)
            {
                _recordingStopped.Dispose();
                _callbacksDrained.Dispose();
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (!TryEnterCallback())
            return;

        try
        {
            if (Volatile.Read(ref _stopping) ||
                Volatile.Read(ref _silenced) ||
                args.BytesRecorded <= 0)
                return;

            var capture = _capture;
            var buffer = _buffer;
            if (capture is null || buffer is null)
                return;

            var peak = AudioSamplePeakMeter.Measure(
                args.Buffer.AsSpan(0, args.BytesRecorded),
                capture.WaveFormat);
            PeakLevelChanged?.Invoke(peak);
            buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
        }
        catch (Exception exception)
        {
            SignalFault(exception);
        }
        finally
        {
            ExitCallback();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (!TryEnterCallback())
            return;

        try
        {
            _recordingStopped.Set();
            if (!Volatile.Read(ref _stopping))
            {
                SignalFault(args.Exception ??
                    new InvalidOperationException("The microphone capture endpoint stopped unexpectedly."));
            }
        }
        finally
        {
            ExitCallback();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (!TryEnterCallback())
            return;

        try
        {
            if (!Volatile.Read(ref _stopping))
            {
                SignalFault(args.Exception ??
                    new InvalidOperationException("The local playback endpoint stopped unexpectedly."));
            }
        }
        finally
        {
            ExitCallback();
        }
    }

    private void SignalFault(Exception exception)
    {
        if (Interlocked.Exchange(ref _faultSignaled, 1) != 0)
            return;

        try
        {
            Faulted?.Invoke(exception);
        }
        catch
        {
            // A subscriber failure must never escape a Core Audio callback.
        }
    }

    private void DisposeResourcesLocked(bool requestCaptureStop)
    {
        if (requestCaptureStop && _capture is not null)
        {
            try
            {
                _capture.StopRecording();
            }
            catch
            {
                // Best-effort during failed startup.
            }
        }

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
        }
        if (_output is not null)
            _output.PlaybackStopped -= OnPlaybackStopped;

        lock (_callbackSync)
        {
            _callbacksClosed = true;
            if (_callbacksInFlight == 0)
                _callbacksDrained.Set();
        }
        _callbacksDrained.Wait(CaptureStopWait);

        _output?.Dispose();
        _capture?.Dispose();
        _renderDevice?.Dispose();
        _captureDevice?.Dispose();
        _enumerator?.Dispose();

        _output = null;
        _capture = null;
        _buffer = null;
        _renderDevice = null;
        _captureDevice = null;
        _enumerator = null;
        _started = false;
    }

    private static MMDevice ResolveDevice(
        MMDeviceEnumerator enumerator,
        ResolvedAudioEndpointSelection selection,
        DataFlow flow)
    {
        if (selection.UseSystemDefault)
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
        if (string.IsNullOrWhiteSpace(selection.DeviceId))
            throw new InvalidOperationException("A manual audio endpoint selection has no device ID.");
        return enumerator.GetDevice(selection.DeviceId);
    }

    private bool TryEnterCallback()
    {
        lock (_callbackSync)
        {
            if (_callbacksClosed)
                return false;

            _callbacksInFlight++;
            if (_callbacksInFlight == 1)
                _callbacksDrained.Reset();
            return true;
        }
    }

    private void ExitCallback()
    {
        lock (_callbackSync)
        {
            _callbacksInFlight--;
            if (_callbacksInFlight == 0)
                _callbacksDrained.Set();
        }
    }

    private static void EnsureActive(MMDevice device, string role)
    {
        if ((device.State & DeviceState.Active) == 0)
        {
            throw new InvalidOperationException(
                $"The selected {role} endpoint is not active: {device.FriendlyName} ({device.State}).");
        }
    }
}
