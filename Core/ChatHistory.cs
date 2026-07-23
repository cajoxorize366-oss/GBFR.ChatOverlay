namespace GBFR.ChatOverlay.Core;

/// <summary>
/// Thread-safe, bounded in-memory history. Game hooks may append messages from a
/// different thread than the render callback, so callers only receive snapshots.
/// </summary>
public sealed class ChatHistory
{
    private readonly object _sync = new();
    private readonly Queue<ChatMessage> _messages;
    private long _nextSequence = 1;

    public ChatHistory(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "History capacity must be positive.");

        Capacity = capacity;
        _messages = new Queue<ChatMessage>(capacity);
    }

    public int Capacity { get; }

    public ChatMessage Add(
        string sender,
        string text,
        ChatMessageKind kind,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sender);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        lock (_sync)
        {
            var message = new ChatMessage(
                _nextSequence++,
                timestamp ?? DateTimeOffset.UtcNow,
                sender,
                text,
                kind);

            _messages.Enqueue(message);
            while (_messages.Count > Capacity)
                _messages.Dequeue();

            return message;
        }
    }

    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_sync)
            return _messages.ToArray();
    }

    public void Clear()
    {
        lock (_sync)
            _messages.Clear();
    }
}
