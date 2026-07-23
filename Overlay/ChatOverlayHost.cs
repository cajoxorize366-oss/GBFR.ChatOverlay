using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DearImguiSharp;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Stt;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Imgui.Hook;
using Reloaded.Imgui.Hook.Direct3D11;
using Reloaded.Imgui.Hook.Implementations;

namespace GBFR.ChatOverlay.Overlay;

public sealed class ChatOverlayHost
{
    private const int VirtualKeyY = 0x59;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmSysChar = 0x0106;
    private const int InputBufferSize = 2_048;

    private static ChatOverlayHost? s_activeHost;

    private readonly ChatSession _session;
    private readonly Func<Config> _getConfiguration;
    private readonly Action<string> _log;
    private readonly VoiceInputCoordinator? _voiceInput;
    private readonly IXInputStateSource _xInputStateSource;
    private readonly PushToTalkChordDetector _controllerVoiceDetector = new();
    private readonly byte[] _inputBuffer = new byte[InputBufferSize];
    private readonly object _requestSync = new();
    private readonly object _renderSync = new();
    private int _openRequested;
    private int _captureKeyboard;
    private int _swallowActivationKeyUntilRelease;
    private int _releaseCaptureFrames;
    private int _voiceStartRequested;
    private int _voiceStopRequested;
    private int _composerOpenSnapshot;
    private int _shutdown;
    private bool _focusInputNextFrame;
    private bool _windowOpen = true;
    private bool _initialized;
    private long _lastRenderedSequence;
    private string? _statusText;

    public ChatOverlayHost(
        ChatSession session,
        Func<Config> getConfiguration,
        Action<string> log,
        VoiceInputCoordinator? voiceInput = null,
        IXInputStateSource? xInputStateSource = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _getConfiguration = getConfiguration ?? throw new ArgumentNullException(nameof(getConfiguration));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _voiceInput = voiceInput;
        _xInputStateSource = xInputStateSource ?? new XInputStateSource();
        PublishComposerState();
    }

    public bool IsInitialized => Volatile.Read(ref _initialized);

    public bool TryRequestOpen()
    {
        lock (_requestSync)
        {
            if (Volatile.Read(ref _shutdown) != 0 ||
                !Volatile.Read(ref _initialized) ||
                !_getConfiguration().EnableOverlay ||
                Volatile.Read(ref _composerOpenSnapshot) != 0 ||
                Volatile.Read(ref _openRequested) != 0 ||
                Volatile.Read(ref _voiceStartRequested) != 0)
            {
                return false;
            }

            Interlocked.Exchange(ref _captureKeyboard, 1);
            Interlocked.Exchange(ref _openRequested, 1);
            return true;
        }
    }

    public bool ShouldCaptureKeyboard() =>
        _getConfiguration().EnableOverlay && Volatile.Read(ref _captureKeyboard) != 0;

    public bool IsVoiceInputEnabled()
    {
        var configuration = _getConfiguration();
        return Volatile.Read(ref _shutdown) == 0 &&
               Volatile.Read(ref _initialized) &&
               configuration.EnableOverlay &&
               configuration.EnableVoiceInput &&
               _voiceInput is { State: not VoiceRecognitionState.Unavailable };
    }

    public bool TryRequestVoiceCapture()
    {
        lock (_requestSync)
        {
            if (!IsVoiceInputEnabled() ||
                Volatile.Read(ref _composerOpenSnapshot) != 0 ||
                Volatile.Read(ref _captureKeyboard) != 0 ||
                Volatile.Read(ref _openRequested) != 0)
            {
                return false;
            }

            return Interlocked.CompareExchange(ref _voiceStartRequested, 1, 0) == 0;
        }
    }

    public void RequestVoiceCaptureEnd() => Interlocked.Exchange(ref _voiceStopRequested, 1);

