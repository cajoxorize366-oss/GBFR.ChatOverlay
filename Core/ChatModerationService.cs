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
                _configuration.BlockedPlayers.Select(static player => player.Identity.Trim()),
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
            var participant = ObserveParticipantCore(input.Participant);

            if (participant.IsLocal)
                return ChatModerationDecision.Allow(input.Text);

            if (IsBlockedCore(participant))
                return CreateBlockedDecision(participant, input.Text);

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
                participant,
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
            var leavingParticipant = participant;
            var keysToForget = _players
                .Where(pair => RepresentsLeavingParticipant(pair.Value.Participant, leavingParticipant))
                .Select(static pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (TryGetStableIdentityKey(participant, out var directKey))
                keysToForget.Add(directKey);

            foreach (var key in keysToForget)
            {
                _players.Remove(key);
                if (key.StartsWith("entity:", StringComparison.Ordinal))
                    continue;

                // Sender and player-number keys are room-local fallbacks. They must not
                // survive a departure because Relink can reuse the same slot for a new member.
                _roomBlockedKeys.Remove(key);
                _autoBlockedKeys.Remove(key);
            }
        }
    }

    public bool SetBlocked(in ChatModerationParticipant participant, bool blocked, bool persistent)
    {
        lock (_sync)
        {
            var resolvedParticipant = ObserveParticipantCore(participant);
            if (resolvedParticipant.IsLocal)
                return false;

            if (persistent)
            {
                if (string.IsNullOrWhiteSpace(resolvedParticipant.EntityId))
                    return false;

                if (blocked)
                {
                    var normalizedEntityId = resolvedParticipant.EntityId.Trim();
                    if (_persistentBlocked.Add(normalizedEntityId))
                    {
                        _configuration.BlockedPlayers.Add(new BlockedPlayerConfiguration
                        {
                            IdentityKind = BlockedPlayerIdentityKind.PlayFabEntityId,
                            Identity = normalizedEntityId,
                            LastKnownName = resolvedParticipant.DisplayName ?? string.Empty,
                            Source = BlockedPlayerSource.Manual,
                        });
                    }

                    AddRoomBlock(resolvedParticipant);
                    return true;
                }

                var entityId = resolvedParticipant.EntityId.Trim();
                _persistentBlocked.Remove(entityId);
                _configuration.BlockedPlayers.RemoveAll(player =>
                    player.IdentityKind == BlockedPlayerIdentityKind.PlayFabEntityId &&
                    string.Equals(player.Identity, entityId, StringComparison.Ordinal));
                RemoveRoomBlock(resolvedParticipant);
                return true;
            }

            if (!TryGetStableIdentityKey(resolvedParticipant, out var roomKey))
                return false;

            if (blocked)
            {
                _roomBlockedKeys.Add(roomKey);
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
            return IsBlockedCore(ResolveCanonicalParticipant(participant));
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
        UseSteamTextFilter = false,
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
                    Identity = player.Identity.Trim(),
                    LastKnownName = player.LastKnownName ?? string.Empty,
                    Source = player.Source,
                    Reason = player.Reason ?? string.Empty,
                    BlockedAtUtc = player.BlockedAtUtc,
                }).ToList(),
        };
    }

    private ChatModerationParticipant ObserveParticipantCore(ChatModerationParticipant participant)
    {
        participant = NormalizeParticipant(participant);
        if (!TryGetStableIdentityKey(participant, out var key))
            return participant;

        if (_players.TryGetValue(key, out var existing))
        {
            existing.Participant = MergeParticipant(existing.Participant, participant, preferIncomingIdentity: true);
            return existing.Participant;
        }

        if (participant.PlayerNumber is < 1 or > PartyMemberSlotMap.MemberCount)
        {
            _players.Add(key, new PlayerState { Participant = participant });
            return participant;
        }

        var sameSlotCandidates = _players
            .Where(pair => pair.Value.Participant.PlayerNumber == participant.PlayerNumber)
            .OrderByDescending(pair => GetIdentityStrength(pair.Value.Participant))
            .ToArray();
        if (sameSlotCandidates.Length == 0)
        {
            _players.Add(key, new PlayerState { Participant = participant });
            return participant;
        }

        var corroboratedCandidates = sameSlotCandidates
            .Where(pair => SharesStableIdentityEvidence(pair.Value.Participant, participant))
            .ToArray();
        if (corroboratedCandidates.Length == 1)
        {
            var corroborated = corroboratedCandidates[0];
            var existingStrength = GetIdentityStrength(corroborated.Value.Participant);
            var incomingStrength = GetIdentityStrength(participant);
            if (incomingStrength > existingStrength)
            {
                _players.Remove(corroborated.Key);
                MigrateTemporaryBlock(corroborated.Key, key);
                corroborated.Value.Participant = MergeParticipant(
                    corroborated.Value.Participant,
                    participant,
                    preferIncomingIdentity: true);
                _players.Add(key, corroborated.Value);
                return corroborated.Value.Participant;
            }

            corroborated.Value.Participant = MergeParticipant(
                corroborated.Value.Participant,
                participant,
                preferIncomingIdentity: false);
            return corroborated.Value.Participant;
        }

        // A slot number alone cannot prove continuity. Slot-only observations do not
        // override a stronger current identity or accrue moderation against it.
        if (GetIdentityStrength(participant) <= 1)
            return participant with { PlayerNumber = 0 };

        // A new sender key or EntityId is authoritative evidence of the current
        // occupant. Replace stale rows without transferring temporary state.
        foreach (var sameSlot in sameSlotCandidates)
            RemoveObservedState(sameSlot.Key);
        _players.Add(key, new PlayerState { Participant = participant });
        return participant;
    }

    private ChatModerationParticipant ResolveCanonicalParticipant(
        ChatModerationParticipant participant)
    {
        participant = NormalizeParticipant(participant);
        if (!TryGetStableIdentityKey(participant, out var key))
            return participant;
        if (_players.TryGetValue(key, out var exact))
            return exact.Participant;
        if (participant.PlayerNumber is < 1 or > PartyMemberSlotMap.MemberCount)
            return participant;

        var incomingStrength = GetIdentityStrength(participant);
        var stronger = _players.Values
            .Where(state =>
                state.Participant.PlayerNumber == participant.PlayerNumber &&
                GetIdentityStrength(state.Participant) > incomingStrength &&
                SharesStableIdentityEvidence(state.Participant, participant))
            .OrderByDescending(state => GetIdentityStrength(state.Participant))
            .ToArray();
        return stronger.Length == 1 ? stronger[0].Participant : participant;
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
               _persistentBlocked.Contains(participant.EntityId.Trim());
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
            key = "entity:" + participant.EntityId.Trim();
            return true;
        }

        if (participant.SenderId != 0)
        {
            key = "sender:" + participant.SenderId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (participant.PlayerNumber is >= 1 and <= PartyMemberSlotMap.MemberCount)
        {
            key = "player:" + participant.PlayerNumber.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        key = string.Empty;
        return false;
    }

    private static bool RepresentsLeavingParticipant(
        ChatModerationParticipant observed,
        ChatModerationParticipant leaving)
    {
        if (!string.IsNullOrWhiteSpace(leaving.EntityId))
        {
            return !string.IsNullOrWhiteSpace(observed.EntityId) &&
                   string.Equals(
                       observed.EntityId.Trim(),
                       leaving.EntityId.Trim(),
                       StringComparison.Ordinal);
        }

        if (leaving.SenderId != 0)
            return observed.SenderId != 0 && observed.SenderId == leaving.SenderId;

        return HasSameValidPlayerNumber(observed, leaving);
    }

    private static bool HasSameValidPlayerNumber(
        ChatModerationParticipant first,
        ChatModerationParticipant second) =>
        first.PlayerNumber is >= 1 and <= PartyMemberSlotMap.MemberCount &&
        second.PlayerNumber == first.PlayerNumber;

    private void MigrateTemporaryBlock(string oldKey, string newKey)
    {
        if (_roomBlockedKeys.Remove(oldKey))
            _roomBlockedKeys.Add(newKey);
        if (_autoBlockedKeys.Remove(oldKey))
            _autoBlockedKeys.Add(newKey);
    }

    private void RemoveObservedState(string key)
    {
        _players.Remove(key);
        if (key.StartsWith("entity:", StringComparison.Ordinal))
            return;

        _roomBlockedKeys.Remove(key);
        _autoBlockedKeys.Remove(key);
    }

    private static ChatModerationParticipant NormalizeParticipant(
        ChatModerationParticipant participant) =>
        participant with
        {
            DisplayName = participant.DisplayName?.Trim() ?? string.Empty,
            EntityId = string.IsNullOrWhiteSpace(participant.EntityId)
                ? null
                : participant.EntityId.Trim(),
        };

    private static ChatModerationParticipant MergeParticipant(
        ChatModerationParticipant existing,
        ChatModerationParticipant incoming,
        bool preferIncomingIdentity)
    {
        var preferred = preferIncomingIdentity ? incoming : existing;
        var fallback = preferIncomingIdentity ? existing : incoming;
        var preferredDisplayName = preferIncomingIdentity
            ? incoming.DisplayName
            : existing.DisplayName;
        var fallbackDisplayName = preferIncomingIdentity
            ? existing.DisplayName
            : incoming.DisplayName;
        return preferred with
        {
            PlayerNumber = preferred.PlayerNumber is >= 1 and <= PartyMemberSlotMap.MemberCount
                ? preferred.PlayerNumber
                : fallback.PlayerNumber,
            DisplayName = string.IsNullOrWhiteSpace(preferredDisplayName)
                ? fallbackDisplayName
                : preferredDisplayName,
            EntityId = !string.IsNullOrWhiteSpace(preferred.EntityId)
                ? preferred.EntityId
                : fallback.EntityId,
            SenderId = preferred.SenderId != 0
                ? preferred.SenderId
                : fallback.SenderId,
            IsLocal = existing.IsLocal || incoming.IsLocal,
        };
    }

    private static int GetIdentityStrength(ChatModerationParticipant participant)
    {
        if (!string.IsNullOrWhiteSpace(participant.EntityId))
            return 3;
        if (participant.SenderId != 0)
            return 2;
        return participant.PlayerNumber is >= 1 and <= PartyMemberSlotMap.MemberCount ? 1 : 0;
    }

    private static bool SharesStableIdentityEvidence(
        ChatModerationParticipant first,
        ChatModerationParticipant second)
    {
        var firstEntityId = first.EntityId?.Trim();
        var secondEntityId = second.EntityId?.Trim();
        if (!string.IsNullOrWhiteSpace(firstEntityId) &&
            !string.IsNullOrWhiteSpace(secondEntityId))
        {
            return string.Equals(firstEntityId, secondEntityId, StringComparison.Ordinal);
        }

        return first.SenderId != 0 && first.SenderId == second.SenderId;
    }

    private sealed class PlayerState
    {
        public ChatModerationParticipant Participant;
        public Queue<DateTimeOffset> WindowHits { get; } = new();
        public DateTimeOffset? LastHitAt;
    }
}
