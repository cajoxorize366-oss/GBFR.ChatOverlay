using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyMemberSlotMapTests
{
    [Fact]
    public void RemoteOrdinals_FollowAscendingActualSlotOrderAroundEveryLocalSlot()
    {
        for (var localSlot = 0; localSlot < 4; localSlot++)
        {
            var expectedOrdinal = 0;
            for (var actualSlot = 0; actualSlot < 4; actualSlot++)
            {
                if (actualSlot == localSlot)
                {
                    Assert.False(PartyMemberSlotMap.TryGetRemoteOrdinal(localSlot, actualSlot, out _));
                    continue;
                }

                expectedOrdinal++;
                Assert.True(PartyMemberSlotMap.TryGetRemoteOrdinal(localSlot, actualSlot, out var remoteOrdinal));
                Assert.Equal(expectedOrdinal, remoteOrdinal);
            }
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 4)]
    [InlineData(-1, 1)]
    public void InvalidRemoteOrdinals_FailClosed(int localSlot, int remoteOrdinal)
    {
        Assert.False(PartyMemberSlotMap.TryGetActualSlot(localSlot, remoteOrdinal, out _));
    }

    [Fact]
    public void ActualSlotAndRemoteOrdinal_RoundTripForEveryValidPair()
    {
        for (var localSlot = 0; localSlot < 4; localSlot++)
        {
            for (var actualSlot = 0; actualSlot < 4; actualSlot++)
            {
                if (actualSlot == localSlot)
                    continue;

                Assert.True(PartyMemberSlotMap.TryGetRemoteOrdinal(localSlot, actualSlot, out var ordinal));
                Assert.True(PartyMemberSlotMap.TryGetActualSlot(localSlot, ordinal, out var roundTripped));
                Assert.Equal(actualSlot, roundTripped);
            }
        }
    }

    [Fact]
    public void PlayerNumber_KeepsUiTwoThroughFourConvention()
    {
        Assert.True(PartyMemberSlotMap.TryGetPlayerNumber(2, 0, out var firstRemote));
        Assert.True(PartyMemberSlotMap.TryGetPlayerNumber(2, 1, out var secondRemote));
        Assert.True(PartyMemberSlotMap.TryGetPlayerNumber(2, 3, out var thirdRemote));

        Assert.Equal(2, firstRemote);
        Assert.Equal(3, secondRemote);
        Assert.Equal(4, thirdRemote);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(4, 0)]
    [InlineData(0, 4)]
    public void InvalidSlots_FailClosed(int localSlot, int actualSlot)
    {
        Assert.False(PartyMemberSlotMap.TryGetRemoteOrdinal(localSlot, actualSlot, out _));
    }
}
