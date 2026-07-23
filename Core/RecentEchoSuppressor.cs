namespace GBFR.ChatOverlay.Core;

public sealed class RecentEchoSuppressor
{
    private readonly object _sync = new();
    private readonly LinkedList<Entry> _entries = new();
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;
    private long _nextId;

    public RecentEchoSuppressor(TimeSpan? lifetime = null, int capacity = 16)
    {
        _lifetime = lifetime ?? TimeSpan.FromSeconds(3);
        if (_lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
    }

    public long Register(string text, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        lock (_sync)
        {
            RemoveExpired(now);
            var id = ++_nextId;
            _entries.AddLast(new Entry(id, text, now + _lifetime));
            while (_entries.Count > _capacity)
                _entries.RemoveFirst();
            return id;
        }
    }

    public bool Cancel(long id)
    {
        lock (_sync)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Id != id)
                    continue;

                _entries.Remove(node);
                return true;
            }

            return false;
        }
    }

    public bool TryConsume(string text, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        lock (_sync)
        {
            RemoveExpired(now);
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (!string.Equals(node.Value.Text, text, StringComparison.Ordinal))
                    continue;

                _entries.Remove(node);
                return true;
            }

            return false;
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

    private readonly record struct Entry(long Id, string Text, DateTimeOffset ExpiresAt);
}
