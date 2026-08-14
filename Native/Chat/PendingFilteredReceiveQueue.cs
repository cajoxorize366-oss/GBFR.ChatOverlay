namespace GBFR.ChatOverlay.Native;

internal sealed class PendingFilteredReceiveQueue
{
    private readonly object _sync = new();
    private readonly LinkedList<Entry> _entries = new();
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;
    private long _nextToken;

    internal PendingFilteredReceiveQueue(TimeSpan? lifetime = null, int capacity = 64)
    {
        _lifetime = lifetime ?? TimeSpan.FromSeconds(10);
        if (_lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
    }

    internal long Enqueue(
        uint senderKey,
        uint category,
        uint metadata,
        DateTimeOffset now)
    {
        lock (_sync)
        {
            RemoveExpired(now);
            var token = ++_nextToken;
            _entries.AddLast(new Entry(
                token,
                senderKey,
                category,
                metadata,
                now + _lifetime));
            while (_entries.Count > _capacity)
                _entries.RemoveFirst();
            return token;
        }
    }

    internal bool Cancel(long token)
    {
        if (token == 0)
            return false;

        lock (_sync)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Token != token)
                    continue;

                _entries.Remove(node);
                return true;
            }

            return false;
        }
    }

    internal bool TryTake(
        uint senderKey,
        uint category,
        uint metadata,
        DateTimeOffset now)
    {
        lock (_sync)
        {
            RemoveExpired(now);
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                var entry = node.Value;
                if (entry.SenderKey != senderKey ||
                    entry.Category != category ||
                    entry.Metadata != metadata)
                {
                    continue;
                }

                _entries.Remove(node);
                return true;
            }

            return false;
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        for (var node = _entries.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.ExpiresAt <= now)
                _entries.Remove(node);
            node = next;
        }
    }

    private readonly record struct Entry(
        long Token,
        uint SenderKey,
        uint Category,
        uint Metadata,
        DateTimeOffset ExpiresAt);
}
