using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatHistoryTests
{
    [Fact]
    public void Add_EvictsOldestMessageAtCapacity()
    {
        var history = new ChatHistory(2);

        history.Add("Io", "one", ChatMessageKind.Party);
        history.Add("Katalina", "two", ChatMessageKind.Party);
        history.Add("Vane", "three", ChatMessageKind.Party);

        var snapshot = history.Snapshot();
        Assert.Equal(new[] { "two", "three" }, snapshot.Select(message => message.Text));
        Assert.Equal(new long[] { 2, 3 }, snapshot.Select(message => message.Sequence));
    }

    [Fact]
    public void Snapshot_IsNotChangedByLaterMessages()
    {
        var history = new ChatHistory(2);
        history.Add("Io", "one", ChatMessageKind.Party);
        var snapshot = history.Snapshot();

        history.Add("Io", "two", ChatMessageKind.Party);

        Assert.Single(snapshot);
        Assert.Equal("one", snapshot[0].Text);
    }

    [Fact]
    public void Snapshot_ReusesCachedArrayUntilHistoryChanges()
    {
        var history = new ChatHistory(3);
        history.Add("Io", "one", ChatMessageKind.Party);

        var first = history.Snapshot();
        var second = history.Snapshot();
        history.Add("Io", "two", ChatMessageKind.Party);
        var changed = history.Snapshot();

        Assert.Same(first, second);
        Assert.NotSame(first, changed);
        Assert.Equal(2, changed.Count);
    }
}
