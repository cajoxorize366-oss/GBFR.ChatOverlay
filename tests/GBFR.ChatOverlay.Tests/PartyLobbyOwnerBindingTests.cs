using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyLobbyOwnerBindingTests
{
    private static readonly nint LobbyA = (nint)0x100;
    private static readonly nint LobbyB = (nint)0x200;

    private static RelinkPartyMemberIdentitySnapshot Snapshot(int localMemberSlot, params string[] entityIds) =>
        new(entityIds, localMemberSlot);

    [Fact]
    public void OwnerAtSlotZeroWithLocalSlotTwo_IsRemoteHostPresentAndHostNumberOne()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");

        var identity = binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth"));

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(1, playerNumber);
    }

    [Fact]
    public void OwnerAtLocalSlot_IsLocalHostAndHostNumberThree()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");

        var identity = binding.ResolveSnapshot(Snapshot(2, "first", "second", "host", "fourth"));

        Assert.Equal(PartyRoomHostState.LocalHost, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(3, playerNumber);
    }

    [Fact]
    public void TwoDistinctCandidatesPresent_AreUnknownAndDoNotBind()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host-a");
        binding.ObserveOwner(LobbyB, "host-b");
        var snapshot = Snapshot(2, "host-a", "host-b", "third", "fourth");

        var identity = binding.ResolveSnapshot(snapshot);

        Assert.Equal(PartyRoomHostState.Unknown, identity.HostState);
        Assert.False(binding.TryResolveHostPlayerNumber(snapshot, out _));
    }

    [Fact]
    public void UniqueOwnerBinding_IsNotReplacedByAnotherLobbySuccess()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        Assert.Equal(
            PartyRoomHostState.RemoteHostPresent,
            binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth")).HostState);

        binding.ObserveOwner(LobbyB, "other");
        var identity = binding.ResolveSnapshot(Snapshot(2, "host", "other", "third", "fourth"));

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(1, playerNumber);
    }

    [Fact]
    public void RelatedLobbyFailure_DoesNotDestroyBindingOrLetUnrelatedOwnerTakeOver()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        Assert.Equal(
            PartyRoomHostState.RemoteHostPresent,
            binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth")).HostState);

        binding.ObserveOwner(LobbyA, null);
        binding.ObserveOwner(LobbyB, "other");
        var identity = binding.ResolveSnapshot(Snapshot(2, "host", "other", "third", "fourth"));

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(1, playerNumber);
    }

    [Fact]
    public void BoundRemoteOwnerPresentThenMissing_IsRemoteHostMissingAndClearsHostNumber()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        Assert.Equal(
            PartyRoomHostState.RemoteHostPresent,
            binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth")).HostState);
        binding.CacheRoomName("Quest Room");

        var missing = binding.ResolveSnapshot(Snapshot(2, "first", "second", "third", "fourth"));

        Assert.Equal(PartyRoomHostState.RemoteHostMissing, missing.HostState);
        Assert.Equal("Quest Room", missing.RoomName);
        Assert.False(binding.TryResolveHostPlayerNumber(Snapshot(2, "first", "second", "third", "fourth"), out _));
        Assert.False(binding.TryGetCachedHostPlayerNumber(out _));
    }

    [Fact]
    public void NeverPresentOwnerMissing_IsUnknownWithoutRoomNameOrHostNumber()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");

        var identity = binding.ResolveSnapshot(Snapshot(2, "first", "second", "third", "fourth"));

        Assert.Equal(PartyRoomHostState.Unknown, identity.HostState);
        Assert.Null(identity.RoomName);
        Assert.False(binding.TryResolveHostPlayerNumber(Snapshot(2, "first", "second", "third", "fourth"), out _));
    }

    [Fact]
    public void LocalOwnerDisappearingAfterOnlyLocalHost_IsUnknownNotRemoteHostMissing()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "local-host");
        Assert.Equal(
            PartyRoomHostState.LocalHost,
            binding.ResolveSnapshot(Snapshot(2, "first", "second", "local-host", "fourth")).HostState);

        var missing = binding.ResolveSnapshot(Snapshot(2, "first", "second", "third", "fourth"));

        Assert.Equal(PartyRoomHostState.Unknown, missing.HostState);
        Assert.Null(missing.RoomName);
        Assert.False(binding.TryGetCachedHostPlayerNumber(out _));
    }

    [Fact]
    public void DuplicateOwnerSnapshot_IsUnknownAndDoesNotReuseCachedHostNumber()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        Assert.Equal(
            PartyRoomHostState.RemoteHostPresent,
            binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth")).HostState);
        binding.CacheRoomName("Quest Room");

        var malformed = binding.ResolveSnapshot(Snapshot(2, "host", "host", "third", "fourth"));

        Assert.Equal(PartyRoomHostState.Unknown, malformed.HostState);
        Assert.Null(malformed.RoomName);
        Assert.False(binding.TryGetCachedHostPlayerNumber(out _));
    }

    [Fact]
    public void MalformedSnapshots_FailClosedToUnknown()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        Assert.Equal(
            PartyRoomHostState.RemoteHostPresent,
            binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth")).HostState);
        binding.CacheRoomName("Quest Room");

        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(Snapshot(2, "host", "   ", "third", "fourth")).HostState);
        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(new RelinkPartyMemberIdentitySnapshot(null!, 2)).HostState);
        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(Snapshot(2, "host", "second", "third")).HostState);
        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(Snapshot(4, "host", "second", "third", "fourth")).HostState);
        Assert.False(binding.TryGetCachedHostPlayerNumber(out _));
    }

    [Fact]
    public void ResetThenNewBinding_ClearsOldRoomNameAndHostNumber()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "old-host");
        Assert.Equal(
            PartyRoomHostState.RemoteHostPresent,
            binding.ResolveSnapshot(Snapshot(2, "old-host", "second", "third", "fourth")).HostState);
        binding.CacheRoomName("Old Room");
        binding.Reset();

        Assert.False(binding.TryResolveHostPlayerNumber(Snapshot(2, "old-host", "second", "third", "fourth"), out _));

        binding.ObserveOwner(LobbyB, "new-host");
        var identity = binding.ResolveSnapshot(Snapshot(2, "first", "second", "new-host", "fourth"));

        Assert.Equal(PartyRoomHostState.LocalHost, identity.HostState);
        Assert.Null(identity.RoomName);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(3, playerNumber);
    }

    [Fact]
    public void CandidateFailure_RemovesOnlyThatLobbyCandidate()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host-a");
        binding.ObserveOwner(LobbyB, "host-b");
        var snapshot = Snapshot(2, "host-a", "host-b", "third", "fourth");
        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(snapshot).HostState);

        binding.ObserveOwner(LobbyA, null);
        var identity = binding.ResolveSnapshot(snapshot);

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(2, playerNumber);
    }

    [Fact]
    public void InvalidOwnerCandidate_RemovesOnlyThatLobbyCandidate()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host-a");
        binding.ObserveOwner(LobbyB, "host-b");
        var snapshot = Snapshot(2, "host-a", "host-b", "third", "fourth");
        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(snapshot).HostState);

        binding.ObserveOwner(LobbyA, "   ");
        var identity = binding.ResolveSnapshot(snapshot);

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(2, playerNumber);
    }

    [Fact]
    public void SameOwnerFromTwoLobbies_BindsAsOneDistinctCandidate()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        binding.ObserveOwner(LobbyB, "host");

        var identity = binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth"));

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(1, playerNumber);
    }

    [Fact]
    public void ZeroLobbyHandleCandidate_DoesNotResetExistingCandidates()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        binding.ObserveOwner(nint.Zero, "ignored");

        var identity = binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth"));

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
    }
}
