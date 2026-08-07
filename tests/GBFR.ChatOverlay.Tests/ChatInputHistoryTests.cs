using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatInputHistoryTests
{
    [Fact]
    public void WheelNavigation_RecallsNewestFirstAndRestoresDraft()
    {
        var history = new ChatInputHistory();
        history.Record("first");
        history.Record("second");

        Assert.Equal("second", history.MovePrevious("unfinished"));
        Assert.Equal("first", history.MovePrevious("second"));
        Assert.Equal("second", history.MoveNext("first"));
        Assert.Equal("unfinished", history.MoveNext("second"));
    }

    [Fact]
    public void EditingRecalledText_StartsANewNavigationSession()
    {
        var history = new ChatInputHistory();
        history.Record("first");
        history.Record("second");
        Assert.Equal("second", history.MovePrevious("draft"));

        Assert.Equal("second", history.MovePrevious("edited second"));
        Assert.Equal("edited second", history.MoveNext("second"));
    }
}
