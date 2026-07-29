using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatOverlayPeerHotkeyTests
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
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
        Assert.Equal("Hello from custom text", transport.LastMessage);
        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public void GlobalMuteHotkey_TogglesAllAvailablePlayersOncePerPress()
    {
        var configuration = new Config
        {
            GlobalMuteKeyboardBinding = "M",
        };
        var muted = new Dictionary<int, bool>
        {
            [2] = false,
            [3] = false,
        };
        var operations = new List<(int Player, bool Muted)>();
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            getPlayerMuteSlots: () =>
            [
                new PartyPlayerMuteSlotStatus(2, true, muted[2], string.Empty),
                new PartyPlayerMuteSlotStatus(3, true, muted[3], string.Empty),
                new PartyPlayerMuteSlotStatus(4, false, false, string.Empty),
            ],
            setPlayerMuted: (player, targetMuted) =>
            {
                operations.Add((player, targetMuted));
                muted[player] = targetMuted;
                return new PartyPlayerMuteOperationResult(true, "ok");
            });

        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'M', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyDown, 'M', nint.Zero);
        peer.ObserveWindowMessage(nint.Zero, WmKeyUp, 'M', nint.Zero);
        PressAndRelease(peer, 'M');

        Assert.Equal(
            new[] { (2, true), (3, true), (2, false), (3, false) },
            operations);
    }

    [Fact]
    public void GlobalMuteHotkey_DoesNotWriteUnavailableSlots()
    {
        var configuration = new Config
        {
            GlobalMuteKeyboardBinding = "N",
        };
        var operationCount = 0;
        using var peer = CreatePeer(
            configuration,
            new RecordingTransport(),
            isOnlineRoomActive: () => true,
            getPlayerMuteSlots: () => PartyPlayerMuteSlotStatus.Unavailable("unavailable"),
            setPlayerMuted: (_, _) =>
            {
                operationCount++;
                return new PartyPlayerMuteOperationResult(true, "unexpected");
            });

        PressAndRelease(peer, 'N');

        Assert.Equal(0, operationCount);
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
        Assert.Equal("sent after close", transport.LastMessage);
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

    private static ChatOverlayPeer CreatePeer(
        Config configuration,
        IChatTransport transport,
        Func<bool>? isOnlineRoomActive = null,
        Func<IReadOnlyList<PartyPlayerMuteSlotStatus>>? getPlayerMuteSlots = null,
        Func<int, bool, PartyPlayerMuteOperationResult>? setPlayerMuted = null) =>
        new(
            new ChatSession(new ChatHistory(10), new ChatComposer(), transport),
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
            () => false,
            _ => { },
            () => { },
            _ => { },
            _ => { });

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
}
