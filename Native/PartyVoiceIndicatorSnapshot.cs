namespace GBFR.ChatOverlay.Native;

internal readonly record struct PartyVoiceEntitySnapshot(
    IReadOnlyCollection<string> EstablishedRemoteEntityIds,
    IReadOnlyCollection<string> TalkingRemoteEntityIds)
{
    internal static PartyVoiceEntitySnapshot Empty =>
        new(Array.Empty<string>(), Array.Empty<string>());
}

internal sealed record PartyVoiceIndicatorSnapshot(
    bool IsValid,
    IReadOnlyList<int> EstablishedRemotePlayers,
    IReadOnlyList<int> OccupiedRemotePlayers,
    IReadOnlyList<int> TalkingRemotePlayers)
{
    internal static PartyVoiceIndicatorSnapshot Unavailable { get; } =
        new(
            false,
            Array.Empty<int>(),
            Array.Empty<int>(),
            Array.Empty<int>());
}
