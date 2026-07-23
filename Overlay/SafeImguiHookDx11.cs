// Adapted from Sewer56/Reloaded.Imgui.Hook, commit
// c3a42c84536c0a8480bc5667cb891afae274f5a7. See the repository's
// THIRD_PARTY_NOTICES.md and licenses/Reloaded.Imgui.Hook-LICENSE.md.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DearImguiSharp;
using Reloaded.Hooks.Definitions;
using Reloaded.Imgui.Hook;
using Reloaded.Imgui.Hook.DirectX.Definitions;
using Reloaded.Imgui.Hook.DirectX.Hooks;
using Reloaded.Imgui.Hook.Implementations;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace GBFR.ChatOverlay.Overlay;

/// <summary>
/// Direct3D 11 ImGui backend with a guarded native callback boundary.
/// </summary>
/// <remarks>
/// The upstream backend lets SharpDX exceptions escape from ResizeBuffers and
/// rebuilds its render target even when the game's ResizeBuffers call failed.
/// An exception escaping an unmanaged hook callback can terminate the game.
/// This implementation forwards the game's call once on every recoverable
/// path, rebuilds only after a successful HRESULT, and disables only overlay
/// rendering if its own graphics work fails.
/// </remarks>
internal sealed unsafe class SafeImguiHookDx11 : IImguiHook
{
    private static readonly nint EFail = unchecked((nint)(int)0x80004005);

    private static readonly string[] SupportedDlls =
    [
        "d3d11.dll",
        "d3d11_1.dll",
        "d3d11_2.dll",
        "d3d11_3.dll",
        "d3d11_4.dll",
    ];

    [ThreadStatic]
    private static bool s_presentRecursionLock;

    [ThreadStatic]
    private static bool s_resizeRecursionLock;

    private static SafeImguiHookDx11? s_instance;

    private readonly Action<string> _log;
    private readonly object _graphicsSync = new();
    private IHook<DX11Hook.Present>? _presentHook;
    private IHook<DX11Hook.ResizeBuffers>? _resizeBuffersHook;
    private RenderTargetView? _renderTargetView;
    private bool _backendInitialized;
    private int _renderingDisabled;
    private int _failureLogged;
    private int _disposed;

    public SafeImguiHookDx11(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsApiSupported()
    {
        foreach (var dll in SupportedDlls)
        {
            if (GetModuleHandle(dll) != nint.Zero)
                return true;
        }

        return false;
    }

    public void Initialize()
    {
        var presentAddress = (long)DX11Hook.DXGIVTable[(int)IDXGISwapChain.Present].FunctionPointer;
        var resizeAddress = (long)DX11Hook.DXGIVTable[(int)IDXGISwapChain.ResizeBuffers].FunctionPointer;

        _presentHook = SDK.Hooks.CreateHook<DX11Hook.Present>(
            typeof(SafeImguiHookDx11),
            nameof(PresentImplStatic),
            presentAddress);
        _resizeBuffersHook = SDK.Hooks.CreateHook<DX11Hook.ResizeBuffers>(
            typeof(SafeImguiHookDx11),
            nameof(ResizeBuffersImplStatic),
            resizeAddress);

        s_instance = this;
        try
        {
            _presentHook.Activate();
            _resizeBuffersHook.Activate();
        }
        catch
        {
            _resizeBuffersHook.Disable();
            _presentHook.Disable();
            if (ReferenceEquals(s_instance, this))
                s_instance = null;
            throw;
        }

        Log("DX11 safety backend attached (guarded Present/ResizeBuffers callbacks).");
    }

    public void Disable()
    {
        _presentHook?.Disable();
        _resizeBuffersHook?.Disable();
    }

    public void Enable()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _presentHook?.Enable();
        _resizeBuffersHook?.Enable();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Disable();

        lock (_graphicsSync)
        {
            try
            {
                Interlocked.Exchange(ref _renderTargetView, null)?.Dispose();
            }
            catch (Exception exception)
            {
                Log($"DX11 render-target cleanup failed: {Describe(exception)}");
            }

            if (_backendInitialized)
            {
                try
                {
                    ImGui.ImGuiImplDX11Shutdown();
                }
                catch (Exception exception)
                {
                    Log($"DX11 ImGui shutdown failed: {Describe(exception)}");
                }
            }
        }

        if (ReferenceEquals(s_instance, this))
            s_instance = null;

        GC.SuppressFinalize(this);
    }

