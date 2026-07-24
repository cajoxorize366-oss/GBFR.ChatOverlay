using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Overlay;

internal readonly record struct VoiceOverlayPresentation(bool IsVisible, string Text);

internal static class VoiceOverlayPresenter
{
    public static VoiceOverlayPresentation Create(PartyVoiceUiStatus status) =>
        status.State switch
        {
            PartyVoiceUiState.Disabled => new(false, string.Empty),
            PartyVoiceUiState.Unavailable =>
                new(true, "[VOICE] 不可用 · 麦克风保持静音"),
            PartyVoiceUiState.WaitingForSession =>
                new(true, "[VOICE] 等待进入联机房间"),
            PartyVoiceUiState.Connecting =>
                new(true, "[VOICE] 正在初始化 · 麦克风已静音"),
            PartyVoiceUiState.WaitingForPeer =>
                new(true, "[VOICE] 等待队友语音通道"),
            PartyVoiceUiState.Ready =>
                new(true, "[VOICE] 已就绪 · 按住 U 说话"),
            PartyVoiceUiState.Speaking =>
                new(true, ">>> [VOICE] 正在语音 · 松开 U 静音 <<<"),
            PartyVoiceUiState.Disconnecting =>
                new(true, "[VOICE] 正在断开 · 麦克风已静音"),
            PartyVoiceUiState.Faulted =>
                new(true, "[VOICE] 异常 · 已强制静音"),
            _ => new(true, "[VOICE] 状态未知 · 麦克风保持静音"),
        };
}
