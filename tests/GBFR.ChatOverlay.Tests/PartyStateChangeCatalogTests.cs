using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyStateChangeCatalogTests
{
    [Theory]
    [InlineData(2u, "CreateNewNetworkCompleted")]
    [InlineData(3u, "ConnectToNetworkCompleted")]
    [InlineData(4u, "AuthenticateLocalUserCompleted")]
    [InlineData(10u, "CreateEndpointCompleted")]
    [InlineData(31u, "CreateChatControlCompleted")]
    [InlineData(46u, "ChatControlJoinedNetwork")]
    [InlineData(48u, "ConnectChatControlCompleted")]
    public void GetName_ReturnsOfficialPartyName(uint value, string expected)
    {
        Assert.Equal(expected, PartyStateChangeCatalog.GetName(value));
    }

    [Theory]
    [InlineData(24u)]
    [InlineData(30u)]
    [InlineData(44u)]
    [InlineData(61u)]
    public void GetName_LabelsUnknownAndReservedValues(uint value)
    {
        Assert.Equal($"Unknown({value})", PartyStateChangeCatalog.GetName(value));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(31u)]
    [InlineData(59u)]
    [InlineData(60u)]
    public void IsKnown_AcceptsOfficialParty11012Values(uint value)
    {
        Assert.True(PartyStateChangeCatalog.IsKnown(value));
    }

    [Theory]
    [InlineData(24u)]
    [InlineData(30u)]
    [InlineData(44u)]
    [InlineData(61u)]
    public void IsKnown_RejectsReservedAndUnknownValues(uint value)
    {
        Assert.False(PartyStateChangeCatalog.IsKnown(value));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(12u)]
    [InlineData(19u)]
    [InlineData(33u)]
    [InlineData(46u)]
    [InlineData(54u)]
    public void IsLifecycle_AllowsLowFrequencyLifecycleEvents(uint value)
    {
        Assert.True(PartyStateChangeCatalog.IsLifecycle(value));
    }

    [Theory]
    [InlineData(21u)]
    [InlineData(22u)]
    [InlineData(36u)]
    [InlineData(37u)]
    [InlineData(59u)]
    [InlineData(61u)]
    public void IsLifecycle_FiltersPayloadAndUnknownEvents(uint value)
    {
        Assert.False(PartyStateChangeCatalog.IsLifecycle(value));
    }
}
