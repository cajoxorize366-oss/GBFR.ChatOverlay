using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GBFR.ChatOverlay.Audio;

internal sealed class WasapiLocalAudioMonitorBackendFactory : ILocalAudioMonitorBackendFactory
{
    private readonly Action<string> _log;

    public WasapiLocalAudioMonitorBackendFactory(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    public ILocalAudioMonitorBackend Create(
        ResolvedAudioEndpointSelection inputSelection,
        ResolvedAudioEndpointSelection outputSelection,
        float inputGain,
        float playbackVolume) =>
        new WasapiLocalAudioMonitorBackend(
            inputSelection,
            outputSelection,
            inputGain,
            playbackVolume,
            _log);
}

/// <summary>
/// One-shot shared-mode WASAPI capture-to-render path. Release closes a lock-free playback gate
/// synchronously, then moves every potentially blocking NAudio Stop/Dispose call to a dedicated
/// background thread. A stuck endpoint cleanup can therefore neither keep audio audible nor block
/// the next menu self-test or DirectInput polling.
/// </summary>
internal sealed class WasapiLocalAudioMonitorBackend : ILocalAudioMonitorBackend
{
    private static readonly TimeSpan CaptureStopWait = TimeSpan.FromMilliseconds(1_500);
    private static readonly TimeSpan CleanupWarningDelay = TimeSpan.FromSeconds(2);

    private readonly ResolvedAudioEndpointSelection _inputSelection;
    private readonly ResolvedAudioEndpointSelection _outputSelection;
    private float _inputGain;
    private float _playbackVolume;
    private readonly Action<string> _log;
    private readonly object _sync = new();
    private readonly object _callbackSync = new();
    private readonly ManualResetEventSlim _recordingStopped = new(initialState: false);
    private readonly ManualResetEventSlim _callbacksDrained = new(initialState: true);

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _captureDevice;
    private MMDevice? _renderDevice;
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private GatedBufferedWaveProvider? _buffer;
    private VolumeSampleProvider? _gainProvider;
    private Timer? _cleanupWatchdog;
    private string _cleanupPhase = "not started";
    private bool _started;
    private bool _callbacksClosed;
    private int _callbacksInFlight;
    private int _disposeRequested;
    private int _cleanupStarted;
    private int _stopping;
    private int _cleanupCompleted;
    private int _faultSignaled;
    private int _playbackStoppedEventObserved;

    public WasapiLocalAudioMonitorBackend(
        ResolvedAudioEndpointSelection inputSelection,
        ResolvedAudioEndpointSelection outputSelection,
        float inputGain,
        float playbackVolume,
        Action<string>? log = null)
    {
        _inputSelection = inputSelection;
        _outputSelection = outputSelection;
        _inputGain = Math.Clamp(inputGain, 0f, 2.0f);
        _playbackVolume = Math.Clamp(playbackVolume, 0f, 0.5f);
        _log = log ?? (_ => { });
    }

    public event Action<float>? PeakLevelChanged;

    public event Action<Exception>? Faulted;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
            if (_started)
                return;

            _enumerator = new MMDeviceEnumerator();
            _captureDevice = ResolveDevice(_enumerator, _inputSelection, DataFlow.Capture);
            _renderDevice = ResolveDevice(_enumerator, _outputSelection, DataFlow.Render);
            EnsureActive(_captureDevice, "microphone");
            EnsureActive(_renderDevice, "playback");

            _capture = new WasapiCapture(
                _captureDevice,
                useEventSync: true,
                audioBufferMillisecondsLength: 40);
            _buffer = new GatedBufferedWaveProvider(_capture.WaveFormat, TimeSpan.FromMilliseconds(250));
            _output = new WasapiOut(
                _renderDevice,
                AudioClientShareMode.Shared,
                useEventSync: true,
                latency: 80);

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _output.PlaybackStopped += OnPlaybackStopped;
            _gainProvider = new VolumeSampleProvider(_buffer.ToSampleProvider())
            {
                Volume = _inputGain,
            };
            _output.Init(_gainProvider.ToWaveProvider());
            _output.Volume = _playbackVolume;