    internal static bool HResultSucceeded(nint result) => unchecked((int)result) >= 0;

    private nint ResizeBuffersImpl(
        nint swapChainPointer,
        uint bufferCount,
        uint width,
        uint height,
        Format newFormat,
        uint swapChainFlags)
    {
        if (s_resizeRecursionLock || Volatile.Read(ref _renderingDisabled) != 0)
        {
            return InvokeOriginalResizeSafely(
                swapChainPointer,
                bufferCount,
                width,
                height,
                newFormat,
                swapChainFlags);
        }

        s_resizeRecursionLock = true;
        var originalStarted = false;
        var originalResult = EFail;
        var stage = "inspecting the swap chain";

        try
        {
            lock (_graphicsSync)
            {
                using var swapChain = AcquireSwapChain(swapChainPointer);
                var windowHandle = swapChain.Description.OutputHandle;
                if (!ImguiHook.CheckWindowHandle(windowHandle))
                {
                    return InvokeOriginalResizeSafely(
                        swapChainPointer,
                        bufferCount,
                        width,
                        height,
                        newFormat,
                        swapChainFlags);
                }

                stage = "invalidating ImGui device objects";
                PreResizeBuffers();

                stage = "calling the game's ResizeBuffers";
                originalStarted = true;
                originalResult = InvokeOriginalResize(
                    swapChainPointer,
                    bufferCount,
                    width,
                    height,
                    newFormat,
                    swapChainFlags);

                if (!HResultSucceeded(originalResult))
                {
                    DisableRendering(
                        $"game ResizeBuffers returned {FormatHResult(originalResult)}; " +
                        "skipped ImGui device-object rebuild");
                    return originalResult;
                }

                stage = "rebuilding ImGui device objects";
                PostResizeBuffers(swapChain);
                return originalResult;
            }
        }
        catch (Exception exception)
        {
            DisableRendering(
                $"ResizeBuffers failed while {stage} " +
                $"({width}x{height}, buffers={bufferCount}, format={newFormat}, flags=0x{swapChainFlags:X8})",
                exception);

            // Once the native function was entered it must never be called a
            // second time, even if a managed wrapper reported a failure.
            if (originalStarted)
                return originalResult;

            return InvokeOriginalResizeSafely(
                swapChainPointer,
                bufferCount,
                width,
                height,
                newFormat,
                swapChainFlags);
        }
        finally
        {
            s_resizeRecursionLock = false;
        }
    }

    private void PreResizeBuffers()
    {
        Interlocked.Exchange(ref _renderTargetView, null)?.Dispose();
        if (_backendInitialized)
            ImGui.ImGuiImplDX11InvalidateDeviceObjects();
    }

    private void PostResizeBuffers(SwapChain swapChain)
    {
        if (!_backendInitialized)
            return;

        ImGui.ImGuiImplDX11CreateDeviceObjects();
        using var device = swapChain.GetDevice<Device>();
        using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
        var replacement = new RenderTargetView(device, backBuffer);
        Interlocked.Exchange(ref _renderTargetView, replacement)?.Dispose();
    }

    private nint PresentImpl(nint swapChainPointer, int syncInterval, PresentFlags flags)
    {
        if (s_presentRecursionLock || Volatile.Read(ref _renderingDisabled) != 0)
            return InvokeOriginalPresentSafely(swapChainPointer, syncInterval, flags);

        s_presentRecursionLock = true;
        var originalStarted = false;
        var originalResult = EFail;
        var stage = "inspecting the swap chain";

        try
        {
            lock (_graphicsSync)
            {
                using var swapChain = AcquireSwapChain(swapChainPointer);
                var windowHandle = swapChain.Description.OutputHandle;
                if (!ImguiHook.CheckWindowHandle(windowHandle))
                    return InvokeOriginalPresentSafely(swapChainPointer, syncInterval, flags);

                stage = "obtaining the D3D11 device";
                using var device = swapChain.GetDevice<Device>();
                if (!_backendInitialized)
                {
                    stage = "initializing the ImGui D3D11 backend";
                    ImguiHook.InitializeWithHandle(windowHandle);
                    ImGui.ImGuiImplDX11Init(
                        (void*)device.NativePointer,
                        (void*)device.ImmediateContext.NativePointer);
                    _backendInitialized = true;

                    using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
                    _renderTargetView = new RenderTargetView(device, backBuffer);
                }

                stage = "rendering the ImGui frame";
                ImGui.ImGuiImplDX11NewFrame();
                ImguiHook.NewFrame();
                device.ImmediateContext.OutputMerger.SetRenderTargets(_renderTargetView);
                using var drawData = ImGui.GetDrawData();
                ImGui.ImGuiImplDX11RenderDrawData(drawData);

                stage = "calling the game's Present";
                originalStarted = true;
                originalResult = InvokeOriginalPresent(swapChainPointer, syncInterval, flags);
                return originalResult;
            }
        }
        catch (Exception exception)
        {
            DisableRendering($"Present failed while {stage}", exception);

            if (originalStarted)
                return originalResult;

            return InvokeOriginalPresentSafely(swapChainPointer, syncInterval, flags);
        }
        finally
        {
            s_presentRecursionLock = false;
        }
    }

