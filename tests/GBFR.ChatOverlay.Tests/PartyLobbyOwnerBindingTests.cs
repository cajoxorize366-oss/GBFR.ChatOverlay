using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyLobbyOwnerBindingTests
{
    private static readonly nint LobbyA = (nint)0x100;
    private static readonly nint LobbyB = (nint)0x200;

    private static RelinkPartyMemberIdentitySnapshot Snapshot(int localMemberSlot, params string[] entityIds) =>
        new(entityIds, localMemberSlot);

    [Fact]
    public void OwnerAtSlotZeroWithLocalSlotTwo_IsRemoteHostPresentAndUiPlayerTwo()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");

        var identity = binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth"), PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(2, playerNumber);
    }

    [Fact]
    public void OwnerAtLocalSlot_IsLocalHostAndUiPlayerOne()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");

        var identity = binding.ResolveSnapshot(Snapshot(2, "first", "second", "host", "fourth"), PartyNetworkLocalRole.Created);

        Assert.Equal(PartyRoomHostState.LocalHost, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(1, playerNumber);
    }

    [Fact]
    public void TwoDistinctCandidatesPresent_AreUnknownAndDoNotBind()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host-a");
        binding.ObserveOwner(LobbyB, "host-b");
        var snapshot = Snapshot(2, "host-a", "host-b", "third", "fourth");

        var identity = binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.Unknown, identity.HostState);
        Assert.False(binding.TryResolveHostPlayerNumber(snapshot, PartyNetworkLocalRole.Connected, out _));
    }

    [Fact]
    public void UniqueOwnerBinding_IsNotReplacedByAnotherLobbySuccess()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        Assert.Equal(
            PartyRoomHostState.RemoteHostPresent,
            binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth"), PartyNetworkLocalRole.Connected).HostState);

        binding.ObserveOwner(LobbyB, "other");
        var identity = binding.ResolveSnapshot(Snapshot(2, "host", "other", "third", "fourth"), PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(2, playerNumber);
    }

    [Fact]
    public void RelatedLobbyFailure_DoesNotDestroyBindingOrLetUnrelatedOwnerTakeOver()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        Assert.Equal(
            PartyRoomHostState.RemoteHostPresent,
            binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth"), PartyNetworkLocalRole.Connected).HostState);

        binding.ObserveOwner(LobbyA, null);
        binding.ObserveOwner(LobbyB, "other");
        var identity = binding.ResolveSnapshot(Snapshot(2, "host", "other", "third", "fourth"), PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(2, playerNumber);
    }

    [Fact]
    public void BoundRemoteOwnerPresentThenMissing_IsRemoteHostMissingAndClearsHostNumber()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        Assert.Equal(
            PartyRoomHostState.RemoteHostPresent,
            binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth"), PartyNetworkLocalRole.Connected).HostState);
        binding.CacheRoomName("Quest Room");

        var missing = binding.ResolveSnapshot(Snapshot(2, "first", "second", "third", "fourth"), PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.RemoteHostMissing, missing.HostState);
        Assert.Equal("Quest Room", missing.RoomName);
        Assert.False(binding.TryResolveHostPlayerNumber(Snapshot(2, "first", "second", "third", "fourth"), PartyNetworkLocalRole.Connected, out _));
        Assert.False(binding.TryGetCachedHostPlayerNumber(out _));
    }

    [Fact]
    public void NeverPresentOwnerMissing_IsUnknownWithoutRoomNameOrHostNumber()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");

        var identity = binding.ResolveSnapshot(Snapshot(2, "first", "second", "third", "fourth"), PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.Unknown, identity.HostState);
        Assert.Null(identity.RoomName);
        Assert.False(binding.TryResolveHostPlayerNumber(Snapshot(2, "first", "second", "third", "fourth"), PartyNetworkLocalRole.Connected, out _));
    }

    [Fact]
    public void ConnectedOnlyLocalOwnerDisappearing_IsUnknownNotRemoteHostMissing()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "local-host");
        Assert.Equal(
            PartyRoomHostState.Unknown,
            binding.ResolveSnapshot(Snapshot(2, "first", "second", "local-host", "fourth"), PartyNetworkLocalRole.Connected).HostState);

        var missing = binding.ResolveSnapshot(Snapshot(2, "first", "second", "third", "fourth"), PartyNetworkLocalRole.Connected);

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
            binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth"), PartyNetworkLocalRole.Connected).HostState);
        binding.CacheRoomName("Quest Room");

        var malformed = binding.ResolveSnapshot(Snapshot(2, "host", "host", "third", "fourth"), PartyNetworkLocalRole.Connected);

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
            binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth"), PartyNetworkLocalRole.Connected).HostState);
        binding.CacheRoomName("Quest Room");

        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(Snapshot(2, "host", "   ", "third", "fourth"), PartyNetworkLocalRole.Connected).HostState);
        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(new RelinkPartyMemberIdentitySnapshot(null!, 2), PartyNetworkLocalRole.Connected).HostState);
        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(Snapshot(2, "host", "second", "third"), PartyNetworkLocalRole.Connected).HostState);
        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(Snapshot(4, "host", "second", "third", "fourth"), PartyNetworkLocalRole.Connected).HostState);
        Assert.False(binding.TryGetCachedHostPlayerNumber(out _));
    }

    [Fact]
    public void ResetThenNewBinding_ClearsOldRoomNameAndHostNumber()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "old-host");
        Assert.Equal(
            PartyRoomHostState.RemoteHostPresent,
            binding.ResolveSnapshot(Snapshot(2, "old-host", "second", "third", "fourth"), PartyNetworkLocalRole.Connected).HostState);
        binding.CacheRoomName("Old Room");
        binding.Reset();

        Assert.False(binding.TryResolveHostPlayerNumber(Snapshot(2, "old-host", "second", "third", "fourth"), PartyNetworkLocalRole.Connected, out _));

        binding.ObserveOwner(LobbyB, "new-host");
        var identity = binding.ResolveSnapshot(Snapshot(2, "first", "second", "new-host", "fourth"), PartyNetworkLocalRole.Created);

        Assert.Equal(PartyRoomHostState.LocalHost, identity.HostState);
        Assert.Null(identity.RoomName);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(1, playerNumber);
    }

    [Fact]
    public void CandidateFailure_RemovesOnlyThatLobbyCandidate()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host-a");
        binding.ObserveOwner(LobbyB, "host-b");
        var snapshot = Snapshot(2, "host-a", "host-b", "third", "fourth");
        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Connected).HostState);

        binding.ObserveOwner(LobbyA, null);
        var identity = binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(3, playerNumber);
    }

    [Fact]
    public void InvalidOwnerCandidate_RemovesOnlyThatLobbyCandidate()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host-a");
        binding.ObserveOwner(LobbyB, "host-b");
        var snapshot = Snapshot(2, "host-a", "host-b", "third", "fourth");
        Assert.Equal(PartyRoomHostState.Unknown, binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Connected).HostState);

        binding.ObserveOwner(LobbyA, "   ");
        var identity = binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(3, playerNumber);
    }

    [Fact]
    public void SameOwnerFromTwoLobbies_BindsAsOneDistinctCandidate()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        binding.ObserveOwner(LobbyB, "host");

        var identity = binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth"), PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(2, playerNumber);
    }

    [Fact]
    public void ZeroLobbyHandleCandidate_DoesNotResetExistingCandidates()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        binding.ObserveOwner(nint.Zero, "ignored");

        var identity = binding.ResolveSnapshot(Snapshot(2, "host", "second", "third", "fourth"), PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
    }

    [Fact]
    public void CreatedRole_IsLocalHostWithoutPlayFabCandidate()
    {
        var binding = new PartyLobbyOwnerBinding();
        var snapshot = Snapshot(2, "first", "second", "third", "fourth");

        var identity = binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Created);

        Assert.Equal(PartyRoomHostState.LocalHost, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var cached));
        Assert.Equal(1, cached);
        Assert.True(binding.TryResolveHostPlayerNumber(snapshot, PartyNetworkLocalRole.Created, out var resolved));
        Assert.Equal(1, resolved);
    }

    [Fact]
    public void CreatedRole_MalformedSnapshot_IsUnknown()
    {
        var binding = new PartyLobbyOwnerBinding();

        var identity = binding.ResolveSnapshot(
            new RelinkPartyMemberIdentitySnapshot(null!, 2),
            PartyNetworkLocalRole.Created);

        Assert.Equal(PartyRoomHostState.Unknown, identity.HostState);
        Assert.False(binding.TryGetCachedHostPlayerNumber(out _));
    }

    [Fact]
    public void ConnectedRole_WithLocalAndRemoteCandidates_BindsRemoteHostOnly()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "local-owner");
        binding.ObserveOwner(LobbyB, "remote-owner");
        var snapshot = Snapshot(2, "remote-owner", "second", "local-owner", "fourth");

        var identity = binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.RemoteHostPresent, identity.HostState);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var cached));
        Assert.Equal(2, cached);
        Assert.True(binding.TryResolveHostPlayerNumber(snapshot, PartyNetworkLocalRole.Connected, out var resolved));
        Assert.Equal(2, resolved);
    }

    [Fact]
    public void ConnectedRole_WithOnlyLocalCandidate_DoesNotSelfHost()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "local-owner");
        var snapshot = Snapshot(2, "first", "second", "local-owner", "fourth");

        var identity = binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.Unknown, identity.HostState);
        Assert.False(binding.TryGetCachedHostPlayerNumber(out _));
        Assert.False(binding.TryResolveHostPlayerNumber(snapshot, PartyNetworkLocalRole.Connected, out _));
    }

    [Fact]
    public void ConnectedRole_WithMultipleRemoteCandidates_IsUnknown()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host-a");
        binding.ObserveOwner(LobbyB, "host-b");
        var snapshot = Snapshot(2, "host-a", "host-b", "third", "fourth");

        var identity = binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.Unknown, identity.HostState);
        Assert.False(binding.TryGetCachedHostPlayerNumber(out _));
    }

    [Fact]
    public void UnknownRole_DoesNotUseOwnerCandidates()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "host");
        var snapshot = Snapshot(2, "host", "second", "third", "fourth");

        var identity = binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Unknown);

        Assert.Equal(PartyRoomHostState.Unknown, identity.HostState);
        Assert.False(binding.TryResolveHostPlayerNumber(snapshot, PartyNetworkLocalRole.Unknown, out _));
    }

    [Fact]
    public void RoleSwitch_FromConnectedRemoteToCreated_ClearsOldBindingAndRoomName()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "remote-owner");
        var snapshot = Snapshot(2, "remote-owner", "second", "third", "fourth");
        Assert.Equal(
            PartyRoomHostState.RemoteHostPresent,
            binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Connected).HostState);
        binding.CacheRoomName("Quest Room");

        var created = binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Created);

        Assert.Equal(PartyRoomHostState.LocalHost, created.HostState);
        Assert.Null(created.RoomName);
        Assert.True(binding.TryGetCachedHostPlayerNumber(out var playerNumber));
        Assert.Equal(1, playerNumber);
    }

    [Fact]
    public void RoleSwitch_FromCreatedToConnected_ClearsLocalHostState()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner(LobbyA, "local-owner");
        var snapshot = Snapshot(2, "first", "second", "local-owner", "fourth");
        Assert.Equal(
            PartyRoomHostState.LocalHost,
            binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Created).HostState);
        binding.CacheRoomName("Quest Room");

        var connected = binding.ResolveSnapshot(snapshot, PartyNetworkLocalRole.Connected);

        Assert.Equal(PartyRoomHostState.Unknown, connected.HostState);
        Assert.Null(connected.RoomName);
        Assert.False(binding.TryGetCachedHostPlayerNumber(out _));
    }
}
