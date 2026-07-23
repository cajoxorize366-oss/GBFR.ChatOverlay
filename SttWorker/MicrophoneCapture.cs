using NAudio.CoreAudioApi;
using NAudio.Wave;
using GBFR.ChatOverlay.Stt;

namespace GBFR.ChatOverlay.SttWorker;

internal sealed class MicrophoneCapture : IAsyncDisposable
{
    private readonly object _writerSync = new();
    private readonly string _rawPath;
    private readonly string _normalizedPath;
    private readonly TimeSpan _maximumDuration;
    private readonly long _requestId;
    private readonly string _deviceSelector;
    private readonly WorkerDiagnostics _diagnostics;
    private readonly CancellationTokenSource _durationCancellation = new();
    private readonly TaskCompletionSource<StoppedEventArgs> _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WasapiCapture? _capture;
    private AudioDeviceLease? _deviceLease;
    private AudioCaptureDeviceSelection? _deviceSelection;
    private string? _rawWaveFormat;
    private WaveFileWriter? _writer;
    private Task? _durationTask;
    private int _stopRequested;
    private int _disposed;

    public MicrophoneCapture(
        string workDirectory,
        long requestId,
        int maximumCaptureSeconds,
        string deviceSelector,
        WorkerDiagnostics diagnostics)
    {
        var stem = $"capture-{requestId}-{Guid.NewGuid():N}";
        _rawPath = Path.Combine(workDirectory, stem + ".raw.wav");
        _normalizedPath = Path.Combine(workDirectory, stem + ".wav");
        _maximumDuration = TimeSpan.FromSeconds(maximumCaptureSeconds);
        _requestId = requestId;
        _deviceSelector = deviceSelector;
        _diagnostics = diagnostics;
    }

