namespace GBFR.ChatOverlay.Core;

/// <summary>
/// Thread-safe, bounded in-memory history. Game hooks may append messages from a
/// different thread than the render callback, so callers only receive snapshots.
/// </summary>
public sealed class ChatHistory
{
    private readonly object _sync = new();
    private readonly Queue<ChatMessage> _messages;
    private IReadOnlyList<ChatMessage>? _cachedSnapshot;
    private long _nextSequence = 1;

    public ChatHistory(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "History capacity must be positive.");

        _capacity = capacity;
        _messages = new Queue<ChatMessage>(capacity);
    }

    public int Capacity
    {
        get
        {
            lock (_sync)
                return _capacity;
        }
    }

    private int _capacity;

    public ChatMessage Add(
        string sender,
        string text,
        ChatMessageKind kind,
        DateTimeOffset? timestamp = null,
        uint senderId = 0,
        int playerNumber = 0,
        ChatCommunicationCue communicationCue = ChatCommunicationCue.None)
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
                kind,
                senderId,
                playerNumber,
                communicationCue);

            _messages.Enqueue(message);
            while (_messages.Count > _capacity)
                _messages.Dequeue();
            _cachedSnapshot = null;

            return message;
        }
    }

    public void Resize(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "History capacity must be positive.");

        lock (_sync)
        {
            if (_capacity == capacity)
                return;
            _capacity = capacity;
            while (_messages.Count > _capacity)
                _messages.Dequeue();
            _cachedSnapshot = null;
        }
    }

    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_sync)
            return _cachedSnapshot ??= Array.AsReadOnly(_messages.ToArray());
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (_messages.Count == 0)
                return;
            _messages.Clear();
            _cachedSnapshot = null;
        }
    }
}