    public async Task InitializeAsync(IReloadedHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _shutdown) != 0, this);

        if (Interlocked.CompareExchange(ref s_activeHost, this, null) is not null)
            throw new InvalidOperationException("Only one GBFR chat overlay host can be active.");

        SDK.Init(hooks, message => _log($"ImGui: {message}"));

        var options = new ImguiHookOptions
        {
            EnableViewports = false,
            IgnoreWindowUnactivate = false,
            CustomWndProcHandlerPointer = GetCustomWndProcPointer(),
            Implementations = new List<IImguiHook> { new ImguiHookDx11() },
        };

        try
        {
            await ImguiHook.Create(Render, options).ConfigureAwait(false);
            try
            {
                ConfigureFont();
            }
            catch (Exception exception)
            {
                _log($"CJK font setup failed; using the default ImGui font: {exception.Message}");
            }
            Volatile.Write(ref _initialized, true);
            if (Volatile.Read(ref _shutdown) != 0)
            {
                ImguiHook.Disable();
                Volatile.Write(ref _initialized, false);
                Interlocked.CompareExchange(ref s_activeHost, null, this);
                return;
            }
            _log("DirectX 11 ImGui hook initialized.");
        }
        catch
        {
            Interlocked.CompareExchange(ref s_activeHost, null, this);
            throw;
        }
    }

    public void Suspend()
    {
        lock (_renderSync)
        {
            _voiceInput?.CancelCapture();
            _controllerVoiceDetector.Reset();
            _session.Composer.Cancel();
            Interlocked.Exchange(ref _openRequested, 0);
            Interlocked.Exchange(ref _captureKeyboard, 0);
            Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 0);
            Interlocked.Exchange(ref _voiceStartRequested, 0);
            Interlocked.Exchange(ref _voiceStopRequested, 0);
            _releaseCaptureFrames = 0;
            _focusInputNextFrame = false;
            _statusText = null;
            PublishComposerState();
            if (_initialized)
                ImguiHook.Disable();
        }
    }

    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
            return;

        Suspend();
        Volatile.Write(ref _initialized, false);
        Interlocked.CompareExchange(ref s_activeHost, null, this);
    }

    public void Resume()
    {
        if (Volatile.Read(ref _shutdown) == 0 && _initialized)
            ImguiHook.Enable();
    }

    private void Render()
    {
        lock (_renderSync)
        {
            if (Volatile.Read(ref _shutdown) != 0)
                return;

            RenderCore();
        }
    }

    private void RenderCore()
    {
        _session.DrainIncoming();
        var configuration = _getConfiguration();
        _controllerVoiceDetector.Poll(
            _xInputStateSource,
            IsVoiceInputEnabled(),
            TryRequestVoiceCapture,
            RequestVoiceCaptureEnd);

        if (!configuration.EnableOverlay)
        {
            if (_voiceInput?.State is VoiceRecognitionState.Recording or VoiceRecognitionState.Transcribing)
                _voiceInput.CancelCapture();
            _session.Composer.Cancel();
            Interlocked.Exchange(ref _openRequested, 0);
            Interlocked.Exchange(ref _captureKeyboard, 0);
            PublishComposerState();
            return;
        }

        if (!configuration.EnableVoiceInput &&
            _voiceInput?.State is VoiceRecognitionState.Recording or VoiceRecognitionState.Transcribing)
        {
            _voiceInput.CancelCapture();
        }

        ProcessVoiceInput();
        PublishComposerState();

        if (_releaseCaptureFrames > 0 && --_releaseCaptureFrames == 0 && !_session.Composer.IsOpen)
            Interlocked.Exchange(ref _captureKeyboard, 0);

        var openedThisFrame = Interlocked.Exchange(ref _openRequested, 0) != 0;
        if (openedThisFrame)
        {
            _session.Composer.OpenKeyboard();
            PublishComposerState();
            SyncInputBufferFromDraft();
            _focusInputNextFrame = true;
            _statusText = _session.TransportStatusText;
        }

        if (_session.Composer.IsOpen && ImGui.IsKeyPressed((int)ImGuiKey.Escape, false))
        {
            if (_voiceInput?.State is VoiceRecognitionState.Recording or VoiceRecognitionState.Transcribing)
                _voiceInput.CancelCapture();
            else
                _session.Composer.Cancel();
            _releaseCaptureFrames = 2;
            _statusText = null;
            PublishComposerState();
        }

        DrawChatWindow(configuration, openedThisFrame);
        PublishComposerState();
    }

    private void DrawChatWindow(Config configuration, bool openedThisFrame)
    {
        var viewport = ImGui.GetMainViewport();
        var workPosition = viewport.WorkPos;
        var workSize = viewport.WorkSize;
        var width = Math.Clamp(configuration.OverlayWidth, 320, 1_200);
        var height = Math.Clamp(configuration.OverlayHeight, 160, 800);
        var x = workPosition.X + 24.0f;
        var y = workPosition.Y + Math.Max(0.0f, workSize.Y - height - 24.0f);

        using var position = CreateVector2(x, y);
        using var size = CreateVector2(width, height);
        using var pivot = CreateVector2(0.0f, 0.0f);
        ImGui.SetNextWindowPos(position, (int)ImGuiCond.Always, pivot);
        ImGui.SetNextWindowSize(size, (int)ImGuiCond.Always);

        var composerOpen = _session.Composer.IsOpen;
        var opacity = composerOpen
            ? Math.Clamp((float)configuration.BackgroundOpacity, 0.0f, 1.0f)
            : Math.Clamp((float)configuration.BackgroundOpacity * 0.45f, 0.0f, 1.0f);
        ImGui.SetNextWindowBgAlpha(opacity);

        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoResize;
        if (!composerOpen)
            flags |= ImGuiWindowFlags.NoInputs;

        if (!ImGui.Begin("GBFR Chat##GBFRChatOverlay", ref _windowOpen, (int)flags))
        {
            ImGui.End();
            return;
        }

        DrawHistory(composerOpen);
        if (composerOpen)
            DrawComposer(openedThisFrame);

        ImGui.End();
    }

    private void DrawHistory(bool composerOpen)
    {
        var childHeight = composerOpen ? -58.0f : 0.0f;
        using var childSize = CreateVector2(0.0f, childHeight);
        if (ImGui.BeginChildStr("##GBFRChatHistory", childSize, false, (int)ImGuiWindowFlags.NoBackground))
        {
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
        ImGui.EndChild();
    }

    private unsafe void DrawComposer(bool openedThisFrame)
    {
        ImGui.Separator();
        if (_session.Composer.Mode is ChatInputMode.VoiceRecording)
        {
            ImGui.TextWrapped(_voiceInput?.StatusText ?? "Voice input is unavailable.");
            return;
        }

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

    private bool ShouldCaptureWindowMessage(uint message, nint wParam)
    {
        if (!_getConfiguration().EnableOverlay)
            return false;

        var composerOpen = Volatile.Read(ref _composerOpenSnapshot) != 0;
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

    private void ProcessVoiceInput()
    {
        if (_voiceInput is null)
        {
            Interlocked.Exchange(ref _voiceStartRequested, 0);
            Interlocked.Exchange(ref _voiceStopRequested, 0);
            return;
        }

        if (Interlocked.Exchange(ref _voiceStartRequested, 0) != 0)
        {
            if (_voiceInput.TryBeginCapture())
            {
                Interlocked.Exchange(ref _captureKeyboard, 0);
                _statusText = _voiceInput.StatusText;
            }
            else
            {
                _statusText = _voiceInput.StatusText;
            }
        }

        if (Interlocked.Exchange(ref _voiceStopRequested, 0) != 0 && _voiceInput.TryEndCapture())
            _statusText = _voiceInput.StatusText;

        var update = _voiceInput.Drain();
        if (update.DraftChanged)
        {
            SyncInputBufferFromDraft();
            Interlocked.Exchange(ref _captureKeyboard, 1);
            _focusInputNextFrame = true;
            _statusText = _voiceInput.StatusText;
        }
        else if (update.StateChanged && _session.Composer.IsOpen)
        {
            _statusText = _voiceInput.StatusText;
        }
    }

    private void SyncInputBufferFromDraft()
    {
        Array.Clear(_inputBuffer);
        var bytes = Encoding.UTF8.GetBytes(_session.Composer.Draft);
        var count = Math.Min(bytes.Length, _inputBuffer.Length - 1);
        bytes.AsSpan(0, count).CopyTo(_inputBuffer);
    }

    private string ReadInputBuffer()
    {
        var length = Array.IndexOf(_inputBuffer, (byte)0);
        if (length < 0)
            length = _inputBuffer.Length;
        return Encoding.UTF8.GetString(_inputBuffer, 0, length);
    }

    private void PublishComposerState() =>
        Volatile.Write(ref _composerOpenSnapshot, _session.Composer.IsOpen ? 1 : 0);

    private unsafe void ConfigureFont()
    {
        var fontDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var candidates = new[] { "msyh.ttc", "msyhbd.ttc", "simhei.ttf" };
        var fontPath = candidates
            .Select(fileName => Path.Combine(fontDirectory, fileName))
            .FirstOrDefault(File.Exists);

        if (fontPath is null)
        {
            _log("No known CJK system font was found; using the default ImGui font.");
            return;
        }

        var fonts = ImguiHook.IO.Fonts;
        var glyphRanges = ImGui.ImFontAtlasGetGlyphRangesChineseFull(fonts);
        ImGui.ImFontAtlasAddFontFromFileTTF(fonts, fontPath, 18.0f, null!, ref glyphRanges[0]);
        _log($"Loaded CJK font: {fontPath}");
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
        ImGui.ImplWin32_WndProcHandler((void*)hWnd, message, wParam, lParam);
        if (s_activeHost?.ShouldCaptureWindowMessage(message, wParam) is true)
            return nint.Zero;

        return WndProcHook.Instance.Hook.OriginalFunction.Value.Invoke(hWnd, message, wParam, lParam);
    }
}
