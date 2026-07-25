using System.Buffers.Binary;
using System.Text;
using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class ImeCandidateListParserTests
{
    [Fact]
    public void UnicodeCandidateListParsesSelectionAndVisiblePage()
    {
        var buffer = BuildCandidateList(
            selection: 1,
            pageStart: 0,
            pageSize: 3,
            "我",
            "窝",
            "握");

        var parsed = ImeCandidateListParser.TryParse(buffer, 0, out var snapshot);

        Assert.True(parsed);
        Assert.NotNull(snapshot);
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(1, snapshot.SelectedIndex);
        Assert.Equal(0, snapshot.PageStart);
        Assert.Equal(3, snapshot.PageSize);
        Assert.Equal(new[] { "我", "窝", "握" }, snapshot.Candidates);
        Assert.Equal("候选：1.我   [2.窝]   3.握", snapshot.BuildDisplayText());
    }

    [Fact]
    public void ZeroPageSizeFallsBackToAtMostNineVisibleCandidates()
    {
        var candidates = Enumerable.Range(1, 12).Select(index => $"词{index}").ToArray();
        var buffer = BuildCandidateList(
            selection: 9,
            pageStart: 3,
            pageSize: 0,
            candidates);

        var parsed = ImeCandidateListParser.TryParse(buffer, 2, out var snapshot);

        Assert.True(parsed);
        Assert.NotNull(snapshot);
        Assert.Equal((uint)2, snapshot.ListIndex);
        Assert.Equal(3, snapshot.PageStart);
        Assert.Equal(9, snapshot.PageSize);
        Assert.Contains("[7.词10]", snapshot.BuildDisplayText());
        Assert.EndsWith("9.词12", snapshot.BuildDisplayText());
    }

    [Fact]
    public void ExplicitPageSizeIsLimitedToTenNumberKeys()
    {
        var candidates = Enumerable.Range(1, 12).Select(index => $"词{index}").ToArray();
        var buffer = BuildCandidateList(
            selection: 9,
            pageStart: 0,
            pageSize: 12,
            candidates);

        var parsed = ImeCandidateListParser.TryParse(buffer, 0, out var snapshot);

        Assert.True(parsed);
        Assert.NotNull(snapshot);
        Assert.Equal(10, snapshot.PageSize);
        Assert.Contains("[0.词10]", snapshot.BuildDisplayText());
        Assert.DoesNotContain("词11", snapshot.BuildDisplayText());
    }

    [Fact]
    public void OutOfRangeStringOffsetIsRejected()
    {
        var buffer = BuildCandidateList(0, 0, 1, "我");
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24, 4), uint.MaxValue);

        var parsed = ImeCandidateListParser.TryParse(buffer, 0, out var snapshot);

        Assert.False(parsed);
        Assert.Null(snapshot);
    }

    [Theory]
    [InlineData(Win32ImeCandidateReader.ImnOpenCandidate)]
    [InlineData(Win32ImeCandidateReader.ImnChangeCandidate)]
    [InlineData(Win32ImeCandidateReader.ImnSetCandidatePosition)]
    public void CandidateRefreshNotificationsAreRecognized(uint notification)
    {
        Assert.True(Win32ImeCandidateReader.IsRefreshNotification(notification));
    }

    [Fact]
    public void CandidateCloseNotificationDoesNotRequestARefresh()
    {
        Assert.False(
            Win32ImeCandidateReader.IsRefreshNotification(
                Win32ImeCandidateReader.ImnCloseCandidate));
    }

    private static byte[] BuildCandidateList(
        uint selection,
        uint pageStart,
        uint pageSize,
        params string[] candidates)
    {
        const int fixedHeaderSize = 24;
        var encodedCandidates = candidates
            .Select(candidate => Encoding.Unicode.GetBytes(candidate + '\0'))
            .ToArray();
        var offsetTableEnd = fixedHeaderSize + candidates.Length * sizeof(uint);
        var totalSize = offsetTableEnd + encodedCandidates.Sum(candidate => candidate.Length);
        var buffer = new byte[totalSize];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer, checked((uint)totalSize));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), checked((uint)candidates.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), selection);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), pageStart);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), pageSize);

        var nextStringOffset = offsetTableEnd;
        for (var index = 0; index < encodedCandidates.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                buffer.AsSpan(fixedHeaderSize + index * sizeof(uint)),
                checked((uint)nextStringOffset));
            encodedCandidates[index].CopyTo(buffer.AsSpan(nextStringOffset));
            nextStringOffset += encodedCandidates[index].Length;
        }

        return buffer;
    }
}
