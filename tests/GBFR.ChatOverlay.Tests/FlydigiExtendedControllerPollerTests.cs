using GBFR.ChatOverlay.Input;

namespace GBFR.ChatOverlay.Tests;

public sealed class FlydigiExtendedControllerPollerTests
{
    [Fact]
    public void ParseButtons_MapsVader5ProtocolBits()
    {
        var payload = new byte[31];
        payload[0] = 0x5A;
        payload[1] = 0xA5;
        payload[2] = 0xEF;
        payload[13] = 0xFF;
        payload[14] = 0x01;

        var buttons = FlydigiExtendedControllerPoller.ParseButtons(payload);

        Assert.Equal(
            ExtendedControllerButtons.C |
            ExtendedControllerButtons.Z |
            ExtendedControllerButtons.M1 |
            ExtendedControllerButtons.M2 |
            ExtendedControllerButtons.M3 |
            ExtendedControllerButtons.M4 |
            ExtendedControllerButtons.LM |
            ExtendedControllerButtons.RM |
            ExtendedControllerButtons.Circle,
            buttons);
    }

    [Fact]
    public void NormalizeReport_AcceptsLeadingReportPlaceholder()
    {
        byte[] report = [0, 0x5A, 0xA5, 0xEF, 0, 0];

        Assert.True(FlydigiExtendedControllerPoller.TryNormalizeReport(report, out var payload));
        Assert.Equal(0x5A, payload[0]);
        Assert.Equal(0xEF, payload[2]);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ParseTakeoverStatus_UsesTheVader5StatusFlag(byte flag, bool expected)
    {
        var payload = new byte[31];
        payload[0] = 0x5A;
        payload[1] = 0xA5;
        payload[2] = 0x10;
        payload[9] = flag;

        Assert.True(FlydigiExtendedControllerPoller.TryParseTakeoverStatus(
            payload,
            out var takeoverAllowed));
        Assert.Equal(expected, takeoverAllowed);
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(0, 0, false)]
    public void ParseAcquisitionStatus_MatchesTheFlydigiAcquireReply(
        byte acquired,
        byte alternateStatus,
        bool expected)
    {
        var payload = new byte[31];
        payload[0] = 0x5A;
        payload[1] = 0xA5;
        payload[2] = 0x1C;
        payload[5] = acquired;
        payload[6] = alternateStatus;

        Assert.True(FlydigiExtendedControllerPoller.TryParseAcquisitionStatus(
            payload,
            out var acquisitionSucceeded));
        Assert.Equal(expected, acquisitionSucceeded);
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, true)]
    public void InputReports_RequireConfirmedTakeoverAndNoAcquisitionFailure(
        bool takeoverKnown,
        bool takeoverAllowed,
        bool acquisitionKnown,
        bool acquisitionSucceeded,
        bool expected)
    {
        Assert.Equal(
            expected,
            FlydigiExtendedControllerPoller.ShouldAcceptInputReport(
                takeoverKnown,
                takeoverAllowed,
                acquisitionKnown,
                acquisitionSucceeded));
    }
}
