using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DearImguiSharp;
using GBFR.ChatOverlay.Audio;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Native;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Imgui.Hook;
using Reloaded.Imgui.Hook.Implementations;

namespace GBFR.ChatOverlay.Overlay;

public sealed class ChatOverlayHost
{
    private const int VirtualKeyY = 0x59;
    private const int VirtualKeyF10 = 0x79;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmSysChar = 0x0106;
    private const uint WmKillFocus = 0x0008;
    private const uint WmActivate = 0x0006;
    private const uint WmActivateApp = 0x001C;
    private const int InputBufferSize = 2_048;
    private const float ComposerReservedHeight = 58.0f;

    private static ChatOverlayHost? s_activeHost;
    private static int s_hasOriginalWndProc;
    private static WndProcHook.WndProc s_originalWndProc;

    private readonly ChatSession _session;
    private readonly Func<Config> _getConfiguration;
    private readonly Func<bool> _isOnlineRoomActive;
    private readonly Action _onOnlineRoomUnavailable;
    private readonly Func<PartyVoiceUiStatus> _getVoiceUiStatus;
    private readonly Func<float, float, float, float, IReadOnlyList<PartyHudAnchor>> _getPartyHudAnchors;
    private readonly InGameAudioSettingsController? _audioSettings;
    private readonly Action<Action<Config>> _updateConfiguration;
    private readonly Action<bool> _setLocalSelfTestRequested;
    private readonly Action _forceReleaseVoiceInputs;
    private readonly Action<string> _log;
    private readonly MouseInteractionGate _mouseInteractionGate = new();
    private readonly byte[] _inputBuffer = new byte[InputBufferSize];
    private ImeCandidateSnapshot? _imeCandidateSnapshot;
    private int _openRequested;
    private int _settingsToggleRequested;
    private int _settingsToggleKeyDown;
    private int _settingsMenuOpen;
    private int _captureKeyboard;
    private int _swallowActivationKeyUntilRelease;
    private int _wndProcFailureLogged;
    private int _imeCompatibilityLogged;
    private int _imeCandidateUiLogged;
    private int _imeDecodeFailureLogged;
    private int _imeCandidateCaptureLogged;
    private int _imeCandidateReadFailureLogged;
    private int _imeCompositionWithoutCandidatesLogged;
    private int _imeCompositionObserved;
    private int _imeCandidateCapturedInComposition;
    private int _platformImeBridgeLogged;
    private int _renderThreadLogged;
    private int _onlineRoomGateFailureLogged;
    private int _onlineRoomWasInactive = 1;
    private int _graphicsFailureHandled;
    private int _cursorReleaseHookFailureLogged;
    private int _releaseCaptureFrames;
    private int _pendingAnsiLeadByte = -1;
    private nint _windowHandle;
    private bool _focusInputNextFrame;
    private bool _windowOpen = true;
    private bool _settingsWindowOpen = true;
    private bool _initialized;
    private bool _hasSavedClipRect;
    private NativeRect _savedClipRect;
    private nint _savedCaptureWindow;
    private ChatOverlayRect? _editedChatRect;
    private float _editWorkX;
    private float _editWorkY;
    private float _editWorkWidth;
    private float _editWorkHeight;
    private long _lastRenderedSequence;
    private string? _statusText;

    internal ChatOverlayHost(
        ChatSession session,
        Func<Config> getConfiguration,
        Func<bool> isOnlineRoomActive,
        Action onOnlineRoomUnavailable,
        Func<PartyVoiceUiStatus> getVoiceUiStatus,
        Func<float, float, float, float, IReadOnlyList<PartyHudAnchor>> getPartyHudAnchors,
        InGameAudioSettingsController? audioSettings,
        Action<Action<Config>> updateConfiguration,
        Action<bool> setLocalSelfTestRequested,
        Action forceReleaseVoiceInputs,
        Action<string> log)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _getConfiguration = getConfiguration ?? throw new ArgumentNullException(nameof(getConfiguration));
        _isOnlineRoomActive = isOnlineRoomActive ?? throw new ArgumentNullException(nameof(isOnlineRoomActive));
        _onOnlineRoomUnavailable = onOnlineRoomUnavailable ??
            throw new ArgumentNullException(nameof(onOnlineRoomUnavailable));
        _getVoiceUiStatus = getVoiceUiStatus ?? throw new ArgumentNullException(nameof(getVoiceUiStatus));
        _getPartyHudAnchors = getPartyHudAnchors ?? throw new ArgumentNullException(nameof(getPartyHudAnchors));
        _audioSettings = audioSettings;
        _updateConfiguration = updateConfiguration ?? throw new ArgumentNullException(nameof(updateConfiguration));
        _setLocalSelfTestRequested = setLocalSelfTestRequested ??
            throw new ArgumentNullException(nameof(setLocalSelfTestRequested));
        _forceReleaseVoiceInputs = forceReleaseVoiceInputs ??
            throw new ArgumentNullException(nameof(forceReleaseVoiceInputs));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsInitialized => Volatile.Read(ref _initialized);

    public bool TryRequestOpen()
    {
        if (!Volatile.Read(ref _initialized) ||
            !_getConfiguration().EnableOverlay ||
            !IsOnlineRoomActive() ||
            _session.Composer.IsOpen)
            return false;

        Interlocked.Exchange(ref _captureKeyboard, 1);
        Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
        ClearImeCandidateSnapshot();
        Interlocked.Exchange(ref _openRequested, 1);
        return true;
    }

    public bool ShouldCaptureKeyboard() =>
        Volatile.Read(ref _initialized) &&
        (Volatile.Read(ref _settingsMenuOpen) != 0 ||
         (_getConfiguration().EnableOverlay &&
          IsOnlineRoomActive() &&
          Volatile.Read(ref _captureKeyboard) != 0));

    public void ObserveSettingsMenuKey(bool pressed)
    {
        if (!Volatile.Read(ref _initialized))
            return;
        var previous = Interlocked.Exchange(ref _settingsToggleKeyDown, pressed ? 1 : 0);
        if (pressed && previous == 0)
            Interlocked.Increment(ref _settingsToggleRequested);
    }

    public async Task InitializeAsync(IReloadedHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);

