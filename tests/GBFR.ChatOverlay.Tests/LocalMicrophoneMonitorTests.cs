using GBFR.ChatOverlay.Audio;

namespace GBFR.ChatOverlay.Tests;

public sealed class LocalMicrophoneMonitorTests
{
    private static readonly ResolvedAudioEndpointSelection Input =
        new(false, "mic-id", "Test microphone", false);
    private static readonly ResolvedAudioEndpointSelection Output =
        new(false, "speaker-id", "Test speakers", false);

    [Fact]
    public void Hold_StartsLocalPath_DetectsSignal_AndStopsOnRelease()
    {
        var backend = new FakeBackend();
        var factory = new FakeFactory(backend);
        var logs = new List<string>();
        using var monitor = CreateMonitor(factory, logs);

        monitor.SetPressed(true);

        Assert.Equal(LocalMicrophoneMonitorState.Monitoring, monitor.State);
        Assert.True(backend.Started);

        backend.ReportPeak(0.2f);

        Assert.Equal(LocalMicrophoneMonitorState.SignalDetected, monitor.State);

        monitor.SetPressed(false);

        Assert.Equal(LocalMicrophoneMonitorState.Idle, monitor.State);
        Assert.True(backend.Silenced);
        Assert.True(backend.Stopped);
        Assert.True(backend.Disposed);
        Assert.Contains(logs, line =>
            line.Contains("playback was gated off", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("result: PASS", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleaseWithoutSignal_ReportsNoObservedInput()
    {
        var backend = new FakeBackend();
        var logs = new List<string>();
        using var monitor = CreateMonitor(new FakeFactory(backend), logs);

        monitor.SetPressed(true);
        monitor.SetPressed(false);

        Assert.Contains(logs, line =>
            line.Contains("no microphone signal", StringComparison.Ordinal));
    }

    [Fact]
    public void SecondHold_StartsANewBackendAfterTheFirstRelease()
    {
        var first = new FakeBackend();
        var second = new FakeBackend();
        var factory = new FakeFactory(first, second);
        using var monitor = CreateMonitor(factory, []);

        monitor.SetPressed(true);
        monitor.SetPressed(false);
        monitor.SetPressed(true);

        Assert.Equal(2, factory.CreateCount);
        Assert.True(first.Silenced);
        Assert.True(first.Stopped);
        Assert.True(first.Disposed);
        Assert.True(second.Started);
        Assert.Equal(LocalMicrophoneMonitorState.Monitoring, monitor.State);
    }

    [Fact]
    public void EndpointFault_StopsAndDisposesTheLocalPath()
    {
        var backend = new FakeBackend();
        var logs = new List<string>();
        using var monitor = CreateMonitor(new FakeFactory(backend), logs);

        monitor.SetPressed(true);
        backend.ReportFault(new InvalidOperationException("device removed"));

        Assert.Equal(LocalMicrophoneMonitorState.Faulted, monitor.State);
        Assert.True(backend.Stopped);
        Assert.True(backend.Disposed);
        Assert.Contains(logs, line => line.Contains("failed closed", StringComparison.Ordinal));
    }

    [Fact]
    public void PhysicalReleaseClearsAReportedFailureForTheNextUiState()
    {
        var backend = new FakeBackend();
        using var monitor = CreateMonitor(new FakeFactory(backend), []);

        monitor.SetPressed(true);
        backend.ReportFault(new InvalidOperationException("device removed"));
        Assert.Equal(LocalMicrophoneMonitorState.Faulted, monitor.State);

        monitor.SetPressed(false);

        Assert.Equal(LocalMicrophoneMonitorState.Idle, monitor.State);
    }

    [Fact]
    public void ReleaseBeforeQueuedStart_CannotOpenAudioLater()
    {
        var queue = new Queue<Action>();
        var factory = new FakeFactory(new FakeBackend());
        using var monitor = new LocalMicrophoneMonitor(
            factory,
            Input,
            Output,
            0.35f,
            _ => { },
            queue.Enqueue);

        monitor.SetPressed(true);
        monitor.SetPressed(false);

        Assert.Single(queue);
        queue.Dequeue().Invoke();

        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(LocalMicrophoneMonitorState.Idle, monitor.State);
    }

    [Fact]
    public async Task ReleaseWhileEndpointStartIsInFlight_GatesAndPreventsResurrection()
    {
        using var startEntered = new ManualResetEventSlim();
        using var allowStartToReturn = new ManualResetEventSlim();
        var backend = new FakeBackend
        {
            BeforeStartReturns = () =>
            {
                startEntered.Set();
                Assert.True(allowStartToReturn.Wait(TimeSpan.FromSeconds(3)));
            },
        };
        var factory = new FakeFactory(backend);
        using var monitor = new LocalMicrophoneMonitor(
            factory,
            Input,
            Output,
            0.35f,
            _ => { },
            action => { _ = Task.Run(action); });

        monitor.SetPressed(true);
        Assert.True(startEntered.Wait(TimeSpan.FromSeconds(3)));

        monitor.SetPressed(false);

        Assert.True(backend.Silenced);
        Assert.Equal(LocalMicrophoneMonitorState.Idle, monitor.State);
        allowStartToReturn.Set();
        Assert.True(SpinWait.SpinUntil(
            () => backend.Stopped && backend.Disposed,
            TimeSpan.FromSeconds(3)));
        await Task.Yield();
        Assert.Equal(LocalMicrophoneMonitorState.Idle, monitor.State);
    }

    [Fact]
    public void Suspend_ForcesAnActiveMonitorOffUntilResume()
    {
        var first = new FakeBackend();
        var second = new FakeBackend();
        var factory = new FakeFactory(first, second);
        using var monitor = CreateMonitor(factory, []);

        monitor.SetPressed(true);
        monitor.Suspend();

        Assert.Equal(LocalMicrophoneMonitorState.Suspended, monitor.State);
        Assert.True(first.Stopped);
        Assert.False(monitor.IsAvailable);

        monitor.Resume();
        monitor.SetPressed(true);

        Assert.Equal(LocalMicrophoneMonitorState.Monitoring, monitor.State);
        Assert.True(second.Started);
    }

    private static LocalMicrophoneMonitor CreateMonitor(
        ILocalAudioMonitorBackendFactory factory,
        List<string> logs) =>
        new(factory, Input, Output, 0.35f, logs.Add, action => action());

    private sealed class FakeFactory(params FakeBackend[] backends) : ILocalAudioMonitorBackendFactory
    {
        private readonly Queue<FakeBackend> _backends = new(backends);

        public int CreateCount { get; private set; }

        public ILocalAudioMonitorBackend Create(
            ResolvedAudioEndpointSelection inputSelection,
            ResolvedAudioEndpointSelection outputSelection,
            float volume)
        {
            CreateCount++;
            Assert.Equal(Input, inputSelection);
            Assert.Equal(Output, outputSelection);
            Assert.Equal(0.35f, volume);
            return _backends.Dequeue();
        }
    }

    private sealed class FakeBackend : ILocalAudioMonitorBackend
    {
        public event Action<float>? PeakLevelChanged;

        public event Action<Exception>? Faulted;

        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public bool Silenced { get; private set; }

        public bool Disposed { get; private set; }

        public Action? BeforeStartReturns { get; init; }

        public void Start()
        {
            Started = true;
            BeforeStartReturns?.Invoke();
        }

        public void SilenceImmediately() => Silenced = true;

        public void Stop() => Stopped = true;

        public void Dispose() => Disposed = true;

        public void ReportPeak(float peak) => PeakLevelChanged?.Invoke(peak);

        public void ReportFault(Exception exception) => Faulted?.Invoke(exception);
    }
}
