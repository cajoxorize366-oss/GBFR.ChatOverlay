using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Overlay;
using System.Diagnostics;
using System.Reflection;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatOverlayPeerHotkeyTests
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VirtualKeyBackspace = 0x08;

    [Fact]
    public void KeyboardCapture_CanBeRepeatedAfterAStaleKeyDown_AndCustomTextSends()
    {
        var action = new QuickActionConfiguration
        {
            Name = "Custom",
            Kind = QuickActionKind.CustomText,
            Text = "Hello from custom text",
        };
        var configuration = new Config
        {
            QuickActions = [action],
        };
        var transport = new RecordingTransport();
        using var peer = CreatePeer(configuration, transport);
        var request = new BindingCaptureRequest(
            BindingTarget.QuickAction,
            BindingCaptureDevice.Keyboard,
            action.Id);

        peer.BeginBindingCapture(request);
        PressAndRelease(peer, 'P');
        Assert.Equal("P", action.KeyboardBinding);

        // Reproduce a missed WM_KEYUP between two capture sessions.
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'K', nint.Zero);

        peer.BeginBindingCapture(request);
        PressAndRelease(peer, 'Q');
        Assert.Equal("Q", action.KeyboardBinding);

        PressAndRelease(peer, 'Q');
        Assert.Equal(0, transport.SendCount);
        Assert.Equal(1, peer.DrainQuickActionRequests());
        Assert.Equal("Hello from custom text", transport.LastMessage);
        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public void ControllerBinding_CanBeClearedWithoutAConnectedController()
    {
        var configuration = new Config
        {
            OpenChatControllerBinding = "RS",
        };
        using var peer = CreatePeer(configuration, new RecordingTransport());
        var request = new BindingCaptureRequest(
            BindingTarget.OpenChat,
            BindingCaptureDevice.Controller,
            null);

        var cleared = peer.ClearBinding(request);

        Assert.True(cleared);
        Assert.Equal(string.Empty, configuration.OpenChatControllerBinding);
    }

    [Fact]
    public void ControllerCapture_RejectsGameReservedDPadDown()
    {
        var configuration = new Config();
        using var peer = CreatePeer(configuration, new RecordingTransport());
        peer.BeginBindingCapture(new BindingCaptureRequest(
            BindingTarget.OpenChat,
            BindingCaptureDevice.Controller,
            null));

        peer.ObserveNativeInputSnapshot(new DirectInputBrokerSnapshot
        {
            AbiVersion = DirectInputBrokerSnapshot.ExpectedAbiVersion,
            StructSize = DirectInputBrokerSnapshot.ExpectedStructSize,
            Readiness = DirectInputBrokerReadiness.Controller,
            ControllerButtons = ControllerButtons.DPadDown,
        });

        Assert.Equal(string.Empty, configuration.OpenChatControllerBinding);
        Assert.Contains("DPadDown", GetPrivateField<string>(peer, "_captureStatusText"));

        peer.ObserveNativeInputSnapshot(new DirectInputBrokerSnapshot
        {
            AbiVersion = DirectInputBrokerSnapshot.ExpectedAbiVersion,
            StructSize = DirectInputBrokerSnapshot.ExpectedStructSize,
            Readiness = DirectInputBrokerReadiness.Controller,
            ControllerButtons = ControllerButtons.None,
        });
        peer.ObserveNativeInputSnapshot(new DirectInputBrokerSnapshot
        {
            AbiVersion = DirectInputBrokerSnapshot.ExpectedAbiVersion,
            StructSize = DirectInputBrokerSnapshot.ExpectedStructSize,
            Readiness = DirectInputBrokerReadiness.Controller,
            ControllerButtons = ControllerButtons.X,
        });
        peer.ObserveNativeInputSnapshot(new DirectInputBrokerSnapshot
        {
            AbiVersion = DirectInputBrokerSnapshot.ExpectedAbiVersion,
            StructSize = DirectInputBrokerSnapshot.ExpectedStructSize,
            Readiness = DirectInputBrokerReadiness.Controller,
            ControllerButtons = ControllerButtons.None,
        });

        Assert.Equal("X", configuration.OpenChatControllerBinding);
    }

    [Fact]
    public void SettingsBinding_DoesNotClearItsOnlyConfiguredDevice()
    {
        var configuration = new Config
        {
            SettingsMenuKeyboardBinding = "F10",
            SettingsMenuControllerBinding = string.Empty,
        };
        using var peer = CreatePeer(configuration, new RecordingTransport());
        var request = new BindingCaptureRequest(
            BindingTarget.SettingsMenu,
            BindingCaptureDevice.Keyboard,
            null);

        var cleared = peer.ClearBinding(request);

        Assert.False(cleared);
        Assert.Equal("F10", configuration.SettingsMenuKeyboardBinding);
    }

    [Fact]
    public void GlobalMuteHotkey_TogglesRoomChatBlacklistOncePerPress()
    {
        var configuration = new Config
        {
            GlobalMuteKeyboardBinding = "M",
        };
        var blacklist = new ChatBlacklist();
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            chatBlacklist: blacklist);

        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'M', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'M', nint.Zero);
        Assert.True(blacklist.AreAllRemotePlayersMuted);
        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'M', nint.Zero);
        PressAndRelease(peer, 'M');

        Assert.False(blacklist.AreAllRemotePlayersMuted);
    }

    [Fact]
    public void GlobalMuteHotkey_DoesNotWriteVoiceMuteSlots()
    {
        var configuration = new Config
        {
            GlobalMuteKeyboardBinding = "N",
        };
        var operationCount = 0;
        var blacklist = new ChatBlacklist();
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            getPlayerMuteSlots: () => PartyPlayerMuteSlotStatus.Unavailable("unavailable"),
            setPlayerMuted: (_, _) =>
            {
                operationCount++;
                return new PartyPlayerMuteOperationResult(true, "unexpected");
            },
            chatBlacklist: blacklist);

        PressAndRelease(peer, 'N');

        Assert.Equal(0, operationCount);
        Assert.True(blacklist.AreAllRemotePlayersMuted);
    }

    [Fact]
    public void RemotePlayerChatMuteHotkey_MapsDisplayPlayerOneToChatSlotTwo_WithoutVoiceMute()
    {
        var configuration = new Config
        {
            RemotePlayer1ChatMuteKeyboardBinding = "B",
        };
        var voiceMuteOperationCount = 0;
        var blacklist = new ChatBlacklist();
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            setPlayerMuted: (_, _) =>
            {
                voiceMuteOperationCount++;
                return new PartyPlayerMuteOperationResult(true, "unexpected");
            },
            chatBlacklist: blacklist);

        PressAndRelease(peer, 'B');

        Assert.Equal(0, voiceMuteOperationCount);
        Assert.True(blacklist.IsMuted(2));
        Assert.False(blacklist.IsMuted(3));
        Assert.False(blacklist.IsMuted(4));
    }

    [Fact]
    public void SettingsMenu_SwallowsQuickActionsAndGlobalMuteHotkeys()
    {
        var action = new QuickActionConfiguration
        {
            Kind = QuickActionKind.CustomText,
            Text = "should not send",
            KeyboardBinding = "P",
        };
        var configuration = new Config
        {
            GlobalMuteKeyboardBinding = "M",
            QuickActions = [action],
        };
        var transport = new RecordingTransport();
        var muteOperations = 0;
        using var peer = CreatePeer(
            configuration,
            transport,
            isOnlineRoomActive: () => true,
            getPlayerMuteSlots: () =>
            [
                new PartyPlayerMuteSlotStatus(2, true, false, string.Empty),
            ],
            setPlayerMuted: (_, _) =>
            {
                muteOperations++;
                return new PartyPlayerMuteOperationResult(true, "ok");
            });

        peer.SetSettingsMenuOpen(true);
        var actionDown = peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'P', nint.Zero);
        var muteDown = peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'M', nint.Zero);

        Assert.True(actionDown.Handled);
        Assert.True(muteDown.Handled);
        Assert.Equal(0, transport.SendCount);
        Assert.Equal(0, muteOperations);
    }

    [Fact]
    public void Tick_ReadsOneVoiceIndicatorSnapshotAndSharesItsNormalizedTalkingList()
    {
        var reads = 0;
        var configuration = new Config
        {
            EnableOverlay = false,
            EnableVoiceIndicators = true,
        };
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            getVoiceIndicatorSnapshot: () =>
            {
                reads++;
                return new PartyVoiceIndicatorSnapshot(
                    true,
                    [3, 3, 0],
                    [3, 1, 3],
                    [3, 3, 0, 4]);
            },
            getRemotePlayerName: playerNumber => $"Remote {playerNumber}");
        SetInitialized(peer);
        SetPrivateField(peer, "_statusText", "existing bottom status");

        peer.Tick();

        Assert.Equal(1, reads);
        var cachedSnapshot = GetPrivateField<PartyVoiceIndicatorSnapshot>(
            peer,
            "_voiceIndicatorSnapshot");
        Assert.True(cachedSnapshot!.IsValid);
        Assert.Equal([3], cachedSnapshot.EstablishedRemotePlayers);
        Assert.Equal([1, 3], cachedSnapshot.OccupiedRemotePlayers);
        Assert.Equal([3], cachedSnapshot.TalkingRemotePlayers);
        var startedTalkers = Assert.IsType<HashSet<int>>(
            GetPrivateField<HashSet<int>>(peer, "_talkingRemotePlayers"));
        Assert.Equal([3], startedTalkers);
        Assert.Equal("existing bottom status", GetPrivateField<string>(peer, "_statusText"));
    }

    [Fact]
    public void VoiceTalkers_NormalizeRemoteNamesInOrderAndDeduplicate()
    {
        using var peer = CreatePeer(
            new Config { InterfaceLanguage = UiLanguage.SimplifiedChinese },
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            getVoiceUiStatus: () => new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            getVoiceIndicatorSnapshot: () => new PartyVoiceIndicatorSnapshot(
                true,
                [],
                [],
                [3, 1, 3, 0, 4, 2]),
            getRemotePlayerName: playerNumber => $"Remote {playerNumber}");
        SetInitialized(peer);

        peer.Tick();

        var talkers = ResolveVoiceTalkerNames(
            peer,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            GetPrivateField<PartyVoiceIndicatorSnapshot>(peer, "_voiceIndicatorSnapshot")!);
        var presentation = VoiceOverlayPresenter.Create(
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            UiLanguage.SimplifiedChinese,
            talkers);

        Assert.Equal(["Remote 1", "Remote 2", "Remote 3"], talkers);
        Assert.Equal("[语音] Remote 1、Remote 2、Remote 3 正在使用语音", presentation.Text);
    }

    [Fact]
    public void VoiceTalkers_AddLocalOnlyWhenNativeStateIsSpeaking()
    {
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            getLocalPlayerName: () => "Kuro");
        var snapshot = new PartyVoiceIndicatorSnapshot(true, [], [], []);

        var readyTalkers = ResolveVoiceTalkerNames(
            peer,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            snapshot);
        var speakingTalkers = ResolveVoiceTalkerNames(
            peer,
            new PartyVoiceUiStatus(PartyVoiceUiState.Speaking),
            snapshot);

        Assert.Empty(readyTalkers);
        Assert.Equal(["Kuro"], speakingTalkers);
    }

    [Fact]
    public void VoiceTalkers_PutLocalBeforeRemoteAndFallbackWhenLocalNameFails()
    {
        using var peer = CreatePeer(
            new Config { InterfaceLanguage = UiLanguage.English },
            new RecordingTransport(),
            getLocalPlayerName: () => throw new InvalidOperationException("local name failure"),
            getRemotePlayerName: playerNumber => $"Remote {playerNumber}");
        var snapshot = new PartyVoiceIndicatorSnapshot(true, [], [], [2]);

        var talkers = ResolveVoiceTalkerNames(
            peer,
            new PartyVoiceUiStatus(PartyVoiceUiState.Speaking),
            snapshot);
        var presentation = VoiceOverlayPresenter.Create(
            new PartyVoiceUiStatus(PartyVoiceUiState.Speaking),
            UiLanguage.English,
            talkers);

        Assert.Equal(["You", "Remote 2"], talkers);
        Assert.Equal("[Voice] You, Remote 2 using voice", presentation.Text);
    }

    [Fact]
    public void VoiceTalkers_EmptyLocalNameFallsBackToChineseSelfLabel()
    {
        using var peer = CreatePeer(
            new Config { InterfaceLanguage = UiLanguage.SimplifiedChinese },
            new RecordingTransport(),
            getLocalPlayerName: () => " ");

        var talkers = ResolveVoiceTalkerNames(
            peer,
            new PartyVoiceUiStatus(PartyVoiceUiState.Speaking),
            new PartyVoiceIndicatorSnapshot(true, [], [], []));

        Assert.Equal(["你"], talkers);
    }

    [Fact]
    public void VoiceTalkers_RemoteNameFailureUsesUiPlayerNumberFallback()
    {
        using var peer = CreatePeer(
            new Config { InterfaceLanguage = UiLanguage.SimplifiedChinese },
            new RecordingTransport(),
            getRemotePlayerName: _ => null);

        var talkers = ResolveVoiceTalkerNames(
            peer,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            new PartyVoiceIndicatorSnapshot(true, [], [], [1, 3]));

        Assert.Equal(["玩家 2", "玩家 4"], talkers);
    }

    [Fact]
    public void VoiceTalkers_DoNotConsumeInvalidOrNonReadySnapshotTalkers()
    {
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            getRemotePlayerName: playerNumber => $"Remote {playerNumber}");
        var validSnapshot = new PartyVoiceIndicatorSnapshot(true, [], [], [1]);

        var nonReadyTalkers = ResolveVoiceTalkerNames(
            peer,
            new PartyVoiceUiStatus(PartyVoiceUiState.WaitingForPeer),
            validSnapshot);
        var invalidTalkers = ResolveVoiceTalkerNames(
            peer,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            PartyVoiceIndicatorSnapshot.Unavailable);

        Assert.Empty(nonReadyTalkers);
        Assert.Empty(invalidTalkers);
    }

    [Fact]
    public void VoiceTalkers_LocalSpeakingSurvivesUnavailableRemoteSnapshot()
    {
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            getLocalPlayerName: () => "Kuro");

        var talkers = ResolveVoiceTalkerNames(
            peer,
            new PartyVoiceUiStatus(PartyVoiceUiState.Speaking),
            PartyVoiceIndicatorSnapshot.Unavailable);

        Assert.Equal(["Kuro"], talkers);
    }

    [Fact]
    public void Tick_PublishesUnavailableAndClearsStartedTalkersWhenSnapshotGetterThrows()
    {
        var shouldThrow = false;
        var logs = new List<string>();
        using var peer = CreatePeer(
            new Config
            {
                EnableOverlay = false,
                EnableVoiceIndicators = true,
            },
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            getVoiceIndicatorSnapshot: () =>
            {
                if (shouldThrow)
                    throw new InvalidOperationException("snapshot failure");
                return new PartyVoiceIndicatorSnapshot(
                    true,
                    [1],
                    [1],
                    [1]);
            },
            log: logs.Add);
        SetInitialized(peer);

        peer.Tick();
        shouldThrow = true;
        peer.Tick();
        peer.Tick();

        var cachedSnapshot = GetPrivateField<PartyVoiceIndicatorSnapshot>(
            peer,
            "_voiceIndicatorSnapshot");
        Assert.False(cachedSnapshot!.IsValid);
        Assert.Empty(Assert.IsType<HashSet<int>>(
            GetPrivateField<HashSet<int>>(peer, "_talkingRemotePlayers")));
        Assert.Empty(ResolveVoiceTalkerNames(
            peer,
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            cachedSnapshot));
        Assert.Single(logs, line =>
            line.Contains(
                "Voice indicator membership snapshot lookup failed",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Tick_LogsVoiceIndicatorSnapshotTransitionsAndOneRecovery()
    {
        var shouldThrow = false;
        var talking = false;
        var logs = new List<string>();
        using var peer = CreatePeer(
            new Config
            {
                EnableOverlay = false,
                EnableVoiceIndicators = true,
            },
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            getVoiceIndicatorSnapshot: () =>
            {
                if (shouldThrow)
                    throw new InvalidOperationException("snapshot failure");
                return new PartyVoiceIndicatorSnapshot(
                    true,
                    [2],
                    [1, 2],
                    talking ? [2] : []);
            },
            log: logs.Add);
        SetInitialized(peer);

        peer.Tick();
        peer.Tick();
        talking = true;
        peer.Tick();
        shouldThrow = true;
        peer.Tick();
        shouldThrow = false;
        peer.Tick();

        Assert.Equal(
            3,
            logs.Count(line => line.Contains(
                "Voice indicator membership snapshot changed",
                StringComparison.Ordinal)));
        Assert.Contains(logs, line =>
            line.Contains("established=[2]", StringComparison.Ordinal) &&
            line.Contains("occupied=[1,2]", StringComparison.Ordinal) &&
            line.Contains("talking=[2]", StringComparison.Ordinal));
        Assert.Single(logs, line =>
            line.Contains(
                "Voice indicator membership snapshot lookup recovered",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Suspend_PublishesUnavailableVoiceSnapshotAndClearsStartedTalkers()
    {
        using var peer = CreatePeer(
            new Config
            {
                EnableOverlay = false,
                EnableVoiceIndicators = true,
            },
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            getVoiceIndicatorSnapshot: () => new PartyVoiceIndicatorSnapshot(
                true,
                [1],
                [1],
                [1]));
        SetInitialized(peer);

        peer.Tick();
        peer.Suspend();

        var cachedSnapshot = GetPrivateField<PartyVoiceIndicatorSnapshot>(
            peer,
            "_voiceIndicatorSnapshot");
        Assert.False(cachedSnapshot!.IsValid);
        Assert.Empty(Assert.IsType<HashSet<int>>(
            GetPrivateField<HashSet<int>>(peer, "_talkingRemotePlayers")));
    }

    [Fact]
    public void CompactMode_KeepsRenderRequestedForVoiceHudWhileChatIsClosed()
    {
        var configuration = new Config
        {
            CompactMode = true,
        };
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true);
        SetInitialized(peer);

        Assert.True(peer.WantsRender);
    }

    [Fact]
    public void VoiceIndicators_RequestRenderWhenChatOverlayIsDisabled()
    {
        var configuration = new Config
        {
            EnableOverlay = false,
            EnableVoiceIndicators = true,
        };
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true);
        SetInitialized(peer);

        Assert.True(peer.WantsRender);
    }

    [Fact]
    public void VoiceIndicators_StopRequestingRenderAfterOnlineRoomExit()
    {
        var onlineRoomActive = true;
        var configuration = new Config
        {
            EnableOverlay = false,
            EnableVoiceIndicators = true,
        };
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => onlineRoomActive);
        SetInitialized(peer);

        Assert.True(peer.WantsRender);
        onlineRoomActive = false;
        peer.Tick();

        Assert.False(peer.WantsRender);
    }

    [Fact]
    public void SettingsMenu_ForwardsUnboundEditingKeysToImGui()
    {
        var configuration = new Config
        {
            GlobalMuteKeyboardBinding = "M",
        };
        using var peer = CreatePeer(configuration, new RecordingTransport());

        peer.SetSettingsMenuOpen(true);
        var backspaceDown = peer.ObserveWindowMessage(
            nint.Zero,
            WmKeyDown,
            new nint(VirtualKeyBackspace),
            nint.Zero);
        var backspaceUp = peer.ObserveWindowMessage(
            nint.Zero,
            WmKeyUp,
            new nint(VirtualKeyBackspace),
            nint.Zero);

        Assert.False(backspaceDown.Handled);
        Assert.False(backspaceUp.Handled);
    }

    [Theory]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, true, true, true, true, true)]
    [InlineData(false, true, false, true, true, false)]
    public void ImeTextCapture_IncludesSettingsAndActiveChatComposer(
        bool settingsMenuOpen,
        bool overlayEnabled,
        bool onlineRoomActive,
        bool captureKeyboard,
        bool composerOpen,
        bool expected)
    {
        Assert.Equal(expected, ChatOverlayPeer.ShouldCaptureImeTextInput(
            settingsMenuOpen,
            overlayEnabled,
            onlineRoomActive,
            captureKeyboard,
            composerOpen));
    }

    [Fact]
    public void RemovedQuickActionsPanelBinding_IsNotAnEnumCaptureTarget()
    {
        Assert.DoesNotContain("QuickActionsPanel", Enum.GetNames<BindingTarget>());
    }

    [Fact]
    public void NumpadCustomTextHotkey_SendsOnRenderThreadDrain()
    {
        var action = new QuickActionConfiguration
        {
            Kind = QuickActionKind.CustomText,
            Text = "快放奥义",
            KeyboardBinding = "VK_61",
        };
        var configuration = new Config { QuickActions = [action] };
        var transport = new RecordingTransport();
        using var peer = CreatePeer(
            configuration,
            transport,
            isOnlineRoomActive: () => true);

        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, new nint(0x61), nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, new nint(0x61), nint.Zero);

        Assert.Equal(0, transport.SendCount);
        Assert.Equal(1, peer.DrainQuickActionRequests());
        Assert.Equal("快放奥义", transport.LastMessage);
        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public void PushToTalkWindowHotkey_ReportsOnlyPhysicalPressAndReleaseEdges()
    {
        var configuration = new Config
        {
            EnableVoiceInput = true,
            PushToTalkKeyboardBinding = "U",
        };
        var states = new List<bool>();
        var logs = new List<string>();
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            canUseVoicePushToTalk: () => true,
            setVoicePushToTalkPressed: states.Add,
            log: logs.Add);
        SetInitialized(peer);

        var firstDown = peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        var repeatedDown = peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        Assert.Equal("[语音] 正在通话中", GetPrivateField<string>(peer, "_statusText"));
        var release = peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'U', nint.Zero);

        Assert.True(firstDown.Handled);
        Assert.True(repeatedDown.Handled);
        Assert.True(release.Handled);
        Assert.Equal([true, false], states);
        Assert.Null(GetPrivateField<string>(peer, "_statusText"));
        Assert.Single(logs, line =>
            line.Contains("entered the safety gate", StringComparison.Ordinal));
    }

    [Fact]
    public void PushToTalkWindowHotkey_DoesNotActivateMidHoldWhenPeerBecomesReady()
    {
        var configuration = new Config
        {
            EnableVoiceInput = true,
            PushToTalkKeyboardBinding = "U",
        };
        var canUse = false;
        var states = new List<bool>();
        var logs = new List<string>();
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            canUseVoicePushToTalk: () => canUse,
            setVoicePushToTalkPressed: states.Add,
            log: logs.Add);
        SetInitialized(peer);

        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        canUse = true;
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        Assert.Empty(states);

        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'U', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'U', nint.Zero);

        Assert.Equal([true, false], states);
        Assert.Single(logs, line =>
            line.Contains("Party voice is not ready", StringComparison.Ordinal));
    }

    [Fact]
    public void PushToTalkWindowHotkey_TickHeartbeatsOnlyWhilePhysicalBindingIsDown()
    {
        var configuration = new Config
        {
            EnableVoiceInput = true,
            PushToTalkKeyboardBinding = "U",
        };
        var physicalKeys = new HashSet<int> { 'U' };
        var timestamp = 0L;
        var states = new List<bool>();
        VoicePushToTalkSafetyGate? gate = null;
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            canUseVoicePushToTalk: () => true,
            setVoicePushToTalkPressed: states.Add,
            createWindowVoicePushToTalkGate: callback => gate = new VoicePushToTalkSafetyGate(
                callback,
                log: null,
                heartbeatTimeout: TimeSpan.FromMilliseconds(350),
                getTimestamp: () => timestamp,
                startWatchdog: false),
            isWindowKeyDown: virtualKey => physicalKeys.Contains(virtualKey));
        SetInitialized(peer);

        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        timestamp += Stopwatch.Frequency * 300 / 1000;
        peer.Tick();
        timestamp += Stopwatch.Frequency * 300 / 1000;
        gate!.CheckForTimeout();
        Assert.Equal([true], states);

        physicalKeys.Clear();
        peer.Tick();
        Assert.Equal([true, false], states);
    }

    [Fact]
    public void PushToTalkWindowHotkey_ReadinessLossRevokesHoldUntilPhysicalReleaseAndRepress()
    {
        var configuration = new Config
        {
            EnableVoiceInput = true,
            PushToTalkKeyboardBinding = "U",
        };
        var canUse = true;
        var physicalKeys = new HashSet<int> { 'U' };
        var states = new List<bool>();
        var logs = new List<string>();
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            canUseVoicePushToTalk: () => canUse,
            setVoicePushToTalkPressed: states.Add,
            isWindowKeyDown: virtualKey => physicalKeys.Contains(virtualKey),
            log: logs.Add);
        SetInitialized(peer);

        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        canUse = false;
        peer.Tick();

        Assert.Equal([true, false], states);
        Assert.Null(GetPrivateField<string>(peer, "_statusText"));
        Assert.Single(logs, line =>
            line.Contains("hold was revoked", StringComparison.Ordinal));

        canUse = true;
        peer.Tick();
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        Assert.Equal([true, false], states);

        physicalKeys.Clear();
        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'U', nint.Zero);
        physicalKeys.Add('U');
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'U', nint.Zero);

        Assert.Equal([true, false, true, false], states);
    }

    [Fact]
    public void PushToTalkWindowHotkey_TimesOutWithoutTickAfterLostKeyUp()
    {
        var configuration = new Config
        {
            EnableVoiceInput = true,
            PushToTalkKeyboardBinding = "U",
        };
        var timestamp = 0L;
        var states = new List<bool>();
        VoicePushToTalkSafetyGate? gate = null;
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            canUseVoicePushToTalk: () => true,
            setVoicePushToTalkPressed: states.Add,
            createWindowVoicePushToTalkGate: callback => gate = new VoicePushToTalkSafetyGate(
                callback,
                log: null,
                heartbeatTimeout: TimeSpan.FromMilliseconds(350),
                getTimestamp: () => timestamp,
                startWatchdog: false),
            isWindowKeyDown: _ => true);
        SetInitialized(peer);

        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        timestamp += Stopwatch.Frequency * 351 / 1000;
        gate!.CheckForTimeout();

        Assert.Equal([true, false], states);

        peer.Tick();
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        Assert.Equal([true, false], states);

        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'U', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'U', nint.Zero);
        Assert.Equal([true, false, true, false], states);
    }

    [Fact]
    public void PushToTalkWindowHotkey_SuspendAndDisposeForceReleaseActiveWindowSource()
    {
        var configuration = new Config
        {
            EnableVoiceInput = true,
            PushToTalkKeyboardBinding = "U",
        };
        var states = new List<bool>();
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            canUseVoicePushToTalk: () => true,
            setVoicePushToTalkPressed: states.Add,
            isWindowKeyDown: _ => true);
        SetInitialized(peer);

        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        peer.Suspend();
        Assert.Equal([true, false], states);

        peer.Resume();
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        peer.Dispose();

        Assert.Equal([true, false, true, false], states);
    }

    [Fact]
    public void PushToTalkWindowHotkey_HostUnavailableForcesRelease()
    {
        var configuration = new Config
        {
            EnableVoiceInput = true,
            PushToTalkKeyboardBinding = "U",
        };
        var states = new List<bool>();
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            canUseVoicePushToTalk: () => true,
            setVoicePushToTalkPressed: states.Add,
            isWindowKeyDown: _ => true);
        SetInitialized(peer);

        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        peer.OnHostUnavailable("peer-local failure: synthetic test");

        Assert.Equal([true, false], states);
        Assert.False(peer.IsInitialized);
    }

    [Fact]
    public void QuickActionHotkeys_QueuePhysicalEdgesWithoutReplacingTheGamesCooldown()
    {
        var first = new QuickActionConfiguration
        {
            Kind = QuickActionKind.CustomText,
            Text = "first",
            KeyboardBinding = "P",
        };
        var second = new QuickActionConfiguration
        {
            Kind = QuickActionKind.CustomText,
            Text = "second",
            KeyboardBinding = "Q",
        };
        var configuration = new Config { QuickActions = [first, second] };
        var transport = new RecordingTransport();
        using var peer = CreatePeer(
            configuration,
            transport,
            isOnlineRoomActive: () => true);

        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'P', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'P', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'P', nint.Zero);
        PressAndRelease(peer, 'P');
        PressAndRelease(peer, 'Q');

        Assert.Equal(0, transport.SendCount);
        Assert.Equal(3, peer.DrainQuickActionRequests());
        Assert.Equal(3, transport.SendCount);
        Assert.Equal("second", transport.LastMessage);
    }

    [Theory]
    [InlineData(0.0f, 0.0f, true)]
    [InlineData(96.0f, 100.0f, true)]
    [InlineData(95.9f, 100.0f, false)]
    public void HistoryNearBottom_UsesSmallStableTolerance(
        float scrollY,
        float scrollMaxY,
        bool expected)
    {
        Assert.Equal(expected, ChatOverlayPeer.IsHistoryNearBottom(scrollY, scrollMaxY));
    }

    [Theory]
    [InlineData(UiLanguage.SimplifiedChinese, "Quick Action 2", "快捷动作 2")]
    [InlineData(UiLanguage.English, "快捷动作 2", "Quick Action 2")]
    [InlineData(UiLanguage.English, "快捷动作 3 / Quick Action 3", "Quick Action 3")]
    [InlineData(UiLanguage.SimplifiedChinese, "Quick Action Dance", "Quick Action Dance")]
    public void DefaultQuickActionNames_FollowLanguageWithoutChangingCustomNames(
        UiLanguage language,
        string stored,
        string expected)
    {
        Assert.Equal(expected, ChatOverlayPeer.LocalizeQuickActionName(language, stored));
    }

    [Theory]
    [InlineData(UiLanguage.SimplifiedChinese, true, "[房主] Kuro:")]
    [InlineData(UiLanguage.English, true, "[Host] Kuro:")]
    [InlineData(UiLanguage.SimplifiedChinese, false, "Kuro:")]
    [InlineData(UiLanguage.English, false, "Kuro:")]
    public void HistorySenderLabel_UsesCurrentUiLanguageOnly(
        UiLanguage language,
        bool isHost,
        string expected)
    {
        Assert.Equal(expected, ChatOverlayPeer.FormatHistorySenderLabel("Kuro", isHost, language));
    }

    [Theory]
    [InlineData(UiLanguage.SimplifiedChinese, false, ChatCommunicationCue.Victory, "Kuro（胜利）:")]
    [InlineData(UiLanguage.English, false, ChatCommunicationCue.Victory, "Kuro (Victory):")]
    [InlineData(UiLanguage.SimplifiedChinese, false, ChatCommunicationCue.LinkAttack, "Kuro（连携攻击）:")]
    [InlineData(UiLanguage.English, false, ChatCommunicationCue.LinkAttack, "Kuro (Link Attack):")]
    [InlineData(UiLanguage.SimplifiedChinese, false, ChatCommunicationCue.Thanks, "Kuro（感谢）:")]
    [InlineData(UiLanguage.English, false, ChatCommunicationCue.Thanks, "Kuro (Thanks):")]
    [InlineData(UiLanguage.SimplifiedChinese, false, ChatCommunicationCue.Official, "Kuro（官方提示）:")]
    [InlineData(UiLanguage.English, false, ChatCommunicationCue.Official, "Kuro (Official):")]
    [InlineData(UiLanguage.SimplifiedChinese, true, ChatCommunicationCue.LinkAttack, "[房主] Kuro（连携攻击）:")]
    [InlineData(UiLanguage.English, true, ChatCommunicationCue.LinkAttack, "[Host] Kuro (Link Attack):")]
    public void HistorySenderLabel_FormatsCommunicationCues(
        UiLanguage language,
        bool isHost,
        ChatCommunicationCue communicationCue,
        string expected)
    {
        Assert.Equal(
            expected,
            ChatOverlayPeer.FormatHistorySenderLabel("Kuro", isHost, language, communicationCue));
    }

    [Fact]
    public void HistoryHostPredicate_SelfUsesPlayerOneInsteadOfStalePlayerNumber()
    {
        var selfMessage = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Kuro",
            "Hello",
            ChatMessageKind.Self,
            PlayerNumber: 3);

        Assert.False(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(selfMessage, 2));
        Assert.True(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(selfMessage, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void HistoryHostPredicate_InvalidAuthoritativeHostNeverShowsHostLabel(int? hostPlayerNumber)
    {
        var partyMessage = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Narmaya",
            "Hello",
            ChatMessageKind.Party,
            PlayerNumber: 2);

        Assert.False(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(partyMessage, hostPlayerNumber));
    }

    [Fact]
    public void HistoryHostPredicate_NullAuthoritativeHostNeverShowsHostLabel()
    {
        var partyMessage = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Narmaya",
            "Hello",
            ChatMessageKind.Party,
            PlayerNumber: 2);

        Assert.False(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(partyMessage, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void HistoryHostPredicate_UnknownPartyPlayerNeverShowsHostLabel(int playerNumber)
    {
        var partyMessage = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Unknown",
            "Hello",
            ChatMessageKind.Party,
            PlayerNumber: playerNumber);

        Assert.False(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(partyMessage, 1));
        Assert.False(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(partyMessage, 2));
        Assert.False(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(partyMessage, 4));
    }

    [Fact]
    public void HistoryHostPredicate_PartyPlayerTwoMatchesOnlyAuthoritativeHostTwo()
    {
        var partyMessage = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Narmaya",
            "Hello",
            ChatMessageKind.Party,
            PlayerNumber: 2);

        Assert.True(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(partyMessage, 2));
        Assert.False(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(partyMessage, null));
        Assert.False(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(partyMessage, 0));
        Assert.False(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(partyMessage, 5));
    }

    [Theory]
    [InlineData(UiLanguage.SimplifiedChinese, "[房主] Narmaya:", "Kuro:")]
    [InlineData(UiLanguage.English, "[Host] Narmaya:", "Kuro:")]
    public void HistoryHostLabel_CombinesAuthoritativeHostPredicateWithSenderFormatting(
        UiLanguage language,
        string expectedRemoteHostLabel,
        string expectedSelfLabel)
    {
        var selfMessage = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Kuro",
            "Hello",
            ChatMessageKind.Self,
            PlayerNumber: 4);
        var remoteHostMessage = new ChatMessage(
            2,
            DateTimeOffset.UtcNow,
            "Narmaya",
            "Hello",
            ChatMessageKind.Party,
            PlayerNumber: 2);

        var selfLabel = ChatOverlayPeer.FormatHistorySenderLabel(
            selfMessage.Sender,
            ChatOverlayPeer.IsHistoryMessageHostedByPlayer(selfMessage, 2),
            language);
        var remoteHostLabel = ChatOverlayPeer.FormatHistorySenderLabel(
            remoteHostMessage.Sender,
            ChatOverlayPeer.IsHistoryMessageHostedByPlayer(remoteHostMessage, 2),
            language);

        Assert.Equal(expectedSelfLabel, selfLabel);
        Assert.DoesNotContain("[房主]", selfLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("[Host]", selfLabel, StringComparison.Ordinal);
        Assert.Equal(expectedRemoteHostLabel, remoteHostLabel);
    }

    [Fact]
    public void HistoryFallbackNeverRebindsToTheCurrentRemoteSlot()
    {
        var requestedRemotePlayer = 0;
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            getRemotePlayerName: playerNumber =>
            {
                requestedRemotePlayer = playerNumber;
                return "Narmaya";
            });
        var message = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Player 00001234",
            "Thanks!",
            ChatMessageKind.Party,
            SenderId: 0x1234,
            PlayerNumber: 3,
            CommunicationCue: ChatCommunicationCue.Thanks);

        Assert.Equal("Player 00001234", peer.ResolveHistorySender(message));
        Assert.Equal(0, requestedRemotePlayer);
    }

    [Fact]
    public void SelfFallbackSender_StaysOnPlayerOneAndNeverResolvesRemoteName()
    {
        var resolverCalls = 0;
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            getRemotePlayerName: _ =>
            {
                resolverCalls++;
                return "trick";
            });
        var message = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Player 00000000",
            "Hello",
            ChatMessageKind.Self,
            SenderId: 0,
            PlayerNumber: 3);

        Assert.Equal(1, ChatOverlayPeer.ResolveHistoryPlayerNumber(message));
        Assert.Equal("Player 00000000", peer.ResolveHistorySender(message));
        Assert.DoesNotContain("trick", peer.ResolveHistorySender(message), StringComparison.Ordinal);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public void SelfStructuredVictory_KeepsVerifiedNameWithoutResolvingRemoteName()
    {
        var resolverCalls = 0;
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            getRemotePlayerName: _ =>
            {
                resolverCalls++;
                return "trick";
            });
        var message = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Kuro",
            "Victory!",
            ChatMessageKind.Self,
            SenderId: 0,
            PlayerNumber: 4,
            CommunicationCue: ChatCommunicationCue.Victory);

        Assert.Equal(1, ChatOverlayPeer.ResolveHistoryPlayerNumber(message));
        Assert.Equal("Kuro", peer.ResolveHistorySender(message));
        Assert.Equal(ChatCommunicationCue.Victory, ChatOverlayPeer.GetEffectiveCommunicationCue(message));
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public void StructuredVictory_KeepsTheNameResolvedAtEnqueueTime()
    {
        var requestedRemotePlayer = 0;
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            getRemotePlayerName: playerNumber =>
            {
                requestedRemotePlayer = playerNumber;
                return "Narmaya";
            });
        var message = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Narmaya",
            "Victory!",
            ChatMessageKind.Party,
            SenderId: 0x1234,
            PlayerNumber: 3,
            CommunicationCue: ChatCommunicationCue.Victory);

        var cue = ChatOverlayPeer.GetEffectiveCommunicationCue(message);

        Assert.Equal("Narmaya", peer.ResolveHistorySender(message));
        Assert.Equal(0, requestedRemotePlayer);
        Assert.Equal(ChatCommunicationCue.Victory, cue);
        Assert.Equal(
            "Narmaya (Victory):",
            ChatOverlayPeer.FormatHistorySenderLabel(
                peer.ResolveHistorySender(message),
                false,
                UiLanguage.English,
                cue));
    }

    [Fact]
    public void VerifiedLobbyNameMatchingCueSyntaxRemainsAPlayerName()
    {
        var message = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "vo_CMM_win_3",
            "Hello",
            ChatMessageKind.Party,
            SenderId: 0x1234,
            PlayerNumber: 3);
        var resolverCalls = 0;
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            getRemotePlayerName: _ =>
            {
                resolverCalls++;
                return "trick";
            });

        Assert.Equal("vo_CMM_win_3", peer.ResolveHistorySender(message));
        Assert.Equal(ChatCommunicationCue.None, ChatOverlayPeer.GetEffectiveCommunicationCue(message));
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public void StructuredVictoryWithoutAValidPlayerSlotUsesStableSenderIdFallback()
    {
        var message = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Player 00001234",
            "Victory!",
            ChatMessageKind.Party,
            SenderId: 0x1234,
            PlayerNumber: 0,
            CommunicationCue: ChatCommunicationCue.Victory);
        using var peer = CreatePeer(new Config(), new RecordingTransport());

        Assert.Equal("Player 00001234", peer.ResolveHistorySender(message));
        Assert.Equal(ChatCommunicationCue.Victory, ChatOverlayPeer.GetEffectiveCommunicationCue(message));
    }

    [Fact]
    public void UnresolvedFallbackDoesNotFabricateCommunicationCue()
    {
        var message = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Player 00001234",
            "Unknown",
            ChatMessageKind.Party,
            SenderId: 0x1234,
            PlayerNumber: 3);
        using var peer = CreatePeer(new Config(), new RecordingTransport());

        var resolvedSender = peer.ResolveHistorySender(message);
        var cue = ChatOverlayPeer.GetEffectiveCommunicationCue(message);

        Assert.Equal("Player 00001234", resolvedSender);
        Assert.Equal(ChatCommunicationCue.None, cue);
        Assert.Equal(
            "Player 00001234:",
            ChatOverlayPeer.FormatHistorySenderLabel(
                resolvedSender,
                false,
                UiLanguage.English,
                cue));
    }

    [Fact]
    public void PlayerNameContainingMachinePrefixRemainsUnchanged()
    {
        var message = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Kuro_vo_CMM_win_3",
            "Hello",
            ChatMessageKind.Self,
            SenderId: 0x1234,
            PlayerNumber: 3);
        using var peer = CreatePeer(new Config(), new RecordingTransport());

        Assert.Equal(message.Sender, peer.ResolveHistorySender(message));
        Assert.Equal(ChatCommunicationCue.None, ChatOverlayPeer.GetEffectiveCommunicationCue(message));
    }

    [Fact]
    public void PlayerNameBeginningWithFallbackPrefixIsNotResolvedAsAnotherPartyMember()
    {
        var resolverCalls = 0;
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            getRemotePlayerName: _ =>
            {
                resolverCalls++;
                return "trick";
            });
        var message = new ChatMessage(
            1,
            DateTimeOffset.UtcNow,
            "Player One",
            "Hello",
            ChatMessageKind.Party,
            SenderId: 0x1234,
            PlayerNumber: 3);

        Assert.Equal("Player One", peer.ResolveHistorySender(message));
        Assert.Equal(0, resolverCalls);
    }

    [Theory]
    [InlineData(
        UiLanguage.SimplifiedChinese,
        PartyRoomExitReason.SelfLeft,
        "Arca",
        "你已退出Arca的房间，原因是：自行退房")]
    [InlineData(
        UiLanguage.SimplifiedChinese,
        PartyRoomExitReason.HostDisconnected,
        "Arca",
        "你已退出Arca的房间，原因是：房主掉线")]
    [InlineData(
        UiLanguage.SimplifiedChinese,
        PartyRoomExitReason.Kicked,
        "Arca",
        "你已退出Arca的房间，原因是：你已被踢除房间")]
    [InlineData(
        UiLanguage.SimplifiedChinese,
        PartyRoomExitReason.NetworkInterrupted,
        "Arca",
        "你已退出Arca的房间，原因是：网络波动已退出房间")]
    [InlineData(
        UiLanguage.SimplifiedChinese,
        PartyRoomExitReason.None,
        null,
        "你已退出当前房间，原因是：网络波动已退出房间")]
    [InlineData(
        UiLanguage.English,
        PartyRoomExitReason.SelfLeft,
        "Arca",
        "You left Arca's room. Reason: Left voluntarily")]
    [InlineData(
        UiLanguage.English,
        PartyRoomExitReason.HostDisconnected,
        "Arca",
        "You left Arca's room. Reason: Host disconnected")]
    [InlineData(
        UiLanguage.English,
        PartyRoomExitReason.Kicked,
        "Arca",
        "You left Arca's room. Reason: You were kicked from the room")]
    [InlineData(
        UiLanguage.English,
        PartyRoomExitReason.NetworkInterrupted,
        "Arca",
        "You left Arca's room. Reason: Network interruption caused you to leave")]
    [InlineData(
        UiLanguage.English,
        PartyRoomExitReason.None,
        "",
        "You left the current room. Reason: Network interruption caused you to leave")]
    public void RoomTransitionNotice_FormatsExitReasonsAndRoomNames(
        UiLanguage language,
        object reasonValue,
        string? roomName,
        string expected)
    {
        var reason = Assert.IsType<PartyRoomExitReason>(reasonValue);
        Assert.Equal(
            expected,
            ChatOverlayPeer.FormatRoomTransitionNotice(
                new PartyRoomTransition(
                    PartyRoomTransitionKind.Exited,
                    reason,
                    roomName),
                language));
    }

    [Theory]
    [InlineData(UiLanguage.SimplifiedChinese, "Arca", 2, 5, "已进入Arca的房间，5人成功建立语音通道")]
    [InlineData(UiLanguage.English, "", 3, 1, "Entered the current room; 3 people established voice channels.")]
    public void RoomTransitionNotice_UsesMaximumEstablishedVoiceCount(
        UiLanguage language,
        string roomName,
        int transitionCount,
        int establishedCount,
        string expected)
    {
        Assert.Equal(
            expected,
            ChatOverlayPeer.FormatRoomTransitionNotice(
                new PartyRoomTransition(
                    PartyRoomTransitionKind.Entered,
                    RoomName: roomName,
                    VoiceParticipantCount: transitionCount),
                language,
                establishedCount));
    }

    [Fact]
    public void Tick_DrainsAllRoomTransitions_WritesSystemHistory_AndUsesMaximumVoiceCount()
    {
        var transitions = new Queue<PartyRoomTransition>([
            new(
                PartyRoomTransitionKind.Entered,
                RoomName: "Arca",
                VoiceParticipantCount: 2),
            new(
                PartyRoomTransitionKind.Exited,
                PartyRoomExitReason.HostDisconnected,
                "Arca"),
        ]);
        var history = new ChatHistory(10);
        var now = DateTimeOffset.UtcNow;
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            isOnlineRoomActive: () => false,
            history: history,
            readRoomTransition: () => transitions.Count == 0 ? null : transitions.Dequeue(),
            getEstablishedVoiceParticipantCount: () => 5,
            getCurrentTime: () => now);
        SetInitialized(peer);

        peer.Tick();

        var snapshot = history.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.All(snapshot, message => Assert.Equal(ChatMessageKind.System, message.Kind));
        Assert.Equal("已进入Arca的房间，5人成功建立语音通道", snapshot[0].Text);
        Assert.Equal("你已退出Arca的房间，原因是：房主掉线", snapshot[1].Text);
        Assert.Empty(transitions);
    }

    [Theory]
    [InlineData(
        UiLanguage.SimplifiedChinese,
        "Lyria 加入了房间。|Narmaya 加入了房间。|Lyria 离开了房间，原因：主动离开。|Narmaya 离开了房间，原因：认证失效。")]
    [InlineData(
        UiLanguage.English,
        "Lyria joined the room.|Narmaya joined the room.|Lyria left the room. Reason: Left voluntarily.|Narmaya left the room. Reason: Authentication lost.")]
    public void Tick_DrainsAllMemberTransitionsInOrder_WritesBilingualSystemHistory(
        UiLanguage language,
        string expectedText)
    {
        var transitions = new Queue<PartyMemberTransition>([
            new(PartyMemberTransitionKind.Joined, 1, "entity-1"),
            new(PartyMemberTransitionKind.Joined, 2, "entity-2"),
            new(
                PartyMemberTransitionKind.Left,
                1,
                "entity-1",
                PartyMemberLeaveReason.Requested),
            new(
                PartyMemberTransitionKind.Left,
                2,
                "entity-2",
                PartyMemberLeaveReason.DeviceLostAuthentication),
        ]);
        var requestedOrdinals = new List<int>();
        var configuration = new Config { InterfaceLanguage = language };
        var history = new ChatHistory(10);
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            history: history,
            getRemotePlayerName: remotePlayerOrdinal =>
            {
                requestedOrdinals.Add(remotePlayerOrdinal);
                return remotePlayerOrdinal switch
                {
                    1 => "Lyria",
                    2 => "Narmaya",
                    _ => null,
                };
            },
            readMemberTransition: () => transitions.Count == 0 ? null : transitions.Dequeue());
        SetInitialized(peer);

        peer.Tick();

        var snapshot = history.Snapshot();
        Assert.Equal(expectedText.Split('|'), snapshot.Select(message => message.Text).ToArray());
        Assert.All(snapshot, message =>
        {
            Assert.Equal(ChatMessageKind.System, message.Kind);
            Assert.Equal(language == UiLanguage.English ? "System" : "系统", message.Sender);
        });
        Assert.Empty(transitions);
        Assert.Equal([1, 2], requestedOrdinals);
    }

    [Fact]
    public void Tick_UsesCachedJoinedNameWhenLeftResolverIsUnavailable()
    {
        var resolverCalls = 0;
        var resolverAvailable = true;
        var transitions = new Queue<PartyMemberTransition>([
            new(PartyMemberTransitionKind.Joined, 1, "entity-1"),
            new(
                PartyMemberTransitionKind.Left,
                1,
                "entity-1",
                PartyMemberLeaveReason.Disconnected),
        ]);
        var history = new ChatHistory(10);
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            history: history,
            getRemotePlayerName: _ =>
            {
                resolverCalls++;
                if (!resolverAvailable)
                    throw new InvalidOperationException("name table unavailable");
                return "Cached Name";
            },
            readMemberTransition: () =>
            {
                var transition = transitions.Count == 0 ? (PartyMemberTransition?)null : transitions.Dequeue();
                if (resolverCalls == 1)
                    resolverAvailable = false;
                return transition;
            });
        SetInitialized(peer);

        peer.Tick();

        Assert.Equal(1, resolverCalls);
        Assert.Equal(
            [
                "Cached Name 加入了房间。",
                "Cached Name 离开了房间，原因：连接中断。",
            ],
            history.Snapshot().Select(message => message.Text));
    }

    [Fact]
    public void Tick_BaselineCachesNameWithoutHistoryAndUsesItForLaterLeft()
    {
        var resolverAvailable = true;
        var transitions = new Queue<PartyMemberTransition>([
            new(PartyMemberTransitionKind.Baseline, 1, "host-entity"),
        ]);
        var history = new ChatHistory(10);
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            history: history,
            getRemotePlayerName: _ =>
            {
                if (!resolverAvailable)
                    throw new InvalidOperationException("name table unavailable");
                return "Room Host";
            },
            readMemberTransition: () => transitions.Count == 0 ? null : transitions.Dequeue());
        SetInitialized(peer);

        peer.Tick();

        Assert.Empty(history.Snapshot());

        resolverAvailable = false;
        transitions.Enqueue(new PartyMemberTransition(
            PartyMemberTransitionKind.Left,
            1,
            "host-entity",
            PartyMemberLeaveReason.Disconnected));
        peer.Tick();

        var message = Assert.Single(history.Snapshot());
        Assert.Equal(ChatMessageKind.System, message.Kind);
        Assert.Equal("Room Host 离开了房间，原因：连接中断。", message.Text);
    }

    [Theory]
    [InlineData(UiLanguage.SimplifiedChinese, (int)PartyMemberLeaveReason.Unknown, "原因未知")]
    [InlineData(UiLanguage.SimplifiedChinese, (int)PartyMemberLeaveReason.Requested, "主动离开")]
    [InlineData(UiLanguage.SimplifiedChinese, (int)PartyMemberLeaveReason.Disconnected, "连接中断")]
    [InlineData(UiLanguage.SimplifiedChinese, (int)PartyMemberLeaveReason.Kicked, "被踢出房间")]
    [InlineData(UiLanguage.SimplifiedChinese, (int)PartyMemberLeaveReason.DeviceLostAuthentication, "认证失效")]
    [InlineData(UiLanguage.SimplifiedChinese, (int)PartyMemberLeaveReason.CreationFailed, "联机端点创建失败")]
    [InlineData(UiLanguage.English, (int)PartyMemberLeaveReason.Unknown, "Unknown")]
    [InlineData(UiLanguage.English, (int)PartyMemberLeaveReason.Requested, "Left voluntarily")]
    [InlineData(UiLanguage.English, (int)PartyMemberLeaveReason.Disconnected, "Connection lost")]
    [InlineData(UiLanguage.English, (int)PartyMemberLeaveReason.Kicked, "Kicked")]
    [InlineData(UiLanguage.English, (int)PartyMemberLeaveReason.DeviceLostAuthentication, "Authentication lost")]
    [InlineData(UiLanguage.English, (int)PartyMemberLeaveReason.CreationFailed, "Online endpoint creation failed")]
    public void Tick_WritesEveryMemberLeaveReasonToHistory(
        UiLanguage language,
        int reasonValue,
        string expectedReason)
    {
        var reason = (PartyMemberLeaveReason)reasonValue;
        var transitions = new Queue<PartyMemberTransition>([
            new(PartyMemberTransitionKind.Joined, 2, "entity-reason"),
            new(PartyMemberTransitionKind.Left, 2, "entity-reason", reason),
        ]);
        var configuration = new Config { InterfaceLanguage = language };
        var history = new ChatHistory(10);
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            history: history,
            getRemotePlayerName: remotePlayerOrdinal =>
                remotePlayerOrdinal == 2 ? "Narmaya" : null,
            readMemberTransition: () => transitions.Count == 0 ? null : transitions.Dequeue());
        SetInitialized(peer);

        peer.Tick();

        var snapshot = history.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.All(snapshot, message => Assert.Equal(ChatMessageKind.System, message.Kind));
        Assert.Equal(
            language == UiLanguage.English
                ? "Narmaya joined the room."
                : "Narmaya 加入了房间。",
            snapshot[0].Text);
        Assert.Equal(
            language == UiLanguage.English
                ? $"Narmaya left the room. Reason: {expectedReason}."
                : $"Narmaya 离开了房间，原因：{expectedReason}。",
            snapshot[1].Text);
    }

    [Fact]
    public void Tick_ConsumesEachMemberTransitionOnlyOnceWithDuplicateReaderPattern()
    {
        var transition = new PartyMemberTransition(PartyMemberTransitionKind.Joined, 1, "entity-1");
        var pending = transition;
        var returnedTransitionCount = 0;
        var history = new ChatHistory(10);
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            history: history,
            getRemotePlayerName: _ => "Lyria",
            readMemberTransition: () =>
            {
                var current = pending;
                pending = default;
                if (current != default)
                    returnedTransitionCount++;
                return current == default ? null : current;
            });
        SetInitialized(peer);

        peer.Tick();
        peer.Tick();

        Assert.Equal(1, returnedTransitionCount);
        var message = Assert.Single(history.Snapshot());
        Assert.Equal(ChatMessageKind.System, message.Kind);
        Assert.Equal("Lyria 加入了房间。", message.Text);
    }

    [Theory]
    [InlineData((int)PartyMemberTransitionKind.Joined, 99, null, "未知玩家 加入了房间。")]
    [InlineData((int)PartyMemberTransitionKind.Left, 0, "", "未知玩家 离开了房间，原因：原因未知。")]
    public void Tick_InvalidMemberTransitionDataFailsSafeWithLocalizedFallback(
        int kindValue,
        int remotePlayerOrdinal,
        string? entityId,
        string expectedText)
    {
        var kind = (PartyMemberTransitionKind)kindValue;
        var transitions = new Queue<PartyMemberTransition>([
            new(kind, remotePlayerOrdinal, entityId),
        ]);
        var history = new ChatHistory(10);
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            history: history,
            getRemotePlayerName: _ => throw new InvalidOperationException("invalid input must not escape"),
            readMemberTransition: () => transitions.Count == 0 ? null : transitions.Dequeue());
        SetInitialized(peer);

        var exception = Record.Exception(peer.Tick);

        Assert.Null(exception);
        var message = Assert.Single(history.Snapshot());
        Assert.Equal(ChatMessageKind.System, message.Kind);
        Assert.Equal(expectedText, message.Text);
    }

    [Fact]
    public void Tick_ClearsMemberNameCacheWhenRoomBecomesInactive_AndDoesNotLeakAcrossRooms()
    {
        var onlineRoomActive = true;
        var currentName = "Room A Name";
        var memberTransitions = new Queue<PartyMemberTransition>([
            new(PartyMemberTransitionKind.Joined, 1, "entity-1"),
        ]);
        var roomTransitions = new Queue<PartyRoomTransition>([
            new(PartyRoomTransitionKind.Exited, PartyRoomExitReason.SelfLeft, "Room A"),
        ]);
        var history = new ChatHistory(10);
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            isOnlineRoomActive: () => onlineRoomActive,
            history: history,
            getRemotePlayerName: _ => currentName,
            readMemberTransition: () => memberTransitions.Count == 0 ? null : memberTransitions.Dequeue(),
            readRoomTransition: () => roomTransitions.Count == 0 ? null : roomTransitions.Dequeue());
        SetInitialized(peer);

        peer.Tick();
        onlineRoomActive = false;
        peer.Tick();
        currentName = "Room B Name";
        onlineRoomActive = true;
        memberTransitions.Enqueue(
            new(
                PartyMemberTransitionKind.Left,
                1,
                "entity-1",
                PartyMemberLeaveReason.Unknown));
        peer.Tick();

        var messages = history.Snapshot().Select(message => message.Text).ToArray();
        Assert.Contains("Room A Name 加入了房间。", messages);
        Assert.Contains("Room B Name 离开了房间，原因：原因未知。", messages);
        Assert.DoesNotContain("Room A Name 离开了房间，原因：原因未知。", messages);
    }

    [Fact]
    public void Tick_ProcessesTheSameRoomTransitionOnlyOnce()
    {
        var transition = new PartyRoomTransition(
            PartyRoomTransitionKind.Entered,
            RoomName: "Arca",
            VoiceParticipantCount: 1);
        var pending = transition;
        var history = new ChatHistory(10);
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            history: history,
            readRoomTransition: () =>
            {
                var current = pending;
                pending = default;
                return current == default ? null : current;
            });
        SetInitialized(peer);

        peer.Tick();
        peer.Tick();

        Assert.Single(history.Snapshot());
    }

    [Fact]
    public void Tick_PersistsPendingAutoBlockBeforeRoomExitClearsTransientState()
    {
        var configuration = new Config();
        configuration.ChatFilter.Enabled = true;
        configuration.ChatFilter.UseSteamTextFilter = false;
        configuration.ChatFilter.AutoBlockEnabled = true;
        configuration.ChatFilter.AutoBlockThreshold = 2;
        configuration.ChatFilter.Rules.Add(new ChatFilterRuleConfiguration
        {
            Id = "bad",
            Term = "bad",
        });
        var moderation = new ChatModerationService();
        moderation.ApplyConfiguration(configuration.ChatFilter);
        var participant = new ChatModerationParticipant(2, "Remote", "entity-remote");
        var now = DateTimeOffset.UtcNow;
        _ = moderation.Evaluate(new ChatModerationInput(participant, "bad", now));
        _ = moderation.Evaluate(new ChatModerationInput(participant, "bad", now.AddSeconds(1)));
        var pending = new PartyRoomTransition(
            PartyRoomTransitionKind.Exited,
            PartyRoomExitReason.SelfLeft,
            "Arca");
        var history = new ChatHistory(10);
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            history: history,
            readRoomTransition: () =>
            {
                var current = pending;
                pending = default;
                return current == default ? null : current;
            },
            getCurrentTime: () => now.AddSeconds(2),
            chatModeration: moderation);
        SetInitialized(peer);

        peer.Tick();

        var blocked = Assert.Single(configuration.ChatFilter.BlockedPlayers);
        Assert.Equal("entity-remote", blocked.Identity);
        Assert.Equal(BlockedPlayerSource.FilterThreshold, blocked.Source);
        Assert.Contains(
            history.Snapshot(),
            message => message.Kind == ChatMessageKind.System &&
                       message.Text.Contains("Remote", StringComparison.Ordinal) &&
                       message.Text.Contains("触发过滤条件次数过多", StringComparison.Ordinal));
        Assert.Empty(moderation.GetSnapshot().Players);
        Assert.Equal(0, moderation.GetSnapshot().SessionFilteredMessageCount);
        Assert.True(moderation.IsBlocked(participant));
        Assert.False(moderation.TryReadEvent(out _));
    }

    [Fact]
    public void Tick_FirstActiveRoomFrameDoesNotDiscardNativeModerationEvents()
    {
        var configuration = new Config();
        configuration.ChatFilter.Enabled = true;
        configuration.ChatFilter.UseSteamTextFilter = false;
        configuration.ChatFilter.AutoBlockEnabled = true;
        configuration.ChatFilter.AutoBlockThreshold = 1;
        configuration.ChatFilter.Rules.Add(new ChatFilterRuleConfiguration
        {
            Id = "bad",
            Term = "bad",
        });
        var moderation = new ChatModerationService();
        moderation.ApplyConfiguration(configuration.ChatFilter);
        var participant = new ChatModerationParticipant(2, "Remote", "entity-remote");
        _ = moderation.Evaluate(new ChatModerationInput(
            participant,
            "bad",
            DateTimeOffset.UtcNow));
        var history = new ChatHistory(10);
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            history: history,
            chatModeration: moderation);
        SetInitialized(peer);

        peer.Tick();

        Assert.Single(configuration.ChatFilter.BlockedPlayers);
        Assert.Contains(
            history.Snapshot(),
            message => message.Kind == ChatMessageKind.System &&
                       message.Text.Contains("Remote", StringComparison.Ordinal));
        Assert.True(moderation.IsBlocked(participant));
        Assert.False(moderation.TryReadEvent(out _));
    }

    [Fact]
    public void WantsRender_RemainsTrueForOfflineCompactTransientNotice()
    {
        var configuration = new Config
        {
            EnableOverlay = true,
            CompactMode = true,
        };
        var pending = new PartyRoomTransition(
            PartyRoomTransitionKind.Exited,
            PartyRoomExitReason.Kicked,
            "Arca");
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => false,
            readRoomTransition: () =>
            {
                var current = pending;
                pending = default;
                return current == default ? null : current;
            });
        SetInitialized(peer);

        peer.Tick();

        Assert.True(peer.WantsRender);
    }

    [Fact]
    public void TransientNotice_RemainsActiveForFiveSecondsAndExpiresAfterward()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(5);

        Assert.True(ChatOverlayPeer.IsTransientNoticeActive("notice", now.AddSeconds(4.999), expiresAt));
        Assert.False(ChatOverlayPeer.IsTransientNoticeActive("notice", expiresAt, expiresAt));
    }

    [Fact]
    public void Tick_KeepsIncomingQueuedUntilTheOnlineRoomIsActive()
    {
        var onlineRoomActive = false;
        var history = new ChatHistory(10);
        var incoming = new StubIncomingSource(
            new IncomingChatMessage(
                "Lyria",
                "Ready!",
                1,
                2,
                3,
                DateTimeOffset.UtcNow,
                2));
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            isOnlineRoomActive: () => onlineRoomActive,
            incoming: incoming,
            history: history);
        SetInitialized(peer);

        peer.Tick();
        Assert.Empty(history.Snapshot());

        onlineRoomActive = true;
        peer.Tick();

        var message = Assert.Single(history.Snapshot());
        Assert.Equal("Lyria", message.Sender);
        Assert.Equal("Ready!", message.Text);
    }

    [Fact]
    public void Tick_PreservesHistoryAcrossATemporaryOnlineRoomReset()
    {
        var onlineRoomActive = true;
        var history = new ChatHistory(10);
        history.Add("Lyria", "Still here", ChatMessageKind.Party, DateTimeOffset.UtcNow);
        using var peer = CreatePeer(
            new Config(),
            new RecordingTransport(),
            isOnlineRoomActive: () => onlineRoomActive,
            history: history);
        SetInitialized(peer);

        peer.Tick();
        onlineRoomActive = false;
        peer.Tick();
        onlineRoomActive = true;
        peer.Tick();

        var message = Assert.Single(history.Snapshot());
        Assert.Equal("Still here", message.Text);
    }

    private static ChatOverlayPeer CreatePeer(
        Config configuration,
        IChatTransport transport,
        Func<bool>? isOnlineRoomActive = null,
        Func<IReadOnlyList<PartyPlayerMuteSlotStatus>>? getPlayerMuteSlots = null,
        Func<int, bool, PartyPlayerMuteOperationResult>? setPlayerMuted = null,
        ChatBlacklist? chatBlacklist = null,
        Func<bool>? canUseVoicePushToTalk = null,
        Action<bool>? setVoicePushToTalkPressed = null,
        IIncomingChatSource? incoming = null,
        ChatHistory? history = null,
        Func<int, string?>? getRemotePlayerName = null,
        Func<PartyVoiceIndicatorSnapshot>? getVoiceIndicatorSnapshot = null,
        Func<PartyVoiceUiStatus>? getVoiceUiStatus = null,
        Func<string?>? getLocalPlayerName = null,
        Func<PartyMemberTransition?>? readMemberTransition = null,
        Func<PartyRoomTransition?>? readRoomTransition = null,
        Func<int>? getEstablishedVoiceParticipantCount = null,
        Func<DateTimeOffset>? getCurrentTime = null,
        Func<Action<bool>, VoicePushToTalkSafetyGate>? createWindowVoicePushToTalkGate = null,
        Func<int, bool>? isWindowKeyDown = null,
        Action<string>? log = null,
        IChatModerationService? chatModeration = null) =>
        new(
            new ChatSession(history ?? new ChatHistory(10), new ChatComposer(), transport, incoming: incoming),
            () => configuration,
            isOnlineRoomActive ?? (() => false),
            () => { },
            getVoiceUiStatus ?? (() => PartyVoiceUiStatus.Unavailable),
            (_, _, _, _) => Array.Empty<PartyHudAnchor>(),
            getPlayerMuteSlots ?? (() => Array.Empty<PartyPlayerMuteSlotStatus>()),
            setPlayerMuted ?? ((_, _) => new PartyPlayerMuteOperationResult(false, string.Empty)),
            (_, _) => ChatSendResult.Unavailable(),
            null,
            update => update(configuration),
            _ => { },
            canUseVoicePushToTalk ?? (() => false),
            setVoicePushToTalkPressed ?? (_ => { }),
            () => { },
            _ => { },
            log ?? (_ => { }),
            chatBlacklist: chatBlacklist,
            getRemotePlayerName: getRemotePlayerName,
            getVoiceIndicatorSnapshot: getVoiceIndicatorSnapshot,
            readMemberTransition: readMemberTransition,
            readRoomTransition: readRoomTransition,
            getEstablishedVoiceParticipantCount: getEstablishedVoiceParticipantCount,
            getCurrentTime: getCurrentTime,
            createWindowVoicePushToTalkGate: createWindowVoicePushToTalkGate,
            isWindowKeyDown: isWindowKeyDown,
            getLocalPlayerName: getLocalPlayerName,
            chatModeration: chatModeration);

    private static void SetInitialized(ChatOverlayPeer peer) =>
        typeof(ChatOverlayPeer)
            .GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(peer, true);

    private static T? GetPrivateField<T>(ChatOverlayPeer peer, string name) =>
        (T?)typeof(ChatOverlayPeer)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(peer);

    private static void SetPrivateField<T>(ChatOverlayPeer peer, string name, T value) =>
        typeof(ChatOverlayPeer)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(peer, value);

    private static IReadOnlyList<string> ResolveVoiceTalkerNames(
        ChatOverlayPeer peer,
        PartyVoiceUiStatus status,
        PartyVoiceIndicatorSnapshot snapshot) =>
        (IReadOnlyList<string>)typeof(ChatOverlayPeer)
            .GetMethod("ResolveVoiceTalkerNames", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(peer, [status, snapshot])!;

    private static void PressAndRelease(ChatOverlayPeer peer, char key)
    {
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, key, nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, key, nint.Zero);
    }

    private sealed class RecordingTransport : IChatTransport
    {
        public string? LastMessage { get; private set; }
        public int SendCount { get; private set; }

        public ChatSendResult Send(string message)
        {
            LastMessage = message;
            SendCount++;
            return ChatSendResult.Sent();
        }
    }

    private sealed class StubIncomingSource(params IncomingChatMessage[] messages) : IIncomingChatSource
    {
        private readonly Queue<IncomingChatMessage> _messages = new(messages);

        public bool TryRead(out IncomingChatMessage message) => _messages.TryDequeue(out message);
    }
}