    private nint InvokeOriginalResize(
        nint swapChainPointer,
        uint bufferCount,
        uint width,
        uint height,
        Format newFormat,
        uint swapChainFlags) =>
        _resizeBuffersHook?.OriginalFunction.Value.Invoke(
            swapChainPointer,
            bufferCount,
            width,
            height,
            newFormat,
            swapChainFlags) ?? EFail;

    private nint InvokeOriginalResizeSafely(
        nint swapChainPointer,
        uint bufferCount,
        uint width,
        uint height,
        Format newFormat,
        uint swapChainFlags)
    {
        try
        {
            return InvokeOriginalResize(
                swapChainPointer,
                bufferCount,
                width,
                height,
                newFormat,
                swapChainFlags);
        }
        catch (Exception exception)
        {
            DisableRendering("original ResizeBuffers invocation failed", exception);
            return EFail;
        }
    }

    private nint InvokeOriginalPresent(nint swapChainPointer, int syncInterval, PresentFlags flags) =>
        _presentHook?.OriginalFunction.Value.Invoke(swapChainPointer, syncInterval, flags) ?? EFail;

    private nint InvokeOriginalPresentSafely(nint swapChainPointer, int syncInterval, PresentFlags flags)
    {
        try
        {
            return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);
        }
        catch (Exception exception)
        {
            DisableRendering("original Present invocation failed", exception);
            return EFail;
        }
    }

    private static SwapChain AcquireSwapChain(nint swapChainPointer)
    {
        // SharpDX's IntPtr constructor borrows the pointer without AddRef, while
        // Dispose calls Release. Take a matching reference so the wrapper can be
        // deterministically disposed without releasing the game's own reference.
        Marshal.AddRef(swapChainPointer);
        try
        {
            return new SwapChain(swapChainPointer);
        }
        catch
        {
            Marshal.Release(swapChainPointer);
            throw;
        }
    }

    private void DisableRendering(string reason, Exception? exception = null)
    {
        Interlocked.Exchange(ref _renderingDisabled, 1);
        if (Interlocked.Exchange(ref _failureLogged, 1) != 0)
            return;

        var detail = exception is null ? reason : $"{reason}: {Describe(exception)}";
        Log(
            $"DX11 overlay disabled after a graphics error; game rendering will continue. {detail}. " +
            "Please send this complete log to the mod author.");
    }

    private void Log(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // A diagnostic logger must never break a native graphics callback.
        }
    }

    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name} (0x{unchecked((uint)exception.HResult):X8}): {exception.Message}";

    private static string FormatHResult(nint result) =>
        $"0x{unchecked((uint)(int)result):X8}";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string moduleName);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint ResizeBuffersImplStatic(
        nint swapChainPointer,
        uint bufferCount,
        uint width,
        uint height,
        Format newFormat,
        uint swapChainFlags)
    {
        var instance = s_instance;
        if (instance is null)
            return EFail;

        try
        {
            return instance.ResizeBuffersImpl(
                swapChainPointer,
                bufferCount,
                width,
                height,
                newFormat,
                swapChainFlags);
        }
        catch (Exception exception)
        {
            instance.DisableRendering("unhandled ResizeBuffers callback failure", exception);
            return EFail;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint PresentImplStatic(nint swapChainPointer, int syncInterval, PresentFlags flags)
    {
        var instance = s_instance;
        if (instance is null)
            return EFail;

        try
        {
            return instance.PresentImpl(swapChainPointer, syncInterval, flags);
        }
        catch (Exception exception)
        {
            instance.DisableRendering("unhandled Present callback failure", exception);
            return EFail;
        }
    }
}
