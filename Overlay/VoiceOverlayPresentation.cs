using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Overlay;

internal readonly record struct VoiceOverlayPresentation(bool IsVisible, string Text);

internal static class VoiceOverlayPresenter
{
    public static VoiceOverlayPresentation Create(
        PartyVoiceUiStatus status,
        UiLanguage language = UiLanguage.SimplifiedChinese) =>
        status.State switch
        {
            PartyVoiceUiState.Disabled => new(false, string.Empty),
            PartyVoiceUiState.Unavailable =>
                new(true, T(language, "[语音] 不可用", "[Voice] Unavailable")),
            PartyVoiceUiState.WaitingForSession =>
                new(true, T(language, "[语音] 等待联机房间", "[Voice] Waiting for an online room")),
            PartyVoiceUiState.Connecting =>
                new(true, T(language, "[语音] 正在连接", "[Voice] Connecting")),
            PartyVoiceUiState.WaitingForPeer =>
                new(true, T(language, "[语音] 等待队友", "[Voice] Waiting for players")),
            PartyVoiceUiState.LocalSelfTesting =>
                new(true, T(language, "[语音] 测试中，请说话", "[Voice] Testing; please speak")),
            PartyVoiceUiState.LocalSelfTestSignalDetected =>
                new(true, T(language, "[语音] 测试通过", "[Voice] Test passed")),
            PartyVoiceUiState.LocalSelfTestFailed =>
                new(true, T(language, "[语音] 测试失败", "[Voice] Test failed")),
            PartyVoiceUiState.Ready =>
                new(true, T(language, "[语音] 已就绪", "[Voice] Ready")),
            PartyVoiceUiState.Speaking =>
                new(true, T(language, "[语音] 正在通话中", "[Voice] Transmitting")),
            PartyVoiceUiState.Disconnecting =>
                new(true, T(language, "[语音] 正在断开", "[Voice] Disconnecting")),
            PartyVoiceUiState.Faulted =>
                new(true, T(language, "[语音] 已静音", "[Voice] Muted")),
            _ => new(true, T(language, "[语音] 状态未知", "[Voice] Unknown state")),
        };

    private static string T(UiLanguage language, string chinese, string english) =>
        UiLocalization.Select(language, chinese, english);
}
