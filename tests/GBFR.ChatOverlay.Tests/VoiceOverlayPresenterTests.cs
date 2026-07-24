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
    [InlineData((int)PartyVoiceUiState.WaitingForPeer, true, "等待队友语音通道")]
    [InlineData((int)PartyVoiceUiState.Ready, true, "按住 U 说话")]
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