        if (Interlocked.CompareExchange(ref s_activeHost, this, null) is not null)
            throw new InvalidOperationException("Only one GBFR chat overlay host can be active.");

        SDK.Init(hooks, message => _log($"ImGui: {message}"));

        var options = new ImguiHookOptions
        {
            EnableViewports = false,
            IgnoreWindowUnactivate = true,
            CustomWndProcHandlerPointer = GetCustomWndProcPointer(),
            Implementations = new List<IImguiHook>
            {
                new CjkConfiguredDx11Hook(_log, HandlePermanentGraphicsFailure),
            },
        };

        try
        {
            await ImguiHook.Create(Render, options).ConfigureAwait(false);
            Volatile.Write(ref _initialized, true);
            _log(
                "DirectX 11 ImGui hook initialized with the Extra Sigil Present-only " +
                "hook-chain and native SEH compatibility path.");
        }
        catch
        {
            Interlocked.CompareExchange(ref s_activeHost, null, this);
            throw;
        }
    }

    public void Suspend()
    {
        SetSettingsMenuOpen(false);
        _session.Composer.Cancel();
        Interlocked.Exchange(ref _openRequested, 0);
        Interlocked.Exchange(ref _settingsToggleRequested, 0);
        Interlocked.Exchange(ref _settingsToggleKeyDown, 0);
        Interlocked.Exchange(ref _captureKeyboard, 0);
        Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 0);
        Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
        ClearImeCandidateSnapshot();
        _releaseCaptureFrames = 0;
        _focusInputNextFrame = false;
        _statusText = null;
        if (_initialized)
            ImguiHook.Disable();
    }

    public void Resume()
    {
        if (_initialized)
            ImguiHook.Enable();
    }

    private void Render()
    {
        try
        {
            if (Interlocked.CompareExchange(ref _renderThreadLogged, 1, 0) == 0)
                LogSafely($"First Direct3D11 Present callback: OS TID {GetCurrentThreadId()}.");

            _session.DrainIncoming();
            var configuration = _getConfiguration();
            if (!configuration.EnableImeCandidateFallback)
                ClearImeCandidateSnapshot();
            var onlineRoomActive = IsOnlineRoomActive();
            var previousOnlineRoomInactive = Interlocked.Exchange(
                ref _onlineRoomWasInactive,
                onlineRoomActive ? 0 : 1);
            if (!onlineRoomActive && previousOnlineRoomInactive == 0)
                NotifyOnlineRoomUnavailable();
            var voiceUiStatus = _getVoiceUiStatus();
            if (onlineRoomActive || configuration.ShowAllVoiceIndicatorSlots)
                VoiceIndicatorOverlay.Draw(configuration, voiceUiStatus, _getPartyHudAnchors);

            if ((Interlocked.Exchange(ref _settingsToggleRequested, 0) & 1) != 0)
                SetSettingsMenuOpen(Volatile.Read(ref _settingsMenuOpen) == 0);
            var settingsOpen = Volatile.Read(ref _settingsMenuOpen) != 0;
            if (settingsOpen && ImGui.IsKeyPressed((int)ImGuiKey.Escape, false))
            {
                SetSettingsMenuOpen(false);
                settingsOpen = false;
            }

            if (settingsOpen || (configuration.EnableOverlay && onlineRoomActive))
                BindPlatformImeWindow();

            if (settingsOpen)
            {
                _mouseInteractionGate.Observe(MouseButtonStateTracker.PressedButtons != 0);
                DrawSettingsMenu();
                if (!_settingsWindowOpen)
                {
                    SetSettingsMenuOpen(false);
                    settingsOpen = false;
                }
            }
            if (settingsOpen)
                _ = ClipCursor(nint.Zero);
            ImGui.GetIO().MouseDrawCursor = settingsOpen;

            if (!configuration.EnableOverlay || !onlineRoomActive)
            {
                ResetChatInteractionState();
                if (settingsOpen)
                    DrawChatWindow(configuration, openedThisFrame: false, voiceUiStatus, editMode: true);
                return;
            }

            if (previousOnlineRoomInactive != 0)
            {
                LogSafely(
                    "Relink online Party room became active; overlay rendering and Y/U hotkeys are now enabled. " +
                    "F10 settings remain available in every scene.");
            }

            if (settingsOpen)
            {
                ResetChatInteractionState();
                DrawChatWindow(configuration, openedThisFrame: false, voiceUiStatus, editMode: true);
                return;
            }

            if (_releaseCaptureFrames > 0 && --_releaseCaptureFrames == 0 && !_session.Composer.IsOpen)
                Interlocked.Exchange(ref _captureKeyboard, 0);

            var openedThisFrame = Interlocked.Exchange(ref _openRequested, 0) != 0;
            if (openedThisFrame)
            {
                _session.Composer.OpenKeyboard();
                SyncInputBufferFromDraft();
                _focusInputNextFrame = true;
                _statusText = _session.TransportStatusText;
            }

            if (_session.Composer.IsOpen && ImGui.IsKeyPressed((int)ImGuiKey.Escape, false))
            {
                _session.Composer.Cancel();
                _releaseCaptureFrames = 2;
                Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
                ClearImeCandidateSnapshot();
                _statusText = null;
            }

            DrawChatWindow(configuration, openedThisFrame, voiceUiStatus, editMode: false);
        }
        catch (Exception exception)
        {
            ResetInteractionState();
            LogSafely($"Render callback recovered from an exception: {exception}");
        }
    }

    private void DrawChatWindow(
        Config configuration,
        bool openedThisFrame,
        PartyVoiceUiStatus voiceUiStatus,
        bool editMode)
    {
        var viewport = ImGui.GetMainViewport();
        var workPosition = viewport.WorkPos;
        var workSize = viewport.WorkSize;
        var rect = editMode && _editedChatRect is { } edited
            ? edited
            : ChatOverlayLayout.Resolve(
                configuration,
                workPosition.X,
                workPosition.Y,
                workSize.X,
                workSize.Y);
        if (editMode)
        {
            _editedChatRect = rect;
            _editWorkX = workPosition.X;
            _editWorkY = workPosition.Y;
            _editWorkWidth = workSize.X;
            _editWorkHeight = workSize.Y;
        }

        using var position = CreateVector2(rect.X, rect.Y);
        using var size = CreateVector2(rect.Width, rect.Height);
        using var pivot = CreateVector2(0.0f, 0.0f);
        ImGui.SetNextWindowPos(position, (int)ImGuiCond.Always, pivot);
        ImGui.SetNextWindowSize(size, (int)ImGuiCond.Always);

        var composerOpen = _session.Composer.IsOpen;
        var opacity = editMode
            ? Math.Clamp((float)configuration.BackgroundOpacity, 0.25f, 1.0f)
            : composerOpen
            ? Math.Clamp((float)configuration.BackgroundOpacity, 0.0f, 1.0f)
            : Math.Clamp((float)configuration.BackgroundOpacity * 0.45f, 0.0f, 1.0f);
        ImGui.SetNextWindowBgAlpha(opacity);

        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoResize;
        if (!composerOpen && !editMode)
            flags |= ImGuiWindowFlags.NoInputs;

        var began = ImGui.Begin("GBFR Chat##GBFRChatOverlay", ref _windowOpen, (int)flags);
        try
        {
            if (!began)
                return;

            var imeCandidateText = composerOpen
                ? GetImeCandidateFallbackText(configuration.EnableImeCandidateFallback)
                : null;
            DrawVoiceStatus(voiceUiStatus);
            DrawHistory(composerOpen, imeCandidateText);
            if (composerOpen)
                DrawComposer(openedThisFrame, imeCandidateText);
            if (editMode)
            {
                DrawChatEditHandles(
                    viewport,
                    ref rect,
                    workPosition.X,
                    workPosition.Y,
                    workSize.X,
                    workSize.Y);
                _editedChatRect = rect;
            }
        }
        finally
        {
            ImGui.End();
        }
    }

    private void DrawSettingsMenu()
    {
        var viewport = ImGui.GetMainViewport();
        var workPosition = viewport.WorkPos;
        var workSize = viewport.WorkSize;
        var width = Math.Min(900.0f, Math.Max(1.0f, workSize.X - 48.0f));
        var height = Math.Min(610.0f, Math.Max(1.0f, workSize.Y - 48.0f));
        using var position = CreateVector2(
            workPosition.X + Math.Max(0.0f, (workSize.X - width) * 0.5f),
            workPosition.Y + Math.Max(0.0f, (workSize.Y - height) * 0.5f));
        using var size = CreateVector2(width, height);
        using var pivot = CreateVector2(0.0f, 0.0f);
        ImGui.SetNextWindowPos(position, (int)ImGuiCond.Always, pivot);
        ImGui.SetNextWindowSize(size, (int)ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.96f);

        var flags = ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoResize;
        var began = ImGui.Begin("语音与聊天框设置  [F10]##GBFRSettings", ref _settingsWindowOpen, (int)flags);
        try
        {
            if (!began)
                return;

            ImGui.BeginDisabled(!_mouseInteractionGate.IsArmed);
            try
            {
                ImGui.Text("语音");
                ImGui.Separator();
                if (_audioSettings is null)
                {
                    ImGui.TextWrapped("本机语音自检不可用；请确认实验语音功能已启用后重启 Mod。");
                }
                else
                {
                    var snapshot = _audioSettings.GetSnapshot();
                    if (DrawEndpointCombo(
                            "麦克风##GBFRMicrophone",
                            snapshot.MicrophoneDeviceId,
                            snapshot.Microphones,
                            out var microphoneId))
                    {
                        _setLocalSelfTestRequested(false);
                        _audioSettings.SelectMicrophone(microphoneId);
                    }

                    if (DrawEndpointCombo(
                            "扬声器##GBFRSpeaker",
                            snapshot.SpeakerDeviceId,
                            snapshot.Speakers,
                            out var speakerId))
                    {
                        _setLocalSelfTestRequested(false);
                        _audioSettings.SelectSpeaker(speakerId);
                    }

                    var inputGainPercent = snapshot.MicrophoneInputGain * 100.0f;
                    if (ImGui.SliderFloat(
                            "麦克风音量（本地测试输入增益）##GBFRMicGain",
                            ref inputGainPercent,
                            0.0f,
                            200.0f,
                            "%.0f%%",
                            0))
                    {
                        _audioSettings.SetMicrophoneInputGain(inputGainPercent / 100.0f);
                    }

                    var speakerVolumePercent = snapshot.SpeakerVolume * 100.0f;
                    if (ImGui.SliderFloat(
                            "扬声器音量（本地测试回放）##GBFRSpeakerVolume",
                            ref speakerVolumePercent,
                            0.0f,
                            50.0f,
                            "%.0f%%",
                            0))
                    {
                        _audioSettings.SetSpeakerVolume(speakerVolumePercent / 100.0f);
                    }

                    using var testButtonSize = CreateVector2(150.0f, 42.0f);
                    var selfTesting = snapshot.IsSelfTestRequested &&
                                      snapshot.SelfTestState is not LocalMicrophoneMonitorState.Faulted;
                    if (ImGui.Button(selfTesting ? "停止麦克风测试" : "麦克风测试", testButtonSize))
                        _setLocalSelfTestRequested(!selfTesting);

                    ImGui.TextWrapped(DescribeSelfTest(snapshot.SelfTestState));
                    using var meterSize = CreateVector2(-1.0f, 26.0f);
                    ImGui.ProgressBar(
                        Math.Clamp(snapshot.PeakLevel, 0.0f, 1.0f),
                        meterSize,
                        $"输入电平  {snapshot.PeakLevel:P0}");
                    ImGui.TextWrapped(
                        "设备选择与本地测试立即生效。当前版本的 Party 语音设备会在重启 Mod 后应用；" +
                        "测试时建议佩戴耳机，避免声学回授。");
                }

                ImGui.Separator();
                ImGui.Text("聊天框布局");
                ImGui.TextWrapped(
                    "拖动聊天框顶部可移动；拖动右下角三角标记可缩放。关闭本菜单时自动保存，" +
                    "位置会按当前可用画面比例适配其他分辨率。");
                ImGui.TextWrapped("按 F10 或 Esc 关闭设置菜单。设置菜单打开时，鼠标与键盘不会传给游戏。");
            }
            finally
            {
                ImGui.EndDisabled();
            }
        }
        finally
        {
            ImGui.End();
        }
    }

    private static bool DrawEndpointCombo(
        string label,
        string selectedId,
        IReadOnlyList<AudioEndpointInfo> endpoints,
        out string newSelection)
    {
        newSelection = selectedId;
        var preview = AudioEndpointSelectionValues.IsSystemDefault(selectedId)
            ? AudioEndpointSelectionValues.SystemDefaultLabel
            : endpoints.FirstOrDefault(endpoint => string.Equals(
                endpoint.Id,
                selectedId,
                StringComparison.Ordinal))?.FriendlyName ?? "已保存的设备当前不可用";
        if (!ImGui.BeginCombo(label, preview, 0))
            return false;

        try
        {
            using var zero = CreateVector2(0.0f, 0.0f);
            var defaultSelected = AudioEndpointSelectionValues.IsSystemDefault(selectedId);
            if (ImGui.SelectableBool(
                    AudioEndpointSelectionValues.SystemDefaultLabel,
                    defaultSelected,
                    0,
                    zero))
            {
                newSelection = AudioEndpointSelectionValues.SystemDefault;
                return true;
            }

            foreach (var endpoint in endpoints)
            {
                var suffix = endpoint.IsDefaultCommunicationsDevice ? "  [Windows 通信默认]" : string.Empty;
                if (ImGui.SelectableBool(
                        endpoint.FriendlyName + suffix + "##" + endpoint.Id,
                        string.Equals(endpoint.Id, selectedId, StringComparison.Ordinal),
                        0,
                        zero))
                {
                    newSelection = endpoint.Id;
                    return true;
                }
            }
        }
        finally
        {
            ImGui.EndCombo();
        }

        return false;
    }

    private void DrawChatEditHandles(
        ImGuiViewport viewport,
        ref ChatOverlayRect rect,
        float workX,
        float workY,
        float workWidth,
        float workHeight)
    {
        ImGui.BeginDisabled(!_mouseInteractionGate.IsArmed);
        try
        {
            using var movePosition = CreateVector2(rect.X + 4.0f, rect.Y + 3.0f);
            using var moveSize = CreateVector2(Math.Max(1.0f, rect.Width - 38.0f), 22.0f);
            ImGui.SetCursorScreenPos(movePosition);
            _ = ImGui.InvisibleButton("##GBFRMoveChat", moveSize, 0);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(0, 0.0f))
            {
                using var delta = CreateVector2(0.0f, 0.0f);
                ImGui.GetMouseDragDelta(delta, 0, 0.0f);
                rect = ChatOverlayLayout.Move(
                    rect,
                    delta.X,
                    delta.Y,
                    workX,
                    workY,
                    workWidth,
                    workHeight);
                ImGui.ResetMouseDragDelta(0);
            }

            using var resizePosition = CreateVector2(
                rect.X + rect.Width - 30.0f,
                rect.Y + rect.Height - 30.0f);
            using var resizeSize = CreateVector2(30.0f, 30.0f);
            ImGui.SetCursorScreenPos(resizePosition);
            _ = ImGui.InvisibleButton("##GBFRResizeChat", resizeSize, 0);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(0, 0.0f))
            {
                using var delta = CreateVector2(0.0f, 0.0f);
                ImGui.GetMouseDragDelta(delta, 0, 0.0f);
                rect = ChatOverlayLayout.Resize(
                    rect,
                    delta.X,
                    delta.Y,
                    workX,
                    workY,
                    workWidth,
                    workHeight);
                ImGui.ResetMouseDragDelta(0);
            }
        }
        finally
        {
            ImGui.EndDisabled();
        }

        var drawList = ImGui.GetForegroundDrawListViewportPtr(viewport);
        using var topLeft = CreateVector2(rect.X + 5.0f, rect.Y + 4.0f);
        using var topRight = CreateVector2(rect.X + rect.Width - 34.0f, rect.Y + 4.0f);
        ImGui.ImDrawListAddLine(drawList, topLeft, topRight, PackColor(105, 224, 255, 0.75f), 2.0f);
        using var triangleTop = CreateVector2(rect.X + rect.Width - 5.0f, rect.Y + rect.Height - 25.0f);
        using var triangleCorner = CreateVector2(rect.X + rect.Width - 5.0f, rect.Y + rect.Height - 5.0f);
        using var triangleLeft = CreateVector2(rect.X + rect.Width - 25.0f, rect.Y + rect.Height - 5.0f);
        ImGui.ImDrawListAddTriangleFilled(
            drawList,
            triangleTop,
            triangleCorner,
            triangleLeft,
            PackColor(105, 224, 255, 0.92f));
    }

    private static string DescribeSelfTest(LocalMicrophoneMonitorState state) => state switch
    {
        LocalMicrophoneMonitorState.Starting => "正在启动所选音频设备……",
        LocalMicrophoneMonitorState.Monitoring => "正在监听；请对着麦克风说话。",
        LocalMicrophoneMonitorState.SignalDetected => "已检测到麦克风输入。",
        LocalMicrophoneMonitorState.Faulted => "自检启动失败；请重新选择可用设备后再试。",
        LocalMicrophoneMonitorState.Suspended => "Mod 已暂停，本地自检不可用。",
        _ => "点击“麦克风测试”后，可从下方音量条直观看到输入等级。",
    };

    private static uint PackColor(byte red, byte green, byte blue, float alpha)
    {
        var a = (uint)Math.Clamp((int)MathF.Round(Math.Clamp(alpha, 0.0f, 1.0f) * 255.0f), 0, 255);
        return (uint)red | ((uint)green << 8) | ((uint)blue << 16) | (a << 24);
    }

    private static void DrawVoiceStatus(PartyVoiceUiStatus voiceUiStatus)
    {
        var presentation = VoiceOverlayPresenter.Create(voiceUiStatus);
        if (!presentation.IsVisible)
            return;

        ImGui.TextWrapped(presentation.Text);
        ImGui.Separator();
    }

    private void DrawHistory(bool composerOpen, string? imeCandidateText)
    {
        var candidateHeight = MeasureWrappedTextItemHeight(imeCandidateText);
        var childHeight = CalculateHistoryChildHeight(composerOpen, candidateHeight);
        using var childSize = CreateVector2(0.0f, childHeight);
        var began = ImGui.BeginChildStr(
            "##GBFRChatHistory",
            childSize,
            false,
            (int)ImGuiWindowFlags.NoBackground);
        try
        {
            if (!began)
                return;

            var snapshot = _session.History.Snapshot();
            foreach (var message in snapshot)
            {
                var prefix = $"[{message.Timestamp:HH:mm}] {message.Sender}: ";
                ImGui.TextWrapped(prefix + message.Text);
            }

            if (snapshot.Count > 0 && snapshot[^1].Sequence != _lastRenderedSequence)
            {
                _lastRenderedSequence = snapshot[^1].Sequence;
                ImGui.SetScrollHereY(1.0f);
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    internal static float CalculateHistoryChildHeight(bool composerOpen, float candidateHeight)
    {
        if (!composerOpen)
            return 0.0f;

        var safeCandidateHeight = float.IsFinite(candidateHeight) && candidateHeight > 0.0f
            ? candidateHeight
            : 0.0f;
        return -(ComposerReservedHeight + safeCandidateHeight);
    }

    private static float MeasureWrappedTextItemHeight(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0.0f;

        using var available = CreateVector2(0.0f, 0.0f);
        ImGui.GetContentRegionAvail(available);
        using var textSize = CreateVector2(0.0f, 0.0f);
        ImGui.CalcTextSize(
            textSize,
            text,
            null!,
            false,
            Math.Max(1.0f, available.X));
        var itemSpacing = Math.Max(
            0.0f,
            ImGui.GetTextLineHeightWithSpacing() - ImGui.GetTextLineHeight());
        return Math.Max(0.0f, textSize.Y) + itemSpacing;
    }

    private unsafe void DrawComposer(bool openedThisFrame, string? imeCandidateText)
    {
        ImGui.Separator();
        DrawImeCandidateFallback(imeCandidateText);
        if (_focusInputNextFrame && !openedThisFrame)
        {
            ImGui.SetKeyboardFocusHere(0);
            _focusInputNextFrame = false;
        }

        ImGui.SetNextItemWidth(-1.0f);
        bool submitRequested;
        fixed (byte* buffer = _inputBuffer)
        {
            submitRequested = ImGui.InputText(
                "##GBFRChatInput",
                (sbyte*)buffer,
                (nint)_inputBuffer.Length,
                (int)ImGuiInputTextFlags.EnterReturnsTrue,
                null!,
                nint.Zero);
        }

        _session.Composer.SetDraft(ReadInputBuffer());
        if (submitRequested)
        {
            var result = _session.SendDraft();
            if (result.Succeeded)
            {
                Array.Clear(_inputBuffer);
                _releaseCaptureFrames = 2;
                Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
                ClearImeCandidateSnapshot();
                _statusText = null;
            }
            else
            {
                _statusText = result.Error ?? result.Status.ToString();
                _focusInputNextFrame = true;
            }
        }

        if (!string.IsNullOrEmpty(_statusText))
            ImGui.TextWrapped(_statusText);
    }

    private bool ShouldCaptureWindowMessage(uint message, nint wParam, nint lParam)
    {
        if (wParam == VirtualKeyF10)
        {
            if (message is WmKeyDown or WmSysKeyDown)
            {
                ObserveSettingsMenuKey(true);
                return true;
            }
            if (message is WmKeyUp or WmSysKeyUp)
            {
                ObserveSettingsMenuKey(false);
                return true;
            }
        }

        if (Volatile.Read(ref _settingsMenuOpen) != 0)
            return WindowInputClassifier.ShouldCapture(message, lParam);

        if (!_getConfiguration().EnableOverlay || !IsOnlineRoomActive())
            return false;

        var composerOpen = _session.Composer.IsOpen;
        if (!composerOpen &&
            (message is WmKeyDown or WmSysKeyDown) &&
            wParam == VirtualKeyY)
        {
            if (TryRequestOpen())
            {
                Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 1);
                return true;
            }

            return false;
        }

        if ((message is WmKeyUp or WmSysKeyUp) &&
            wParam == VirtualKeyY &&
            Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 0) != 0)
        {
            return true;
        }

        return Volatile.Read(ref _captureKeyboard) != 0 &&
               message is WmKeyDown or WmKeyUp or WmChar or WmSysKeyDown or WmSysKeyUp or WmSysChar;
    }

    private bool IsCapturingTextInput() =>
        _getConfiguration().EnableOverlay &&
        IsOnlineRoomActive() &&
        Volatile.Read(ref _captureKeyboard) != 0 &&
        _session.Composer.IsOpen;

    private bool ShouldIgnoreUnactivateBeforeBackend(uint message, nint wParam) =>
        IsCapturingTextInput() &&
        ImguiHook.Options?.IgnoreWindowUnactivate == true &&
        (message == WmKillFocus ||
         ((message is WmActivate or WmActivateApp) && wParam == nint.Zero));

    private bool TryHandleImeCharacter(nint windowHandle, uint message, nint wParam)
    {
        if (!IsCapturingTextInput())
        {
            Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
            return false;
        }

        var unicodeWindow = Win32ImeCompatibility.IsUnicodeWindow(windowHandle);
        if (message == Win32ImeCompatibility.WmImeChar)
        {
            Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
            ClearImeCandidateSnapshot();
            if (unicodeWindow)
            {
                ImGui.ImGuiIO_AddInputCharacterUTF16(
                    ImguiHook.IO,
                    unchecked((ushort)wParam));
                LogImeCompatibility(windowHandle, 0);
                return true;
            }

            var codePage = Win32ImeCompatibility.GetActiveInputCodePage();
            if (Win32ImeCompatibility.TryDecodePackedAnsiCharacter(
                    unchecked((uint)wParam),
                    codePage,
                    out var committedText))
            {
                AddUtf8Input(committedText);
                LogImeCompatibility(windowHandle, codePage);
            }
            else
            {
                LogImeDecodeFailure(unchecked((uint)wParam), codePage);
            }

            // Do not let DefWindowProcA split a DBCS WM_IME_CHAR into two
            // Latin-1-looking WM_CHAR messages (for example CE D2 -> ÎÒ).
            return true;
        }

        if (message != WmChar || unicodeWindow)
            return false;

        var activeCodePage = Win32ImeCompatibility.GetActiveInputCodePage();
        var pendingLeadByte = Volatile.Read(ref _pendingAnsiLeadByte);
        if (Win32ImeCompatibility.TryConsumeAnsiWindowCharacter(
                unchecked((uint)wParam),
                activeCodePage,
                ref pendingLeadByte,
                out var text))
        {
            Volatile.Write(ref _pendingAnsiLeadByte, pendingLeadByte);
            AddUtf8Input(text);
            LogImeCompatibility(windowHandle, activeCodePage);
        }
        else
        {
            Volatile.Write(ref _pendingAnsiLeadByte, -1);
            LogImeDecodeFailure(unchecked((uint)wParam), activeCodePage);
        }

        // The ANSI message has either been queued as UTF-8 or retained as a
        // DBCS lead byte. In both cases the stock ImGui WM_CHAR path must not run.
        return true;
    }

    private bool ShouldRouteImeUiToDefault(uint message)
    {
        if (!IsCapturingTextInput() || !Win32ImeCompatibility.IsImeUiMessage(message))
            return false;

        if (message == Win32ImeCompatibility.WmImeEndComposition)
            Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
        return true;
    }

    private void ObserveImeUiMessage(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        if (!_getConfiguration().EnableImeCandidateFallback)
        {
            ClearImeCandidateSnapshot();
            return;
        }

        try
        {
            if (message == Win32ImeCompatibility.WmImeStartComposition)
            {
                ClearImeCandidateSnapshot();
                Interlocked.Exchange(ref _imeCompositionObserved, 1);
                Interlocked.Exchange(ref _imeCandidateCapturedInComposition, 0);
                return;
            }

            if (message == Win32ImeCompatibility.WmImeEndComposition)
            {
                var compositionObserved = Interlocked.Exchange(ref _imeCompositionObserved, 0) != 0;
                var candidateCaptured = Interlocked.Exchange(
                    ref _imeCandidateCapturedInComposition,
                    0) != 0;
                ClearImeCandidateSnapshot();
                if (compositionObserved &&
                    !candidateCaptured &&
                    Interlocked.Exchange(ref _imeCompositionWithoutCandidatesLogged, 1) == 0)
                {
                    LogSafely(
                        "Win32 IME composition ended without an IMM32 candidate list. " +
                        "This input method may expose candidates only through its external TSF/Qt UI.");
                }

                return;
            }

            uint candidateMask;
            var reportReadFailure = false;
            if (message == Win32ImeCompatibility.WmImeNotify)
            {
                var notification = unchecked((uint)(nuint)wParam);
                if (notification == Win32ImeCandidateReader.ImnCloseCandidate)
                {
                    ClearImeCandidateSnapshot();
                    return;
                }

                if (!Win32ImeCandidateReader.IsRefreshNotification(notification))
                    return;

                candidateMask = unchecked((uint)(nuint)lParam);
                reportReadFailure = notification is
                    Win32ImeCandidateReader.ImnOpenCandidate or
                    Win32ImeCandidateReader.ImnChangeCandidate;
            }
            else if (message == Win32ImeCompatibility.WmImeComposition)
            {
                Interlocked.Exchange(ref _imeCompositionObserved, 1);
                candidateMask = 1;
            }
            else
            {
                return;
            }

            if (Win32ImeCandidateReader.TryReadFirstCandidateList(
                    windowHandle,
                    candidateMask,
                    out var snapshot,
                    out var failure) &&
                snapshot is not null)
            {
                Volatile.Write(ref _imeCandidateSnapshot, snapshot);
                Interlocked.Exchange(ref _imeCandidateCapturedInComposition, 1);
                if (Interlocked.Exchange(ref _imeCandidateCaptureLogged, 1) == 0)
                {
                    LogSafely(
                        $"Win32 IME candidate fallback captured list {snapshot.ListIndex}: " +
                        $"count={snapshot.Count}, selection={snapshot.SelectedIndex}, " +
                        $"pageStart={snapshot.PageStart}, pageSize={snapshot.PageSize}; " +
                        "candidates are now drawn inside the Overlay.");
                }

                return;
            }

            if (reportReadFailure &&
                Interlocked.Exchange(ref _imeCandidateReadFailureLogged, 1) == 0)
            {
                LogSafely(
                    $"Win32 IME candidate notification did not expose a readable IMM32 list: {failure}; " +
                    "further read failures are suppressed.");
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _imeCandidateReadFailureLogged, 1) == 0)
            {
                LogSafely(
                    "Win32 IME candidate fallback recovered from an exception; " +
                    $"further failures are suppressed: {exception.GetType().Name}: {exception.Message}.");
            }
        }
    }

    private string? GetImeCandidateFallbackText(bool enabled)
    {
        if (!enabled)
            return null;

        var snapshot = Volatile.Read(ref _imeCandidateSnapshot);
        if (snapshot is null)
            return null;

        var displayText = snapshot.BuildDisplayText();
        return string.IsNullOrEmpty(displayText) ? null : displayText;
    }

    private static void DrawImeCandidateFallback(string? displayText)
    {
        if (!string.IsNullOrEmpty(displayText))
            ImGui.TextWrapped(displayText);
    }

    private void ClearImeCandidateSnapshot() =>
        Volatile.Write(ref _imeCandidateSnapshot, null);

    private static void AddUtf8Input(string text)
    {
        if (!string.IsNullOrEmpty(text))
            ImGui.ImGuiIO_AddInputCharactersUTF8(ImguiHook.IO, text);
    }

    private void BindPlatformImeWindow()
    {
        var windowHandle = Volatile.Read(ref _windowHandle);
        if (windowHandle == nint.Zero)
            return;

        var viewport = ImGui.GetMainViewport();
        if (viewport.PlatformHandleRaw != windowHandle)
            viewport.PlatformHandleRaw = windowHandle;

        if (Interlocked.Exchange(ref _platformImeBridgeLogged, 1) == 0)
        {
            var platformCallbackAvailable = ImguiHook.IO.SetPlatformImeDataFn is not null;
            LogSafely(
                $"Dear ImGui platform IME bridge bound to game window 0x{unchecked((nuint)windowHandle):X}; " +
                $"platform callback available={platformCallbackAvailable}; " +
                "IMM32 composition and candidate positioning follow the active text caret.");
        }
    }

    private bool IsOnlineRoomActive()
    {
        try
        {
            return _isOnlineRoomActive();
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _onlineRoomGateFailureLogged, 1) == 0)
            {
                LogSafely(
                    $"Overlay online-room gate failed closed; further failures are suppressed: " +
                    $"{exception.GetType().Name}: {exception.Message}.");
            }

            return false;
        }
    }

    private void SetSettingsMenuOpen(bool open)
    {
        var newValue = open ? 1 : 0;
        if (Interlocked.Exchange(ref _settingsMenuOpen, newValue) == newValue)
            return;

        if (open)
        {
            ResetChatInteractionState();
            _settingsWindowOpen = true;
            _editedChatRect = null;
            _mouseInteractionGate.Open();
            _forceReleaseVoiceInputs();
            _audioSettings?.RefreshEndpointsAsync();
            BeginReleasedMouse();
            var cursorHooks = DxgiPresentBridge.SetCursorReleaseActive(true);
            if (cursorHooks != DxgiPresentBridge.CursorReleaseHook.All &&
                Interlocked.Exchange(ref _cursorReleaseHookFailureLogged, 1) == 0)
            {
                LogSafely(
                    $"F10 cursor release installed only {cursorHooks}; " +
                    "the per-frame ClipCursor fallback remains active.");
            }
            _ = ClipCursor(nint.Zero);
            ResetImGuiMouseState();
            LogSafely(
                "F10 settings opened; the game cursor lock/recenter path is suspended and " +
                "Win32, Raw Input, DirectInput keyboard and mouse are captured.");
            return;
        }

        _ = DxgiPresentBridge.SetCursorReleaseActive(false);
        _setLocalSelfTestRequested(false);
        _audioSettings?.FlushPendingLevelSave();
        _mouseInteractionGate.Close();
        PersistEditedChatLayout();
        _editedChatRect = null;
        MouseButtonStateTracker.Reset();
        RestoreMouseCapture();
        LogSafely("F10 settings closed; held DirectInput keys and mouse buttons will drain before release.");
    }

    private void BeginReleasedMouse()
    {
        _hasSavedClipRect = GetClipCursor(out _savedClipRect);
        _savedCaptureWindow = GetCapture();
        if (_savedCaptureWindow != nint.Zero)
            ReleaseCapture();
        _ = ClipCursor(nint.Zero);
    }

    private void RestoreMouseCapture()
    {
        ReleaseCapture();
        if (_hasSavedClipRect)
            _ = ClipCursorRect(ref _savedClipRect);
        if (_savedCaptureWindow != nint.Zero && IsWindow(_savedCaptureWindow))
            _ = SetCapture(_savedCaptureWindow);
        var gameWindow = ImguiHook.WindowHandle;
        if (gameWindow != nint.Zero && IsWindow(gameWindow))
            _ = SetForegroundWindow(gameWindow);
        _hasSavedClipRect = false;
        _savedCaptureWindow = nint.Zero;
    }

    private void PersistEditedChatLayout()
    {
        if (_editedChatRect is not { } rect || _editWorkWidth <= 0.0f || _editWorkHeight <= 0.0f)
            return;

        var ratios = ChatOverlayLayout.ToRatios(
            rect,
            _editWorkX,
            _editWorkY,
            _editWorkWidth,
            _editWorkHeight);
        try
        {
            _updateConfiguration(configuration =>
            {
                configuration.OverlayWidth = (int)MathF.Round(rect.Width);
                configuration.OverlayHeight = (int)MathF.Round(rect.Height);
                configuration.OverlayPositionXRatio = ratios.XRatio;
                configuration.OverlayPositionYRatio = ratios.YRatio;
            });
        }
        catch (Exception exception)
        {
            LogSafely($"Chat layout could not be persisted: {exception.Message}");
        }
    }

    private static void ResetImGuiMouseState()
    {
        var io = ImguiHook.IO;
        ImGui.ImGuiIO_ClearInputKeys(io);
        for (var button = 0; button < 5; button++)
            ImGui.ImGuiIO_AddMouseButtonEvent(io, button, false);
        ImGui.ClearActiveID();
    }

    private void ResetChatInteractionState()
    {
        _session.Composer.Cancel();
        Interlocked.Exchange(ref _openRequested, 0);
        Interlocked.Exchange(ref _captureKeyboard, 0);
        Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 0);
        Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
        ClearImeCandidateSnapshot();
        Interlocked.Exchange(ref _imeCompositionObserved, 0);
        Interlocked.Exchange(ref _imeCandidateCapturedInComposition, 0);
        _releaseCaptureFrames = 0;
        _focusInputNextFrame = false;
        _statusText = null;
    }

    private void ResetInteractionState()
    {
        SetSettingsMenuOpen(false);
        ResetChatInteractionState();
        Interlocked.Exchange(ref _settingsToggleRequested, 0);
        Interlocked.Exchange(ref _settingsToggleKeyDown, 0);
        MouseButtonStateTracker.Reset();
    }

    private void NotifyOnlineRoomUnavailable()
    {
        try
        {
            _onOnlineRoomUnavailable();
        }
        catch (Exception exception)
        {
            LogSafely(
                $"Overlay online-room release callback failed closed: " +
                $"{exception.GetType().Name}: {exception.Message}.");
        }
    }

    private void HandlePermanentGraphicsFailure()
    {
        if (Interlocked.Exchange(ref _graphicsFailureHandled, 1) != 0)
            return;

        Volatile.Write(ref _initialized, false);
        ResetInteractionState();
        NotifyOnlineRoomUnavailable();
        LogSafely(
            "Overlay graphics backend failed closed; chat/voice UI and input capture are disabled " +
            "while the game continues through its native Present path.");
    }

    private void LogImeCompatibility(nint windowHandle, uint codePage)
    {
        if (Interlocked.Exchange(ref _imeCompatibilityLogged, 1) != 0)
            return;

        var windowKind = Win32ImeCompatibility.IsUnicodeWindow(windowHandle)
            ? "Unicode"
            : $"ANSI/code page {codePage}";
        LogSafely(
            $"Win32 IME compatibility active for the {windowKind} game window; " +
            "committed text is normalized to UTF-8 and candidate placement follows the chat input.");
    }

    private void LogImeCandidateUi(nint originalLParam, nint forwardedLParam)
    {
        if (Interlocked.Exchange(ref _imeCandidateUiLogged, 1) != 0)
            return;

        LogSafely(
            "Win32 IME candidate UI enabled for the active chat context: " +
            $"WM_IME_SETCONTEXT lParam 0x{unchecked((nuint)originalLParam):X} -> " +
            $"0x{unchecked((nuint)forwardedLParam):X}.");
    }

    private void LogImeDecodeFailure(uint rawCharacter, uint codePage)
    {
        if (Interlocked.Exchange(ref _imeDecodeFailureLogged, 1) != 0)
            return;

        LogSafely(
            $"Win32 IME discarded an undecodable ANSI character 0x{rawCharacter:X4} " +
            $"from code page {codePage}; further decode failures are suppressed.");
    }

    private void SyncInputBufferFromDraft()
    {
        Array.Clear(_inputBuffer);
        Encoding.UTF8.GetEncoder().Convert(
            _session.Composer.Draft.AsSpan(),
            _inputBuffer.AsSpan(0, _inputBuffer.Length - 1),
            true,
            out _,
            out _,
            out _);
    }

    private string ReadInputBuffer()
    {
        var length = Array.IndexOf(_inputBuffer, (byte)0);
        if (length < 0)
            length = _inputBuffer.Length;
        return Encoding.UTF8.GetString(_inputBuffer, 0, length);
    }

    private static ImVec2 CreateVector2(float x, float y)
    {
        var vector = new ImVec2();
        vector.X = x;
        vector.Y = y;
        return vector;
    }

    private static unsafe nint GetCustomWndProcPointer() =>
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&CustomWndProc;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe nint CustomWndProc(nint hWnd, uint message, nint wParam, nint lParam)
    {
        var host = s_activeHost;
        var originalStarted = false;
        try
        {
            if (host is not null && host.IsInitialized)
            {
                Volatile.Write(ref host._windowHandle, hWnd);
                MouseButtonStateTracker.ObserveWindowMessage(message, wParam);

                // Some third-party IME candidate windows transiently take OS
                // focus. Preserve ImGui's active InputText while that UI is up.
                if (host.ShouldIgnoreUnactivateBeforeBackend(message, wParam))
                    return nint.Zero;

                if (host.TryHandleImeCharacter(hWnd, message, wParam))
                    return nint.Zero;

                // Preserve the system IME lifecycle through the proper
                // ANSI/Unicode default procedure. The Overlay also snapshots an
                // IMM32 candidate list here as a fallback for invisible Qt UI.
                if (host.ShouldRouteImeUiToDefault(message))
                {
                    host.ObserveImeUiMessage(hWnd, message, wParam, lParam);
                    var forwardedLParam = Win32ImeCompatibility.PrepareImeUiLParam(
                        message,
                        wParam,
                        lParam);
                    if (message == Win32ImeCompatibility.WmImeSetContext && wParam != nint.Zero)
                        host.LogImeCandidateUi(lParam, forwardedLParam);
                    return Win32ImeCompatibility.CallDefaultWindowProc(
                        hWnd,
                        message,
                        wParam,
                        forwardedLParam);
                }
                ImGui.ImplWin32_WndProcHandler((void*)hWnd, message, wParam, lParam);
                if (ImguiHook.Options?.IgnoreWindowUnactivate == true)
                {
                    if (message == WmKillFocus)
                        return nint.Zero;
                    if ((message is WmActivate or WmActivateApp) && wParam == nint.Zero)
                        return nint.Zero;
                }

                if (host.ShouldCaptureWindowMessage(message, wParam, lParam))
                    return nint.Zero;
            }

            var hook = WndProcHook.Instance;
            if (hook is not null)
            {
                var original = hook.Hook.OriginalFunction;
                s_originalWndProc = original;
                Volatile.Write(ref s_hasOriginalWndProc, 1);
                originalStarted = true;
                return original.Value.Invoke(hWnd, message, wParam, lParam);
            }

            if (Volatile.Read(ref s_hasOriginalWndProc) != 0)
            {
                originalStarted = true;
                return s_originalWndProc.Value.Invoke(hWnd, message, wParam, lParam);
            }

            return Win32ImeCompatibility.CallDefaultWindowProc(hWnd, message, wParam, lParam);
        }
        catch (Exception exception)
        {
            host?.LogWndProcFallback(exception);
            if (host is not null &&
                Volatile.Read(ref host._settingsMenuOpen) != 0 &&
                WindowInputClassifier.IsAlwaysCaptured(message))
            {
                return nint.Zero;
            }
            if (!originalStarted && Volatile.Read(ref s_hasOriginalWndProc) != 0)
            {
                try
                {
                    return s_originalWndProc.Value.Invoke(hWnd, message, wParam, lParam);
                }
                catch (Exception fallbackException)
                {
                    host?.LogWndProcFallback(fallbackException);
                }
            }

            return Win32ImeCompatibility.CallDefaultWindowProc(hWnd, message, wParam, lParam);
        }
    }

    private void LogSafely(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // A logger must never let an exception cross a native render callback.
        }
    }

    private void LogWndProcFallback(Exception exception)
    {
        if (Interlocked.Exchange(ref _wndProcFailureLogged, 1) != 0)
            return;

        try
        {
            _log(
                "Custom WndProc recovered through the ANSI/Unicode default procedure after an exception: " +
                $"{exception.GetType().Name} (0x{unchecked((uint)exception.HResult):X8}): " +
                $"{exception.Message}.");
        }
        catch
        {
            // A logger must not let an exception cross an unmanaged callback.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClipCursor(out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(nint rectangle);

    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursorRect(ref NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern nint GetCapture();

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
