using System.Globalization;
using System.Text;
using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Core;

internal sealed class ChatModerationService : IChatModerationService
{
    private readonly object _sync = new();
    private readonly IOfficialTextFilter? _officialFilter;
    private readonly TimeProvider _timeProvider;

    private ChatFilterConfiguration _configuration;
    private HashSet<string> _persistentBlocked = new(StringComparer.Ordinal);
    private HashSet<string> _roomBlockedKeys = new(StringComparer.Ordinal);
    private HashSet<string> _autoBlockedKeys = new(StringComparer.Ordinal);
    private Dictionary<string, PlayerState> _players = new(StringComparer.Ordinal);
    private Dictionary<string, int> _ruleHitCounts = new(StringComparer.Ordinal);
    private OfficialTextFilterStatus _officialFilterStatus;
    private readonly Queue<ChatModerationEvent> _pendingEvents = new();
    private int _sessionFilteredMessageCount;

    internal ChatModerationService(
        IOfficialTextFilter? officialFilter = null,
        TimeProvider? timeProvider = null)
    {
        _officialFilter = officialFilter;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _configuration = CreateDefaultConfiguration();
        _officialFilterStatus = officialFilter?.Status
            ?? OfficialTextFilterStatus.Unavailable("No official text filter configured.");
    }

    public void ApplyConfiguration(ChatFilterConfiguration? configuration)
    {
        lock (_sync)
        {
            var previousPersistentBlocked = _persistentBlocked;
            _configuration = CloneConfiguration(configuration);
            _persistentBlocked = new HashSet<string>(
                _configuration.BlockedPlayers.Select(static player => player.Identity),
                StringComparer.Ordinal);

            foreach (var entityId in previousPersistentBlocked)
            {
                if (_persistentBlocked.Contains(entityId))
                    continue;

                var removedKey = "entity:" + entityId;
                _roomBlockedKeys.Remove(removedKey);
                _autoBlockedKeys.Remove(removedKey);
            }
        }
    }

    public ChatModerationDecision Evaluate(in ChatModerationInput input)
    {
        lock (_sync)
        {
            ObserveParticipantCore(input.Participant);

            if (input.Participant.IsLocal)
                return ChatModerationDecision.Allow(input.Text);

            if (IsBlockedCore(input.Participant))
                return CreateBlockedDecision(input.Participant, input.Text);

            if (input.CommunicationCue != ChatCommunicationCue.None || !_configuration.Enabled)
                return ChatModerationDecision.Allow(input.Text);

            var (customMatched, customMaskedText, matchedRuleIds) = ApplyCustomRules(input.Text);
            var officialText = customMaskedText;
            var officialMatched = false;

            if (_configuration.UseSteamTextFilter && _officialFilter is not null)
            {
                try
                {
                    var officialResult = _officialFilter.Filter(customMaskedText);
                    if (officialResult.Succeeded)
                    {
                        officialText = officialResult.Text;
                        officialMatched = officialResult.Matched;
                    }
                }
                catch (Exception)
                {
                    officialMatched = false;
                }
            }

            if (!customMatched && !officialMatched)
                return new ChatModerationDecision(
                    ChatModerationDisposition.Allow,
                    officialText,
                    false,
                    false,
                    false,
                    []);

            _sessionFilteredMessageCount++;
            foreach (var ruleId in matchedRuleIds)
            {
                _ruleHitCounts.TryGetValue(ruleId, out var hitCount);
                _ruleHitCounts[ruleId] = hitCount + 1;
            }

            var autoBlocked = ApplyAutoBlock(
                input.Participant,
                GetMessageTime(input.ReceivedAt),
                out _);
            var disposition = _configuration.Action == ChatFilterAction.HideEntireMessage
                ? ChatModerationDisposition.Block
                : ChatModerationDisposition.Mask;

            return new ChatModerationDecision(
                disposition,
                officialText,
                true,
                officialMatched,
                autoBlocked,
                matchedRuleIds.ToArray());
        }
    }

