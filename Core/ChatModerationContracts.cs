using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Core;

internal enum OfficialTextFilterState
{
    Unavailable = 0,
    Passthrough = 1,
    Ready = 2,
}

internal readonly record struct OfficialTextFilterStatus(
    OfficialTextFilterState State,
    string Detail)
{
    internal static OfficialTextFilterStatus Unavailable(string detail) =>
        new(OfficialTextFilterState.Unavailable, detail);
}

internal readonly record struct OfficialTextFilterResult(
    string Text,
    int FilteredCharacterCount,
    bool Succeeded)
{
    internal bool Matched => Succeeded && FilteredCharacterCount > 0;
}

internal interface IOfficialTextFilter
{
    OfficialTextFilterStatus Status { get; }

    OfficialTextFilterStatus Refresh();

    OfficialTextFilterResult Filter(string text);
}

internal readonly record struct ChatModerationParticipant(
    int PlayerNumber,
    string DisplayName,
    string? EntityId,
    uint SenderId = 0,
    bool IsLocal = false)
{
    internal bool HasPersistentIdentity => !string.IsNullOrWhiteSpace(EntityId);
}

internal readonly record struct ChatModerationInput(
    ChatModerationParticipant Participant,
    string Text,
    DateTimeOffset ReceivedAt,
    ChatCommunicationCue CommunicationCue = ChatCommunicationCue.None);

internal enum ChatModerationDisposition
{
    Allow = 0,
    Mask = 1,
    Block = 2,
}

internal readonly record struct ChatModerationDecision(
    ChatModerationDisposition Disposition,
    string Text,
    bool Matched,
    bool OfficialFilterMatched,
    bool AutoBlocked,
    IReadOnlyList<string> MatchedRuleIds)
{
    internal static ChatModerationDecision Allow(string text) =>
        new(ChatModerationDisposition.Allow, text, false, false, false, []);
}

internal readonly record struct ChatModerationPreview(
    ChatModerationDisposition Disposition,
    string Text,
    bool Matched,
    bool OfficialFilterMatched,
    IReadOnlyList<string> MatchedRuleIds);

internal readonly record struct ChatModerationEvent(
    ChatModerationParticipant Participant,
    int HitCount,
    int Threshold,
    DateTimeOffset OccurredAt,
    bool PersistIdentity);

internal readonly record struct ChatModerationRuleStatus(
    string RuleId,
    int SessionHitCount);

internal readonly record struct ChatModerationPlayerStatus(
    ChatModerationParticipant Participant,
    int WindowHitCount,
    DateTimeOffset? LastHitAt,
    bool IsRoomBlocked,
    bool IsPersistentlyBlocked);

internal readonly record struct ChatModerationSnapshot(
    OfficialTextFilterStatus OfficialFilter,
    int SessionFilteredMessageCount,
    IReadOnlyList<ChatModerationRuleStatus> Rules,
    IReadOnlyList<ChatModerationPlayerStatus> Players);

internal interface IChatModerationService
{
    void ApplyConfiguration(ChatFilterConfiguration? configuration);

    ChatModerationDecision Evaluate(in ChatModerationInput input);

    void ObserveParticipant(in ChatModerationParticipant participant);

    void ForgetParticipant(in ChatModerationParticipant participant);

    bool SetBlocked(in ChatModerationParticipant participant, bool blocked, bool persistent);

    bool IsBlocked(in ChatModerationParticipant participant);

    bool TryReadEvent(out ChatModerationEvent moderationEvent);

    ChatModerationSnapshot GetSnapshot();

    OfficialTextFilterStatus RefreshOfficialFilter();

    ChatModerationPreview Preview(string text);

    void ClearRoom();
}
