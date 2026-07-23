using System.Diagnostics;
using GBFR.ChatOverlay.Stt;

namespace GBFR.ChatOverlay.SttWorker;

internal sealed class SttWorkerHost : IAsyncDisposable
{
    private readonly object _stateSync = new();
    private readonly WorkerOptions _options;
    private readonly ProtocolWriter _protocol;
    private readonly WorkerDiagnostics _diagnostics;
    private readonly WhisperCliTranscriber _transcriber;
    private MicrophoneCapture? _capture;
    private CancellationTokenSource? _transcriptionCancellation;
    private Task? _transcriptionTask;
    private long _activeRequestId;
    private int _shutdown;

    public SttWorkerHost(
        WorkerOptions options,
        ProtocolWriter protocol,
        WorkerDiagnostics diagnostics)
    {
        _options = options;
        _protocol = protocol;
        _diagnostics = diagnostics;
        _transcriber = new WhisperCliTranscriber(options, diagnostics);
    }

    public async Task HandleAsync(SttCommand command)
    {
        switch (command.Type)
        {
            case SttMessageTypes.Start:
                await StartCaptureAsync(command.RequestId).ConfigureAwait(false);
                break;
            case SttMessageTypes.Stop:
                BeginTranscription(command.RequestId);
                break;
            case SttMessageTypes.Cancel:
                await CancelAsync(command.RequestId).ConfigureAwait(false);
                break;
        }
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
            return;

        MicrophoneCapture? capture;
        CancellationTokenSource? cancellation;
        Task? transcription;
        lock (_stateSync)
        {
            capture = _capture;
            _capture = null;
            cancellation = _transcriptionCancellation;
            transcription = _transcriptionTask;
            _activeRequestId = 0;
        }

        cancellation?.Cancel();
        if (capture is not null)
            await capture.CancelAsync().ConfigureAwait(false);
        if (transcription is not null)
        {
            try
            {
                await transcription.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = transcription.ContinueWith(
                    completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);

    private async Task StartCaptureAsync(long requestId)
    {
        if (Volatile.Read(ref _shutdown) != 0)
            return;

        lock (_stateSync)
        {
            if (_activeRequestId != 0)
            {
                _protocol.Write(new SttEvent(
                    SttMessageTypes.Error,
                    requestId,
                    Error: "The STT worker is already handling another utterance."));
                return;
            }
            _activeRequestId = requestId;
        }

        var capture = new MicrophoneCapture(
            _diagnostics.WorkDirectory,
            requestId,
            _options.MaximumCaptureSeconds,
            _options.DeviceSelector,
            _diagnostics);
        capture.MaximumDurationReached += () => BeginTranscription(requestId);
        lock (_stateSync)
            _capture = capture;

        try
        {
            capture.Start();
            _diagnostics.Log($"request={requestId} recording state entered");
            _protocol.Write(new SttEvent(SttMessageTypes.Recording, requestId));
        }
        catch (Exception exception)
        {
            lock (_stateSync)
            {
                if (ReferenceEquals(_capture, capture))
                {
                    _capture = null;
                    _activeRequestId = 0;
                }
            }
            await capture.DisposeAsync().ConfigureAwait(false);
            _diagnostics.Log($"request={requestId} recording failed: {exception}");
            _protocol.Write(new SttEvent(SttMessageTypes.Error, requestId, Error: exception.Message));
        }
    }

    private void BeginTranscription(long requestId)
    {
        MicrophoneCapture? capture;
        CancellationTokenSource cancellation;
        lock (_stateSync)
        {
            if (_activeRequestId != requestId || _capture is null || _transcriptionTask is not null)
                return;

            capture = _capture;
            _capture = null;
            cancellation = new CancellationTokenSource();
            _transcriptionCancellation = cancellation;
            _transcriptionTask = Task.Run(
                () => TranscribeAsync(requestId, capture, cancellation),
                CancellationToken.None);
        }
    }

    private async Task TranscribeAsync(
        long requestId,
        MicrophoneCapture capture,
        CancellationTokenSource cancellation)
    {
        var stopwatch = Stopwatch.StartNew();
        string? audioPath = null;
        try
        {
            _protocol.Write(new SttEvent(SttMessageTypes.Transcribing, requestId));
            audioPath = await capture.StopAndNormalizeAsync(cancellation.Token).ConfigureAwait(false);
            var transcript = await _transcriber.TranscribeAsync(audioPath, requestId, cancellation.Token)
                .ConfigureAwait(false);
            _diagnostics.WriteJson(
                $"request-{requestId}-result.json",
                new
                {
                    requestId,
                    transcript,
                    elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    language = _options.Language,
                    deviceSelector = _options.DeviceSelector,
                });
            _protocol.Write(new SttEvent(
                SttMessageTypes.Result,
                requestId,
                Text: transcript,
                ElapsedMilliseconds: stopwatch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _diagnostics.Log($"request={requestId} transcription cancelled");
            _protocol.Write(new SttEvent(SttMessageTypes.Cancelled, requestId));
        }
        catch (Exception exception)
        {
            _diagnostics.Log(
                $"request={requestId} transcription failed elapsedMs={stopwatch.ElapsedMilliseconds}: {exception}");
            _protocol.Write(new SttEvent(
                SttMessageTypes.Error,
                requestId,
                Error: exception.Message,
                ElapsedMilliseconds: stopwatch.ElapsedMilliseconds));
        }
        finally
        {
            if (audioPath is not null && !_diagnostics.PreserveArtifacts)
                TryDelete(audioPath);
            await capture.DisposeAsync().ConfigureAwait(false);
            lock (_stateSync)
            {
                if (_activeRequestId == requestId)
                {
                    _activeRequestId = 0;
                    _transcriptionTask = null;
                    _transcriptionCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task CancelAsync(long requestId)
    {
        MicrophoneCapture? capture = null;
        CancellationTokenSource? cancellation = null;
        lock (_stateSync)
        {
            if (_activeRequestId != requestId)
                return;

            if (_capture is not null)
            {
                capture = _capture;
                _capture = null;
                _activeRequestId = 0;
            }
            else
            {
                cancellation = _transcriptionCancellation;
            }
        }

        if (capture is not null)
        {
            await capture.CancelAsync().ConfigureAwait(false);
            await capture.DisposeAsync().ConfigureAwait(false);
            _protocol.Write(new SttEvent(SttMessageTypes.Cancelled, requestId));
        }
        else
        {
            cancellation?.Cancel();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
