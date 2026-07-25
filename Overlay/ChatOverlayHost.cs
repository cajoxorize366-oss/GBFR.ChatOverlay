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
    private readonly Func<bool> _isOnlineRoomActive;
    private readonly Action _onOnlineRoomUnavailable;
    private readonly Func<PartyVoiceUiStatus> _getVoiceUiStatus;
    private readonly Action<string> _log;
    private readonly byte[] _inputBuffer = new byte[InputBufferSize];
    private int _openRequested;
    private int _captureKeyboard;
    private int _swallowActivationKeyUntilRelease;
    private int _wndProcFailureLogged;
    private int _imeCompatibilityLogged;
    private int _imeDecodeFailureLogged;
    private int _renderThreadLogged;
    private int _onlineRoomGateFailureLogged;
    private int _onlineRoomWasInactive = 1;
    private int _releaseCaptureFrames;
    private int _pendingAnsiLeadByte = -1;
    private int _attachedDefaultImeContext;
    private nint _windowHandle;
    private bool _focusInputNextFrame;
    private bool _windowOpen = true;
    private bool _initialized;
    private long _lastRenderedSequence;
    private string? _statusText;

    internal ChatOverlayHost(
        ChatSession session,
        Func<Config> getConfiguration,
        Func<bool> isOnlineRoomActive,
        Action onOnlineRoomUnavailable,
        Func<PartyVoiceUiStatus> getVoiceUiStatus,
        Action<string> log)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _getConfiguration = getConfiguration ?? throw new ArgumentNullException(nameof(getConfiguration));
        _isOnlineRoomActive = isOnlineRoomActive ?? throw new ArgumentNullException(nameof(isOnlineRoomActive));
        _onOnlineRoomUnavailable = onOnlineRoomUnavailable ??
            throw new ArgumentNullException(nameof(onOnlineRoomUnavailable));
        _getVoiceUiStatus = getVoiceUiStatus ?? throw new ArgumentNullException(nameof(getVoiceUiStatus));
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
        Interlocked.Exchange(ref _openRequested, 1);
        return true;
    }

    public bool ShouldCaptureKeyboard() =>
        _getConfiguration().EnableOverlay &&
        IsOnlineRoomActive() &&
        Volatile.Read(ref _captureKeyboard) != 0;

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
        Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
        DetachDefaultImeContextIfOwned();
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
            var onlineRoomActive = IsOnlineRoomActive();
            var previousOnlineRoomInactive = Interlocked.Exchange(
                ref _onlineRoomWasInactive,
                onlineRoomActive ? 0 : 1);
            if (!onlineRoomActive && previousOnlineRoomInactive == 0)
                NotifyOnlineRoomUnavailable();
            if (!configuration.EnableOverlay || !onlineRoomActive)
            {
                ResetInteractionState();
                return;
            }

            if (previousOnlineRoomInactive != 0)
            {
                LogSafely(
                    "Relink online Party room became active; overlay rendering and Y/U/I hotkeys are now enabled.");
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
                DetachDefaultImeContextIfOwned();
                _statusText = null;
            }

            DrawChatWindow(configuration, openedThisFrame);
        }
        catch (Exception exception)
        {
            ResetInteractionState();
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

        UpdateImeCandidatePlacement();

        _session.Composer.SetDraft(ReadInputBuffer());
        if (submitRequested)
        {
            var result = _session.SendDraft();
            if (result.Succeeded)
            {
                Array.Clear(_inputBuffer);
                _releaseCaptureFrames = 2;
                Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
                DetachDefaultImeContextIfOwned();
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

    private static void AddUtf8Input(string text)
    {
        if (!string.IsNullOrEmpty(text))
            ImGui.ImGuiIO_AddInputCharactersUTF8(ImguiHook.IO, text);
    }

    private void UpdateImeCandidatePlacement()
    {
        if (!ImGui.IsItemActive())
            return;

        var windowHandle = Volatile.Read(ref _windowHandle);
        if (windowHandle == nint.Zero)
            return;

        using var itemMinimum = new ImVec2();
        using var itemMaximum = new ImVec2();
        ImGui.GetItemRectMin(itemMinimum);
        ImGui.GetItemRectMax(itemMaximum);
        var placementUpdated = Win32ImeCompatibility.UpdateCandidatePlacement(
            windowHandle,
            itemMinimum.X,
            itemMinimum.Y,
            itemMaximum.X,
            itemMaximum.Y,
            out var attachedDefaultContext);
        if (attachedDefaultContext)
            Interlocked.Exchange(ref _attachedDefaultImeContext, 1);

        if (placementUpdated)
        {
            LogImeCompatibility(
                windowHandle,
                Win32ImeCompatibility.IsUnicodeWindow(windowHandle)
                    ? 0
                    : Win32ImeCompatibility.GetActiveInputCodePage());
        }
    }

    private void DetachDefaultImeContextIfOwned()
    {
        if (Interlocked.Exchange(ref _attachedDefaultImeContext, 0) == 0)
            return;

        var windowHandle = Volatile.Read(ref _windowHandle);
        if (windowHandle != nint.Zero)
            Win32ImeCompatibility.DetachDefaultContext(windowHandle);
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

    private void ResetInteractionState()
    {
        _session.Composer.Cancel();
        Interlocked.Exchange(ref _openRequested, 0);
        Interlocked.Exchange(ref _captureKeyboard, 0);
        Interlocked.Exchange(ref _swallowActivationKeyUntilRelease, 0);
        Interlocked.Exchange(ref _pendingAnsiLeadByte, -1);
        DetachDefaultImeContextIfOwned();
        _releaseCaptureFrames = 0;
        _focusInputNextFrame = false;
        _statusText = null;
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
            if (host is not null)
            {
                Volatile.Write(ref host._windowHandle, hWnd);

                // Some third-party IME candidate windows transiently take OS
                // focus. Preserve ImGui's active InputText while that UI is up.
                if (host.ShouldIgnoreUnactivateBeforeBackend(message, wParam))
                    return nint.Zero;

                if (host.TryHandleImeCharacter(hWnd, message, wParam))
                    return nint.Zero;

                // The overlay does not paint an IME UI itself. Let the proper
                // ANSI/Unicode default window proc own composition and candidate
                // lifecycle messages instead of Relink's chat WndProc.
                if (host.ShouldRouteImeUiToDefault(message))
                    return Win32ImeCompatibility.CallDefaultWindowProc(hWnd, message, wParam, lParam);
            }

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

            return Win32ImeCompatibility.CallDefaultWindowProc(hWnd, message, wParam, lParam);
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
}
