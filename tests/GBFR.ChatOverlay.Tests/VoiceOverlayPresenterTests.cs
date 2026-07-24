using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class VoiceOverlayPresenterTests
{
    [Theory]
    [InlineData((int)PartyVoiceUiState.Disabled, false, "")]
    [InlineData((int)PartyVoiceUiState.Unavailable, true, "不可用")]
    [InlineData((int)PartyVoiceUiState.WaitingForSession, true, "等待进入联机房间")]
    [InlineData((int)PartyVoiceUiState.Connecting, true, "正在初始化")]
    [InlineData((int)PartyVoiceUiState.WaitingForPeer, true, "按住 I 本地监听")]
    [InlineData((int)PartyVoiceUiState.LocalSelfTesting, true, "本地监听中")]
    [InlineData((int)PartyVoiceUiState.LocalSelfTestSignalDetected, true, "本地自检通过")]
    [InlineData((int)PartyVoiceUiState.LocalSelfTestFailed, true, "本地监听失败")]
    [InlineData((int)PartyVoiceUiState.Ready, true, "U 队友通话 / I 本地监听")]
    [InlineData((int)PartyVoiceUiState.Speaking, true, "正在语音")]
    [InlineData((int)PartyVoiceUiState.Disconnecting, true, "正在断开")]
    [InlineData((int)PartyVoiceUiState.Faulted, true, "已强制静音")]
    public void Create_MapsRuntimeVoiceStateToStableUiText(
        int stateValue,
        bool expectedVisible,
        string expectedText)
    {
        var state = (PartyVoiceUiState)stateValue;
        var presentation = VoiceOverlayPresenter.Create(new PartyVoiceUiStatus(state));

        Assert.Equal(expectedVisible, presentation.IsVisible);
        Assert.Contains(expectedText, presentation.Text, StringComparison.Ordinal);
    }
}
