namespace GBFR.ChatOverlay.Native;

internal enum PartyMemberTransitionKind
{
    Joined,
    Left,
}

internal enum PartyMemberLeaveReason
{
    Unknown = 0,
    Requested = 1,
    Disconnected = 2,
    Kicked = 3,
    DeviceLostAuthentication = 4,
    CreationFailed = 5,
}

internal readonly record struct PartyMemberTransition(
    PartyMemberTransitionKind Kind,
    int RemotePlayerOrdinal,
    string? EntityId,
    PartyMemberLeaveReason LeaveReason = PartyMemberLeaveReason.Unknown,
    uint NativeReason = 0,
    uint ErrorDetail = 0);