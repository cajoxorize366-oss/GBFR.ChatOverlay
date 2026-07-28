using System.Runtime.InteropServices;
using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class DirectInputBrokerTests
{
    [Fact]
    public void Snapshot_ManagedLayoutMatchesNativeAbi()
    {
        Assert.Equal(
            DirectInputBrokerSnapshot.ExpectedStructSize,
            (uint)Marshal.SizeOf<DirectInputBrokerSnapshot>());
    }

    [Fact]
    public void Snapshot_NativeAndManagedAbiAgreeBeforeInstallation()
    {
        DxgiPresentBridge.Configure(AppContext.BaseDirectory);

        Assert.True(DirectInputBrokerBridge.Instance.TryGetSnapshot(out var snapshot));
        Assert.True(snapshot.HasExpectedLayout);
        Assert.Equal(DirectInputBrokerReadiness.None, snapshot.Readiness);
        Assert.Equal(DirectInputBrokerPolicy.None, snapshot.Policy);
    }

    [Fact]
    public void Poll_HiddenOverlayLeavesFullKeyboardAndMouseCaptureDisabled()
    {
        var backend = new FakeDirectInputBrokerBackend();
        using var hook = CreateHook(
            backend,
            canActivate: () => true,
            shouldCapture: () => false,
            shouldCaptureMouse: () => false,
            settingsAvailable: () => true);

        hook.Initialize();
        hook.Poll();

        Assert.True(backend.InstallCalled);
        Assert.Equal(new[] { true }, backend.ActiveRequests);
        Assert.Equal(
            DirectInputBrokerPolicy.SuppressActivation |
            DirectInputBrokerPolicy.SuppressSettings,
            Assert.Single(backend.PolicyRequests));
    }

    [Fact]
    public void Poll_CombinesHostAndGuestCaptureIntoOneNativePolicy()
    {
        var backend = new FakeDirectInputBrokerBackend();
        using var hook = CreateHook(
            backend,
            canActivate: () => false,
            shouldCapture: () => true,
            shouldCaptureMouse: () => true,
            voiceEnabled: () => true,
            settingsAvailable: () => true);

        hook.Initialize();
        hook.Poll();

        Assert.Equal(
            DirectInputBrokerPolicy.CaptureKeyboard |
            DirectInputBrokerPolicy.CaptureMouse |
            DirectInputBrokerPolicy.SuppressSettings |
            DirectInputBrokerPolicy.SuppressPushToTalk,
            Assert.Single(backend.PolicyRequests));
    }

    [Fact]
    public void Poll_ProcessesOnlyChangedNativeSnapshots()
    {
        var backend = new FakeDirectInputBrokerBackend();
        var activations = 0;
        var settings = new List<bool>();
        var voice = new List<bool>();
        using var hook = CreateHook(
            backend,
            canActivate: () => true,
            tryActivate: () =>
            {
                activations++;
                return true;
            },
            voiceEnabled: () => true,
            settingsAvailable: () => true,
            setVoicePressed: voice.Add,
            reportSettings: settings.Add);

        hook.Initialize();
        backend.Snapshot = CreateSnapshot(
            sequence: 10,
            DirectInputBrokerKeys.Activation |
            DirectInputBrokerKeys.Settings |
            DirectInputBrokerKeys.PushToTalk);
        hook.Poll();
        hook.Poll();

        Assert.Equal(1, activations);
        Assert.Equal(new[] { true }, settings);
        Assert.Equal(new[] { true }, voice);

        backend.Snapshot = CreateSnapshot(11, DirectInputBrokerKeys.None);
        hook.Poll();

        Assert.Equal(new[] { true, false }, settings);
        Assert.Equal(new[] { true, false }, voice);
    }

    [Fact]
    public void SuspendAndResume_ReleaseAndRestoreBrokerOwnership()
    {
        var backend = new FakeDirectInputBrokerBackend();
        using var hook = CreateHook(backend);

        hook.Initialize();
        hook.Poll();
        hook.Suspend();
        var snapshotsBeforeSuspendedPoll = backend.SnapshotReads;
        hook.Poll();
        hook.Resume();
        hook.Poll();

        Assert.Equal(new[] { true, false, true }, backend.ActiveRequests);
        Assert.Contains(DirectInputBrokerPolicy.None, backend.PolicyRequests);
        Assert.Equal(snapshotsBeforeSuspendedPoll + 1, backend.SnapshotReads);
    }

    [Fact]
    public async Task Suspend_WaitsForInFlightPollBeforeReleasingNativePolicy()
    {
        var backend = new FakeDirectInputBrokerBackend
        {
            PolicyEntered = new ManualResetEventSlim(false),
            ReleasePolicy = new ManualResetEventSlim(false),
        };
        using var hook = CreateHook(backend, shouldCapture: () => true);
        hook.Initialize();

        var poll = Task.Run(hook.Poll);
        Assert.True(backend.PolicyEntered.Wait(TimeSpan.FromSeconds(2)));
        var suspend = Task.Run(hook.Suspend);

        Assert.False(backend.InactiveRequested.Wait(TimeSpan.FromMilliseconds(100)));
        backend.ReleasePolicy.Set();
        await Task.WhenAll(poll, suspend);

        Assert.True(backend.InactiveRequested.IsSet);
        Assert.False(backend.ActiveRequests[^1]);
        Assert.Equal(DirectInputBrokerPolicy.None, backend.PolicyRequests[^1]);
    }

    [Fact]
    public void PollFailure_ReleasesKeyboardAndMouseFailOpen()
    {
        var backend = new FakeDirectInputBrokerBackend
        {
            SnapshotSucceeds = false,
        };
        var logs = new List<string>();
        using var hook = CreateHook(
            backend,
            shouldCapture: () => true,
            shouldCaptureMouse: () => true,
            log: logs.Add);

        hook.Initialize();
        hook.Poll();
        hook.Poll();

        Assert.False(backend.ActiveRequests[^1]);
        Assert.Equal(DirectInputBrokerPolicy.None, backend.PolicyRequests[^1]);
        Assert.Single(logs, message => message.Contains("released fail-open"));
    }

    private static DirectInputKeyboardHook CreateHook(
        IDirectInputBrokerBackend backend,
        Func<bool>? canActivate = null,
        Func<bool>? tryActivate = null,
        Func<bool>? shouldCapture = null,
        Func<bool>? shouldCaptureMouse = null,
        Func<bool>? voiceEnabled = null,
        Func<bool>? settingsAvailable = null,
        Action<bool>? setVoicePressed = null,
        Action<bool>? reportSettings = null,
        Action<string>? log = null) =>
        new(
            backend,
            canActivate ?? (() => false),
            tryActivate ?? (() => false),
            shouldCapture ?? (() => false),
            shouldCaptureMouse ?? (() => false),
            voiceEnabled ?? (() => false),
            setVoicePressed ?? (_ => { }),
            () => { },
            settingsAvailable ?? (() => false),
            reportSettings ?? (_ => { }),
            _ => { },
            log ?? (_ => { }));

    private static DirectInputBrokerSnapshot CreateSnapshot(
        ulong sequence,
        DirectInputBrokerKeys keys) =>
        new()
        {
            AbiVersion = DirectInputBrokerSnapshot.ExpectedAbiVersion,
            StructSize = DirectInputBrokerSnapshot.ExpectedStructSize,
            Sequence = sequence,
            Keys = keys,
            Readiness = DirectInputBrokerReadiness.GameImport |
                        DirectInputBrokerReadiness.Factory |
                        DirectInputBrokerReadiness.Keyboard |
                        DirectInputBrokerReadiness.Mouse,
            Active = 1,
        };

    private sealed class FakeDirectInputBrokerBackend : IDirectInputBrokerBackend
    {
        internal bool InstallCalled { get; private set; }
        internal bool SnapshotSucceeds { get; init; } = true;
        internal int SnapshotReads { get; private set; }
        internal List<bool> ActiveRequests { get; } = new();
        internal List<DirectInputBrokerPolicy> PolicyRequests { get; } = new();
        internal ManualResetEventSlim InactiveRequested { get; } = new(false);
        internal ManualResetEventSlim? PolicyEntered { get; init; }
        internal ManualResetEventSlim? ReleasePolicy { get; init; }
        internal DirectInputBrokerSnapshot Snapshot { get; set; } =
            CreateSnapshot(0, DirectInputBrokerKeys.None);

        public bool Install()
        {
            InstallCalled = true;
            return true;
        }

        public bool SetActive(bool active)
        {
            ActiveRequests.Add(active);
            if (!active)
                InactiveRequested.Set();
            return true;
        }

        public bool SetPolicy(DirectInputBrokerPolicy policy)
        {
            PolicyRequests.Add(policy);
            PolicyEntered?.Set();
            ReleasePolicy?.Wait(TimeSpan.FromSeconds(2));
            return true;
        }

        public bool TryGetSnapshot(out DirectInputBrokerSnapshot snapshot)
        {
            SnapshotReads++;
            snapshot = Snapshot;
            return SnapshotSucceeds;
        }
    }
}
