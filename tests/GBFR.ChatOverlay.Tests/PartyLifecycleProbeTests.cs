using GBFR.ChatOverlay.Native;
using System.Reflection;
using ReloadedHooksApi = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyLifecycleProbeTests
{
    [Fact]
    public void Dispose_IsIdempotentAndPreventsResume()
    {
        var invalidationCount = 0;
        var hooks = DispatchProxy.Create<ReloadedHooksApi, UnusedReloadedHooksProxy>();
        var probe = new PartyLifecycleProbe(
            hooks,
            _ => { },
            invalidateRoomIdentity: () => invalidationCount++);

        probe.Dispose();
        probe.Dispose();

        Assert.False(probe.IsInitialized);
        Assert.False(probe.IsOnlineRoomActive);
        Assert.Equal(1, invalidationCount);
        Assert.Throws<ObjectDisposedException>(probe.Resume);
    }

    [Fact]
    public void MapTalkingRemotePlayers_SkipsLocalSlotAndUsesAscendingRemoteOrdinals()
    {
        var snapshot = new RelinkPartyMemberIdentitySnapshot(
            ["entity-0", "entity-1", "entity-2", "entity-3"],
            LocalMemberSlot: 2);

        var talking = PartyLifecycleProbe.MapTalkingRemotePlayers(
            snapshot,
            ["entity-0", "entity-2", "entity-3"]);

        Assert.Equal([1, 3], talking);
    }

    [Fact]
    public void MapTalkingRemotePlayers_ReturnsEmptyWhenNoTalkersOrSnapshotIsInvalid()
    {
        var snapshot = new RelinkPartyMemberIdentitySnapshot(
            ["entity-0", "entity-1", "entity-2", "entity-3"],
            LocalMemberSlot: 2);

        Assert.Empty(PartyLifecycleProbe.MapTalkingRemotePlayers(snapshot, []));
        Assert.Empty(PartyLifecycleProbe.MapTalkingRemotePlayers(
            new RelinkPartyMemberIdentitySnapshot([], -1),
            ["entity-0"]));
    }

    [Theory]
    [InlineData("entity-0", "entity-0")]
    public void MapTalkingRemotePlayers_RejectsDuplicateNonEmptyEntityIds(
        string first,
        string second)
    {
        var snapshot = new RelinkPartyMemberIdentitySnapshot(
            [first, second, "entity-2", "entity-3"],
            LocalMemberSlot: 2);

        Assert.Empty(PartyLifecycleProbe.MapTalkingRemotePlayers(snapshot, ["entity-0"]));
    }

    [Fact]
    public void MapTalkingRemotePlayers_RejectsNullAndWhitespaceEntityIds()
    {
        Assert.Empty(PartyLifecycleProbe.MapTalkingRemotePlayers(
            new RelinkPartyMemberIdentitySnapshot(
                [null!, "", "entity-2", "entity-3"],
                LocalMemberSlot: 2),
            ["entity-2"]));
        Assert.Empty(PartyLifecycleProbe.MapTalkingRemotePlayers(
            new RelinkPartyMemberIdentitySnapshot(
                ["entity-0", " ", "entity-2", "entity-3"],
                LocalMemberSlot: 2),
            ["entity-2"]));
    }

    [Fact]
    public void MapRemotePlayers_MapsEstablishedEntityIdsToExactRemoteOrdinals()
    {
        var snapshot = new RelinkPartyMemberIdentitySnapshot(
            ["slot-0", "slot-1", "slot-2", "slot-3"],
            LocalMemberSlot: 2);

        Assert.Equal(
            [1, 3],
            PartyLifecycleProbe.MapRemotePlayers(snapshot, ["slot-0", "slot-3"]));
    }

    [Fact]
    public void MapRemotePlayers_IgnoresUnknownAndLocalEntityIds()
    {
        var snapshot = new RelinkPartyMemberIdentitySnapshot(
            ["slot-0", "slot-1", "slot-2", "slot-3"],
            LocalMemberSlot: 2);

        Assert.Empty(PartyLifecycleProbe.MapRemotePlayers(snapshot, ["unknown"]));
        Assert.Empty(PartyLifecycleProbe.MapRemotePlayers(snapshot, ["slot-2"]));
    }

    [Fact]
    public void MapRemotePlayers_RejectsDuplicateSelectedEntityIds()
    {
        var snapshot = new RelinkPartyMemberIdentitySnapshot(
            ["slot-0", "slot-1", "slot-2", "slot-3"],
            LocalMemberSlot: 2);

        Assert.Empty(PartyLifecycleProbe.MapRemotePlayers(snapshot, ["slot-0", "slot-0"]));
    }

    [Fact]
    public void MapOccupiedRemotePlayers_HandlesLocalInNonzeroSlotAndSparseSlots()
    {
        var sparse = new RelinkPartyMemberIdentitySnapshot(
            ["slot-0", "", "slot-2", "slot-3"],
            LocalMemberSlot: 2);

        Assert.Equal([1, 3], PartyLifecycleProbe.MapOccupiedRemotePlayers(sparse));

        var onlySecondRemote = new RelinkPartyMemberIdentitySnapshot(
            ["", "slot-1", "slot-2", ""],
            LocalMemberSlot: 0);

        Assert.Equal([1, 2], PartyLifecycleProbe.MapOccupiedRemotePlayers(onlySecondRemote));
    }

    [Fact]
    public void MapOccupiedRemotePlayers_ReturnsEmptyForInvalidOrMissingIdentity()
    {
        Assert.Empty(PartyLifecycleProbe.MapOccupiedRemotePlayers(
            new RelinkPartyMemberIdentitySnapshot([], -1)));
        Assert.Empty(PartyLifecycleProbe.MapOccupiedRemotePlayers(
            new RelinkPartyMemberIdentitySnapshot(
                ["duplicate", "duplicate", "", ""],
                LocalMemberSlot: 1)));
        Assert.Empty(PartyLifecycleProbe.MapOccupiedRemotePlayers(
            new RelinkPartyMemberIdentitySnapshot(
                ["slot-0", " ", "slot-2", "slot-3"],
                LocalMemberSlot: 2)));
    }

    [Fact]
    public void MapVoiceIndicatorSnapshot_UsesOneCoherentIdentitySnapshotForAllStates()
    {
        var identity = new RelinkPartyMemberIdentitySnapshot(
            ["slot-0", "", "local", "slot-3"],
            LocalMemberSlot: 2);
        var entities = new PartyVoiceEntitySnapshot(
            EstablishedRemoteEntityIds: ["slot-0", "slot-3"],
            TalkingRemoteEntityIds: ["slot-3"]);

        var snapshot = PartyLifecycleProbe.MapVoiceIndicatorSnapshot(identity, entities);

        Assert.True(snapshot.IsValid);
        Assert.Equal([1, 3], snapshot.EstablishedRemotePlayers);
        Assert.Equal([1, 3], snapshot.OccupiedRemotePlayers);
        Assert.Equal([3], snapshot.TalkingRemotePlayers);
    }

    [Fact]
    public void MapVoiceIndicatorSnapshot_FailsClosedForAnIncoherentIdentitySnapshot()
    {
        var snapshot = PartyLifecycleProbe.MapVoiceIndicatorSnapshot(
            new RelinkPartyMemberIdentitySnapshot(
                ["duplicate", "duplicate", "", ""],
                LocalMemberSlot: 1),
            new PartyVoiceEntitySnapshot(["duplicate"], ["duplicate"]));

        Assert.False(snapshot.IsValid);
        Assert.Empty(snapshot.EstablishedRemotePlayers);
        Assert.Empty(snapshot.OccupiedRemotePlayers);
        Assert.Empty(snapshot.TalkingRemotePlayers);
    }

    private class UnusedReloadedHooksProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"Unexpected Reloaded.Hooks call during lifecycle-only test: {targetMethod?.Name}");
    }
}
