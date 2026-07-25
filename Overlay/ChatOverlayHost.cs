using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DearImguiSharp;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Native;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Imgui.Hook;
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
    private const uint WmKillFocus = 0x0008;
    private const uint WmActivate = 0x0006;
    private const uint WmActivateApp = 0x001C;
    private const int InputBufferSize = 2_048;

    private static ChatOverlayHost? s_activeHost;
    private static int s_hasOriginalWndProc;
    private static WndProcHook.WndProc s_originalWndProc;

    private readonly ChatSession _session;
    private readonly Func<Config> _getConfiguration;
    private readonly Func<PartyVoiceUiStatus> _getVoiceUiStatus;
    private readonly Action<string> _log;
    private readonly byte[] _inputBuffer = new byte[InputBufferSize];
    private int _openRequested;
    private int _captureKeyboard;
    private int _swallowActivationKeyUntilRelease;
    private int _wndProcFailureLogged;
    private int _renderThreadLogged;
    private int _releaseCaptureFrames;
    private bool _focusInputNextFrame;
    private bool _windowOpen = true;
    private bool _initialized;
    private long _lastRenderedSequence;
    private string? _statusText;

    internal ChatOverlayHost(
        ChatSession session,
        Func<Config> getConfiguration,
        Func<PartyVoiceUiStatus> getVoiceUiStatus,
        Action<string> log)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _getConfiguration = getConfiguration ?? throw new ArgumentNullException(nameof(getConfiguration));
        _getVoiceUiStatus = getVoiceUiStatus ?? throw new ArgumentNullException(nameof(getVoiceUiStatus));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsInitialized => Volatile.Read(ref _initialized);

    public bool TryRequestOpen()
    {
        if (!Volatile.Read(ref _initialized) ||
            !_getConfiguration().EnableOverlay ||
            _session.Composer.IsOpen)
            return false;

        Interlocked.Exchange(ref _captureKeyboard, 1);
        Interlocked.Exchange(ref _openRequested, 1);
        return true;
    }

    public bool ShouldCaptureKeyboard() =>
        _getConfiguration().EnableOverlay && Volatile.Read(ref _captureKeyboard) != 0;

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
            Implementations = new List<IImguiHook> { new CjkConfiguredDx11Hook(_log) },
        };

        try
        {
            await ImguiHook.Create(Render, options).ConfigureAwait(false);
            Volatile.Write(ref _initialized, true);
            _log("DirectX 11 ImGui hook initialized with the Extra Sigil compatibility path.");
        }
        catch
        {
            Interlocked.CompareExchange(ref s_activeHost, null, this);
            throw;
        }
    }

    public void Suspend()
    {
        _session.Composer.Cancel();
        Interlocked.Exchange(ref _openRequested, 0);
        Interlocked.Exchange(ref _captureKeyboard, 0);
        Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 0);
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
            if (!configuration.EnableOverlay)
            {
                _session.Composer.Cancel();
                Interlocked.Exchange(ref _openRequested, 0);
                Interlocked.Exchange(ref _captureKeyboard, 0);
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
                _statusText = null;
            }

            DrawChatWindow(configuration, openedThisFrame);
        }
        catch (Exception exception)
        {
            _session.Composer.Cancel();
            Interlocked.Exchange(ref _openRequested, 0);
            Interlocked.Exchange(ref _captureKeyboard, 0);
            LogSafely($"Render callback recovered from an exception: {exception}");
        }
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

        var began = ImGui.Begin("GBFR Chat##GBFRChatOverlay", ref _windowOpen, (int)flags);
        try
        {
            if (!began)
                return;

            DrawVoiceStatus();
            DrawHistory(composerOpen);
            if (composerOpen)
                DrawComposer(openedThisFrame);
        }
        finally
        {
            ImGui.End();
        }
    }

    private void DrawVoiceStatus()
    {
        var presentation = VoiceOverlayPresenter.Create(_getVoiceUiStatus());
        if (!presentation.IsVisible)
            return;

        ImGui.TextWrapped(presentation.Text);
        ImGui.Separator();
    }

    private void DrawHistory(bool composerOpen)
    {
        var childHeight = composerOpen ? -58.0f : 0.0f;
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

    private unsafe void DrawComposer(bool openedThisFrame)
    {
        ImGui.Separator();
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

        var composerOpen = _session.Composer.IsOpen;
        if (!composerOpen &&
            (message is WmKeyDown or WmSysKeyDown) &&
            wParam == VirtualKeyY)
        {
            TryRequestOpen();
            Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 1);
            return true;
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
            ImGui.ImplWin32_WndProcHandler((void*)hWnd, message, wParam, lParam);
            if (ImguiHook.Options?.IgnoreWindowUnactivate == true)
            {
                if (message == WmKillFocus)
                    return nint.Zero;
                if ((message is WmActivate or WmActivateApp) && wParam == nint.Zero)
                    return nint.Zero;
            }

            if (host?.ShouldCaptureWindowMessage(message, wParam) is true)
                return nint.Zero;

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

            return DefWindowProcW(hWnd, message, wParam, lParam);
        }
        catch (Exception exception)
        {
            host?.LogWndProcFallback(exception);
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

            return DefWindowProcW(hWnd, message, wParam, lParam);
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
                "Custom WndProc recovered through DefWindowProcW after an exception: " +
                $"{exception.GetType().Name} (0x{unchecked((uint)exception.HResult):X8}): " +
                $"{exception.Message}.");
        }
        catch
        {
            // A logger must not let an exception cross an unmanaged callback.
        }
    }

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
