using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyLifecycleProbeTests
{
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
}
