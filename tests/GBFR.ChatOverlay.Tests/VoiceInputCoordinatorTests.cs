using System.Collections.Concurrent;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Stt;

namespace GBFR.ChatOverlay.Tests;

public sealed class VoiceInputCoordinatorTests
{
    [Fact]
    public void Capture_TransitionsFromRecordingToReview()
    {
        var composer = new ChatComposer();
        using var worker = new FakeWorker();
        using var coordinator = new VoiceInputCoordinator(composer, worker);

        Assert.True(coordinator.TryBeginCapture());
        var requestId = coordinator.ActiveRequestId;
        Assert.Equal(new SttCommand(SttMessageTypes.Start, requestId), worker.Sent.Single());
        Assert.True(coordinator.TryEndCapture());
        worker.Enqueue(new SttEvent(SttMessageTypes.Result, requestId, Text: "一起出发"));

        var drained = coordinator.Drain();

        Assert.True(drained.DraftChanged);
        Assert.Equal(VoiceRecognitionState.Review, coordinator.State);
        Assert.Equal(ChatInputMode.VoiceReview, composer.Mode);
        Assert.Equal("一起出发", composer.Draft);
    }

    [Fact]
    public void StaleResult_DoesNotReplaceCurrentDraft()
    {
        var composer = new ChatComposer();
        using var worker = new FakeWorker();
        using var coordinator = new VoiceInputCoordinator(composer, worker);
        coordinator.TryBeginCapture();
        var activeRequestId = coordinator.ActiveRequestId;
        worker.Enqueue(new SttEvent(SttMessageTypes.Result, activeRequestId + 1, Text: "stale"));

        var drained = coordinator.Drain();

        Assert.False(drained.DraftChanged);
        Assert.Equal(ChatInputMode.VoiceRecording, composer.Mode);
        Assert.Empty(composer.Draft);
    }

    [Fact]
    public void WorkerError_LeavesReviewOpenWithActionableStatus()
    {
        var composer = new ChatComposer();
        using var worker = new FakeWorker();
        using var coordinator = new VoiceInputCoordinator(composer, worker);
        coordinator.TryBeginCapture();
        var requestId = coordinator.ActiveRequestId;
        worker.Enqueue(new SttEvent(SttMessageTypes.Error, requestId, Error: "Microphone unavailable"));

        coordinator.Drain();

        Assert.Equal(VoiceRecognitionState.Review, coordinator.State);
        Assert.Equal(ChatInputMode.VoiceReview, composer.Mode);
        Assert.Contains("Microphone unavailable", coordinator.StatusText);
    }

    [Fact]
    public void Cancel_SendsCorrelatedCommandAndClosesComposer()
    {
        var composer = new ChatComposer();
        using var worker = new FakeWorker();
        using var coordinator = new VoiceInputCoordinator(composer, worker);
        coordinator.TryBeginCapture();
        var requestId = coordinator.ActiveRequestId;

        coordinator.CancelCapture();

        Assert.Equal(new SttCommand(SttMessageTypes.Cancel, requestId), worker.Sent.Last());
        Assert.Equal(VoiceRecognitionState.Idle, coordinator.State);
        Assert.Equal(ChatInputMode.Closed, composer.Mode);
    }

    [Fact]
    public void UnavailableWorker_RejectsCaptureWithoutOpeningComposer()
    {
        var composer = new ChatComposer();
        using var worker = new FakeWorker(isAvailable: false, unavailableReason: "runtime missing");
        using var coordinator = new VoiceInputCoordinator(composer, worker);

        Assert.False(coordinator.TryBeginCapture());
        Assert.Equal(VoiceRecognitionState.Unavailable, coordinator.State);
        Assert.Equal(ChatInputMode.Closed, composer.Mode);
        Assert.Contains("runtime missing", coordinator.StatusText);
    }

    [Fact]
    public void GlobalWorkerFailureWhileIdle_DoesNotOpenOrReplaceComposer()
    {
        var composer = new ChatComposer();
        composer.SetDraft("keep me");
        using var worker = new FakeWorker();
        using var coordinator = new VoiceInputCoordinator(composer, worker);
        worker.Enqueue(new SttEvent(SttMessageTypes.Error, Error: "worker exited"));

        coordinator.Drain();

        Assert.Equal(VoiceRecognitionState.Unavailable, coordinator.State);
        Assert.Equal(ChatInputMode.Closed, composer.Mode);
        Assert.Equal("keep me", composer.Draft);
    }

    [Fact]
    public void GlobalWorkerFailureAfterResult_PreservesReviewAndTranscript()
    {
        var composer = new ChatComposer();
        using var worker = new FakeWorker();
        using var coordinator = new VoiceInputCoordinator(composer, worker);
        coordinator.TryBeginCapture();
        var requestId = coordinator.ActiveRequestId;
        worker.Enqueue(new SttEvent(SttMessageTypes.Result, requestId, Text: "ready draft"));
        worker.Enqueue(new SttEvent(SttMessageTypes.Error, Error: "worker exited"));

        coordinator.Drain();

        Assert.Equal(VoiceRecognitionState.Review, coordinator.State);
        Assert.Equal(ChatInputMode.VoiceReview, composer.Mode);
        Assert.Equal("ready draft", composer.Draft);
    }

    private sealed class FakeWorker : ISttWorkerClient
    {
        private readonly ConcurrentQueue<SttEvent> _events = new();

        public FakeWorker(bool isAvailable = true, string? unavailableReason = null)
        {
            IsAvailable = isAvailable;
            UnavailableReason = unavailableReason;
        }

        public bool IsAvailable { get; }
        public string? UnavailableReason { get; }
        public List<SttCommand> Sent { get; } = new();

        public bool TrySend(SttCommand command)
        {
            if (!IsAvailable)
                return false;
            Sent.Add(command);
            return true;
        }

        public bool TryRead(out SttEvent message) => _events.TryDequeue(out message!);
        public void Enqueue(SttEvent message) => _events.Enqueue(message);
        public void Dispose() { }
    }
}
