using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyRoomIdentitySnapshotResolverTests
{
    private static RelinkPartyMemberIdentitySnapshot Snapshot(int localMemberSlot, params string[] entityIds) =>
        new(entityIds, localMemberSlot);

    [Fact]
    public void ResolveHostState_OwnerAtSlotZeroWithDifferentLocalSlot_IsRemoteHostPresent()
    {
        var snapshot = Snapshot(2, "owner", "second", "third", "fourth");

        var state = PartyRoomIdentitySnapshotResolver.ResolveHostState(
            "owner",
            snapshot,
            hasObservedRemoteHostPresent: true,
            out var ownerSlot);

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, state);
        Assert.Equal(0, ownerSlot);
    }

    [Fact]
    public void ResolveHostState_OwnerAtLocalMemberSlot_IsLocalHost()
    {
        var snapshot = Snapshot(2, "first", "second", "owner", "fourth");

        var state = PartyRoomIdentitySnapshotResolver.ResolveHostState(
            "owner",
            snapshot,
            hasObservedRemoteHostPresent: false,
            out var ownerSlot);

        Assert.Equal(PartyRoomHostState.LocalHost, state);
        Assert.Equal(2, ownerSlot);
    }

    [Fact]
    public void ResolveHostState_MissingPreviouslyPresentRemoteOwner_IsRemoteHostMissing()
    {
        var snapshot = Snapshot(2, "first", "second", "third", "fourth");

        var state = PartyRoomIdentitySnapshotResolver.ResolveHostState(
            "owner",
            snapshot,
            hasObservedRemoteHostPresent: true,
            out var ownerSlot);

        Assert.Equal(PartyRoomHostState.RemoteHostMissing, state);
        Assert.Equal(-1, ownerSlot);
    }

    [Fact]
    public void ResolveHostState_MissingNeverPresentOwner_IsUnknown()
    {
        var snapshot = Snapshot(2, "first", "second", "third", "fourth");

        var state = PartyRoomIdentitySnapshotResolver.ResolveHostState(
            "owner",
            snapshot,
            hasObservedRemoteHostPresent: false,
            out var ownerSlot);

        Assert.Equal(PartyRoomHostState.Unknown, state);
        Assert.Equal(-1, ownerSlot);
    }

    [Fact]
    public void ResolveHostState_MalformedSnapshots_FailClosedToUnknown()
    {
        Assert.Equal(
            PartyRoomHostState.Unknown,
            PartyRoomIdentitySnapshotResolver.ResolveHostState(
                "owner",
                new RelinkPartyMemberIdentitySnapshot(null!, 0),
                hasObservedRemoteHostPresent: true,
                out _));
        Assert.Equal(
            PartyRoomHostState.Unknown,
            PartyRoomIdentitySnapshotResolver.ResolveHostState(
                "owner",
                Snapshot(4, "owner", "second", "third", "fourth"),
                hasObservedRemoteHostPresent: true,
                out _));
        Assert.Equal(
            PartyRoomHostState.Unknown,
            PartyRoomIdentitySnapshotResolver.ResolveHostState(
                "owner",
                Snapshot(2, "owner", "second", "third"),
                hasObservedRemoteHostPresent: true,
                out _));
        Assert.Equal(
            PartyRoomHostState.Unknown,
            PartyRoomIdentitySnapshotResolver.ResolveHostState(
                "owner",
                Snapshot(2, "owner", "   ", "third", "fourth"),
                hasObservedRemoteHostPresent: true,
                out _));
        Assert.Equal(
            PartyRoomHostState.Unknown,
            PartyRoomIdentitySnapshotResolver.ResolveHostState(
                "owner",
                Snapshot(2, "owner", "owner", "third", "fourth"),
                hasObservedRemoteHostPresent: true,
                out _));
    }
}
