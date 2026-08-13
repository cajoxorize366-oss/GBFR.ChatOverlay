using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Audio;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyVoiceSessionTests
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
        using var session = new PartyVoiceSession(api, logs.Add, action => action());

        session.CaptureManager(Manager, "test");
        ObserveReadySession(session);
        session.OnBatchFinished(Manager);

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
        Assert.Equal(PartyVoiceSessionPhase.ConfiguringMutedAudio, session.Phase);

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateChatControlCompleted)
        {
            Result = 0,
            LocalDevice = LocalDevice,
            LocalUser = LocalUser,
            ChatControl = LocalChatControl,
            AsyncIdentifier = session.CreateAsyncIdentifier,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlCreated)
        {
            ChatControl = LocalChatControl,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalChatAudioInputChanged)
        {
            ChatControl = LocalChatControl,
            AudioInputState = PartyAudioInputState.Initialized,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalChatAudioOutputChanged)
        {
            ChatControl = LocalChatControl,
            AudioOutputState = PartyAudioOutputState.Initialized,
        });
        session.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioInputCompleted,
            session.AudioInputAsyncIdentifier));
        session.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioOutputCompleted,
            session.AudioOutputAsyncIdentifier));
        session.OnBatchFinished(Manager);

        Assert.Equal("ConnectChatControl", api.Calls[^1]);
        Assert.Equal(PartyVoiceSessionPhase.Connecting, session.Phase);

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ConnectChatControlCompleted)
        {
            Result = 0,
            Network = Network,
            ChatControl = LocalChatControl,
            AsyncIdentifier = session.ConnectAsyncIdentifier,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = Network,
            ChatControl = LocalChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Equal(PartyVoiceSessionPhase.JoinedMuted, session.Phase);
        Assert.Contains(logs, line => line.Contains("permissions remain None", StringComparison.Ordinal));

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = LocalChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Equal("DestroyChatControl", api.Calls[^1]);
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.DestroyChatControlCompleted)
        {
            Result = 0,
            LocalDevice = LocalDevice,
            ChatControl = LocalChatControl,
            AsyncIdentifier = session.DestroyAsyncIdentifier,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlDestroyed)
        {
            ChatControl = LocalChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Equal(PartyVoiceSessionPhase.Completed, session.Phase);
    }

    [Fact]
    public void ManualAudioDevices_AreSelectedIndependentlyBeforeConnect()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
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

        session.CaptureManager(Manager, "test");
        ObserveReadySession(session);
        session.OnBatchFinished(Manager);

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

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateChatControlCompleted)
        {
            Result = 0,
            LocalDevice = LocalDevice,
            LocalUser = LocalUser,
            ChatControl = LocalChatControl,
            AsyncIdentifier = session.CreateAsyncIdentifier,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlCreated)
        {
            ChatControl = LocalChatControl,
        });
        session.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioInputCompleted,
            session.AudioInputAsyncIdentifier,
            PartyAudioDeviceSelectionType.Manual,
            "capture-endpoint-id"));
        session.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioOutputCompleted,
            session.AudioOutputAsyncIdentifier,
            PartyAudioDeviceSelectionType.Manual,
            "render-endpoint-id"));
        session.OnBatchFinished(Manager);

        Assert.Equal("ConnectChatControl", api.Calls[^1]);
        Assert.Equal(PartyVoiceSessionPhase.Connecting, session.Phase);
    }

    [Fact]
    public void ManualAudioCompletion_WithDifferentEndpointIdFailsClosed()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
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

        session.CaptureManager(Manager, "test");
        ObserveReadySession(session);
        session.OnBatchFinished(Manager);
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateChatControlCompleted)
        {
            Result = 0,
            LocalDevice = LocalDevice,
            LocalUser = LocalUser,
            ChatControl = LocalChatControl,
            AsyncIdentifier = session.CreateAsyncIdentifier,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlCreated)
        {
            ChatControl = LocalChatControl,
        });
        session.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioInputCompleted,
            session.AudioInputAsyncIdentifier,
            PartyAudioDeviceSelectionType.Manual,
            "different-capture-endpoint-id"));
        session.OnBatchFinished(Manager);

        Assert.DoesNotContain("ConnectChatControl", api.Calls);
        Assert.Equal("DestroyChatControl", api.Calls[^1]);
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);
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
        using var session = new PartyVoiceSession(api, _ => { }, action => action());

        session.CaptureManager(Manager, "test");
        ObserveReadySession(session);
        session.OnBatchFinished(Manager);

        Assert.DoesNotContain("SetSystemDefaultAudioInput", api.Calls);
        Assert.DoesNotContain("SetSystemDefaultAudioOutput", api.Calls);
        Assert.DoesNotContain("ConnectChatControl", api.Calls);
        Assert.Equal("DestroyChatControl", api.Calls[^1]);
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);
    }

    [Fact]
    public void ExistingLocalChatControl_DisablesVoiceSessionWithoutTakingOwnership()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            ExistingLocalChatControlCount = 1,
        };
        using var session = new PartyVoiceSession(api, _ => { }, action => action());

        session.CaptureManager(Manager, "test");
        ObserveReadySession(session);
        session.OnBatchFinished(Manager);

        Assert.Equal(
            new[] { "GetLocalDevice", "GetLocalChatControlCount" },
            api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.Disabled, session.Phase);
    }

    [Fact]
    public void RemoteCreatedAndJoinedEvents_AreLoggedWithoutChangingLocalPhase()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(api, logs.Add, action => action());

        session.CaptureManager(Manager, "test");
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlCreated)
        {
            ChatControl = (nint)0x7777,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = Network,
            ChatControl = (nint)0x7777,
        });

        Assert.Equal(PartyVoiceSessionPhase.WaitingForAuthenticatedSession, session.Phase);
        Assert.Contains(logs, line => line.Contains("ChatControlCreated (remote/other)", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("ChatControlJoinedNetwork (remote/other)", StringComparison.Ordinal));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Voice_GrantsMicrophoneOnlyPermissions_AndUsesHoldToTalkMute()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        Assert.Equal(PartyVoiceUiState.WaitingForSession, session.VoiceUiStatus.State);

        AdvanceToJoined(session);

        Assert.Equal(PartyVoiceUiState.WaitingForPeer, session.VoiceUiStatus.State);

        ObserveRemoteJoined(session);
        session.OnBatchFinished(Manager);

        Assert.Equal(PartyVoiceSessionPhase.VoiceReady, session.Phase);
        Assert.Equal(PartyVoiceUiState.Ready, session.VoiceUiStatus.State);
        Assert.Contains("SetPermissions:7000:0x0005", api.Calls);
        Assert.DoesNotContain(api.Calls, call => call == "SetAudioInputMuted:False");
        Assert.Contains(
            logs,
            line => line.Contains("native selected microphone path is active", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs,
            line => line.Contains("audio-manipulation", StringComparison.OrdinalIgnoreCase));

        api.Calls.Clear();
        session.SetPushToTalkPressed(true);
        session.SetPushToTalkPressed(true);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:False",
                "GetAudioInputMuted",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyVoiceUiState.Speaking, session.VoiceUiStatus.State);
        Assert.Contains(logs, line => line.Contains("microphone UNMUTED", StringComparison.Ordinal));
        Assert.Contains(
            logs,
            line => line.Contains(
                "Party is capturing the configured Windows microphone directly",
                StringComparison.Ordinal));

        api.Calls.Clear();
        session.SetPushToTalkPressed(false);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "GetAudioInputMuted",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyVoiceUiState.Ready, session.VoiceUiStatus.State);
        Assert.Contains(logs, line => line.Contains("Party voice microphone muted", StringComparison.Ordinal));
    }

    [Fact]
    public void Voice_ReconcilesRemoteChatControlThatJoinedBeforeLocalControl()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            NetworkChatControls = [RemoteChatControl, LocalChatControl],
        };
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToJoined(session, expectedPhase: PartyVoiceSessionPhase.VoiceReady);

        var discoveryIndex = api.Calls.IndexOf("GetNetworkChatControls");
        var permissionIndex = api.Calls.IndexOf("SetPermissions:7000:0x0005");
        Assert.True(discoveryIndex >= 0);
        Assert.True(permissionIndex > discoveryIndex);
        Assert.Equal(PartyVoiceSessionPhase.VoiceReady, session.Phase);
        Assert.True(session.IsRemotePushToTalkReady);
        Assert.Contains(logs, line =>
            line.Contains("remoteAdded=1", StringComparison.Ordinal) &&
            line.Contains("joined before the local Mod ChatControl", StringComparison.Ordinal));
    }

    [Fact]
    public void Voice_NetworkChatControlReconciliationFailureKeepsJoinEventFallback()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            GetNetworkChatControlsResult = 0x55,
        };
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToJoined(session);

        Assert.Equal(PartyVoiceSessionPhase.JoinedMuted, session.Phase);
        Assert.Equal(1, api.Calls.Count(call => call == "GetNetworkChatControls"));
        Assert.Contains(logs, line =>
            line.Contains("returned 0x00000055", StringComparison.Ordinal) &&
            line.Contains("join events remain active", StringComparison.Ordinal));

        api.Calls.Clear();
        ObserveRemoteJoined(session);
        session.OnBatchFinished(Manager);

        Assert.Contains("SetPermissions:7000:0x0005", api.Calls);
        Assert.DoesNotContain("GetNetworkChatControls", api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.VoiceReady, session.Phase);
    }

    [Fact]
    public void Voice_HeldUBeforePeerReadinessDoesNotLatchAnUnmute()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToJoined(session);
        Assert.False(session.IsRemotePushToTalkReady);

        session.SetPushToTalkPressed(true);
        ObserveRemoteJoined(session);
        session.OnBatchFinished(Manager);

        Assert.True(session.IsRemotePushToTalkReady);
        Assert.Equal(PartyVoiceUiState.Ready, session.VoiceUiStatus.State);
        Assert.DoesNotContain("SetAudioInputMuted:False", api.Calls);

        session.SetPushToTalkPressed(false);
        session.SetPushToTalkPressed(true);

        Assert.Contains("SetAudioInputMuted:False", api.Calls);
    }

    [Fact]
    public void VoiceDiagnostics_CapturesOfficialSignalPathAndEmitsPassSummary()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToVoiceReady(session);
        api.DiagnosticCalls.Clear();
        api.LocalIndicatorOverride = PartyLocalChatControlChatIndicator.Talking;
        api.RemoteIndicator = PartyChatControlChatIndicator.Talking;

        session.SetPushToTalkPressed(true);
        session.RequestVoiceDiagnosticSample();
        session.SetPushToTalkPressed(false);
        session.PrepareForNetworkLeave(Network);

        Assert.Contains("GetLocalChatIndicator", api.DiagnosticCalls);
        Assert.Contains("GetAudioInput", api.DiagnosticCalls);
        Assert.Contains("GetAudioOutput", api.DiagnosticCalls);
        Assert.Contains("GetPermissions:7000", api.DiagnosticCalls);
        Assert.Contains("GetChatIndicator:7000", api.DiagnosticCalls);
        Assert.Contains("GetIncomingAudioMuted:7000", api.DiagnosticCalls);
        Assert.Contains("GetAudioRenderVolume:7000", api.DiagnosticCalls);
        Assert.Contains(logs, line =>
            line.Contains("PASS_LOCAL_MICROPHONE_SIGNAL_CAPTURED", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("PASS_REMOTE_AUDIO_RECEIVED_BY_PARTY", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("verdict=PASS_PARTY_BIDIRECTIONAL_SIGNAL_PATH", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("default-communications-capture", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("default-communications-render", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceActivitySnapshot_ReadsTalkingEntityIdWithoutWritingIncomingAudioMute()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        api.EntityIds[RemoteChatControl] = "remote-player-one";
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        api.RemoteIndicator = PartyChatControlChatIndicator.Talking;
        session.RequestVoiceDiagnosticSample();

        Assert.Equal(["remote-player-one"], session.GetTalkingRemoteEntityIds());
        Assert.Equal(0, api.IncomingAudioMuteWrites);
    }

    [Fact]
    public void VoiceDiagnostics_DoesNotTreatTalkingIndicatorAsEvidenceWhileInputIsMuted()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToVoiceReady(session);
        api.LocalIndicatorOverride = PartyLocalChatControlChatIndicator.Talking;
        api.RemoteIndicator = PartyChatControlChatIndicator.Talking;

        session.RequestVoiceDiagnosticSample();
        session.PrepareForNetworkLeave(Network);

        Assert.Contains(logs, line =>
            line.Contains("FAIL_LOCAL_TALKING_WHILE_INPUT_EXPECTED_MUTED", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("verdict=FAIL_LOCAL_TALKING_NOT_OBSERVED", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line =>
            line.Contains("verdict=PASS_PARTY_BIDIRECTIONAL_SIGNAL_PATH", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceDiagnostics_NeverCombinesPartialEvidenceFromDifferentPeersIntoPass()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToJoined(session);
        api.LocalIndicatorOverride = PartyLocalChatControlChatIndicator.Talking;
        api.RemoteIndicatorOverrides[RemoteChatControl] = PartyChatControlChatIndicator.Talking;
        api.IncomingAudioMutedOverrides[RemoteChatControl] = true;
        api.RemoteIndicatorOverrides[SecondRemoteChatControl] = PartyChatControlChatIndicator.Silent;
        api.IncomingAudioMutedOverrides[SecondRemoteChatControl] = false;
        ObserveRemoteJoined(session, RemoteChatControl);
        ObserveRemoteJoined(session, SecondRemoteChatControl);
        session.OnBatchFinished(Manager);

        session.SetPushToTalkPressed(true);
        session.RequestVoiceDiagnosticSample();
        session.SetPushToTalkPressed(false);
        session.PrepareForNetworkLeave(Network);

        Assert.Contains(logs, line =>
            line.Contains("verdict=FAIL_NO_SINGLE_PEER_COMPLETED_REMOTE_PATH", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line =>
            line.Contains("verdict=PASS_PARTY_BIDIRECTIONAL_SIGNAL_PATH", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("0x7000[", StringComparison.Ordinal) &&
            line.Contains("complete=False", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("0x8000[", StringComparison.Ordinal) &&
            line.Contains("complete=False", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceDiagnostics_DoesNotReuseEvidenceFromAPeerThatAlreadyLeft()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToJoined(session);
        api.LocalIndicatorOverride = PartyLocalChatControlChatIndicator.Talking;
        api.RemoteIndicatorOverrides[RemoteChatControl] = PartyChatControlChatIndicator.Talking;
        api.RemoteIndicatorOverrides[SecondRemoteChatControl] = PartyChatControlChatIndicator.Silent;
        ObserveRemoteJoined(session, RemoteChatControl);
        ObserveRemoteJoined(session, SecondRemoteChatControl);
        session.OnBatchFinished(Manager);

        session.SetPushToTalkPressed(true);
        session.RequestVoiceDiagnosticSample();
        session.SetPushToTalkPressed(false);
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = RemoteChatControl,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = SecondRemoteChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Contains(logs, line =>
            line.Contains("verdict=FAIL_REMOTE_TALKING_NOT_OBSERVED", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line =>
            line.Contains("verdict=PASS_PARTY_BIDIRECTIONAL_SIGNAL_PATH", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceDiagnostics_GetterExceptionsRemainEvidenceOnlyAndNeverTearDownVoice()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToVoiceReady(session);
        api.Calls.Clear();
        api.ThrowOnDiagnosticGetter = true;

        session.RequestVoiceDiagnosticSample();

        Assert.Equal(PartyVoiceSessionPhase.VoiceReady, session.Phase);
        Assert.DoesNotContain("DestroyChatControl", api.Calls);
        Assert.Contains(logs, line =>
            line.Contains("INCONCLUSIVE_LOCAL_GETTER_ERROR", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("INCONCLUSIVE_REMOTE_GETTER_ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceDiagnostics_TranslatesAudioStateErrorDetailThroughParty()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToVoiceReady(session);
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalChatAudioInputChanged)
        {
            ChatControl = LocalChatControl,
            AudioInputState = PartyAudioInputState.UnknownError,
            ErrorDetail = 0xBEEF,
        });
        session.OnBatchFinished(Manager);

        Assert.Contains("GetErrorMessage:0x0000BEEF", api.DiagnosticCalls);
        Assert.Contains(logs, line =>
            line.Contains("Synthetic Party error 0x0000BEEF", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("FAIL_LOCAL_INPUT_UnknownError", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceDiagnostics_DistinguishesOfficialRemoteBlockersAndInvalidVolume()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToVoiceReady(session);
        api.PermissionReadbackOverrides[RemoteChatControl] = PartyChatPermissionOptions.None;
        session.RequestVoiceDiagnosticSample();

        api.PermissionReadbackOverrides[RemoteChatControl] =
            PartyChatPermissionOptions.SendMicrophoneAudio |
            PartyChatPermissionOptions.ReceiveMicrophoneAudio;
        api.RemoteIndicator = PartyChatControlChatIndicator.NoRemoteInput;
        session.RequestVoiceDiagnosticSample();

        api.RemoteIndicator = PartyChatControlChatIndicator.Talking;
        api.IncomingAudioMuted = true;
        session.RequestVoiceDiagnosticSample();

        api.IncomingAudioMuted = false;
        api.AudioRenderVolume = float.NaN;
        session.RequestVoiceDiagnosticSample();

        Assert.Contains(logs, line => line.Contains(
            "FAIL_PERMISSION_READBACK_MISSING_SEND_OR_RECEIVE_MICROPHONE_AUDIO",
            StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("FAIL_REMOTE_HAS_NO_AUDIO_INPUT", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("FAIL_REMOTE_AUDIO_MUTED_LOCALLY", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("FAIL_REMOTE_RENDER_VOLUME_NOT_POSITIVE", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceDiagnostics_IdentifiesAnUnusableOutputDeviceBeforeRemoteTransport()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToVoiceReady(session);
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalChatAudioOutputChanged)
        {
            ChatControl = LocalChatControl,
            AudioOutputState = PartyAudioOutputState.AlreadyInUse,
            ErrorDetail = 0xCAFE,
        });
        session.OnBatchFinished(Manager);

        Assert.Contains(logs, line =>
            line.Contains("FAIL_LOCAL_OUTPUT_AlreadyInUse", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("Synthetic Party error 0x0000CAFE", StringComparison.Ordinal));
    }

    [Fact]
    public void Voice_IgnoresRemoteChatControlOnAnotherNetwork()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToJoined(session);
        api.Calls.Clear();
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = (nint)0x2222,
            ChatControl = RemoteChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Empty(api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.JoinedMuted, session.Phase);
    }

    [Fact]
    public void Voice_DefersPushToTalkUntilRelinkFinishesStateBatch()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        api.Calls.Clear();

        session.BeginStateChangeBatch(Manager);
        session.SetPushToTalkPressed(true);

        Assert.Empty(api.Calls);
        Assert.Equal(PartyVoiceUiState.Ready, session.VoiceUiStatus.State);

        session.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:False",
                "GetAudioInputMuted",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyVoiceUiState.Speaking, session.VoiceUiStatus.State);
    }

    [Fact]
    public void Voice_UnknownAudioStatesBlockRemotePushToTalkUntilInitialized()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToJoined(session, observeAudioStates: false);
        Assert.Equal(PartyVoiceUiState.WaitingForPeer, session.VoiceUiStatus.State);

        ObserveRemoteJoined(session);
        session.OnBatchFinished(Manager);

        Assert.Equal(PartyVoiceSessionPhase.VoiceReady, session.Phase);
        Assert.False(session.IsRemotePushToTalkReady);
        Assert.Equal(PartyVoiceUiState.Connecting, session.VoiceUiStatus.State);
        Assert.DoesNotContain("DestroyChatControl", api.Calls);

        api.Calls.Clear();
        session.SetPushToTalkPressed(true);
        Assert.DoesNotContain("SetAudioInputMuted:False", api.Calls);
    }

    [Fact]
    public void Voice_AudioStateLossWhileSpeakingMutesAndRequiresReleaseAndPressAfterRecovery()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        session.SetPushToTalkPressed(true);
        Assert.Equal(PartyVoiceUiState.Speaking, session.VoiceUiStatus.State);
        api.Calls.Clear();

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalChatAudioInputChanged)
        {
            ChatControl = LocalChatControl,
            AudioInputState = PartyAudioInputState.NotFound,
        });
        session.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "GetAudioInputMuted",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.False(session.IsRemotePushToTalkReady);
        Assert.Equal(PartyVoiceUiState.Connecting, session.VoiceUiStatus.State);
        Assert.DoesNotContain("DestroyChatControl", api.Calls);

        api.Calls.Clear();
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalChatAudioInputChanged)
        {
            ChatControl = LocalChatControl,
            AudioInputState = PartyAudioInputState.Initialized,
        });
        session.OnBatchFinished(Manager);

        Assert.True(session.IsRemotePushToTalkReady);
        Assert.Equal(PartyVoiceUiState.Ready, session.VoiceUiStatus.State);
        Assert.DoesNotContain("SetAudioInputMuted:False", api.Calls);

        session.SetPushToTalkPressed(false);
        session.SetPushToTalkPressed(true);
        Assert.Contains("SetAudioInputMuted:False", api.Calls);
    }

    [Fact]
    public void DisableFailClosed_WhileSpeaking_ExecutesMuteAndDestroy()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        session.SetPushToTalkPressed(true);
        api.Calls.Clear();

        session.DisableFailClosed("synthetic external fail-closed");

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);
        Assert.Equal(PartyVoiceUiState.Faulted, session.VoiceUiStatus.State);
    }

    [Fact]
    public void DisableFailClosed_WhileSpeakingDuringStateBatch_DefersUntilBatchFinished()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        session.SetPushToTalkPressed(true);
        session.BeginStateChangeBatch(Manager);
        api.Calls.Clear();

        session.DisableFailClosed("synthetic external fail-closed during batch");

        Assert.Empty(api.Calls);
        session.OnBatchFinished(Manager);
        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);
    }

    [Fact]
    public void Voice_NonInitializedAudioStateBlocksPushToTalkAfterVoiceReady()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        Assert.True(session.IsRemotePushToTalkReady);
        api.Calls.Clear();

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalChatAudioOutputChanged)
        {
            ChatControl = LocalChatControl,
            AudioOutputState = PartyAudioOutputState.NotFound,
        });
        session.OnBatchFinished(Manager);

        Assert.False(session.IsRemotePushToTalkReady);
        Assert.Equal(PartyVoiceUiState.Connecting, session.VoiceUiStatus.State);
        Assert.DoesNotContain("DestroyChatControl", api.Calls);

        api.Calls.Clear();
        session.SetPushToTalkPressed(true);
        Assert.DoesNotContain("SetAudioInputMuted:False", api.Calls);
    }

    [Fact]
    public void Voice_LastRemoteLeaveForcesMute()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        session.SetPushToTalkPressed(true);
        api.Calls.Clear();

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = RemoteChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.JoinedMuted, session.Phase);
    }

    [Fact]
    public void Voice_OnlyLastRemoteLeaveForcesMute()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToJoined(session);
        ObserveRemoteJoined(session, RemoteChatControl);
        ObserveRemoteJoined(session, SecondRemoteChatControl);
        session.OnBatchFinished(Manager);

        Assert.Contains("SetPermissions:7000:0x0005", api.Calls);
        Assert.Contains("SetPermissions:8000:0x0005", api.Calls);

        session.SetPushToTalkPressed(true);
        api.Calls.Clear();

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = RemoteChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Empty(api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.VoiceReady, session.Phase);

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = SecondRemoteChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.JoinedMuted, session.Phase);
    }

    [Fact]
    public void Voice_RemoteDestroyedForcesMute_AndLaterLeftEventIsIdempotent()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        session.SetPushToTalkPressed(true);
        api.Calls.Clear();

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlDestroyed)
        {
            ChatControl = RemoteChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "GetAudioInputMuted",
            },
            api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.JoinedMuted, session.Phase);

        api.Calls.Clear();
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = RemoteChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Voice_PermissionFailureNeverUnmutesAndDestroysLocalControl()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            SetPermissionsResult = 0x99,
        };
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToJoined(session);
        api.Calls.Clear();
        ObserveRemoteJoined(session);
        session.OnBatchFinished(Manager);

        Assert.Contains("SetPermissions:7000:0x0005", api.Calls);
        Assert.DoesNotContain("SetAudioInputMuted:False", api.Calls);
        Assert.Contains("DestroyChatControl", api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);
        Assert.Contains(logs, line => line.Contains("voice path failed closed", StringComparison.Ordinal));
    }

    [Fact]
    public void Voice_UnmuteFailureForcesMuteAndDestroysLocalControl()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToVoiceReady(session);
        api.Calls.Clear();
        api.SetAudioInputMutedResult = 0x55;

        session.SetPushToTalkPressed(true);

        Assert.Contains("SetAudioInputMuted:False", api.Calls);
        Assert.Contains("SetAudioInputMuted:True", api.Calls);
        Assert.Contains("DestroyChatControl", api.Calls);
        Assert.DoesNotContain(logs, line => line.Contains("microphone UNMUTED", StringComparison.Ordinal));
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);
        Assert.Equal(PartyVoiceUiState.Faulted, session.VoiceUiStatus.State);
    }

    [Fact]
    public void VoiceUi_NeverReportsSpeakingWhenNativeReadbackRemainsMuted()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            ReportMutedOverride = true,
        };
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToVoiceReady(session);
        Assert.Equal(PartyVoiceUiState.Ready, session.VoiceUiStatus.State);
        api.Calls.Clear();

        session.SetPushToTalkPressed(true);

        Assert.Contains("SetAudioInputMuted:False", api.Calls);
        Assert.Contains("GetAudioInputMuted", api.Calls);
        Assert.Contains("SetAudioInputMuted:True", api.Calls);
        Assert.DoesNotContain(logs, line => line.Contains("microphone UNMUTED", StringComparison.Ordinal));
        Assert.Equal(PartyVoiceUiState.Faulted, session.VoiceUiStatus.State);
    }

    [Fact]
    public void Voice_ConcurrentFailClosedDuringUnmute_ReMutesAndDestroys()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToVoiceReady(session);
        api.Calls.Clear();
        api.AfterAudioInputStateChanged = muted =>
        {
            if (!muted)
                session.DisableFailClosed("synthetic concurrent lifecycle fault");
        };

        session.SetPushToTalkPressed(true);

        Assert.Contains("SetAudioInputMuted:False", api.Calls);
        Assert.Contains("SetAudioInputMuted:True", api.Calls);
        Assert.Contains("DestroyChatControl", api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);
        Assert.Contains(
            logs,
            line => line.Contains("failed closed while the microphone was open", StringComparison.Ordinal));
    }

    [Fact]
    public void Voice_PermissionExceptionNeverUnmutesAndDestroysLocalControl()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl)
        {
            ThrowOnSetPermissions = true,
        };
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToJoined(session);
        api.Calls.Clear();
        ObserveRemoteJoined(session);
        session.OnBatchFinished(Manager);

        Assert.Contains("SetPermissions:7000:0x0005", api.Calls);
        Assert.DoesNotContain("SetAudioInputMuted:False", api.Calls);
        Assert.Contains("SetAudioInputMuted:True", api.Calls);
        Assert.Contains("DestroyChatControl", api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);
    }

    [Fact]
    public void Voice_LeavingNetworkWhileSpeaking_MutesBeforeDestroying()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        session.SetPushToTalkPressed(true);
        api.Calls.Clear();

        session.PrepareForNetworkLeave(Network);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);
    }

    [Fact]
    public void Voice_DisconnectMuteFailure_SkipsWaitAndDestroysImmediately()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        session.SetPushToTalkPressed(true);
        api.Calls.Clear();
        api.SetAudioInputMutedResult = 0x55;

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ConnectChatControlCompleted)
        {
            Result = 1,
            ErrorDetail = 0x99,
            Network = Network,
            ChatControl = LocalChatControl,
            AsyncIdentifier = session.ConnectAsyncIdentifier,
        });
        session.OnBatchFinished(Manager);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
        Assert.DoesNotContain("DisconnectChatControl", api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);
    }

    [Fact]
    public void NetworkLeaveBoundary_QueuesMutedDestroyBeforeGameLeave_AndDoesNotDuplicateIt()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(api, logs.Add, action => action());

        AdvanceToJoined(session);
        api.Calls.Clear();

        session.PrepareForNetworkLeave(Network);
        session.PrepareForNetworkLeave(Network);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.Destroying, session.Phase);
        Assert.Contains(
            logs,
            line => line.Contains(
                "pre-leave DestroyChatControl queued before Relink PartyNetworkLeaveNetwork",
                StringComparison.Ordinal));

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = LocalChatControl,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.DestroyChatControlCompleted)
        {
            Result = 0,
            LocalDevice = LocalDevice,
            ChatControl = LocalChatControl,
            AsyncIdentifier = session.DestroyAsyncIdentifier,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlDestroyed)
        {
            ChatControl = LocalChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Equal(PartyVoiceSessionPhase.Completed, session.Phase);
        Assert.Contains(logs, line => line.Contains("voice session cleanup complete", StringComparison.Ordinal));
    }

    [Fact]
    public void NetworkLeaveBoundary_DuringStateBatchMakesNoOverlappingPartyCalls()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(
            api,
            logs.Add,
            action => action());

        AdvanceToVoiceReady(session);
        session.SetPushToTalkPressed(true);
        session.BeginStateChangeBatch(Manager);
        api.Calls.Clear();

        session.PrepareForNetworkLeave(Network);

        Assert.Empty(api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.Disabled, session.Phase);
        Assert.Contains(logs, line =>
            line.Contains("issued no overlapping Party calls", StringComparison.Ordinal));
        session.OnBatchFinished(Manager);
    }

    [Fact]
    public void NetworkLeaveBoundary_IgnoresAnUntrackedNetwork()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(api, _ => { }, action => action());

        AdvanceToJoined(session);
        api.Calls.Clear();

        session.PrepareForNetworkLeave((nint)0x7777);

        Assert.Empty(api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.JoinedMuted, session.Phase);
    }

    [Fact]
    public void NetworkLeaveBoundary_DestroyErrorFailsClosedWithoutRetrying()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var logs = new List<string>();
        using var session = new PartyVoiceSession(api, logs.Add, action => action());

        AdvanceToJoined(session);
        api.Calls.Clear();
        api.DestroyChatControlResult = 0x1234;

        session.PrepareForNetworkLeave(Network);
        session.PrepareForNetworkLeave(Network);

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
        Assert.Equal(PartyVoiceSessionPhase.Disabled, session.Phase);
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
        using var session = new PartyVoiceSession(api, logs.Add, action => action());

        AdvanceToJoined(session);
        api.Calls.Clear();
        api.ThrowOnDestroyChatControl = true;

        var exception = Record.Exception(() => session.PrepareForNetworkLeave(Network));

        Assert.Null(exception);
        Assert.Equal(PartyVoiceSessionPhase.Disabled, session.Phase);
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
        using var session = new PartyVoiceSession(api, logs.Add, action => action());

        AdvanceToJoined(session);
        session.PrepareForNetworkLeave(Network);
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.DestroyChatControlCompleted)
        {
            Result = 1,
            ErrorDetail = 0x4321,
            LocalDevice = LocalDevice,
            ChatControl = LocalChatControl,
            AsyncIdentifier = session.DestroyAsyncIdentifier,
        });

        Assert.Equal(PartyVoiceSessionPhase.Disabled, session.Phase);
        Assert.Contains(
            logs,
            line => line.Contains(
                "DestroyChatControlCompleted did not confirm the owned voice session operation",
                StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownStateType_DisablesVoiceSessionWithoutNativeCalls()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(api, _ => { }, action => action());

        session.CaptureManager(Manager, "test");
        session.Observe(Manager, new PartyStateChangeSnapshot(61));
        session.OnBatchFinished(Manager);

        Assert.Equal(PartyVoiceSessionPhase.Disabled, session.Phase);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void ManagerCleanupFailure_LeavesVoiceSessionFailClosed()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(api, _ => { }, action => action());

        session.CaptureManager(Manager, "test");
        session.BeginManagerCleanup(Manager);
        session.CompleteManagerCleanup(Manager, succeeded: false);

        Assert.Equal(PartyVoiceSessionPhase.Disabled, session.Phase);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void ManagerCleanup_WhileVoiceIsOpen_ForcesMuteBeforePartyTakesOwnership()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        session.SetPushToTalkPressed(true);
        api.Calls.Clear();

        session.BeginManagerCleanup(Manager);

        Assert.Equal(new[] { "SetAudioInputMuted:True" }, api.Calls);
    }

    [Fact]
    public void Suspend_WhileVoiceIsOpen_ForcesMuteBeforeDestroying()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        session.SetPushToTalkPressed(true);
        api.Calls.Clear();

        session.SuspendBestEffort();

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
        var session = new PartyVoiceSession(api, _ => { }, action => deferred = action);

        session.CaptureManager(Manager, "test");
        ObserveReadySession(session);
        session.OnBatchFinished(Manager);
        Assert.NotNull(deferred);

        session.Dispose();
        deferred!();

        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Dispose_WhileVoiceIsOpen_MutesBeforeDestroying()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        session.SetPushToTalkPressed(true);
        api.Calls.Clear();

        session.Dispose();

        Assert.Equal(
            new[]
            {
                "SetAudioInputMuted:True",
                "DestroyChatControl",
            },
            api.Calls);
    }

    [Fact]
    public void EstablishedRemoteEntityIds_AreAvailableWithoutTalkingAndReturnCopies()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        api.EntityIds[RemoteChatControl] = "slot-0";
        api.EntityIds[SecondRemoteChatControl] = "slot-3";
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToJoined(session);
        ObserveRemoteJoined(session, RemoteChatControl);
        session.OnBatchFinished(Manager);

        Assert.Equal(["slot-0"], session.GetEstablishedRemoteEntityIds());

        ObserveRemoteJoined(session, SecondRemoteChatControl);
        session.OnBatchFinished(Manager);

        var first = session.GetEstablishedRemoteEntityIds();
        var second = session.GetEstablishedRemoteEntityIds();
        Assert.Equal(["slot-0", "slot-3"], second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void VoiceEntitySnapshot_CapturesEstablishedAndTalkingMembersTogether()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        api.EntityIds[RemoteChatControl] = "slot-0";
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        api.RemoteIndicator = PartyChatControlChatIndicator.Talking;
        session.RequestVoiceDiagnosticSample();

        var snapshot = session.GetVoiceEntitySnapshot();

        Assert.Equal(["slot-0"], snapshot.EstablishedRemoteEntityIds);
        Assert.Equal(["slot-0"], snapshot.TalkingRemoteEntityIds);
    }

    [Fact]
    public void EstablishedRemoteEntityIds_IgnoreFailedOrBlankIdsAndFailClosedOnDuplicates()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        api.EntityIds[RemoteChatControl] = "slot-0";
        api.EntityIds[SecondRemoteChatControl] = " ";
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToJoined(session);
        ObserveRemoteJoined(session, RemoteChatControl);
        ObserveRemoteJoined(session, SecondRemoteChatControl);
        session.OnBatchFinished(Manager);

        Assert.Equal(["slot-0"], session.GetEstablishedRemoteEntityIds());

        api.EntityIds[SecondRemoteChatControl] = "slot-0";
        session.RequestVoiceDiagnosticSample();

        Assert.Empty(session.GetEstablishedRemoteEntityIds());

        api.EntityIdResults[RemoteChatControl] = 0x55;
        session.RequestVoiceDiagnosticSample();

        Assert.Equal(["slot-0"], session.GetEstablishedRemoteEntityIds());
    }

    [Fact]
    public void EstablishedRemoteEntityIds_ClearOnLeaveAndFailClosed()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        api.EntityIds[RemoteChatControl] = "slot-0";
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        Assert.Equal(["slot-0"], session.GetEstablishedRemoteEntityIds());

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
        {
            Network = Network,
            ChatControl = RemoteChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Empty(session.GetEstablishedRemoteEntityIds());

        ObserveRemoteJoined(session);
        session.OnBatchFinished(Manager);
        Assert.Equal(["slot-0"], session.GetEstablishedRemoteEntityIds());

        session.DisableFailClosed("synthetic fail-closed");
        Assert.Empty(session.GetEstablishedRemoteEntityIds());
    }

    [Fact]
    public void EstablishedRemoteEntityIds_GetterExceptionHidesIdentityWithoutTearingDownVoice()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        api.EntityIds[RemoteChatControl] = "slot-0";
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToVoiceReady(session);
        Assert.Equal(["slot-0"], session.GetEstablishedRemoteEntityIds());

        api.EntityIdThrows[RemoteChatControl] = true;
        session.RequestVoiceDiagnosticSample();

        Assert.Empty(session.GetEstablishedRemoteEntityIds());
        Assert.Equal(PartyVoiceSessionPhase.VoiceReady, session.Phase);
        Assert.DoesNotContain("DestroyChatControl", api.Calls);
    }

    [Fact]
    public void EstablishedVoiceParticipantCount_StartsZeroAndTracksJoinedAndPermissionedRemotes()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        Assert.Equal(0, session.EstablishedVoiceParticipantCount);

        AdvanceToJoined(session);
        Assert.Equal(1, session.EstablishedVoiceParticipantCount);

        ObserveRemoteJoined(session);
        Assert.Equal(1, session.EstablishedVoiceParticipantCount);
        session.OnBatchFinished(Manager);
        Assert.Equal(2, session.EstablishedVoiceParticipantCount);

        ObserveRemoteJoined(session, SecondRemoteChatControl);
        session.OnBatchFinished(Manager);
        Assert.Equal(3, session.EstablishedVoiceParticipantCount);
    }

    [Fact]
    public void DisableFailClosed_AfterJoined_ClearsEstablishedVoiceCount()
    {
        var api = new FakePartyChatControlApi(LocalDevice, LocalChatControl);
        using var session = new PartyVoiceSession(
            api,
            _ => { },
            action => action());

        AdvanceToJoined(session);
        Assert.Equal(1, session.EstablishedVoiceParticipantCount);

        session.DisableFailClosed("local Party user kicked");

        Assert.Equal(0, session.EstablishedVoiceParticipantCount);
        Assert.Equal(PartyVoiceSessionPhase.Disabled, session.Phase);
    }
    private static void ObserveReadySession(PartyVoiceSession session)
    {
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.AuthenticateLocalUserCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateEndpointCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = LocalUser,
            Endpoint = Endpoint,
        });
    }

    private static void AdvanceToJoined(
        PartyVoiceSession session,
        bool observeAudioStates = true,
        PartyVoiceSessionPhase expectedPhase = PartyVoiceSessionPhase.JoinedMuted)
    {
        session.CaptureManager(Manager, "test");
        ObserveReadySession(session);
        session.OnBatchFinished(Manager);

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.CreateChatControlCompleted)
        {
            Result = 0,
            LocalDevice = LocalDevice,
            LocalUser = LocalUser,
            ChatControl = LocalChatControl,
            AsyncIdentifier = session.CreateAsyncIdentifier,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlCreated)
        {
            ChatControl = LocalChatControl,
        });
        if (observeAudioStates)
        {
            session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalChatAudioInputChanged)
            {
                ChatControl = LocalChatControl,
                AudioInputState = PartyAudioInputState.Initialized,
            });
            session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.LocalChatAudioOutputChanged)
            {
                ChatControl = LocalChatControl,
                AudioOutputState = PartyAudioOutputState.Initialized,
            });
        }
        session.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioInputCompleted,
            session.AudioInputAsyncIdentifier));
        session.Observe(Manager, AudioCompleted(
            PartyStateChangeType.SetChatAudioOutputCompleted,
            session.AudioOutputAsyncIdentifier));
        session.OnBatchFinished(Manager);

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ConnectChatControlCompleted)
        {
            Result = 0,
            Network = Network,
            ChatControl = LocalChatControl,
            AsyncIdentifier = session.ConnectAsyncIdentifier,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = Network,
            ChatControl = LocalChatControl,
        });
        session.OnBatchFinished(Manager);

        Assert.Equal(expectedPhase, session.Phase);
    }

    private static void AdvanceToVoiceReady(PartyVoiceSession session)
    {
        AdvanceToJoined(session);
        ObserveRemoteJoined(session);
        session.OnBatchFinished(Manager);
        Assert.Equal(PartyVoiceSessionPhase.VoiceReady, session.Phase);
    }

    private static void ObserveRemoteJoined(
        PartyVoiceSession session,
        nint remoteChatControl = default)
    {
        if (remoteChatControl == nint.Zero)
            remoteChatControl = RemoteChatControl;

        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlCreated)
        {
            ChatControl = remoteChatControl,
        });
        session.Observe(Manager, new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlJoinedNetwork)
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
        private readonly Dictionary<nint, PartyChatPermissionOptions> _permissions = [];
        private bool _muted = true;
        private PartyAudioDeviceSelectionType _inputSelectionType = PartyAudioDeviceSelectionType.SystemDefault;
        private PartyAudioDeviceSelectionType _outputSelectionType = PartyAudioDeviceSelectionType.SystemDefault;
        private string? _inputSelectionContext;
        private string? _outputSelectionContext;

        public FakePartyChatControlApi(nint localDevice, nint localChatControl)
        {
            _localDevice = localDevice;
            _localChatControl = localChatControl;
        }

        public List<string> Calls { get; } = [];

        public List<string> DiagnosticCalls { get; } = [];

        public uint ExistingLocalChatControlCount { get; init; }

        public nint[] NetworkChatControls { get; init; } = [];

        public uint GetNetworkChatControlsResult { get; init; }

        public bool? ReportMutedOverride { get; init; }

        public uint DestroyChatControlResult { get; set; }

        public bool ThrowOnDestroyChatControl { get; set; }

        public uint SetPermissionsResult { get; set; }

        public bool ThrowOnSetPermissions { get; set; }

        public uint SetAudioInputMutedResult { get; set; }

        public Action<bool>? AfterAudioInputStateChanged { get; set; }

        public PartyLocalChatControlChatIndicator? LocalIndicatorOverride { get; set; }

        public PartyChatControlChatIndicator RemoteIndicator { get; set; } =
            PartyChatControlChatIndicator.Silent;

        public Dictionary<nint, PartyChatControlChatIndicator> RemoteIndicatorOverrides { get; } = [];

        public Dictionary<nint, string> EntityIds { get; } = [];

        public Dictionary<nint, uint> EntityIdResults { get; } = [];

        public Dictionary<nint, bool> EntityIdThrows { get; } = [];

        public int IncomingAudioMuteWrites { get; private set; }

        public Dictionary<nint, PartyChatPermissionOptions> PermissionReadbackOverrides { get; } = [];

        public bool IncomingAudioMuted { get; set; }

        public Dictionary<nint, bool> IncomingAudioMutedOverrides { get; } = [];

        public float AudioRenderVolume { get; set; } = 1.0f;

        public Dictionary<nint, float> AudioRenderVolumeOverrides { get; } = [];

        public string SystemDefaultInputDeviceId { get; set; } = "default-communications-capture";

        public string SystemDefaultOutputDeviceId { get; set; } = "default-communications-render";

        public bool ThrowOnDiagnosticGetter { get; set; }

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

        public uint GetNetworkChatControls(nint network, out nint[] chatControls)
        {
            Calls.Add("GetNetworkChatControls");
            chatControls = NetworkChatControls.ToArray();
            return GetNetworkChatControlsResult;
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

        public uint GetPermissions(
            nint localChatControl,
            nint targetChatControl,
            out PartyChatPermissionOptions permissions)
        {
            DiagnosticCalls.Add($"GetPermissions:{(nuint)targetChatControl:X}");
            ThrowIfDiagnosticGetterRequested();
            if (!PermissionReadbackOverrides.TryGetValue(targetChatControl, out permissions))
                _permissions.TryGetValue(targetChatControl, out permissions);
            return 0;
        }

        public uint GetAudioInput(
            nint localChatControl,
            out PartyAudioDeviceSelectionType selectionType,
            out string? selectionContext,
            out string? deviceId)
        {
            DiagnosticCalls.Add("GetAudioInput");
            ThrowIfDiagnosticGetterRequested();
            selectionType = _inputSelectionType;
            selectionContext = _inputSelectionContext;
            deviceId = _inputSelectionType == PartyAudioDeviceSelectionType.Manual
                ? _inputSelectionContext
                : SystemDefaultInputDeviceId;
            return 0;
        }

        public uint GetAudioOutput(
            nint localChatControl,
            out PartyAudioDeviceSelectionType selectionType,
            out string? selectionContext,
            out string? deviceId)
        {
            DiagnosticCalls.Add("GetAudioOutput");
            ThrowIfDiagnosticGetterRequested();
            selectionType = _outputSelectionType;
            selectionContext = _outputSelectionContext;
            deviceId = _outputSelectionType == PartyAudioDeviceSelectionType.Manual
                ? _outputSelectionContext
                : SystemDefaultOutputDeviceId;
            return 0;
        }

        public uint GetAudioRenderVolume(
            nint localChatControl,
            nint targetChatControl,
            out float volume)
        {
            DiagnosticCalls.Add($"GetAudioRenderVolume:{(nuint)targetChatControl:X}");
            ThrowIfDiagnosticGetterRequested();
            volume = AudioRenderVolumeOverrides.TryGetValue(targetChatControl, out var configured)
                ? configured
                : AudioRenderVolume;
            return 0;
        }

        public uint GetIncomingAudioMuted(
            nint localChatControl,
            nint targetChatControl,
            out bool muted)
        {
            DiagnosticCalls.Add($"GetIncomingAudioMuted:{(nuint)targetChatControl:X}");
            ThrowIfDiagnosticGetterRequested();
            muted = IncomingAudioMutedOverrides.TryGetValue(targetChatControl, out var configured)
                ? configured
                : IncomingAudioMuted;
            return 0;
        }

        public uint GetEntityId(nint chatControl, out string? entityId)
        {
            DiagnosticCalls.Add($"GetEntityId:{(nuint)chatControl:X}");
            ThrowIfDiagnosticGetterRequested();
            if (EntityIdThrows.TryGetValue(chatControl, out var shouldThrow) && shouldThrow)
                throw new InvalidOperationException("Synthetic EntityId lookup failure.");
            if (EntityIdResults.TryGetValue(chatControl, out var result) && result != 0)
            {
                entityId = null;
                return result;
            }

            entityId = EntityIds.TryGetValue(chatControl, out var configured)
                ? configured
                : $"entity-{(nuint)chatControl:X}";
            return 0;
        }

        public uint SetIncomingAudioMuted(
            nint localChatControl,
            nint targetChatControl,
            bool muted)
        {
            IncomingAudioMuteWrites++;
            Calls.Add($"SetIncomingAudioMuted:{(nuint)targetChatControl:X}:{muted}");
            return 0;
        }

        public uint GetLocalChatIndicator(
            nint localChatControl,
            out PartyLocalChatControlChatIndicator indicator)
        {
            DiagnosticCalls.Add("GetLocalChatIndicator");
            ThrowIfDiagnosticGetterRequested();
            indicator = LocalIndicatorOverride ?? (_muted
                ? PartyLocalChatControlChatIndicator.AudioInputMuted
                : PartyLocalChatControlChatIndicator.Silent);
            return 0;
        }

        public uint GetChatIndicator(
            nint localChatControl,
            nint targetChatControl,
            out PartyChatControlChatIndicator indicator)
        {
            DiagnosticCalls.Add($"GetChatIndicator:{(nuint)targetChatControl:X}");
            ThrowIfDiagnosticGetterRequested();
            indicator = RemoteIndicatorOverrides.TryGetValue(targetChatControl, out var configured)
                ? configured
                : RemoteIndicator;
            return 0;
        }

        public uint GetErrorMessage(uint error, out string? errorMessage)
        {
            DiagnosticCalls.Add($"GetErrorMessage:0x{error:X8}");
            errorMessage = $"Synthetic Party error 0x{error:X8}";
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
            if (SetPermissionsResult == 0)
                _permissions[targetChatControl] = permissions;
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
            _inputSelectionType = selectionType;
            _inputSelectionContext = selectionContext;
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
            _outputSelectionType = selectionType;
            _outputSelectionContext = selectionContext;
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

        private void ThrowIfDiagnosticGetterRequested()
        {
            if (ThrowOnDiagnosticGetter)
                throw new InvalidOperationException("Synthetic diagnostic getter failure.");
        }
    }
}
