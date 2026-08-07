using GBFR.ChatOverlay.Native;
using GBFR.ChatOverlay.Overlay;
using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Tests;

public sealed class VoiceOverlayPresenterTests
{
    [Theory]
    [InlineData((int)PartyVoiceUiState.Disabled, false, "")]
    [InlineData((int)PartyVoiceUiState.Unavailable, true, "不可用")]
    [InlineData((int)PartyVoiceUiState.WaitingForSession, true, "等待联机房间")]
    [InlineData((int)PartyVoiceUiState.Connecting, true, "正在连接")]
    [InlineData((int)PartyVoiceUiState.WaitingForPeer, true, "等待队友")]
    [InlineData((int)PartyVoiceUiState.LocalSelfTesting, true, "测试中")]
    [InlineData((int)PartyVoiceUiState.LocalSelfTestSignalDetected, true, "测试通过")]
    [InlineData((int)PartyVoiceUiState.LocalSelfTestFailed, true, "测试失败")]
    [InlineData((int)PartyVoiceUiState.Ready, true, "已就绪")]
    [InlineData((int)PartyVoiceUiState.Speaking, true, "正在通话中")]
    [InlineData((int)PartyVoiceUiState.Disconnecting, true, "正在断开")]
    [InlineData((int)PartyVoiceUiState.Faulted, true, "已静音")]
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

    [Fact]
    public void Create_UsesSelectedLanguage()
    {
        var presentation = VoiceOverlayPresenter.Create(
            new PartyVoiceUiStatus(PartyVoiceUiState.Ready),
            UiLanguage.English);

        Assert.Equal("[Voice] Ready", presentation.Text);
    }
}
