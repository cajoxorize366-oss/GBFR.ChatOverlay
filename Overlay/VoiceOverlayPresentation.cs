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
                new(true, "[VOICE] 队友语音不可用 · F10 设置 / 本地自检"),
            PartyVoiceUiState.WaitingForSession =>
                new(true, "[VOICE] 等待进入联机房间 · F10 设置 / 本地自检"),
            PartyVoiceUiState.Connecting =>
                new(true, "[VOICE] 正在初始化 · 麦克风已静音"),
            PartyVoiceUiState.WaitingForPeer =>
                new(true, "[VOICE] 等待队友语音通道 · F10 设置 / 本地自检"),
            PartyVoiceUiState.LocalSelfTesting =>
                new(true, ">>> [VOICE] 本地自检中 · 请说话 · 在 F10 菜单停止 <<<"),
            PartyVoiceUiState.LocalSelfTestSignalDetected =>
                new(true, ">>> [VOICE] 本地自检通过 · 声音正在回放 <<<"),
            PartyVoiceUiState.LocalSelfTestFailed =>
                new(true, "[VOICE] 本地监听失败 · 已停止并保持安全静音"),
            PartyVoiceUiState.Ready =>
                new(true, "[VOICE] 已就绪 · U 队友通话 / F10 设置与自检"),
            PartyVoiceUiState.Speaking =>
                new(true, ">>> [VOICE] 正在语音 · 松开 U 静音 <<<"),
            PartyVoiceUiState.Disconnecting =>
                new(true, "[VOICE] 正在断开 · 麦克风已静音"),
            PartyVoiceUiState.Faulted =>
                new(true, "[VOICE] 异常 · 已强制静音"),
            _ => new(true, "[VOICE] 状态未知 · 麦克风保持静音"),
        };
}
