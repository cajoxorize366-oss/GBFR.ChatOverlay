using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Stt;

public enum VoiceRecognitionState
{
    Unavailable,
    Idle,
    Recording,
    Transcribing,
    Review,
}

public readonly record struct VoiceDrainResult(bool DraftChanged, bool StateChanged);

/// <summary>
/// Keeps worker/process messages off the render thread until they can be applied safely to the composer.
/// Public state transitions are expected to be called from the game's input/render thread.
/// </summary>
public sealed class VoiceInputCoordinator : IDisposable
{
    private readonly ChatComposer _composer;
    private readonly ISttWorkerClient _worker;
    private long _nextRequestId;
    private long _activeRequestId;
    private int _state;

    public VoiceInputCoordinator(ChatComposer composer, ISttWorkerClient worker)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        State = worker.IsAvailable ? VoiceRecognitionState.Idle : VoiceRecognitionState.Unavailable;
        LastError = worker.UnavailableReason;
    }

    public VoiceRecognitionState State
    {
        get => (VoiceRecognitionState)Volatile.Read(ref _state);
        private set => Volatile.Write(ref _state, (int)value);
    }
    public string? LastError { get; private set; }
    public long ActiveRequestId => _activeRequestId;

    public string StatusText => State switch
    {
        VoiceRecognitionState.Unavailable => LastError ?? "Voice input is unavailable.",
        VoiceRecognitionState.Recording => "Recording... release the push-to-talk buttons to transcribe.",
        VoiceRecognitionState.Transcribing => "Transcribing locally with Whisper base...",
        VoiceRecognitionState.Review when !string.IsNullOrWhiteSpace(LastError) => LastError!,
        VoiceRecognitionState.Review => "Voice transcript ready. Review it, then press Enter to send.",
        _ => "Voice input ready (hold U or LB + R3).",
    };

    public bool TryBeginCapture()
    {
        RefreshIdleState();
        if (State is not VoiceRecognitionState.Idle || !_worker.IsAvailable)
            return false;
        if (!_composer.BeginVoiceCapture())
            return false;

        var requestId = Interlocked.Increment(ref _nextRequestId);
        if (!_worker.TrySend(new SttCommand(SttMessageTypes.Start, requestId)))
        {
            _composer.Cancel();
            State = VoiceRecognitionState.Unavailable;
            LastError = _worker.UnavailableReason ?? "The STT worker did not accept the recording request.";
            return false;
        }

        _activeRequestId = requestId;
        State = VoiceRecognitionState.Recording;
        LastError = null;
        return true;
    }

    public bool TryEndCapture()
    {
        if (State is not VoiceRecognitionState.Recording || _activeRequestId <= 0)
            return false;

        if (!_worker.TrySend(new SttCommand(SttMessageTypes.Stop, _activeRequestId)))
        {
            FailActiveRequest(_worker.UnavailableReason ?? "The STT worker did not accept the stop request.");
            return false;
        }

        State = VoiceRecognitionState.Transcribing;
        return true;
    }

    public void CancelCapture()
    {
        if (_activeRequestId > 0)
            _worker.TrySend(new SttCommand(SttMessageTypes.Cancel, _activeRequestId));

        _activeRequestId = 0;
        _composer.Cancel();
        State = _worker.IsAvailable ? VoiceRecognitionState.Idle : VoiceRecognitionState.Unavailable;
        LastError = _worker.IsAvailable ? null : _worker.UnavailableReason;
    }

    public VoiceDrainResult Drain()
    {
        var draftChanged = false;
        var stateChanged = false;

        while (_worker.TryRead(out var message))
        {
            if (message.Type is SttMessageTypes.Ready)
                continue;

            if (message.RequestId != 0 && message.RequestId != _activeRequestId)
                continue;

            switch (message.Type)
            {
                case SttMessageTypes.Recording:
                    stateChanged |= State is not VoiceRecognitionState.Recording;
                    State = VoiceRecognitionState.Recording;
                    break;

                case SttMessageTypes.Transcribing:
                    stateChanged |= State is not VoiceRecognitionState.Transcribing;
                    State = VoiceRecognitionState.Transcribing;
                    break;

                case SttMessageTypes.Result:
                    _composer.CompleteVoiceCapture(message.Text);
                    _activeRequestId = 0;
                    State = VoiceRecognitionState.Review;
                    LastError = string.IsNullOrWhiteSpace(_composer.Draft)
                        ? "No speech was recognized. Press Escape and try again."
                        : null;
                    draftChanged = true;
                    stateChanged = true;
                    break;

                case SttMessageTypes.Error:
                    var workerError = message.Error ?? "The STT worker reported an unknown error.";
                    if (message.RequestId == 0 && _activeRequestId <= 0)
                    {
                        if (State is VoiceRecognitionState.Idle)
                        {
                            State = VoiceRecognitionState.Unavailable;
                            LastError = workerError;
                            stateChanged = true;
                        }
                        break;
                    }

                    FailActiveRequest(workerError);
                    draftChanged = true;
                    stateChanged = true;
                    break;

                case SttMessageTypes.Cancelled:
                    _activeRequestId = 0;
                    _composer.Cancel();
                    State = _worker.IsAvailable ? VoiceRecognitionState.Idle : VoiceRecognitionState.Unavailable;
                    LastError = null;
                    stateChanged = true;
                    break;
            }
        }

        stateChanged |= RefreshIdleState();
        return new VoiceDrainResult(draftChanged, stateChanged);
    }

    public void Dispose() => _worker.Dispose();

    private bool RefreshIdleState()
    {
        if (State is VoiceRecognitionState.Review && !_composer.IsOpen)
        {
            State = _worker.IsAvailable ? VoiceRecognitionState.Idle : VoiceRecognitionState.Unavailable;
            LastError = _worker.IsAvailable ? null : _worker.UnavailableReason;
            return true;
        }

        return false;
    }

    private void FailActiveRequest(string error)
    {
        if (_composer.Mode is ChatInputMode.VoiceRecording)
            _composer.CompleteVoiceCapture(null);
        else
        {
            _composer.Cancel();
            _composer.BeginVoiceCapture();
            _composer.CompleteVoiceCapture(null);
        }

        _activeRequestId = 0;
        State = VoiceRecognitionState.Review;
        LastError = error;
    }
}
