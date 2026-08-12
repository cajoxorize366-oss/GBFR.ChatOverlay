using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class HostIdentificationIntegrationTests
{
    private static readonly nint Network = (nint)0x1000;
    private static readonly nint CreatorUser = (nint)0x2000;
    private static readonly nint JoinerUser = (nint)0x3000;

    [Fact]
    public void CreatorAndJoinerResolveTheSameHostWithoutJoinerSelfHosting()
    {
        var creatorRole = ResolveActiveRole(CreatorUser, created: true);
        var joinerRole = ResolveActiveRole(JoinerUser, created: false);
        Assert.Equal(PartyNetworkLocalRole.Created, creatorRole);
        Assert.Equal(PartyNetworkLocalRole.Connected, joinerRole);

        var creatorBinding = CreateBindingWithBothOwnerCandidates();
        var creatorSnapshot = new RelinkPartyMemberIdentitySnapshot(
            ["creator", "joiner", string.Empty, string.Empty],
            LocalMemberSlot: 0);
        Assert.True(creatorBinding.TryResolveHostPlayerNumber(
            creatorSnapshot,
            creatorRole,
            out var creatorViewHost));
        Assert.Equal(1, creatorViewHost);

        var joinerBinding = CreateBindingWithBothOwnerCandidates();
        var joinerSnapshot = new RelinkPartyMemberIdentitySnapshot(
            ["creator", "joiner", string.Empty, string.Empty],
            LocalMemberSlot: 1);
        Assert.True(joinerBinding.TryResolveHostPlayerNumber(
            joinerSnapshot,
            joinerRole,
            out var joinerViewHost));
        Assert.Equal(2, joinerViewHost);

        var creatorLocalMessage = Message("Creator", ChatMessageKind.Self, playerNumber: 4);
        var creatorRemoteMessage = Message("Creator", ChatMessageKind.Party, playerNumber: 2);
        var joinerLocalMessage = Message("Joiner", ChatMessageKind.Self, playerNumber: 2);

        Assert.True(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(
            creatorLocalMessage,
            creatorViewHost));
        Assert.True(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(
            creatorRemoteMessage,
            joinerViewHost));
        Assert.False(ChatOverlayPeer.IsHistoryMessageHostedByPlayer(
            joinerLocalMessage,
            joinerViewHost));
    }

    private static PartyLobbyOwnerBinding CreateBindingWithBothOwnerCandidates()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner((nint)0x5000, "creator");
        binding.ObserveOwner((nint)0x5001, "joiner");
        return binding;
    }

    private static PartyNetworkLocalRole ResolveActiveRole(nint localUser, bool created)
    {
        var tracker = new PartyRoomSessionTracker();
        tracker.Observe(new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.ConnectToNetworkCompleted)
        {
            Result = 0,
            Network = Network,
        });
        if (created)
        {
            tracker.Observe(new PartyStateChangeSnapshot(
                (uint)PartyStateChangeType.CreateNewNetworkCompleted)
            {
                Result = 0,
                LocalUser = localUser,
            });
        }

        tracker.Observe(new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.AuthenticateLocalUserCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = localUser,
        });
        tracker.Observe(new PartyStateChangeSnapshot(
            (uint)PartyStateChangeType.CreateEndpointCompleted)
        {
            Result = 0,
            Network = Network,
            LocalUser = localUser,
            Endpoint = created ? (nint)0x6000 : (nint)0x7000,
        });

        Assert.True(tracker.IsActive);
        return tracker.LocalNetworkRole;
    }

    private static ChatMessage Message(
        string sender,
        ChatMessageKind kind,
        int playerNumber) =>
        new(
            Sequence: 1,
            Timestamp: DateTimeOffset.UtcNow,
            Sender: sender,
            Text: "hello",
            Kind: kind,
            PlayerNumber: playerNumber);
}