    public void ObserveParticipant(in ChatModerationParticipant participant)
    {
        lock (_sync)
        {
            ObserveParticipantCore(participant);
        }
    }

    public void ForgetParticipant(in ChatModerationParticipant participant)
    {
        lock (_sync)
        {
            if (!TryGetStableIdentityKey(participant, out var key))
                return;

            _players.Remove(key);
        }
    }

    public bool SetBlocked(in ChatModerationParticipant participant, bool blocked, bool persistent)
    {
        lock (_sync)
        {
            if (participant.IsLocal)
                return false;

            if (persistent)
            {
                if (string.IsNullOrWhiteSpace(participant.EntityId))
                    return false;

                if (blocked)
                {
                    if (_persistentBlocked.Add(participant.EntityId))
                    {
                        _configuration.BlockedPlayers.Add(new BlockedPlayerConfiguration
                        {
                            IdentityKind = BlockedPlayerIdentityKind.PlayFabEntityId,
                            Identity = participant.EntityId,
                            LastKnownName = participant.DisplayName ?? string.Empty,
                            Source = BlockedPlayerSource.Manual,
                        });
                    }

                    AddRoomBlock(participant);
                    ObserveParticipantCore(participant);
                    return true;
                }

                var entityId = participant.EntityId;
                _persistentBlocked.Remove(entityId);
                _configuration.BlockedPlayers.RemoveAll(player =>
                    player.IdentityKind == BlockedPlayerIdentityKind.PlayFabEntityId &&
                    string.Equals(player.Identity, entityId, StringComparison.Ordinal));
                RemoveRoomBlock(participant);
                return true;
            }

            if (!TryGetStableIdentityKey(participant, out var roomKey))
                return false;

            if (blocked)
            {
                _roomBlockedKeys.Add(roomKey);
                ObserveParticipantCore(participant);
            }
            else
            {
                _roomBlockedKeys.Remove(roomKey);
                _autoBlockedKeys.Remove(roomKey);
            }

            return true;
        }
    }

    public bool IsBlocked(in ChatModerationParticipant participant)
    {
        lock (_sync)
        {
            return IsBlockedCore(participant);
        }
    }

    public bool TryReadEvent(out ChatModerationEvent moderationEvent)
    {
        lock (_sync)
        {
            if (_pendingEvents.Count == 0)
            {
                moderationEvent = default;
                return false;
            }

            moderationEvent = _pendingEvents.Dequeue();
            return true;
        }
    }

    public ChatModerationSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var rules = new ChatModerationRuleStatus[_configuration.Rules.Count];
            for (var i = 0; i < rules.Length; i++)
            {
                var rule = _configuration.Rules[i];
                _ruleHitCounts.TryGetValue(rule.Id, out var hitCount);
                rules[i] = new ChatModerationRuleStatus(rule.Id, hitCount);
            }

            var players = _players.Values
                .Select(state => new ChatModerationPlayerStatus(
                    state.Participant,
                    state.WindowHits.Count,
                    state.LastHitAt,
                    IsRoomBlockedCore(state.Participant),
                    IsPersistentlyBlockedCore(state.Participant)))
                .OrderBy(static status => status.Participant.PlayerNumber)
                .ThenBy(static status => status.Participant.DisplayName, StringComparer.Ordinal)
                .ToArray();

