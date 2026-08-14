using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class PendingFilteredReceiveQueueTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryTake_RequiresTheSameSenderCategoryAndMetadata()
    {
        var queue = new PendingFilteredReceiveQueue();
        queue.Enqueue(0x1234, 7, 9, Start);

        Assert.False(queue.TryTake(0x9999, 7, 9, Start));
        Assert.False(queue.TryTake(0x1234, 8, 9, Start));
        Assert.False(queue.TryTake(0x1234, 7, 10, Start));
        Assert.True(queue.TryTake(0x1234, 7, 9, Start));
        Assert.False(queue.TryTake(0x1234, 7, 9, Start));
    }

    [Fact]
    public void TryTake_DuplicateKeysConsumeInQueueOrder()
    {
        var queue = new PendingFilteredReceiveQueue();
        var first = queue.Enqueue(1, 2, 3, Start);
        var second = queue.Enqueue(1, 2, 3, Start.AddMilliseconds(1));

        Assert.True(queue.TryTake(1, 2, 3, Start.AddSeconds(1)));
        Assert.False(queue.Cancel(first));
        Assert.True(queue.Cancel(second));
    }

    [Fact]
    public void CancelAndClear_RemovePendingCallbacks()
    {
        var queue = new PendingFilteredReceiveQueue();
        var cancelled = queue.Enqueue(1, 2, 3, Start);
        queue.Enqueue(4, 5, 6, Start);

        Assert.True(queue.Cancel(cancelled));
        queue.Clear();

        Assert.False(queue.TryTake(1, 2, 3, Start));
        Assert.False(queue.TryTake(4, 5, 6, Start));
    }

    [Fact]
    public void ExpiredAndEvictedCallbacksCannotPublish()
    {
        var queue = new PendingFilteredReceiveQueue(TimeSpan.FromSeconds(5), capacity: 2);
        queue.Enqueue(1, 1, 1, Start);
        queue.Enqueue(2, 2, 2, Start);
        queue.Enqueue(3, 3, 3, Start);

        Assert.False(queue.TryTake(1, 1, 1, Start));
        Assert.False(queue.TryTake(2, 2, 2, Start.AddSeconds(5)));
        Assert.False(queue.TryTake(3, 3, 3, Start.AddSeconds(5)));
    }

    [Fact]
    public void ConstructorRejectsInvalidBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PendingFilteredReceiveQueue(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PendingFilteredReceiveQueue(TimeSpan.FromSeconds(1), 0));
    }
}
