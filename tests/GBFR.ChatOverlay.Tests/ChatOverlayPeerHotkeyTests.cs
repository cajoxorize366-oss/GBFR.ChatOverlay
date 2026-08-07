using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Overlay;
using System.Reflection;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatOverlayPeerHotkeyTests
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VirtualKeyBackspace = 0x08;
    private const int VirtualKeyEscape = 0x1B;

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
    public void KeyboardCapture_ReplacesAnExistingConflictingBinding()
    {
        var configuration = new Config
        {
            OpenChatKeyboardBinding = "P",
            QuickActionsKeyboardBinding = string.Empty,
        };
        using var peer = CreatePeer(configuration, new RecordingTransport());
        var request = new BindingCaptureRequest(
            BindingTarget.QuickActionsPanel,
            BindingCaptureDevice.Keyboard,
            null);

        peer.BeginBindingCapture(request);
        PressAndRelease(peer, 'P');

        Assert.Equal(string.Empty, configuration.OpenChatKeyboardBinding);
        Assert.Equal("P", configuration.QuickActionsKeyboardBinding);
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
            BindingTarget.QuickActionsPanel,
            BindingCaptureDevice.Controller,
            null));

        peer.ObserveNativeInputSnapshot(new DirectInputBrokerSnapshot
        {
            AbiVersion = DirectInputBrokerSnapshot.ExpectedAbiVersion,
            StructSize = DirectInputBrokerSnapshot.ExpectedStructSize,
            Readiness = DirectInputBrokerReadiness.Controller,
            ControllerButtons = ControllerButtons.DPadDown,
        });

        Assert.Equal(string.Empty, configuration.QuickActionsControllerBinding);
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

        Assert.Equal("X", configuration.QuickActionsControllerBinding);
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
    public void QuickActionsPanel_CapturesKeysAndClosesOnEscapeWithoutDispatching()
    {
        var action = new QuickActionConfiguration
        {
            Kind = QuickActionKind.CustomText,
            Text = "sent after close",
            KeyboardBinding = "P",
        };
        var configuration = new Config { QuickActions = [action] };
        var transport = new RecordingTransport();
        using var peer = CreatePeer(configuration, transport, isOnlineRoomActive: () => true);

        peer.SetQuickActionsPanelOpen(true);
        var blockedAction = peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'P', nint.Zero);
        var escape = peer.ObserveWindowMessage(
            nint.Zero,
            WmKeyDown,
            new nint(VirtualKeyEscape),
            nint.Zero);

        Assert.True(blockedAction.Handled);
        Assert.True(escape.Handled);
        Assert.False(peer.IsQuickActionsPanelOpen);
        Assert.Equal(0, transport.SendCount);

        PressAndRelease(peer, 'P');
        Assert.Equal(1, peer.DrainQuickActionRequests());
        Assert.Equal("sent after close", transport.LastMessage);
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
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            canUseVoicePushToTalk: () => true,
            setVoicePushToTalkPressed: states.Add);
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
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            canUseVoicePushToTalk: () => canUse,
            setVoicePushToTalkPressed: states.Add);
        SetInitialized(peer);

        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        canUse = true;
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        Assert.Empty(states);

        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'U', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'U', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'U', nint.Zero);

        Assert.Equal([true, false], states);
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
        ChatHistory? history = null) =>
        new(
            new ChatSession(history ?? new ChatHistory(10), new ChatComposer(), transport, incoming: incoming),
            () => configuration,
            isOnlineRoomActive ?? (() => false),
            () => { },
            () => PartyVoiceUiStatus.Unavailable,
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
            _ => { },
            chatBlacklist);

    private static void SetInitialized(ChatOverlayPeer peer) =>
        typeof(ChatOverlayPeer)
            .GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(peer, true);

    private static T? GetPrivateField<T>(ChatOverlayPeer peer, string name) =>
        (T?)typeof(ChatOverlayPeer)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(peer);

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
