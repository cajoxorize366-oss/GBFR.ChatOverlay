namespace GBFR.ChatOverlay.Native;

internal enum PartyRoomTransitionKind
{
    Entered,
    Exited,
}

internal enum PartyRoomExitReason
{
    None,
    SelfLeft,
    HostDisconnected,
    Kicked,
    NetworkInterrupted,
}

internal enum PartyRoomHostState
{
    Unknown,
    LocalHost,
    RemoteHostPresent,
    RemoteHostMissing,
}

internal enum PartyNetworkLocalRole
{
    Unknown,
    Created,
    Connected,
}

internal readonly record struct PartyRoomIdentitySnapshot(
    string? RoomName,
    PartyRoomHostState HostState);

internal readonly record struct PartyRoomTransition(
    PartyRoomTransitionKind Kind,
    PartyRoomExitReason ExitReason = PartyRoomExitReason.None,
    string? RoomName = null,
    int VoiceParticipantCount = 0,
    uint NativeReason = 0,
    uint ErrorDetail = 0);
