using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Audio;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyChatControlCanaryTests
{
    private static readonly nint Manager = (nint)0x1000;
    private static readonly nint Network = (nint)0x2000;
    private static readonly nint LocalUser = (nint)0x3000;
    private static readonly nint LocalDevice = (nint)0x4000;
    private static readonly nint LocalChatControl = (nint)0x5000;
    private static readonly nint Endpoint = (nint)0x6000;
    private static readonly nint RemoteChatControl = (nint)0x7000;
    private static readonly nint SecondRemoteChatControl = (nint)0x8000;

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
    public void ManualAudioDevices_AreSelectedIndependentlyBeforeConnect()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(
            api,
            logs.Add,
            action => action(),
            audioInputSelection: new ResolvedAudioEndpointSelection(
                UseSystemDefault: false,
                DeviceId: "capture-endpoint-id",
                DisplayName: "Desk Microphone",
                FellBack: false),
            audioOutputSelection: new ResolvedAudioEndpointSelection(
                UseSystemDefault: false,
                DeviceId: "render-endpoint-id",
                DisplayName: "USB Headset",
                FellBack: false));

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
                "SetAudioInput:Manual:capture-endpoint-id",
                "SetAudioOutput:Manual:render-endpoint-id",
            },
            api.Calls);
        Assert.Contains(
            logs,
            line => line.Contains("microphone=\"Desk Microphone\" (Manual)", StringComparison.Ordinal));
        Assert.Contains(
            logs,
            line => line.Contains("playback=\"USB Headset\" (Manual)", StringComparison.Ordinal));

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
            canary.AudioInputAsyncIdentifier,
            PartyAudioDeviceSelectionType.Manual,
            "capture-endpoint-id"));
        canary.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioOutputCompleted,
            canary.AudioOutputAsyncIdentifier,
            PartyAudioDeviceSelectionType.Manual,
            "render-endpoint-id"));
        canary.OnBatchFinished(Manager);

        Assert.Equal("ConnectChatControl", api.Calls[^1]);
        Assert.Equal(PartyChatControlCanaryPhase.Connecting, canary.Phase);
    }

    [Fact]
    public void ManualAudioCompletion_WithDifferentEndpointIdFailsClosed()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(
            api,
            logs.Add,
            action => action(),
            audioInputSelection: new ResolvedAudioEndpointSelection(
                UseSystemDefault: false,
                DeviceId: "capture-endpoint-id",
                DisplayName: "Desk Microphone",
                FellBack: false),
            audioOutputSelection: new ResolvedAudioEndpointSelection(
                UseSystemDefault: false,
                DeviceId: "render-endpoint-id",
                DisplayName: "USB Headset",
                FellBack: false));

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
            canary.AudioInputAsyncIdentifier,
            PartyAudioDeviceSelectionType.Manual,
            "different-capture-endpoint-id"));
        canary.OnBatchFinished(Manager);

        Assert.DoesNotContain("ConnectChatControl", api.Calls);
        Assert.Equal("DestroyChatControl", api.Calls[^1]);
        Assert.Equal(PartyChatControlCanaryPhase.Destroying, canary.Phase);
        Assert.Contains(logs, line =>
            line.Contains("did not confirm the owned Manual device operation", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_FailsClosedBeforeDeviceSelection_WhenMuteCannotBeVerified()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            ReportMutedOverride = false,
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
    public void VoiceTest_GrantsMicrophoneOnlyPermissions_AndUsesHoldToTalkMute()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(
            api,
            logs.Add,
            action => action(),
            enableVoiceTest: true);

        Assert.Equal(PartyVoiceUiState.WaitingForSession, canary.VoiceUiStatus.State);

        AdvanceToJoined(canary);

        Assert.Equal(PartyVoiceUiState.WaitingForPeer, canary.VoiceUiStatus.State);

        ObserveRemoteJoined(canary);
        canary.OnBatchFinished(Manager);

        Assert.Equal(PartyChatControlCanaryPhase.VoiceReady, canary.Phase);
        Assert.Equal(PartyVoiceUiState.Ready, canary.VoiceUiStatus.State);
        Assert.Contains("SetPermissions:7000:0x0005", api.Calls);
        Assert.DoesNotContain(api.Calls, call => call == "SetAudioInputMuted:False");

        api.Calls.Clear();
        canary.SetPushToTalkPressed(true);
        canary.SetPushToTalkPressed(true);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:False",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyVoiceUiState.Speaking, canary.VoiceUiStatus.State);
        Assert.Contains(logs, line => line.Contains("microphone UNMUTED", StringComparison.Ordinal));

        api.Calls.Clear();
        canary.SetPushToTalkPressed(false);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyVoiceUiState.Ready, canary.VoiceUiStatus.State);
        Assert.Contains(logs, line => line.Contains("push-to-talk microphone muted", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceTest_IgnoresRemoteChatControlOnAnotherNetwork()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(
            api,
            _ => { },
            action => action(),
            enableVoiceTest: true);

        AdvanceToJoined(canary);
        api.Calls.Clear();
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = (nint)0x2222,
            ChatControl = RemoteChatControl,
        });
        canary.OnBatchFinished(Manager);

        Assert.Empty(api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.JoinedMuted, canary.Phase);
    }

    [Fact]
    public void VoiceTest_DefersPushToTalkUntilRelinkFinishesStateBatch()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(
            api,
            _ => { },
            action => action(),
            enableVoiceTest: true);

        AdvanceToVoiceReady(canary);
        api.Calls.Clear();

        canary.BeginStateChangeBatch(Manager);
        canary.SetPushToTalkPressed(true);

        Assert.Empty(api.Calls);
        Assert.Equal(PartyVoiceUiState.Ready, canary.VoiceUiStatus.State);

        canary.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:False",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyVoiceUiState.Speaking, canary.VoiceUiStatus.State);
    }

    [Fact]
    public void VoiceTest_LastRemoteLeaveForcesMute()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(
            api,
            _ => { },
            action => action(),
            enableVoiceTest: true);

        AdvanceToVoiceReady(canary);
        canary.SetPushToTalkPressed(true);
        api.Calls.Clear();

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = RemoteChatControl,
        });
        canary.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.JoinedMuted, canary.Phase);
    }

    [Fact]
    public void VoiceTest_OnlyLastRemoteLeaveForcesMute()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(
            api,
            _ => { },
            action => action(),
            enableVoiceTest: true);

        AdvanceToJoined(canary);
        ObserveRemoteJoined(canary, RemoteChatControl);
        ObserveRemoteJoined(canary, SecondRemoteChatControl);
        canary.OnBatchFinished(Manager);

        Assert.Contains("SetPermissions:7000:0x0005", api.Calls);
        Assert.Contains("SetPermissions:8000:0x0005", api.Calls);

        canary.SetPushToTalkPressed(true);
        api.Calls.Clear();

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = RemoteChatControl,
        });
        canary.OnBatchFinished(Manager);

        Assert.Empty(api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.VoiceReady, canary.Phase);

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = SecondRemoteChatControl,
        });
        canary.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.JoinedMuted, canary.Phase);
    }

    [Fact]
    public void VoiceTest_RemoteDestroyedForcesMute_AndLaterLeftEventIsIdempotent()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(
            api,
            _ => { },
            action => action(),
            enableVoiceTest: true);

        AdvanceToVoiceReady(canary);
        canary.SetPushToTalkPressed(true);
        api.Calls.Clear();

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlDestroyed)
        {
            ChatControl = RemoteChatControl,
        });
        canary.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.JoinedMuted, canary.Phase);

        api.Calls.Clear();
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = RemoteChatControl,
        });
        canary.OnBatchFinished(Manager);

        Assert.Empty(api.Calls);
    }

    [Fact]
    public void VoiceTest_PermissionFailureNeverUnmutesAndDestroysLocalControl()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            SetPermissionsResult = 0x99,
        };
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(
            api,
            logs.Add,
            action => action(),
            enableVoiceTest: true);

        AdvanceToJoined(canary);
        api.Calls.Clear();
        ObserveRemoteJoined(canary);
        canary.OnBatchFinished(Manager);

        Assert.Contains("SetPermissions:7000:0x0005", api.Calls);
        Assert.DoesNotContain("SetAudioInputMuted:False", api.Calls);
        Assert.Contains("DestroyChatControl", api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.Destroying, canary.Phase);
        Assert.Contains(logs, line => line.Contains("voice test failed closed", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceTest_UnmuteFailureForcesMuteAndDestroysLocalControl()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(
            api,
            logs.Add,
            action => action(),
            enableVoiceTest: true);

        AdvanceToVoiceReady(canary);
        api.Calls.Clear();
        api.SetAudioInputMutedResult = 0x55;

        canary.SetPushToTalkPressed(true);

        Assert.Contains("SetAudioInputMuted:False", api.Calls);
        Assert.Contains("SetAudioInputMuted:True", api.Calls);
        Assert.Contains("DestroyChatControl", api.Calls);
        Assert.DoesNotContain(logs, line => line.Contains("microphone UNMUTED", StringComparison.Ordinal));
        Assert.Equal(PartyChatControlCanaryPhase.Destroying, canary.Phase);
        Assert.Equal(PartyVoiceUiState.Faulted, canary.VoiceUiStatus.State);
    }

    [Fact]
    public void VoiceUi_NeverReportsSpeakingWhenNativeReadbackRemainsMuted()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            ReportMutedOverride = true,
        };
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(
            api,
            logs.Add,
            action => action(),
            enableVoiceTest: true);

        AdvanceToVoiceReady(canary);
        Assert.Equal(PartyVoiceUiState.Ready, canary.VoiceUiStatus.State);
        api.Calls.Clear();

        canary.SetPushToTalkPressed(true);

        Assert.Contains("SetAudioInputMuted:False", api.Calls);
        Assert.Contains("GetAudioInputMuted", api.Calls);
        Assert.Contains("SetAudioInputMuted:True", api.Calls);
        Assert.DoesNotContain(logs, line => line.Contains("microphone UNMUTED", StringComparison.Ordinal));
        Assert.Equal(PartyVoiceUiState.Faulted, canary.VoiceUiStatus.State);
    }

    [Fact]
    public void VoiceTest_ConcurrentFailClosedDuringUnmute_ReMutesAndDestroys()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var canary = new PartyChatControlCanary(
            api,
            logs.Add,
            action => action(),
            enableVoiceTest: true);

        AdvanceToVoiceReady(canary);
        api.Calls.Clear();
        api.AfterAudioInputStateChanged = muted =>
        {
            if (!muted)
                canary.DisableFailClosed("synthetic concurrent lifecycle fault");
        };

        canary.SetPushToTalkPressed(true);

        Assert.Contains("SetAudioInputMuted:False", api.Calls);
        Assert.Contains("SetAudioInputMuted:True", api.Calls);
        Assert.Contains("DestroyChatControl", api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.Destroying, canary.Phase);
        Assert.Contains(
            logs,
            line => line.Contains("failed closed while the microphone was open", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceTest_PermissionExceptionNeverUnmutesAndDestroysLocalControl()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            ThrowOnSetPermissions = true,
        };
        using var canary = new PartyChatControlCanary(
            api,
            _ => { },
            action => action(),
            enableVoiceTest: true);

        AdvanceToJoined(canary);
        api.Calls.Clear();
        ObserveRemoteJoined(canary);
        canary.OnBatchFinished(Manager);

        Assert.Contains("SetPermissions:7000:0x0005", api.Calls);
        Assert.DoesNotContain("SetAudioInputMuted:False", api.Calls);
        Assert.Contains("SetAudioInputMuted:True", api.Calls);
        Assert.Contains("DestroyChatControl", api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.Destroying, canary.Phase);
    }

    [Fact]
    public void VoiceTest_LeavingNetworkWhileSpeaking_MutesBeforeDestroying()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(
            api,
            _ => { },
            action => action(),
            enableVoiceTest: true);

        AdvanceToVoiceReady(canary);
        canary.SetPushToTalkPressed(true);
        api.Calls.Clear();

        canary.PrepareForNetworkLeave(Network);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.Destroying, canary.Phase);
    }

    [Fact]
    public void VoiceTest_DisconnectMuteFailure_SkipsWaitAndDestroysImmediately()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(
            api,
            _ => { },
            action => action(),
            enableVoiceTest: true);

        AdvanceToVoiceReady(canary);
        canary.SetPushToTalkPressed(true);
        api.Calls.Clear();
        api.SetAudioInputMutedResult = 0x55;

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ConnectChatControlCompleted)
        {
            Result = 1,
            ErrorDetail = 0x99,
            Network = Network,
            ChatControl = LocalChatControl,
            AsyncIdentifier = canary.ConnectAsyncIdentifier,
        });
        canary.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
        Assert.DoesNotContain("DisconnectChatControl", api.Calls);
        Assert.Equal(PartyChatControlCanaryPhase.Destroying, canary.Phase);
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
    public void ManagerCleanup_WhileVoiceIsOpen_ForcesMuteBeforePartyTakesOwnership()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(
            api,
            _ => { },
            action => action(),
            enableVoiceTest: true);

        AdvanceToVoiceReady(canary);
        canary.SetPushToTalkPressed(true);
        api.Calls.Clear();

        canary.BeginManagerCleanup(Manager);

        Assert.Equal(new[] { "SetAudioInputMuted:True" }, api.Calls);
    }

    [Fact]
    public void Suspend_WhileVoiceIsOpen_ForcesMuteBeforeDestroying()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var canary = new PartyChatControlCanary(
            api,
            _ => { },
            action => action(),
            enableVoiceTest: true);

        AdvanceToVoiceReady(canary);
        canary.SetPushToTalkPressed(true);
        api.Calls.Clear();

        canary.SuspendBestEffort();

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
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

    [Fact]
    public void Dispose_WhileVoiceIsOpen_MutesBeforeDestroying()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var canary = new PartyChatControlCanary(
            api,
            _ => { },
            action => action(),
            enableVoiceTest: true);

        AdvanceToVoiceReady(canary);
        canary.SetPushToTalkPressed(true);
        api.Calls.Clear();

        canary.Dispose();

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
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

    private static void AdvanceToVoiceReady(PartyChatControlCanary canary)
    {
        AdvanceToJoined(canary);
        ObserveRemoteJoined(canary);
        canary.OnBatchFinished(Manager);
        Assert.Equal(PartyChatControlCanaryPhase.VoiceReady, canary.Phase);
    }

    private static void ObserveRemoteJoined(
        PartyChatControlCanary canary,
        nint remoteChatControl = default)
    {
        if (remoteChatControl == nint.Zero)
            remoteChatControl = RemoteChatControl;

        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlCreated)
        {
            ChatControl = remoteChatControl,
        });
        canary.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = Network,
            ChatControl = remoteChatControl,
        });
    }

    private static PartyStateChangeSnapshot AudioCompleted(
        PartyStateChangeType type,
        nint asyncIdentifier,
        PartyAudioDeviceSelectionType selectionType = PartyAudioDeviceSelectionType.SystemDefault,
        string? selectionContext = null) =>
        new((uint)type)
        {
            Result = 0,
            Value = (uint)selectionType,
            AudioDeviceSelectionContext = selectionContext,
            ChatControl = LocalChatControl,
            AsyncIdentifier = asyncIdentifier,
        };

    private sealed class FakePartyChatControlApi : IPartyChatControlApi
    {
        private readonly nint _localDevice;
        private readonly nint _localChatControl;
        private bool _muted = true;

        public FakePartyChatControlApi(nint localDevice, nint localChatControl)
        {
            _localDevice = localDevice;
            _localChatControl = localChatControl;
        }

        public List<string> Calls { get; } = [];

        public uint ExistingLocalChatControlCount { get; init; }

        public bool? ReportMutedOverride { get; init; }

        public uint DestroyChatControlResult { get; set; }

        public bool ThrowOnDestroyChatControl { get; set; }

        public uint SetPermissionsResult { get; set; }

        public bool ThrowOnSetPermissions { get; set; }

        public uint SetAudioInputMutedResult { get; set; }

        public Action<bool>? AfterAudioInputStateChanged { get; set; }

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
            if (SetAudioInputMutedResult == 0)
                _muted = muted;
            AfterAudioInputStateChanged?.Invoke(muted);
            return SetAudioInputMutedResult;
        }

        public uint GetAudioInputMuted(nint localChatControl, out bool muted)
        {
            Calls.Add("GetAudioInputMuted");
            muted = ReportMutedOverride ?? _muted;
            return 0;
        }

        public uint SetPermissions(
            nint localChatControl,
            nint targetChatControl,
            PartyChatPermissionOptions permissions)
        {
            Calls.Add($"SetPermissions:{(nuint)targetChatControl:X}:0x{(uint)permissions:X4}");
            if (ThrowOnSetPermissions)
                throw new InvalidOperationException("Synthetic permissions failure.");
            return SetPermissionsResult;
        }

        public uint SetAudioInput(
            nint localChatControl,
            PartyAudioDeviceSelectionType selectionType,
            string? selectionContext,
            nint asyncIdentifier)
        {
            Calls.Add(selectionType == PartyAudioDeviceSelectionType.SystemDefault
                ? "SetSystemDefaultAudioInput"
                : $"SetAudioInput:{selectionType}:{selectionContext}");
            return 0;
        }

        public uint SetAudioOutput(
            nint localChatControl,
            PartyAudioDeviceSelectionType selectionType,
            string? selectionContext,
            nint asyncIdentifier)
        {
            Calls.Add(selectionType == PartyAudioDeviceSelectionType.SystemDefault
                ? "SetSystemDefaultAudioOutput"
                : $"SetAudioOutput:{selectionType}:{selectionContext}");
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
