using System.Runtime.InteropServices;
using GBFR.ChatOverlay.Configuration;
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
        Assert.Equal(
            GBFR.OverlayHub.Contracts.OverlayInputDevices.None,
            DirectInputBrokerBridge.Instance.GetEffectiveInputDevices());
    }

    [Fact]
    public void NativeHotkeyBindingAbi_AcceptsValidatedBindingsBeforeInstallation()
    {
        DxgiPresentBridge.Configure(AppContext.BaseDirectory);
        var binding = new DirectInputHotkeyBinding(
            DirectInputKeyboardStateFilter.SettingsMenuScanCode,
            KeyboardModifiers.Control,
            (byte)DirectInputBrokerPolicy.SuppressSettings);

        Assert.True(DirectInputBrokerBridge.Instance.SetHotkeyBindings([binding]));
        Assert.True(DirectInputBrokerBridge.Instance.SetHotkeyBindings([]));
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
        hook.Poll();
        backend.Snapshot = CreateSnapshot(
            sequence: 10,
            DirectInputKeyboardStateFilter.ActivationScanCode,
            DirectInputKeyboardStateFilter.SettingsMenuScanCode,
            DirectInputKeyboardStateFilter.VoicePushToTalkScanCode);
        hook.Poll();
        hook.Poll();

        Assert.Equal(1, activations);
        Assert.Equal(new[] { true }, settings);
        Assert.True(voice[^1]);

        backend.Snapshot = CreateSnapshot(11);
        hook.Poll();

        Assert.Equal(new[] { true, false }, settings);
        Assert.False(voice[^1]);
    }

    [Fact]
    public void Poll_OfficialQuickActionWorksWhenOnlineChatCannotActivate()
    {
        var backend = new FakeDirectInputBrokerBackend();
        var reports = new List<(string Id, bool Pressed)>();
        var configuration = new Config
        {
            EnableOverlay = true,
            QuickActions =
            [
                new QuickActionConfiguration
                {
                    Id = "stamp-thanks",
                    Kind = QuickActionKind.Stamp,
                    OfficialId = 16,
                    KeyboardBinding = "P",
                },
            ],
        };
        using var hook = CreateHook(
            backend,
            canActivate: () => false,
            settingsAvailable: () => true,
            getConfiguration: () => configuration,
            reportQuickAction: (id, pressed) => reports.Add((id, pressed)));

        hook.Initialize();
        hook.Poll();
        backend.Snapshot = CreateSnapshot(sequence: 1, 0x19); // DIK_P
        hook.Poll();

        Assert.Contains(
            DirectInputBrokerPolicy.SuppressQuickActions,
            backend.PolicyRequests.Select(policy => policy & DirectInputBrokerPolicy.SuppressQuickActions));
        Assert.Contains(("stamp-thanks", true), reports);

        backend.Snapshot = CreateSnapshot(sequence: 2);
        hook.Poll();

        Assert.Equal([("stamp-thanks", true), ("stamp-thanks", false)], reports);
    }

    [Fact]
    public void Poll_WithoutConfiguredQuickActionsDoesNotEnableQuickActionSuppression()
    {
        var backend = new FakeDirectInputBrokerBackend();
        var reports = new List<(string Id, bool Pressed)>();
        var configuration = new Config
        {
            EnableOverlay = true,
        };
        using var hook = CreateHook(
            backend,
            canActivate: () => true,
            settingsAvailable: () => true,
            getConfiguration: () => configuration,
            reportQuickAction: (id, pressed) => reports.Add((id, pressed)));

        hook.Initialize();
        hook.Poll();
        backend.Snapshot = CreateSnapshot(sequence: 1, 0x19); // DIK_P
        hook.Poll();

        Assert.DoesNotContain(
            backend.PolicyRequests,
            policy => (policy & DirectInputBrokerPolicy.SuppressQuickActions) != 0);
        Assert.Empty(reports);
    }

    [Fact]
    public void Hook_NoLongerModelsQuickActionsPanelCallback()
    {
        Assert.Null(typeof(DirectInputKeyboardHook).GetField(
            "_reportQuickActionsMenuKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
    }

    [Fact]
    public void Poll_LeavesControllerBindingsToTheActiveXInputPoller()
    {
        var backend = new FakeDirectInputBrokerBackend();
        var activations = 0;
        var configuration = new Config
        {
            OpenChatKeyboardBinding = string.Empty,
            OpenChatControllerBinding = "A",
        };
        using var hook = CreateHook(
            backend,
            canActivate: () => true,
            tryActivate: () =>
            {
                activations++;
                return true;
            },
            getConfiguration: () => configuration);

        hook.Initialize();
        hook.Poll();
        var controllerSnapshot = CreateSnapshot(1);
        controllerSnapshot.ControllerButtons = ControllerButtons.A;
        backend.Snapshot = controllerSnapshot;
        hook.Poll();

        Assert.Equal(0, activations);
    }

    [Fact]
    public void Poll_GlobalMuteKeyboardBindingReportsOneEdgePerPress()
    {
        var backend = new FakeDirectInputBrokerBackend();
        var reports = new List<bool>();
        var configuration = new Config
        {
            GlobalMuteKeyboardBinding = "P",
        };
        using var hook = CreateHook(
            backend,
            canActivate: () => true,
            settingsAvailable: () => true,
            getConfiguration: () => configuration,
            reportGlobalMute: reports.Add);

        hook.Initialize();
        hook.Poll();
        backend.Snapshot = CreateSnapshot(sequence: 1, 0x19); // DIK_P
        hook.Poll();
        hook.Poll();
        backend.Snapshot = CreateSnapshot(sequence: 2);
        hook.Poll();

        Assert.Equal(new[] { true, false }, reports);
    }

    [Fact]
    public void Poll_RemotePlayerChatMuteBindingReportsDisplayPlayerWithoutVoiceMutation()
    {
        var backend = new FakeDirectInputBrokerBackend();
        var reports = new List<(int Player, bool Pressed)>();
        var configuration = new Config
        {
            RemotePlayer2ChatMuteKeyboardBinding = "P",
        };
        using var hook = CreateHook(
            backend,
            canActivate: () => true,
            settingsAvailable: () => true,
            getConfiguration: () => configuration,
            reportRemotePlayerChatMute: (player, pressed) => reports.Add((player, pressed)));

        hook.Initialize();
        hook.Poll();
        backend.Snapshot = CreateSnapshot(sequence: 1, 0x19); // DIK_P
        hook.Poll();
        backend.Snapshot = CreateSnapshot(sequence: 2);
        hook.Poll();

        Assert.Equal([(2, true), (2, false)], reports);
    }

    [Fact]
    public void Poll_RebuildsHotkeySnapshotOnlyWhenConfigurationRevisionChanges()
    {
        var backend = new FakeDirectInputBrokerBackend();
        var configuration = new Config();
        long revision = 0;
        using var hook = CreateHook(
            backend,
            settingsAvailable: () => true,
            getConfiguration: () => configuration,
            getConfigurationRevision: () => revision);

        hook.Initialize();
        hook.Poll();
        hook.Poll();
        hook.Poll();

        Assert.Single(backend.HotkeyBindingRequests);

        configuration.GlobalMuteKeyboardBinding = "P";
        revision += 2;
        hook.Poll();
        hook.Poll();

        Assert.Equal(2, backend.HotkeyBindingRequests.Count);
        Assert.Contains(
            backend.HotkeyBindingRequests[^1],
            binding => binding.ScanCode == 0x19);
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
        using var policyEntered = new ManualResetEventSlim(false);
        using var releasePolicy = new ManualResetEventSlim(false);
        using var suspendStarted = new ManualResetEventSlim(false);
        var backend = new FakeDirectInputBrokerBackend
        {
            PolicyEntered = policyEntered,
            ReleasePolicy = releasePolicy,
        };
        using var hook = CreateHook(backend, shouldCapture: () => true);
        hook.Initialize();

        // Dedicated workers keep this lock-order test independent of the shared test thread pool.
        var poll = Task.Factory.StartNew(
            hook.Poll,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Task? suspend = null;
        try
        {
            Assert.True(policyEntered.Wait(TimeSpan.FromSeconds(10)));
            suspend = Task.Factory.StartNew(
                () =>
                {
                    suspendStarted.Set();
                    hook.Suspend();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(suspendStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(backend.InactiveRequested.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            releasePolicy.Set();
            if (suspend is null)
                await poll.WaitAsync(TimeSpan.FromSeconds(10));
            else
                await Task.WhenAll(poll, suspend).WaitAsync(TimeSpan.FromSeconds(10));
        }

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
        Action<string>? log = null,
        Func<Config>? getConfiguration = null,
        Func<long>? getConfigurationRevision = null,
        Action<string, bool>? reportQuickAction = null,
        Action<bool>? reportGlobalMute = null,
        Action<int, bool>? reportRemotePlayerChatMute = null) =>
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
            log ?? (_ => { }),
            getConfiguration,
            getConfigurationRevision,
            reportQuickActionKey: reportQuickAction,
            reportGlobalMuteKey: reportGlobalMute,
            reportRemotePlayerChatMuteKey: reportRemotePlayerChatMute);

    private static DirectInputBrokerSnapshot CreateSnapshot(
        ulong sequence,
        params int[] scanCodes)
    {
        var snapshot = new DirectInputBrokerSnapshot
        {
            AbiVersion = DirectInputBrokerSnapshot.ExpectedAbiVersion,
            StructSize = DirectInputBrokerSnapshot.ExpectedStructSize,
            Sequence = sequence,
            Readiness = DirectInputBrokerReadiness.GameImport |
                        DirectInputBrokerReadiness.Factory |
                        DirectInputBrokerReadiness.Keyboard |
                        DirectInputBrokerReadiness.Mouse,
            Active = 1,
        };
        foreach (var scanCode in scanCodes)
        {
            var mask = 1UL << (scanCode % 64);
            switch (scanCode / 64)
            {
                case 0: snapshot.KeyboardWord0 |= mask; break;
                case 1: snapshot.KeyboardWord1 |= mask; break;
                case 2: snapshot.KeyboardWord2 |= mask; break;
                default: snapshot.KeyboardWord3 |= mask; break;
            }
        }
        return snapshot;
    }

    private sealed class FakeDirectInputBrokerBackend : IDirectInputBrokerBackend
    {
        internal bool InstallCalled { get; private set; }
        internal bool SnapshotSucceeds { get; init; } = true;
        internal int SnapshotReads { get; private set; }
        internal List<bool> ActiveRequests { get; } = new();
        internal List<DirectInputBrokerPolicy> PolicyRequests { get; } = new();
        internal List<DirectInputHotkeyBinding[]> HotkeyBindingRequests { get; } = new();
        internal ManualResetEventSlim InactiveRequested { get; } = new(false);
        internal ManualResetEventSlim? PolicyEntered { get; init; }
        internal ManualResetEventSlim? ReleasePolicy { get; init; }
        internal DirectInputBrokerSnapshot Snapshot { get; set; } =
            CreateSnapshot(0);

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
            ReleasePolicy?.Wait();
            return true;
        }

        public bool SetHotkeyBindings(DirectInputHotkeyBinding[] bindings)
        {
            HotkeyBindingRequests.Add(bindings.ToArray());
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
