using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyPlayerMuteControllerTests
{
    private static readonly nint Network = (nint)0x1000;
    private static readonly nint LocalChatControl = (nint)0x2000;
    private static readonly nint SecondLocalChatControl = (nint)0x2100;
    private static readonly nint RemoteChatControl = (nint)0x3000;
    private static readonly nint Manager = (nint)0x4000;

    [Fact]
    public void ExactEntityIdMatch_MutesEveryLocalAudioPathAndVerifiesReadback()
    {
        var api = new FakePartyApi();
        api.LocalControls.UnionWith([LocalChatControl, SecondLocalChatControl]);
        api.EntityIds[RemoteChatControl] = "entity-player-2";
        var identities = new FakeIdentityResolver { [1] = "entity-player-2" };
        var controller = new PartyPlayerMuteController(api, identities, _ => { });
        ObserveBatch(
            controller,
            Joined(LocalChatControl),
            Joined(SecondLocalChatControl),
            Joined(RemoteChatControl));

        var before = Assert.Single(controller.GetSnapshot(), slot => slot.PlayerNumber == 2);
        Assert.True(before.IsAvailable);
        Assert.False(before.IsMuted);

        var operation = controller.SetPlayerMuted(2, muted: true);

        Assert.True(operation.Succeeded);
        Assert.Equal(
            new[]
            {
                (LocalChatControl, RemoteChatControl, true),
                (SecondLocalChatControl, RemoteChatControl, true),
            },
            api.SetCalls);
        var after = Assert.Single(controller.GetSnapshot(), slot => slot.PlayerNumber == 2);
        Assert.True(after.IsAvailable);
        Assert.True(after.IsMuted);
    }

    [Fact]
    public void DifferentEntityId_NeverFallsBackToJoinOrder()
    {
        var api = new FakePartyApi();
        api.LocalControls.Add(LocalChatControl);
        api.EntityIds[RemoteChatControl] = "different-player";
        var identities = new FakeIdentityResolver { [1] = "entity-player-2" };
        var controller = new PartyPlayerMuteController(api, identities, _ => { });
        ObserveBatch(controller, Joined(LocalChatControl), Joined(RemoteChatControl));

        var status = Assert.Single(controller.GetSnapshot(), slot => slot.PlayerNumber == 2);
        var operation = controller.SetPlayerMuted(2, muted: true);

        Assert.False(status.IsAvailable);
        Assert.False(operation.Succeeded);
        Assert.Empty(api.SetCalls);
    }

    [Fact]
    public void RemoteLeave_RemovesTheRetainedChatControlBeforeFurtherUiActions()
    {
        var api = new FakePartyApi();
        api.LocalControls.Add(LocalChatControl);
        api.EntityIds[RemoteChatControl] = "entity-player-2";
        var identities = new FakeIdentityResolver { [1] = "entity-player-2" };
        var controller = new PartyPlayerMuteController(api, identities, _ => { });
        ObserveBatch(controller, Joined(LocalChatControl), Joined(RemoteChatControl));
        Assert.True(Assert.Single(controller.GetSnapshot(), slot => slot.PlayerNumber == 2).IsAvailable);

        ObserveBatch(
            controller,
            new PartyStateChangeSnapshot((uint)PartyStateChangeType.ChatControlLeftNetwork)
            {
                Network = Network,
                ChatControl = RemoteChatControl,
            });

        Assert.False(Assert.Single(controller.GetSnapshot(), slot => slot.PlayerNumber == 2).IsAvailable);
        Assert.False(controller.SetPlayerMuted(2, muted: true).Succeeded);
        Assert.Empty(api.SetCalls);
    }

    [Fact]
    public void ReclassifiedControl_CannotRemainInBothLocalAndRemoteSets()
    {
        var api = new FakePartyApi();
        api.EntityIds[RemoteChatControl] = "entity-player-2";
        var identities = new FakeIdentityResolver { [1] = "entity-player-2" };
        var controller = new PartyPlayerMuteController(api, identities, _ => { });
        ObserveBatch(controller, Joined(RemoteChatControl));

        api.LocalControls.Add(RemoteChatControl);
        ObserveBatch(controller, Joined(RemoteChatControl));

        Assert.False(Assert.Single(controller.GetSnapshot(), slot => slot.PlayerNumber == 2).IsAvailable);
        Assert.False(controller.SetPlayerMuted(2, muted: true).Succeeded);
        Assert.Empty(api.SetCalls);
    }

    [Fact]
    public void StateBatch_DoesNotCallPartyIdentityApisUntilOriginalFinishReturns()
    {
        var api = new FakePartyApi();
        api.LocalControls.Add(LocalChatControl);
        api.EntityIds[RemoteChatControl] = "entity-player-2";
        var controller = new PartyPlayerMuteController(
            api,
            new FakeIdentityResolver { [1] = "entity-player-2" },
            _ => { });

        controller.BeginStateChangeBatch(Manager);
        controller.Observe(Joined(LocalChatControl));
        controller.Observe(Joined(RemoteChatControl));

        Assert.Equal(0, api.IdentityInspectionCount);
        Assert.False(controller.SetPlayerMuted(2, muted: true).Succeeded);

        controller.OnBatchFinished(Manager);

        Assert.Equal(3, api.IdentityInspectionCount);
        Assert.True(Assert.Single(controller.GetSnapshot(), slot => slot.PlayerNumber == 2).IsAvailable);
    }

    private static PartyStateChangeSnapshot Joined(nint chatControl) =>
        new((uint)PartyStateChangeType.ChatControlJoinedNetwork)
        {
            Network = Network,
            ChatControl = chatControl,
        };

    private static void ObserveBatch(
        PartyPlayerMuteController controller,
        params PartyStateChangeSnapshot[] states)
    {
        controller.BeginStateChangeBatch(Manager);
        foreach (var state in states)
            controller.Observe(state);
        controller.OnBatchFinished(Manager);
    }

    private sealed class FakeIdentityResolver : IRelinkPartyMemberIdentityResolver
    {
        private readonly Dictionary<int, string> _identities = [];

        internal string this[int slot]
        {
            set => _identities[slot] = value;
        }

        public bool TryResolveSlot(int memberSlot, out string entityId) =>
            _identities.TryGetValue(memberSlot, out entityId!);
    }

    private sealed class FakePartyApi : IPartyChatControlApi
    {
        internal HashSet<nint> LocalControls { get; } = [];

        internal Dictionary<nint, string> EntityIds { get; } = [];

        internal Dictionary<(nint Local, nint Remote), bool> Muted { get; } = [];

        internal List<(nint Local, nint Remote, bool Muted)> SetCalls { get; } = [];

        internal int IdentityInspectionCount { get; private set; }

        public uint GetEntityId(nint chatControl, out string? entityId)
        {
            IdentityInspectionCount++;
            var found = EntityIds.TryGetValue(chatControl, out var value);
            entityId = value;
            return found ? 0u : 1u;
        }

        public uint IsLocal(nint chatControl, out bool isLocal)
        {
            IdentityInspectionCount++;
            isLocal = LocalControls.Contains(chatControl);
            return 0;
        }

        public uint SetIncomingAudioMuted(nint localChatControl, nint targetChatControl, bool muted)
        {
            SetCalls.Add((localChatControl, targetChatControl, muted));
            Muted[(localChatControl, targetChatControl)] = muted;
            return 0;
        }

        public uint GetIncomingAudioMuted(
            nint localChatControl,
            nint targetChatControl,
            out bool muted)
        {
            muted = Muted.GetValueOrDefault((localChatControl, targetChatControl));
            return 0;
        }

        public uint GetLocalDevice(nint manager, out nint localDevice) =>
            throw new NotSupportedException();

        public uint GetLocalChatControlCount(nint localDevice, out uint chatControlCount) =>
            throw new NotSupportedException();

        public uint CreateChatControl(
            nint localDevice,
            nint localUser,
            nint asyncIdentifier,
            out nint localChatControl) =>
            throw new NotSupportedException();

        public uint DestroyChatControl(nint localDevice, nint localChatControl, nint asyncIdentifier) =>
            throw new NotSupportedException();

        public uint SetAudioInputMuted(nint localChatControl, bool muted) =>
            throw new NotSupportedException();

        public uint GetAudioInputMuted(nint localChatControl, out bool muted) =>
            throw new NotSupportedException();

        public uint GetPermissions(
            nint localChatControl,
            nint targetChatControl,
            out PartyChatPermissionOptions permissions) =>
            throw new NotSupportedException();

        public uint GetAudioInput(
            nint localChatControl,
            out PartyAudioDeviceSelectionType selectionType,
            out string? selectionContext,
            out string? deviceId) =>
            throw new NotSupportedException();

        public uint GetAudioOutput(
            nint localChatControl,
            out PartyAudioDeviceSelectionType selectionType,
            out string? selectionContext,
            out string? deviceId) =>
            throw new NotSupportedException();

        public uint GetAudioRenderVolume(
            nint localChatControl,
            nint targetChatControl,
            out float volume) =>
            throw new NotSupportedException();

        public uint GetLocalChatIndicator(
            nint localChatControl,
            out PartyLocalChatControlChatIndicator indicator) =>
            throw new NotSupportedException();

        public uint GetChatIndicator(
            nint localChatControl,
            nint targetChatControl,
            out PartyChatControlChatIndicator indicator) =>
            throw new NotSupportedException();

        public uint GetErrorMessage(uint error, out string? errorMessage) =>
            throw new NotSupportedException();

        public uint SetPermissions(
            nint localChatControl,
            nint targetChatControl,
            PartyChatPermissionOptions permissions) =>
            throw new NotSupportedException();

        public uint SetAudioInput(
            nint localChatControl,
            PartyAudioDeviceSelectionType selectionType,
            string? selectionContext,
            nint asyncIdentifier) =>
            throw new NotSupportedException();

        public uint SetAudioOutput(
            nint localChatControl,
            PartyAudioDeviceSelectionType selectionType,
            string? selectionContext,
            nint asyncIdentifier) =>
            throw new NotSupportedException();

        public uint ConnectChatControl(nint network, nint localChatControl, nint asyncIdentifier) =>
            throw new NotSupportedException();

        public uint DisconnectChatControl(nint network, nint localChatControl, nint asyncIdentifier) =>
            throw new NotSupportedException();
    }
}
