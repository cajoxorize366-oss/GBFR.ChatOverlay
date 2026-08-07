using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyHostSlotResolverTests
{
    private static readonly string[] FourMembers =
        ["entity-player-1", "entity-player-2", "entity-player-3", "entity-player-4"];

    [Theory]
    [InlineData("entity-player-1", 1)]
    [InlineData("entity-player-2", 2)]
    [InlineData("entity-player-3", 3)]
    [InlineData("entity-player-4", 4)]
    public void TryResolvePlayerNumber_MapsExactOwnerEntityId(string ownerEntityId, int expectedPlayerNumber)
    {
        Assert.True(PartyHostSlotResolver.TryResolvePlayerNumber(ownerEntityId, FourMembers, out var playerNumber));
        Assert.Equal(expectedPlayerNumber, playerNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ENTITY-PLAYER-2")]
    [InlineData("entity-player-5")]
    public void TryResolvePlayerNumber_FailsClosedForMissingOrNonExactOwner(string? ownerEntityId)
    {
        Assert.False(PartyHostSlotResolver.TryResolvePlayerNumber(ownerEntityId, FourMembers, out var playerNumber));
        Assert.Equal(0, playerNumber);
    }

    [Fact]
    public void TryResolvePlayerNumber_FailsClosedForDuplicateIdentity()
    {
        string[] members = ["owner", "other", "owner", "fourth"];

        Assert.False(PartyHostSlotResolver.TryResolvePlayerNumber("owner", members, out var playerNumber));
        Assert.Equal(0, playerNumber);
    }

    [Fact]
    public void TryResolvePlayerNumber_AllowsUnoccupiedSlots()
    {
        string[] members = ["owner", "other", string.Empty, "fourth"];

        Assert.True(PartyHostSlotResolver.TryResolvePlayerNumber("owner", members, out var playerNumber));
        Assert.Equal(1, playerNumber);
    }

    [Fact]
    public void TryResolvePlayerNumber_FailsClosedForMalformedWhitespaceIdentity()
    {
        string[] members = ["owner", "other", "   ", "fourth"];

        Assert.False(PartyHostSlotResolver.TryResolvePlayerNumber("owner", members, out var playerNumber));
        Assert.Equal(0, playerNumber);
    }

    [Theory]
    [MemberData(nameof(InvalidMemberTables))]
    public void TryResolvePlayerNumber_RequiresExactlyFourSlots(IReadOnlyList<string>? members)
    {
        Assert.False(PartyHostSlotResolver.TryResolvePlayerNumber("owner", members, out var playerNumber));
        Assert.Equal(0, playerNumber);
    }

    public static TheoryData<IReadOnlyList<string>?> InvalidMemberTables => new()
    {
        null,
        Array.Empty<string>(),
        new[] { "owner", "two", "three" },
        new[] { "owner", "two", "three", "four", "five" },
    };
}
