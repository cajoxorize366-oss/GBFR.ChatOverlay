using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyAudioWorkPumpTests
{
    [Fact]
    public void AutomaticAudioModeLeavesPartyAsTheSoleOwner()
    {
        var api = new FakePartyAudioWorkApi
        {
            AudioMode = PartyWorkMode.Automatic,
        };
        var logs = new List<string>();
        var failures = new List<string>();
        using var pump = new PartyAudioWorkPump(api, logs.Add, failures.Add, TimeSpan.FromMilliseconds(5));

        pump.AttachManager((nint)0x1234, "test");

        Assert.Equal(0, api.DoWorkCallCount);
        Assert.Empty(failures);
        Assert.Contains(logs, line => line.Contains("Audio=Automatic (0)", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("sole owner", StringComparison.Ordinal));
    }

    [Fact]
    public void ManualAudioModePumpsOnlyTheAudioTaskAndStopsSynchronously()
    {
        var api = new FakePartyAudioWorkApi
        {
            AudioMode = PartyWorkMode.Manual,
        };
        var logs = new List<string>();
        var failures = new List<string>();
        using var pump = new PartyAudioWorkPump(api, logs.Add, failures.Add, TimeSpan.FromMilliseconds(5));

        pump.AttachManager((nint)0x5678, "test");
        Assert.True(api.WorkObserved.Wait(TimeSpan.FromSeconds(1)));
        pump.DetachManager((nint)0x5678, "test cleanup");
        var callsAfterDetach = api.DoWorkCallCount;

        Assert.True(callsAfterDetach > 0);
        Assert.All(api.ThreadIds, threadId => Assert.Equal(PartyThreadId.Audio, threadId));
        Assert.Equal(callsAfterDetach, api.DoWorkCallCount);
        Assert.Empty(failures);
        Assert.Contains(logs, line => line.Contains("Audio=Manual (1)", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("5 ms intervals", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("pump stopped", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkModeQueryFailureKeepsVoiceFailClosed()
    {
        var api = new FakePartyAudioWorkApi
        {
            AudioModeResult = 0x10DF,
        };
        var failures = new List<string>();
        using var pump = new PartyAudioWorkPump(api, _ => { }, failures.Add, TimeSpan.FromMilliseconds(5));

        pump.AttachManager((nint)0x1234, "test");

        Assert.Equal(0, api.DoWorkCallCount);
        var failure = Assert.Single(failures);
        Assert.Contains("PartyGetWorkMode(Audio) returned 0x000010DF", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void NewManagerCanStartAfterFailedManagerIsDetached()
    {
        var api = new FakePartyAudioWorkApi
        {
            AudioMode = PartyWorkMode.Manual,
            AudioModeResults = new Queue<uint>([0x10DF, 0]),
        };
        var failures = new List<string>();
        using var pump = new PartyAudioWorkPump(api, _ => { }, failures.Add, TimeSpan.FromMilliseconds(5));

        pump.AttachManager((nint)0x1234, "first manager");
        pump.DetachManager((nint)0x1234, "first manager cleanup");
        pump.AttachManager((nint)0x5678, "replacement manager");

        Assert.True(api.WorkObserved.Wait(TimeSpan.FromSeconds(1)));
        Assert.Single(failures);
        Assert.True(api.DoWorkCallCount > 0);
    }

    [Fact]
    public void NativeDoWorkFailureStopsThePumpAndFailsClosedOnce()
    {
        var api = new FakePartyAudioWorkApi
        {
            AudioMode = PartyWorkMode.Manual,
            DoWorkResult = 0x10D8,
        };
        var failures = new List<string>();
        using var failureObserved = new ManualResetEventSlim();
        using var pump = new PartyAudioWorkPump(
            api,
            _ => { },
            message =>
            {
                failures.Add(message);
                failureObserved.Set();
            },
            TimeSpan.FromMilliseconds(5));

        pump.AttachManager((nint)0x1234, "test");

        Assert.True(failureObserved.Wait(TimeSpan.FromSeconds(1)));
        var failure = Assert.Single(failures);
        Assert.Contains("PartyDoWork(Audio) returned 0x000010D8", failure, StringComparison.Ordinal);
        Assert.Equal(1, api.DoWorkCallCount);
    }

    [Fact]
    public async Task DetachWaitsForAnInFlightAudioWorkCallBeforeReturning()
    {
        using var workEntered = new ManualResetEventSlim();
        using var releaseWork = new ManualResetEventSlim();
        var api = new FakePartyAudioWorkApi
        {
            AudioMode = PartyWorkMode.Manual,
            DoWorkAction = () =>
            {
                workEntered.Set();
                Assert.True(releaseWork.Wait(TimeSpan.FromSeconds(3)));
            },
        };
        using var pump = new PartyAudioWorkPump(api, _ => { }, _ => { }, TimeSpan.FromMilliseconds(5));
        pump.AttachManager((nint)0x1234, "test");
        Assert.True(workEntered.Wait(TimeSpan.FromSeconds(1)));

        var detach = Task.Run(() => pump.DetachManager((nint)0x1234, "test cleanup"));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(detach.IsCompleted);
        releaseWork.Set();

        await detach.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class FakePartyAudioWorkApi : IPartyAudioWorkApi
    {
        private readonly object _threadIdSync = new();
        private readonly List<PartyThreadId> _threadIds = [];
        private int _doWorkCallCount;

        internal PartyWorkMode AudioMode { get; init; } = PartyWorkMode.Automatic;

        internal uint AudioModeResult { get; init; }

        internal Queue<uint>? AudioModeResults { get; init; }

        internal uint DoWorkResult { get; init; }

        internal Action? DoWorkAction { get; init; }

        internal ManualResetEventSlim WorkObserved { get; } = new();

        internal int DoWorkCallCount => Volatile.Read(ref _doWorkCallCount);

        internal PartyThreadId[] ThreadIds
        {
            get
            {
                lock (_threadIdSync)
                    return [.. _threadIds];
            }
        }

        public uint GetWorkMode(PartyThreadId threadId, out PartyWorkMode workMode)
        {
            workMode = threadId == PartyThreadId.Audio
                ? AudioMode
                : PartyWorkMode.Automatic;
            if (threadId != PartyThreadId.Audio)
                return 0;

            return AudioModeResults is { Count: > 0 }
                ? AudioModeResults.Dequeue()
                : AudioModeResult;
        }

        public uint DoWork(nint manager, PartyThreadId threadId)
        {
            Assert.NotEqual(nint.Zero, manager);
            lock (_threadIdSync)
                _threadIds.Add(threadId);
            Interlocked.Increment(ref _doWorkCallCount);
            WorkObserved.Set();
            DoWorkAction?.Invoke();
            return DoWorkResult;
        }
    }
}