    public event Action? MaximumDurationReached;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_capture is not null)
            throw new InvalidOperationException("Microphone capture has already started.");

        AudioDeviceLease? lease = null;
        WasapiCapture? capture = null;
        WaveFileWriter? writer = null;
        try
        {
            lease = AudioDeviceCatalog.Resolve(_deviceSelector);
            capture = new WasapiCapture(lease.Device);
            writer = new WaveFileWriter(_rawPath, capture.WaveFormat);
            capture.DataAvailable += CaptureDataAvailable;
            capture.RecordingStopped += CaptureRecordingStopped;
            _deviceLease = lease;
            _deviceSelection = lease.Selection;
            _rawWaveFormat = capture.WaveFormat.ToString();
            _capture = capture;
            _writer = writer;

            if (!string.IsNullOrWhiteSpace(lease.Selection.Warning))
                _diagnostics.Log($"request={_requestId} {lease.Selection.Warning}");
            _diagnostics.Log(
                $"request={_requestId} recording device=\"{lease.Selection.Device.Name}\" " +
                $"id=\"{lease.Selection.Device.Id}\" format=\"{_rawWaveFormat}\" raw=\"{_rawPath}\"");
            capture.StartRecording();
            _durationTask = MonitorMaximumDurationAsync();
        }
        catch (Exception exception)
        {
            if (capture is not null)
            {
                capture.DataAvailable -= CaptureDataAvailable;
                capture.RecordingStopped -= CaptureRecordingStopped;
            }
            writer?.Dispose();
            capture?.Dispose();
            lease?.Dispose();
            _capture = null;
            _writer = null;
            _deviceLease = null;
            if (!_diagnostics.PreserveArtifacts)
                DeleteIfPresent(_rawPath);
            _diagnostics.Log($"request={_requestId} microphone start failed: {exception}");
            throw;
        }
    }

    public async Task<string> StopAndNormalizeAsync(CancellationToken cancellationToken)
    {
        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        using (var reader = new WaveFileReader(_rawPath))
        {
            if (reader.TotalTime < TimeSpan.FromMilliseconds(150))
                throw new InvalidDataException("The recording was too short to transcribe.");

            var targetFormat = new WaveFormat(16_000, 16, 1);
            using var resampler = new MediaFoundationResampler(reader, targetFormat)
            {
                ResamplerQuality = 60,
            };
            WaveFileWriter.CreateWaveFile(_normalizedPath, resampler);
        }

        WriteAudioDiagnostics();
        if (!_diagnostics.PreserveArtifacts)
            DeleteIfPresent(_rawPath);
        return _normalizedPath;
    }

    public async Task CancelAsync()
    {
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            if (!_diagnostics.PreserveArtifacts)
            {
                DeleteIfPresent(_rawPath);
                DeleteIfPresent(_normalizedPath);
            }
            else
            {
                _diagnostics.Log(
                    $"request={_requestId} capture cancelled; retained raw=\"{_rawPath}\" " +
                    $"normalized=\"{_normalizedPath}\"");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await CancelAsync().ConfigureAwait(false);
        _durationCancellation.Dispose();
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var capture = _capture;
        if (capture is null)
            throw new InvalidOperationException("Microphone capture was not started.");

        StoppedEventArgs? stopped = null;
        Exception? stopFailure = null;
        try
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
                capture.StopRecording();
            stopped = await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }
        finally
        {
            _durationCancellation.Cancel();
            capture.DataAvailable -= CaptureDataAvailable;
            capture.RecordingStopped -= CaptureRecordingStopped;
            lock (_writerSync)
            {
                _writer?.Dispose();
                _writer = null;
            }
            capture.Dispose();
            _capture = null;
            _deviceLease?.Dispose();
            _deviceLease = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (stopFailure is not null)
            throw new InvalidOperationException("Microphone capture did not stop cleanly.", stopFailure);
        if (stopped?.Exception is not null)
            throw new InvalidOperationException("Microphone capture stopped with an error.", stopped.Exception);
    }

    private async Task MonitorMaximumDurationAsync()
    {
        try
        {
            await Task.Delay(_maximumDuration, _durationCancellation.Token).ConfigureAwait(false);
            MaximumDurationReached?.Invoke();
        }
        catch (OperationCanceledException) when (_durationCancellation.IsCancellationRequested)
        {
        }
    }

    private void CaptureDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        lock (_writerSync)
            _writer?.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
    }

    private void CaptureRecordingStopped(object? sender, StoppedEventArgs eventArgs) =>
        _stopped.TrySetResult(eventArgs);

    private void WriteAudioDiagnostics()
    {
        try
        {
            var metrics = AudioFileDiagnostics.Analyze(_normalizedPath);
            _diagnostics.WriteJson(
                $"request-{_requestId}-audio.json",
                new
                {
                    requestId = _requestId,
                    device = _deviceSelection?.Device,
                    usedFallback = _deviceSelection?.UsedFallback,
                    warning = _deviceSelection?.Warning,
                    rawPath = _rawPath,
                    rawBytes = File.Exists(_rawPath) ? new FileInfo(_rawPath).Length : 0,
                    rawFormat = _rawWaveFormat,
                    normalizedPath = _normalizedPath,
                    normalized = metrics,
                });
            _diagnostics.Log(
                $"request={_requestId} normalized durationMs={metrics.DurationMilliseconds:F0} " +
                $"peak={metrics.Peak:F4} rms={metrics.Rms:F4} silence={metrics.SilenceRatio:P1} " +
                $"clipping={metrics.ClippingRatio:P2} wav=\"{_normalizedPath}\"");
            if (metrics.LikelySilent)
            {
                _diagnostics.Log(
                    $"request={_requestId} WARNING input is near-silent; verify the selected microphone and Windows input level.");
            }
            else if (metrics.LikelyClipping)
            {
                _diagnostics.Log(
                    $"request={_requestId} WARNING input is clipping; lower the Windows microphone input level.");
            }
        }
        catch (Exception exception)
        {
            _diagnostics.Log($"request={_requestId} audio diagnostics failed: {exception.Message}");
        }
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