            try
            {
                _output.Play();
                _capture.StartRecording();
                _started = true;
            }
            catch
            {
                RequestStopAndDispose("startup failure");
                throw;
            }
        }
    }

    public void SilenceImmediately()
    {
        // Disable never takes the circular-buffer lock. A racing Read double-checks the gate after
        // copying and overwrites the output with zeroes, so at most the render quantum already
        // handed to WASAPI can remain audible.
        Volatile.Read(ref _buffer)?.Disable();
    }

    public void SetLevels(float inputGain, float playbackVolume)
    {
        lock (_sync)
        {
            _inputGain = Math.Clamp(inputGain, 0f, 2.0f);
            _playbackVolume = Math.Clamp(playbackVolume, 0f, 0.5f);
            if (_gainProvider is not null)
                _gainProvider.Volume = _inputGain;
            if (_output is not null)
                _output.Volume = _playbackVolume;
        }
    }

    public void Stop() => RequestStopAndDispose("stop requested");

    public void Dispose() => RequestStopAndDispose("dispose requested");

    private void RequestStopAndDispose(string reason)
    {
        Interlocked.Exchange(ref _stopping, 1);
        SilenceImmediately();

        Interlocked.Exchange(ref _disposeRequested, 1);
        if (Interlocked.CompareExchange(ref _cleanupStarted, 1, 0) != 0)
            return;

        Thread cleanupThread;
        lock (_sync)
        {
            SetCleanupPhase("queued");
            _cleanupWatchdog = new Timer(
                static state => ((WasapiLocalAudioMonitorBackend)state!).ReportSlowCleanup(),
                this,
                CleanupWarningDelay,
                Timeout.InfiniteTimeSpan);
            cleanupThread = new Thread(CleanupThreadMain)
            {
                IsBackground = true,
                Name = "GBFR local microphone monitor cleanup",
            };
        }

        SafeLog(
            $"Local microphone monitor cleanup queued ({reason}); the playback gate is already closed.");
        try
        {
            cleanupThread.Start();
        }
        catch (Exception exception)
        {
            SafeLog(
                $"Local microphone monitor cleanup thread could not start: {exception.Message}; " +
                "falling back to the thread pool.");
            _ = ThreadPool.QueueUserWorkItem(
                static state => ((WasapiLocalAudioMonitorBackend)state!).CleanupThreadMain(),
                this);
        }
    }

    private void CleanupThreadMain()
    {
        var stopwatch = Stopwatch.StartNew();
        WasapiCapture? capture;
        WasapiOut? output;
        lock (_sync)
        {
            capture = _capture;
            output = _output;
        }

        try
        {
            SetCleanupPhase("requesting microphone stop");
            if (capture is not null)
            {
                try
                {
                    capture.StopRecording();
                }
                catch (Exception exception)
                {
                    SafeLog($"Local microphone monitor capture stop request failed: {exception.Message}");
                }

                var captureStopped = _recordingStopped.Wait(CaptureStopWait);
                SafeLog(
                    $"Local microphone monitor RecordingStopped event observed={captureStopped} " +
                    $"after {stopwatch.ElapsedMilliseconds} ms.");
            }

            SetCleanupPhase("stopping local playback");
            if (output is not null)
            {
                try
                {
                    output.Stop();
                    SafeLog(
                        $"Local microphone monitor playback stopped after " +
                        $"{stopwatch.ElapsedMilliseconds} ms; " +
                        $"PlaybackStopped event observed=" +
                        $"{Volatile.Read(ref _playbackStoppedEventObserved) != 0}.");
                }
                catch (Exception exception)
                {
                    SafeLog($"Local microphone monitor playback stop failed: {exception.Message}");
                }
            }
        }
        finally
        {
            CloseCallbacksAndWait();
            SetCleanupPhase("disposing endpoints");
            try
            {
                lock (_sync)
                    DisposeResourcesLocked();
            }
            catch (Exception exception)
            {
                SafeLog($"Local microphone monitor endpoint disposal failed: {exception.Message}");
            }

            Interlocked.Exchange(ref _cleanupCompleted, 1);
            SetCleanupPhase("complete");
            _cleanupWatchdog?.Dispose();
            SafeLog(
                $"Local microphone monitor cleanup complete after {stopwatch.ElapsedMilliseconds} ms.");
        }
    }

    private void CloseCallbacksAndWait()
    {
        SetCleanupPhase("draining audio callbacks");
        lock (_sync)
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
            }
            if (_output is not null)
                _output.PlaybackStopped -= OnPlaybackStopped;
        }

        lock (_callbackSync)
        {
            _callbacksClosed = true;
            if (_callbacksInFlight == 0)
                _callbacksDrained.Set();
        }

        // This is a disposable background thread. Waiting here is safer than disposing a native
        // endpoint under a callback; the watchdog identifies a pathological delay without ever
        // blocking DirectInput, rendering, or a subsequent menu self-test.
        _callbacksDrained.Wait();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (!TryEnterCallback())
            return;

        try
        {
            if (Volatile.Read(ref _stopping) != 0 || args.BytesRecorded <= 0)
                return;

            var capture = Volatile.Read(ref _capture);
            var buffer = Volatile.Read(ref _buffer);
            if (capture is null || buffer is null)
                return;

            var peak = AudioSamplePeakMeter.Measure(
                args.Buffer.AsSpan(0, args.BytesRecorded),
                capture.WaveFormat);
            PublishPeak(Math.Clamp(peak * Volatile.Read(ref _inputGain), 0.0f, 1.0f));
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
            if (Volatile.Read(ref _stopping) == 0)
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
            Interlocked.Exchange(ref _playbackStoppedEventObserved, 1);
            if (Volatile.Read(ref _stopping) == 0)
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

        var handler = Faulted;
        if (handler is not null)
            QueueManagedCallback(() => handler(exception));
    }

    private void PublishPeak(float peak)
    {
        if (!float.IsFinite(peak))
            return;

        var handler = PeakLevelChanged;
        if (handler is not null)
            QueueManagedCallback(() => handler(peak));
    }

    private void QueueManagedCallback(Action callback)
    {
        if (ThreadPool.QueueUserWorkItem(
                static state =>
                {
                    try
                    {
                        ((Action)state!).Invoke();
                    }
                    catch
                    {
                        // Managed subscribers cannot destabilize Core Audio or cleanup threads.
                    }
                },
                callback))
        {
            return;
        }

        SafeLog("Local microphone monitor could not queue a managed audio notification.");
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

    private void DisposeResourcesLocked()
    {
        _output?.Dispose();
        _capture?.Dispose();
        _renderDevice?.Dispose();
        _captureDevice?.Dispose();
        _enumerator?.Dispose();

        _output = null;
        _capture = null;
        _buffer = null;
        _gainProvider = null;
        _renderDevice = null;
        _captureDevice = null;
        _enumerator = null;
        _started = false;

        lock (_callbackSync)
        {
            if (_callbacksInFlight == 0)
            {
                _recordingStopped.Dispose();
                _callbacksDrained.Dispose();
            }
        }
    }

    private void ReportSlowCleanup()
    {
        if (Volatile.Read(ref _cleanupCompleted) != 0)
            return;

        SafeLog(
            $"Local microphone monitor cleanup is still running after " +
            $"{CleanupWarningDelay.TotalMilliseconds:0} ms at phase=\"{Volatile.Read(ref _cleanupPhase)}\". " +
            "Playback remains gated off and another menu self-test is not blocked.");
    }

    private void SetCleanupPhase(string phase) => Volatile.Write(ref _cleanupPhase, phase);

    private void SafeLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Cleanup and audio callbacks cannot depend on the logger.
        }
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

    private static void EnsureActive(MMDevice device, string role)
    {
        if ((device.State & DeviceState.Active) == 0)
        {
            throw new InvalidOperationException(
                $"The selected {role} endpoint is not active: {device.FriendlyName} ({device.State}).");
        }
    }
}

/// <summary>
/// Buffered provider with a lock-free, one-way silence gate. Disable is safe on DirectInput's
/// thread and never waits for BufferedWaveProvider's circular-buffer lock.
/// </summary>
internal sealed class GatedBufferedWaveProvider : IWaveProvider
{
    private readonly BufferedWaveProvider _inner;
    private int _enabled = 1;

    public GatedBufferedWaveProvider(WaveFormat format, TimeSpan bufferDuration)
    {
        _inner = new BufferedWaveProvider(format)
        {
            BufferDuration = bufferDuration,
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
    }

    public WaveFormat WaveFormat => _inner.WaveFormat;

    public void AddSamples(byte[] buffer, int offset, int count)
    {
        if (Volatile.Read(ref _enabled) == 0)
            return;
        _inner.AddSamples(buffer, offset, count);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (Volatile.Read(ref _enabled) == 0)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        var read = _inner.Read(buffer, offset, count);
        if (Volatile.Read(ref _enabled) == 0)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
        return read;
    }

    public void Disable() => Interlocked.Exchange(ref _enabled, 0);
}
