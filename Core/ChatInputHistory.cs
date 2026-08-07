namespace GBFR.ChatOverlay.Core;

/// <summary>
/// UI-independent history for manually submitted chat text. Navigation preserves
/// the current unsent draft and restores it after moving past the newest entry.
/// </summary>
public sealed class ChatInputHistory
{
    private readonly List<string> _entries = [];
    private readonly int _capacity;
    private int _index = -1;
    private string _pendingDraft = string.Empty;
    private string? _lastNavigationResult;

    public ChatInputHistory(int capacity = 50)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public IReadOnlyList<string> Entries => _entries;

    public void Record(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (_entries.Count == 0 || !string.Equals(_entries[^1], text, StringComparison.Ordinal))
            _entries.Add(text);
        while (_entries.Count > _capacity)
            _entries.RemoveAt(0);
        ResetNavigation();
    }

    public string? MovePrevious(string currentDraft)
    {
        if (_entries.Count == 0)
            return null;

        RestartIfDraftWasEdited(currentDraft);
        if (_index < 0)
        {
            _pendingDraft = currentDraft;
            _index = _entries.Count - 1;
        }
        else if (_index > 0)
        {
            _index--;
        }

        return _lastNavigationResult = _entries[_index];
    }

    public string? MoveNext(string currentDraft)
    {
        if (_index < 0)
            return null;

        RestartIfDraftWasEdited(currentDraft);
        if (_index < 0)
            return null;

        if (_index < _entries.Count - 1)
            return _lastNavigationResult = _entries[++_index];

        var restored = _pendingDraft;
        ResetNavigation();
        return restored;
    }

    public void ResetNavigation()
    {
        _index = -1;
        _pendingDraft = string.Empty;
        _lastNavigationResult = null;
    }

    private void RestartIfDraftWasEdited(string currentDraft)
    {
        if (_index >= 0 && !string.Equals(currentDraft, _lastNavigationResult, StringComparison.Ordinal))
            ResetNavigation();
    }
}
