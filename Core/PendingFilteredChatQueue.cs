namespace GBFR.ChatOverlay.Core;

internal readonly record struct PendingFilteredChatQueueEntry(
    long Token,
    string OriginalText,
    LocalChatIdentity LocalChatIdentity,
    ChatCommunicationCue ChatCommunicationCue,
    DateTimeOffset ExpiresAt);

internal sealed class PendingFilteredChatQueue
{
    private readonly object _sync = new();
    private readonly LinkedList<PendingFilteredChatQueueEntry> _entries = new();
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;
    private long _nextId;

    public PendingFilteredChatQueue(TimeSpan? lifetime = null, int capacity = 32)
    {
        _lifetime = lifetime ?? TimeSpan.FromSeconds(10);
        if (_lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
    }

    public long Enqueue(
        string originalText,
        LocalChatIdentity localChatIdentity,
        ChatCommunicationCue chatCommunicationCue,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(originalText))
            return 0;

        lock (_sync)
        {
            RemoveExpired(now);
            var token = ++_nextId;
            _entries.AddLast(new PendingFilteredChatQueueEntry(
                token,
                originalText,
                localChatIdentity,
                chatCommunicationCue,
                now + _lifetime));

            while (_entries.Count > _capacity)
                _entries.RemoveFirst();

            return token;
        }
    }

    public bool Cancel(long token)
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

    public bool TryTake(
        string finalSanitizedText,
        DateTimeOffset now,
        out PendingFilteredChatQueueEntry entry)
    {
        if (string.IsNullOrWhiteSpace(finalSanitizedText))
        {
            entry = default;
            return false;
        }

        lock (_sync)
        {
            RemoveExpired(now);

            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (!string.Equals(node.Value.OriginalText, finalSanitizedText, StringComparison.Ordinal))
                    continue;

                entry = node.Value;
                _entries.Remove(node);
                return true;
            }

            var first = _entries.First;
            if (first is null)
            {
                entry = default;
                return false;
            }

            entry = first.Value;
            _entries.RemoveFirst();
            return true;
        }
    }

    public bool TryTakeOldest(DateTimeOffset now, out PendingFilteredChatQueueEntry entry)
    {
        lock (_sync)
        {
            RemoveExpired(now);

            var first = _entries.First;
            if (first is null)
            {
                entry = default;
                return false;
            }

            entry = first.Value;
            _entries.RemoveFirst();
            return true;
        }
    }

    public void Clear()
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
}
