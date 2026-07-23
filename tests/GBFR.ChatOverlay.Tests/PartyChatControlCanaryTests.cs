using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyChatControlCanaryTests
{
    private static readonly nint Manager = (nint)0x1000;
    private static readonly nint Network = (nint)0x2000;
    private static readonly nint LocalUser = (nint)0x3000;
    private static readonly nint LocalDevice = (nint)0x4000;
    private static readonly nint LocalChatControl = (nint)0x5000;
    private static readonly nint Endpoint = (nint)0x6000;

    [Fact]
    public void HappyPath_MutesBeforeSelectingDevices_ThenConnectsAndCleansUp()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(api, logs.Add, action => action());

        canary.CaptureManager(Manager, "test");
        ObserveReadySession(canary);
        canary.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "GetLocalDevice",
                "GetLocalChatControlCount",
                "CreateChatControl",
                "SetAudioInputMuted:True",
                "GetAudioInputMuted",
                "SetSystemDefaultAudioInput",
                "SetSystemDefaultAudioOutput",
            },
            api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.ConfiguringMutedAudio, canary.Phase);

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateChatControlCompleted)
        {
            Result = 0,
            LocalDevice = LocalDevice,
            LocalUser = LocalUser,
            ChatControl = LocalChatControl,
            AsyncIdentifier = canary.CreateAsyncIdentifier,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlCreated)
        {
            ChatControl = LocalChatControl,
        });
        canary.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioInputCompleted,
            canary.AudioInputAsyncIdentifier));
        canary.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioOutputCompleted,
            canary.AudioOutputAsyncIdentifier));
        canary.OnBatchFinished(Manager);

        Assert.Equal("ConnectChatControl", api.Calls[^1]);
        Assert.Equal(PartyChatControlCanaryPhase.Connecting, canary.Phase);

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ConnectChatControlCompleted)
        {
            Result = 0,
            Network = Network,
            ChatControl = LocalChatControl,
            AsyncIdentifier = canary.ConnectAsyncIdentifier,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = Network,
            ChatControl = LocalChatControl,
        });
        canary.OnBatchFinished(Manager);

        Assert.Equal(PartyChatControlCanaryPhase.JoinedMuted, canary.Phase);
        Assert.Contains(logs, line => line.Contains("permissions remain None", StringComparison.Ordinal));

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = LocalChatControl,
        });
        canary.OnBatchFinished(Manager);

        Assert.Equal("DestroyChatControl", api.Calls[^1]);
        Assert.Equal(PartyChatControlCanaryPhase.Destroying, canary.Phase);

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.DestroyChatControlCompleted)
        {
            Result = 0,
            LocalDevice = LocalDevice,
            ChatControl = LocalChatControl,
            AsyncIdentifier = canary.DestroyAsyncIdentifier,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlDestroyed)
        {
            ChatControl = LocalChatControl,
        });
        canary.OnBatchFinished(Manager);

        Assert.Equal(PartyChatControlCanaryPhase.Completed, canary.Phase);
    }

    [Fact]
    public void Create_FailsClosedBeforeDeviceSelection_WhenMuteCannotBeVerified()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            ReportMuted = false,
        };
        using var canary = new PartyChatControlCanary(api, _ => { }, action => action());

        canary.CaptureManager(Manager, "test");
        ObserveReadySession(canary);
        canary.OnBatchFinished(Manager);

        Assert.DoesNotContain("SetSystemDefaultAudioInput", api.Calls);
        Assert.DoesNotContain("SetSystemDefaultAudioOutput", api.Calls);
        Assert.DoesNotContain("ConnectChatControl", api.Calls);
        Assert.Equal("DestroyChatControl", api.Calls[^1]);
        Assert.Equal(PartyChatControlCanaryPhase.Destroying, canary.Phase);
    }

    [Fact]
    public void ExistingLocalChatControl_DisablesCanaryWithoutTakingOwnership()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            ExistingLocalChatControlCount = 1,
        };
        using var canary = new PartyChatControlCanary(api, _ => { }, action => action());

        canary.CaptureManager(Manager, "test");
        ObserveReadySession(canary);
        canary.OnBatchFinished(Manager);

        Assert.Equal(
            new[] { "GetLocalDevice", "GetLocalChatControlCount" },
            api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.Disabled, canary.Phase);
    }

    [Fact]
    public void RemoteCreatedAndJoinedEvents_AreLoggedWithoutChangingLocalPhase()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(api, logs.Add, action => action());

        canary.CaptureManager(Manager, "test");
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlCreated)
        {
            ChatControl = (nint)0x7777,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = Network,
            ChatControl = (nint)0x7777,
        });

        Assert.Equal(PartyChatControlCanaryPhase.WaitingForAuthenticatedSession, canary.Phase);
        Assert.Contains(logs, line => line.Contains("ChatControlCreated (remote/other)", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("ChatControlJoinedNetwork (remote/other)", StringComparison.Ordinal));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void NetworkLeaveBoundary_QueuesMutedDestroyBeforeGameLeave_AndDoesNotDuplicateIt()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(api, logs.Add, action => action());

        AdvanceToJoined(canary);
        api.Calls.Clear();

        canary.PrepareForNetworkLeave(Network);
        canary.PrepareForNetworkLeave(Network);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.Destroying, canary.Phase);
        Assert.Contains(
            logs,
            line => line.Contains(
                "pre-leave DestroyChatControl queued before Relink PartyNetworkLeaveNetwork",
                StringComparison.Ordinal));

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = LocalChatControl,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.DestroyChatControlCompleted)
        {
            Result = 0,
            LocalDevice = LocalDevice,
            ChatControl = LocalChatControl,
            AsyncIdentifier = canary.DestroyAsyncIdentifier,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlDestroyed)
        {
            ChatControl = LocalChatControl,
        });
        canary.OnBatchFinished(Manager);

        Assert.Equal(PartyChatControlCanaryPhase.Completed, canary.Phase);
        Assert.Contains(logs, line => line.Contains("Stage 2 cleanup complete", StringComparison.Ordinal));
    }

    [Fact]
    public void NetworkLeaveBoundary_IgnoresAnUntrackedNetwork()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(api, _ => { }, action => action());

        AdvanceToJoined(canary);
        api.Calls.Clear();

        canary.PrepareForNetworkLeave((nint)0x7777);

        Assert.Empty(api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.JoinedMuted, canary.Phase);
    }

    [Fact]
    public void NetworkLeaveBoundary_DestroyErrorFailsClosedWithoutRetrying()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(api, logs.Add, action => action());

        AdvanceToJoined(canary);
        api.Calls.Clear();
        api.DestroyChatControlResult = 0x1234;

        canary.PrepareForNetworkLeave(Network);
        canary.PrepareForNetworkLeave(Network);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.Disabled, canary.Phase);
        Assert.Contains(
            logs,
            line => line.Contains(
                "pre-leave PartyDeviceDestroyChatControl returned 0x00001234",
                StringComparison.Ordinal));
    }

    [Fact]
    public void NetworkLeaveBoundary_NativeExceptionFailsClosedAndReturnsNormally()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(api, logs.Add, action => action());

        AdvanceToJoined(canary);
        api.Calls.Clear();
        api.ThrowOnDestroyChatControl = true;

        var exception = Record.Exception(() => canary.PrepareForNetworkLeave(Network));

        Assert.Null(exception);
        Assert.Equal(PartyChatControlCanaryPhase.Disabled, canary.Phase);
        Assert.Contains(
            logs,
            line => line.Contains(
                "pre-leave native teardown threw InvalidOperationException",
                StringComparison.Ordinal));
    }

    [Fact]
    public void NetworkLeaveBoundary_AsyncDestroyFailureFailsClosed()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(api, logs.Add, action => action());

        AdvanceToJoined(canary);
        canary.PrepareForNetworkLeave(Network);
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.DestroyChatControlCompleted)
        {
            Result = 1,
            ErrorDetail = 0x4321,
            LocalDevice = LocalDevice,
            ChatControl = LocalChatControl,
            AsyncIdentifier = canary.DestroyAsyncIdentifier,
        });

        Assert.Equal(PartyChatControlCanaryPhase.Disabled, canary.Phase);
        Assert.Contains(
            logs,
            line => line.Contains(
                "DestroyChatControlCompleted did not confirm the owned canary operation",
                StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownStateType_DisablesCanaryWithoutNativeCalls()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(api, _ => { }, action => action());

        canary.CaptureManager(Manager, "test");
        canary.Observe(Manager, new PartyStateChangeSnapshot(61));
        canary.OnBatchFinished(Manager);

        Assert.Equal(PartyChatControlCanaryPhase.Disabled, canary.Phase);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void ManagerCleanupFailure_LeavesCanaryFailClosed()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(api, _ => { }, action => action());

        canary.CaptureManager(Manager, "test");
        canary.BeginManagerCleanup(Manager);
        canary.CompleteManagerCleanup(Manager, succeeded: false);

        Assert.Equal(PartyChatControlCanaryPhase.Disabled, canary.Phase);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Dispose_InvalidatesDeferredWorkBeforeItCanCallParty()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        Action? deferred = null;
        var canary = new PartyChatControlCanary(api, _ => { }, action => deferred = action);

        canary.CaptureManager(Manager, "test");
        ObserveReadySession(canary);
        canary.OnBatchFinished(Manager);
        Assert.NotNull(deferred);

        canary.Dispose();
        deferred!();

        Assert.Empty(api.Calls);
    }

    private static void ObserveReadySession(PartyChatControlCanary canary)
    {
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.AuthenticateLocalUserCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateEndpointCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
            Endpoint = Endpoint,
        });
    }

    private static void AdvanceToJoined(PartyChatControlCanary canary)
    {
        canary.CaptureManager(Manager, "test");
        ObserveReadySession(canary);
        canary.OnBatchFinished(Manager);

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateChatControlCompleted)
        {
            Result = 0,
            LocalDevice = LocalDevice,
            LocalUser = LocalUser,
            ChatControl = LocalChatControl,
            AsyncIdentifier = canary.CreateAsyncIdentifier,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlCreated)
        {
            ChatControl = LocalChatControl,
        });
        canary.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioInputCompleted,
            canary.AudioInputAsyncIdentifier));
        canary.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioOutputCompleted,
            canary.AudioOutputAsyncIdentifier));
        canary.OnBatchFinished(Manager);

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ConnectChatControlCompleted)
        {
            Result = 0,
            Network = Network,
            ChatControl = LocalChatControl,
            AsyncIdentifier = canary.ConnectAsyncIdentifier,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = Network,
            ChatControl = LocalChatControl,
        });
        canary.OnBatchFinished(Manager);

        Assert.Equal(PartyChatControlCanaryPhase.JoinedMuted, canary.Phase);
    }

    private static PartyStateChangeSnapshot AudioCompleted(
        PartyStateChangeType type,
        nint asyncIdentifier) =>
        new((uint)type)
        {
            Result = 0,
            Value = 1,
            ChatControl = LocalChatControl,
            AsyncIdentifier = asyncIdentifier,
        };

    private sealed class FakePartyChatControlApi : IPartyChatControlApi
    {
        private readonly nint _localDevice;
        private readonly nint _localChatControl;

        public FakePartyChatControlApi(nint localDevice, nint localChatControl)
        {
            _localDevice = localDevice;
            _localChatControl = localChatControl;
        }

        public List<string> Calls { get; } = [];

        public uint ExistingLocalChatControlCount { get; init; }

        public bool ReportMuted { get; init; } = true;

        public uint DestroyChatControlResult { get; set; }

        public bool ThrowOnDestroyChatControl { get; set; }

        public uint GetLocalDevice(nint manager, out nint localDevice)
        {
            Calls.Add("GetLocalDevice");
            localDevice = _localDevice;
            return 0;
        }

        public uint GetLocalChatControlCount(nint localDevice, out uint chatControlCount)
        {
            Calls.Add("GetLocalChatControlCount");
            chatControlCount = ExistingLocalChatControlCount;
            return 0;
        }

        public uint CreateChatControl(
            nint localDevice,
            nint localUser,
            nint asyncIdentifier,
            out nint localChatControl)
        {
            Calls.Add("CreateChatControl");
            localChatControl = _localChatControl;
            return 0;
        }

        public uint DestroyChatControl(nint localDevice, nint localChatControl, nint asyncIdentifier)
        {
            Calls.Add("DestroyChatControl");
            if (ThrowOnDestroyChatControl)
                throw new InvalidOperationException("Synthetic destroy failure.");
            return DestroyChatControlResult;
        }

        public uint SetAudioInputMuted(nint localChatControl, bool muted)
        {
            Calls.Add($"SetAudioInputMuted:{muted}");
            return 0;
        }

        public uint GetAudioInputMuted(nint localChatControl, out bool muted)
        {
            Calls.Add("GetAudioInputMuted");
            muted = ReportMuted;
            return 0;
        }

        public uint SetSystemDefaultAudioInput(nint localChatControl, nint asyncIdentifier)
        {
            Calls.Add("SetSystemDefaultAudioInput");
            return 0;
        }

        public uint SetSystemDefaultAudioOutput(nint localChatControl, nint asyncIdentifier)
        {
            Calls.Add("SetSystemDefaultAudioOutput");
            return 0;
        }

        public uint ConnectChatControl(nint network, nint localChatControl, nint asyncIdentifier)
        {
            Calls.Add("ConnectChatControl");
            return 0;
        }

        public uint DisconnectChatControl(nint network, nint localChatControl, nint asyncIdentifier)
        {
            Calls.Add("DisconnectChatControl");
            return 0;
        }
    }
}
