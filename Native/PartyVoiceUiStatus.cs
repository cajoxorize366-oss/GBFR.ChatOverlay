namespace GBFR.ChatOverlay.Native;

internal enum PartyVoiceUiState
{
    Disabled,
    Unavailable,
    WaitingForSession,
    Connecting,
    WaitingForPeer,
    LocalSelfTesting,
    LocalSelfTestSignalDetected,
    LocalSelfTestFailed,
    Ready,
    Speaking,
    Disconnecting,
    Faulted,
}

internal readonly record struct PartyVoiceUiStatus(PartyVoiceUiState State)
{
    public static PartyVoiceUiStatus Disabled => new(PartyVoiceUiState.Disabled);

    public static PartyVoiceUiStatus Unavailable => new(PartyVoiceUiState.Unavailable);
}
