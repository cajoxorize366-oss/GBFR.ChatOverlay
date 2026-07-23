using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GBFR.ChatOverlay.SttWorker;

internal sealed class MicrophoneCapture : IAsyncDisposable
{
    private readonly object _writerSync = new();
    private readonly string _rawPath;
    private readonly string _normalizedPath;
    private readonly TimeSpan _maximumDuration;
    private readonly CancellationTokenSource _durationCancellation = new();
    private readonly TaskCompletionSource<StoppedEventArgs> _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private Task? _durationTask;
    private int _stopRequested;
    private int _disposed;

    public MicrophoneCapture(string workDirectory, long requestId, int maximumCaptureSeconds)
    {
        var stem = $"capture-{requestId}-{Guid.NewGuid():N}";
        _rawPath = Path.Combine(workDirectory, stem + ".raw.wav");
        _normalizedPath = Path.Combine(workDirectory, stem + ".wav");
        _maximumDuration = TimeSpan.FromSeconds(maximumCaptureSeconds);
    }

    public event Action? MaximumDurationReached;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_capture is not null)
            throw new InvalidOperationException("Microphone capture has already started.");

        var capture = new WasapiCapture();
        var writer = new WaveFileWriter(_rawPath, capture.WaveFormat);
        capture.DataAvailable += CaptureDataAvailable;
        capture.RecordingStopped += CaptureRecordingStopped;
        _capture = capture;
        _writer = writer;

        try
        {
            capture.StartRecording();
            _durationTask = MonitorMaximumDurationAsync();
        }
        catch
        {
            capture.DataAvailable -= CaptureDataAvailable;
            capture.RecordingStopped -= CaptureRecordingStopped;
            writer.Dispose();
            capture.Dispose();
            _capture = null;
            _writer = null;
            DeleteIfPresent(_rawPath);
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
            DeleteIfPresent(_rawPath);
            DeleteIfPresent(_normalizedPath);
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
