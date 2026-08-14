using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Tests;

public sealed class PendingFilteredChatQueueTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryTake_UnchangedText_ReturnsMatchingEntryWithMetadata()
    {
        var queue = new PendingFilteredChatQueue();
        var token = queue.Enqueue(
            "hello",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.Thanks,
            Start);

        Assert.NotEqual(0, token);
        Assert.True(queue.TryTake("hello", Start.AddSeconds(1), out var entry));
        Assert.Equal(token, entry.Token);
        Assert.Equal("hello", entry.OriginalText);
        Assert.Equal(new LocalChatIdentity("Player 1", 1), entry.LocalChatIdentity);
        Assert.Equal(ChatCommunicationCue.Thanks, entry.ChatCommunicationCue);
        Assert.Equal(Start.AddSeconds(10), entry.ExpiresAt);
        Assert.False(queue.TryTakeOldest(Start.AddSeconds(1), out _));
    }

    [Fact]
    public void TryTake_MaskedFinalText_UsesEarliestEntryAsFallback()
    {
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(5));
        queue.Enqueue(
            "bad",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);

        Assert.True(queue.TryTake("***", Start.AddSeconds(1), out var entry));
        Assert.Equal("bad", entry.OriginalText);
        Assert.False(queue.TryTake("***", Start.AddSeconds(1), out _));
    }

    [Fact]
    public void TryTake_ConsecutiveMessages_RemainInEnqueueOrder()
    {
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(5), 8);
        queue.Enqueue(
            "first",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);
        queue.Enqueue(
            "second",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start.AddMilliseconds(100));
        queue.Enqueue(
            "third",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start.AddMilliseconds(200));

        Assert.True(queue.TryTake("first", Start.AddSeconds(1), out var first));
        Assert.True(queue.TryTake("second", Start.AddSeconds(1), out var second));
        Assert.True(queue.TryTake("third", Start.AddSeconds(1), out var third));
        Assert.Equal("first", first.OriginalText);
        Assert.Equal("second", second.OriginalText);
        Assert.Equal("third", third.OriginalText);
    }

    [Fact]
    public void TryTake_SameText_ReturnsEarliestPendingDuplicate()
    {
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(5), 8);
        var firstToken = queue.Enqueue(
            "same",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);
        var secondToken = queue.Enqueue(
            "same",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start.AddMilliseconds(100));
        var thirdToken = queue.Enqueue(
            "same",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start.AddMilliseconds(200));

        Assert.True(queue.TryTake("same", Start.AddSeconds(1), out var first));
        Assert.Equal(firstToken, first.Token);
        Assert.True(queue.TryTake("same", Start.AddSeconds(1), out var second));
        Assert.Equal(secondToken, second.Token);
        Assert.True(queue.TryTake("same", Start.AddSeconds(1), out var third));
        Assert.Equal(thirdToken, third.Token);
        Assert.False(queue.TryTake("same", Start.AddSeconds(1), out _));
    }

    [Fact]
    public void TryTake_PrefersLaterExactMatchOverEarlierMaskedCandidate()
    {
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(5), 8);
        var earlierToken = queue.Enqueue(
            "bad",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);
        var exactToken = queue.Enqueue(
            "***",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start.AddMilliseconds(100));

        Assert.True(queue.TryTake("***", Start.AddSeconds(1), out var exact));
        Assert.Equal("***", exact.OriginalText);
        Assert.Equal(exactToken, exact.Token);

        Assert.True(queue.TryTake("unmatched", Start.AddSeconds(1), out var fallback));
        Assert.Equal("bad", fallback.OriginalText);
        Assert.Equal(earlierToken, fallback.Token);
    }

    [Fact]
    public void Cancel_RemovesOnlyTheSpecifiedToken()
    {
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(5), 8);
        var first = queue.Enqueue(
            "first",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);
        var second = queue.Enqueue(
            "second",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start.AddMilliseconds(100));

        Assert.True(queue.Cancel(first));
        Assert.False(queue.Cancel(first));
        Assert.False(queue.Cancel(0));

        Assert.True(queue.TryTakeOldest(Start.AddSeconds(1), out var oldest));
        Assert.Equal("second", oldest.OriginalText);
        Assert.False(queue.Cancel(second));
    }

    [Fact]
    public void TryTake_IgnoresExpiredEntries()
    {
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(5));
        queue.Enqueue(
            "hello",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);

        Assert.False(queue.TryTake("hello", Start.AddSeconds(5), out _));
        Assert.False(queue.TryTakeOldest(Start.AddSeconds(5), out _));
    }

    [Fact]
    public void Enqueue_EvictsOldestEntryWhenCapacityIsExceeded()
    {
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(10), 2);
        var first = queue.Enqueue(
            "first",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);
        queue.Enqueue(
            "second",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);
        queue.Enqueue(
            "third",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);

        Assert.False(queue.Cancel(first));
        Assert.True(queue.TryTakeOldest(Start, out var oldest));
        Assert.Equal("second", oldest.OriginalText);
        Assert.True(queue.TryTake("third", Start, out var third));
        Assert.Equal("third", third.OriginalText);
    }

    [Fact]
    public void Clear_RemovesAllPendingEntries()
    {
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(5), 8);
        var first = queue.Enqueue(
            "first",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);
        var second = queue.Enqueue(
            "second",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start.AddMilliseconds(100));

        queue.Clear();

        Assert.False(queue.Cancel(first));
        Assert.False(queue.Cancel(second));
        Assert.False(queue.TryTake("first", Start.AddSeconds(1), out _));
        Assert.False(queue.TryTakeOldest(Start.AddSeconds(1), out _));
    }

    [Fact]
    public void Enqueue_RejectsBlankOriginalTextWithoutThrowing()
    {
        var queue = new PendingFilteredChatQueue();
        var identity = new LocalChatIdentity("Player 1", 1);

        Assert.Equal(0, queue.Enqueue("", identity, ChatCommunicationCue.None, Start));
        Assert.Equal(0, queue.Enqueue("   ", identity, ChatCommunicationCue.None, Start));
        Assert.Equal(0, queue.Enqueue(null!, identity, ChatCommunicationCue.None, Start));
        Assert.False(queue.TryTakeOldest(Start, out _));
    }

    [Fact]
    public void TryTake_RejectsBlankFinalTextWithoutThrowingOrConsuming()
    {
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(5));
        queue.Enqueue(
            "hello",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);

        Assert.False(queue.TryTake("", Start, out _));
        Assert.False(queue.TryTake("   ", Start, out _));
        Assert.False(queue.TryTake(null!, Start, out _));

        Assert.True(queue.TryTake("hello", Start, out var entry));
        Assert.Equal("hello", entry.OriginalText);
    }

    [Fact]
    public void Constructor_UsesTenSecondLifetimeAndThirtyTwoCapacityByDefault()
    {
        var queue = new PendingFilteredChatQueue();
        queue.Enqueue(
            "hello",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);

        Assert.True(queue.TryTake("hello", Start.AddSeconds(9), out var entry));
        Assert.Equal(Start.AddSeconds(10), entry.ExpiresAt);

        var capacityQueue = new PendingFilteredChatQueue();
        for (var index = 0; index < 33; index++)
        {
            capacityQueue.Enqueue(
                $"message-{index}",
                new LocalChatIdentity("Player 1", 1),
                ChatCommunicationCue.None,
                Start);
        }

        Assert.True(capacityQueue.TryTakeOldest(Start, out var oldest));
        Assert.Equal("message-1", oldest.OriginalText);
    }

    [Fact]
    public void Constructor_ValidatesLifetimeAndCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PendingFilteredChatQueue(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PendingFilteredChatQueue(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PendingFilteredChatQueue(TimeSpan.FromSeconds(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PendingFilteredChatQueue(TimeSpan.FromSeconds(1), -1));
    }

    [Fact]
    public void TryTakeOldest_ReturnsOldestUnexpiredEntryAndRejectsWhenEmpty()
    {
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(10));
        queue.Enqueue(
            "first",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);
        queue.Enqueue(
            "second",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start.AddSeconds(1));

        Assert.True(queue.TryTakeOldest(Start.AddSeconds(2), out var first));
        Assert.Equal("first", first.OriginalText);
        Assert.True(queue.TryTakeOldest(Start.AddSeconds(3), out var second));
        Assert.Equal("second", second.OriginalText);
        Assert.False(queue.TryTakeOldest(Start.AddSeconds(4), out _));
    }

    [Fact]
    public void TryTakeOldest_RemovesExpiredBeforeChoosingAndRejectsWhenAllExpired()
    {
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(5));
        queue.Enqueue(
            "expired",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start);
        queue.Enqueue(
            "alive",
            new LocalChatIdentity("Player 1", 1),
            ChatCommunicationCue.None,
            Start.AddSeconds(4));

        Assert.True(queue.TryTakeOldest(Start.AddSeconds(5), out var alive));
        Assert.Equal("alive", alive.OriginalText);
        Assert.False(queue.TryTakeOldest(Start.AddSeconds(10), out _));
    }

    [Fact]
    public void ConcurrentEnqueueAndTryTake_IsThreadSafe()
    {
        const int count = 128;
        var queue = new PendingFilteredChatQueue(TimeSpan.FromSeconds(5), count);
        var tokens = new long[count];
        var identity = new LocalChatIdentity("Player 1", 1);

        Parallel.For(0, count, index =>
        {
            tokens[index] = queue.Enqueue(
                $"message-{index}",
                identity,
                ChatCommunicationCue.None,
                Start);
        });

        var taken = 0;
        Parallel.For(0, count, index =>
        {
            if (queue.TryTake($"message-{index}", Start.AddSeconds(1), out _))
                Interlocked.Increment(ref taken);
        });

        Assert.Equal(count, taken);
    }
}