            return new ChatModerationSnapshot(
                _officialFilterStatus,
                _sessionFilteredMessageCount,
                rules,
                players);
        }
    }

    public OfficialTextFilterStatus RefreshOfficialFilter()
    {
        lock (_sync)
        {
            if (_officialFilter is null)
            {
                _officialFilterStatus =
                    OfficialTextFilterStatus.Unavailable("No official text filter configured.");
                return _officialFilterStatus;
            }

            try
            {
                _officialFilterStatus = _officialFilter.Refresh();
            }
            catch (Exception exception)
            {
                _officialFilterStatus = OfficialTextFilterStatus.Unavailable(
                    $"Official text filter refresh failed: {exception.Message}");
            }

            return _officialFilterStatus;
        }
    }

    public ChatModerationPreview Preview(string text)
    {
        lock (_sync)
        {
            if (!_configuration.Enabled)
            {
                return new ChatModerationPreview(
                    ChatModerationDisposition.Allow,
                    text,
                    false,
                    false,
                    []);
            }

            var (customMatched, customMaskedText, matchedRuleIds) = ApplyCustomRules(text);
            var officialText = customMaskedText;
            var officialMatched = false;

            if (_configuration.UseSteamTextFilter && _officialFilter is not null)
            {
                try
                {
                    var officialResult = _officialFilter.Filter(customMaskedText);
                    if (officialResult.Succeeded)
                    {
                        officialText = officialResult.Text;
                        officialMatched = officialResult.Matched;
                    }
                }
                catch (Exception)
                {
                    officialMatched = false;
                }
            }

            if (!customMatched && !officialMatched)
            {
                return new ChatModerationPreview(
                    ChatModerationDisposition.Allow,
                    officialText,
                    false,
                    false,
                    []);
            }

            var disposition = _configuration.Action == ChatFilterAction.HideEntireMessage
                ? ChatModerationDisposition.Block
                : ChatModerationDisposition.Mask;
            return new ChatModerationPreview(
                disposition,
                officialText,
                true,
                officialMatched,
                matchedRuleIds.ToArray());
        }
    }

    public void ClearRoom()
    {
        lock (_sync)
        {
            _players.Clear();
            _roomBlockedKeys.Clear();
            _autoBlockedKeys.Clear();
            _pendingEvents.Clear();
            _ruleHitCounts.Clear();
            _sessionFilteredMessageCount = 0;
        }
    }

    private static ChatFilterConfiguration CreateDefaultConfiguration() => new()
    {
        UseSteamTextFilter = true,
        Action = ChatFilterAction.MaskMatchedWords,
        AutoBlockThreshold = 3,
        AutoBlockWindowMinutes = 10,
        Rules = [],
        BlockedPlayers = [],
    };

    private static ChatFilterConfiguration CloneConfiguration(ChatFilterConfiguration? configuration)
    {
        if (configuration is null)
            return CreateDefaultConfiguration();

        return new ChatFilterConfiguration
        {
            Enabled = configuration.Enabled,
            UseSteamTextFilter = configuration.UseSteamTextFilter,
            Action = configuration.Action,
            AutoBlockEnabled = configuration.AutoBlockEnabled,
            AutoBlockThreshold = Math.Clamp(configuration.AutoBlockThreshold, 1, 100),
            AutoBlockWindowMinutes = Math.Clamp(configuration.AutoBlockWindowMinutes, 1, 1440),
            NotificationMode = configuration.NotificationMode,
            NotificationTemplate = configuration.NotificationTemplate ?? string.Empty,
            Rules = (configuration.Rules ?? []).Select(static rule => new ChatFilterRuleConfiguration
            {
                Id = rule.Id ?? string.Empty,
                Enabled = rule.Enabled,
                Term = rule.Term ?? string.Empty,
            }).ToList(),
            BlockedPlayers = (configuration.BlockedPlayers ?? [])
                .Where(static player =>
                    player.IdentityKind == BlockedPlayerIdentityKind.PlayFabEntityId &&
                    !string.IsNullOrWhiteSpace(player.Identity))
                .Select(static player => new BlockedPlayerConfiguration
                {
                    IdentityKind = BlockedPlayerIdentityKind.PlayFabEntityId,
                    Identity = player.Identity,
                    LastKnownName = player.LastKnownName ?? string.Empty,
                    Source = player.Source,
                    Reason = player.Reason ?? string.Empty,
                    BlockedAtUtc = player.BlockedAtUtc,
                }).ToList(),
        };
    }

    private void ObserveParticipantCore(ChatModerationParticipant participant)
    {
        if (!TryGetStableIdentityKey(participant, out var key))
            return;

        if (_players.TryGetValue(key, out var existing))
        {
            existing.Participant = participant;
        }
        else
        {
            _players.Add(key, new PlayerState { Participant = participant });
        }
    }

    private bool IsBlockedCore(ChatModerationParticipant participant)
    {
        if (participant.IsLocal)
            return false;

        if (IsPersistentlyBlockedCore(participant))
            return true;

        return TryGetStableIdentityKey(participant, out var roomKey) &&
               _roomBlockedKeys.Contains(roomKey);
    }

    private bool IsPersistentlyBlockedCore(ChatModerationParticipant participant)
    {
        return !string.IsNullOrWhiteSpace(participant.EntityId) &&
               _persistentBlocked.Contains(participant.EntityId);
    }

    private bool IsRoomBlockedCore(ChatModerationParticipant participant)
    {
        return !participant.IsLocal &&
               TryGetStableIdentityKey(participant, out var roomKey) &&
               _roomBlockedKeys.Contains(roomKey);
    }

    private ChatModerationDecision CreateBlockedDecision(
        ChatModerationParticipant participant,
        string text)
    {
        var autoBlocked = TryGetStableIdentityKey(participant, out var roomKey) &&
                          _autoBlockedKeys.Contains(roomKey);
        return new ChatModerationDecision(
            ChatModerationDisposition.Block,
            text,
            false,
            false,
            autoBlocked,
            []);
    }

    private bool ApplyAutoBlock(
        ChatModerationParticipant participant,
        DateTimeOffset receivedAt,
        out bool eventQueued)
    {
        eventQueued = false;

        if (!_configuration.AutoBlockEnabled)
            return false;

        if (!TryGetStableIdentityKey(participant, out var key))
            return false;

        if (!_players.TryGetValue(key, out var state))
        {
            state = new PlayerState { Participant = participant };
            _players.Add(key, state);
        }

        var window = TimeSpan.FromMinutes(_configuration.AutoBlockWindowMinutes);
        while (state.WindowHits.Count > 0 && receivedAt - state.WindowHits.Peek() > window)
            state.WindowHits.Dequeue();

        state.WindowHits.Enqueue(receivedAt);
        state.LastHitAt = receivedAt;
        var hitCount = state.WindowHits.Count;

        if (hitCount < _configuration.AutoBlockThreshold)
            return false;

        if (_roomBlockedKeys.Contains(key))
            return _autoBlockedKeys.Contains(key);

        _roomBlockedKeys.Add(key);
        _autoBlockedKeys.Add(key);
        _pendingEvents.Enqueue(new ChatModerationEvent(
            participant,
            hitCount,
            _configuration.AutoBlockThreshold,
            receivedAt,
            participant.HasPersistentIdentity));
        eventQueued = true;

        return true;
    }

    private void AddRoomBlock(ChatModerationParticipant participant)
    {
        if (TryGetStableIdentityKey(participant, out var key))
            _roomBlockedKeys.Add(key);
    }

    private void RemoveRoomBlock(ChatModerationParticipant participant)
    {
        if (TryGetStableIdentityKey(participant, out var key))
        {
            _roomBlockedKeys.Remove(key);
            _autoBlockedKeys.Remove(key);
        }
    }

    private DateTimeOffset GetMessageTime(DateTimeOffset receivedAt)
        => receivedAt == default ? _timeProvider.GetUtcNow() : receivedAt;

    private (bool Matched, string MaskedText, List<string> RuleIds) ApplyCustomRules(string? text)
    {
        var ruleIds = new List<string>();
        if (string.IsNullOrEmpty(text))
            return (false, text ?? string.Empty, ruleIds);

        var normalized = NormalizeForMatching(
            text,
            out var originalScalars,
            out var normalizedToScalars).Text;
        var ranges = new List<(int Start, int End)>();

        foreach (var rule in _configuration.Rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Term))
                continue;

            var normalizedTerm = rule.Term.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
            if (normalizedTerm.Length == 0)
                continue;

            var searchStart = 0;
            var ruleMatched = false;
            while (searchStart <= normalized.Length - normalizedTerm.Length)
            {
                var index = normalized.IndexOf(normalizedTerm, searchStart, StringComparison.Ordinal);
                if (index < 0)
                    break;

                ranges.Add((index, index + normalizedTerm.Length));
                ruleMatched = true;
                searchStart = index + 1;
            }

            if (ruleMatched)
                ruleIds.Add(rule.Id);
        }

        if (ruleIds.Count == 0)
            return (false, text, ruleIds);

        var matchedScalars = new HashSet<int>();
        foreach (var range in MergeRanges(ranges))
        {
            for (var normalizedIndex = range.Start; normalizedIndex < range.End; normalizedIndex++)
            {
                foreach (var scalarIndex in normalizedToScalars[normalizedIndex])
                    matchedScalars.Add(scalarIndex);
            }
        }

        var masked = new StringBuilder(originalScalars.Length);
        for (var i = 0; i < originalScalars.Length; i++)
            masked.Append(matchedScalars.Contains(i) ? "*" : originalScalars[i]);

        return (true, masked.ToString(), ruleIds);
    }

    private static List<(int Start, int End)> MergeRanges(List<(int Start, int End)> ranges)
    {
        ranges.Sort();
        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End)
            {
                merged.Add(range);
            }
            else if (range.End > merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, range.End);
            }
        }

        return merged;
    }

    private static (string Text, string[] Scalars, int[][] NormalizedToScalars) NormalizeForMatching(
        string text,
        out string[] originalScalars,
        out int[][] normalizedToScalars)
    {
        originalScalars = text.EnumerateRunes().Select(static rune => rune.ToString()).ToArray();
        var normalized = new StringBuilder(text.Length);
        var mapping = new List<int[]>();
        var scalarOffset = 0;
        var index = 0;

        while (index < text.Length)
        {
            var element = StringInfo.GetNextTextElement(text, index);
            var elementScalarCount = CountRunes(element);
            var scalarIndices = new int[elementScalarCount];
            for (var i = 0; i < elementScalarCount; i++)
                scalarIndices[i] = scalarOffset + i;

            var wholeNormalized = element.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
            var perScalarNormalized = new StringBuilder(element.Length);
            var perScalarMapping = new List<int[]>();
            for (var i = 0; i < elementScalarCount; i++)
            {
                var scalarIndex = scalarOffset + i;
                var scalarNormalized = originalScalars[scalarIndex]
                    .Normalize(NormalizationForm.FormKC)
                    .ToUpperInvariant();
                perScalarNormalized.Append(scalarNormalized);
                foreach (var _ in scalarNormalized)
                    perScalarMapping.Add([scalarIndex]);
            }

            if (string.Equals(
                    wholeNormalized,
                    perScalarNormalized.ToString(),
                    StringComparison.Ordinal))
            {
                normalized.Append(perScalarNormalized);
                mapping.AddRange(perScalarMapping);
            }
            else
            {
                normalized.Append(wholeNormalized);
                foreach (var _ in wholeNormalized)
                    mapping.Add(scalarIndices);
            }

            scalarOffset += elementScalarCount;
            index += element.Length;
        }

        normalizedToScalars = mapping.ToArray();
        return (normalized.ToString(), originalScalars, normalizedToScalars);
    }

    private static int CountRunes(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
            count++;
        return count;
    }

    private static bool TryGetStableIdentityKey(
        ChatModerationParticipant participant,
        out string key)
    {
        if (!string.IsNullOrWhiteSpace(participant.EntityId))
        {
            key = "entity:" + participant.EntityId;
            return true;
        }

        if (participant.SenderId != 0)
        {
            key = "sender:" + participant.SenderId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (participant.PlayerNumber > 0)
        {
            key = "player:" + participant.PlayerNumber.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        key = string.Empty;
        return false;
    }

    private sealed class PlayerState
    {
        public ChatModerationParticipant Participant;
        public Queue<DateTimeOffset> WindowHits { get; } = new();
        public DateTimeOffset? LastHitAt;
    }
}
