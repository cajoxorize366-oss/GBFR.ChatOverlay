// This file is based on Reloaded.Imgui.Hook.Direct3D11's ImguiHookDx11.
// Copyright (c) 2020 Sewer56
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DearImguiSharp;
using GBFR.ChatOverlay.Native;
using GBFR.OverlayHub.Runtime;
using Reloaded.Hooks.Definitions;
using Reloaded.Imgui.Hook;
using Reloaded.Imgui.Hook.DirectX.Definitions;
using Reloaded.Imgui.Hook.DirectX.Hooks;
using Reloaded.Imgui.Hook.Implementations;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using static Reloaded.Imgui.Hook.Misc.Native;
using Device = SharpDX.Direct3D11.Device;

namespace GBFR.ChatOverlay.Overlay;

/// <summary>
/// Present-only DX11 ImGui backend ported from the proven Extra Sigil Slots
/// compatibility path. It chains behind existing entry jumps and never hooks
/// ResizeBuffers, so RTSS and other native Present users retain their ordering.
/// </summary>
internal sealed unsafe class RtssSafeImguiHookDx11 : IImguiHook
{
    private const uint MaxPresentHookChainJumps = 16;

    private static readonly string[] SupportedDlls =
    [
        "d3d11.dll",
        "d3d11_1.dll",
        "d3d11_2.dll",
        "d3d11_3.dll",
        "d3d11_4.dll",
    ];

    private static RtssSafeImguiHookDx11? s_instance;
    private static readonly nint FailureResult = new(unchecked((int)0x80004005));
    private static long s_fallbackOriginalPresentAddress;
    private static int s_renderLease;

    [ThreadStatic]
    private static bool s_presentRecursionLock;

    private readonly Action<string> _log;
    private readonly Action _onPermanentFailure;
    private readonly Action _presentTick;
    private readonly Func<bool> _shouldRenderFrontend;
    private readonly object _hookStateLock = new();
    private readonly ReaderWriterLockSlim _presentLifetimeLock =
        new(LockRecursionPolicy.SupportsRecursion);
    private IHook<DX11Hook.Present> _presentHook = null!;
    private long _originalPresentAddress;
    private long _initializedDevicePointer;
    private bool _initialized;
    private bool _frontendRenderedLastPresent;
    private int _presentFailureCount;
    private int _nativePresentFailureHandled;
    private int _presentStopping;
    private bool _disposed;

    internal RtssSafeImguiHookDx11(
        Action presentTick,
        Func<bool> shouldRenderFrontend,
        Action<string> log,
        Action onPermanentFailure)
    {
        _presentTick = presentTick ?? throw new ArgumentNullException(nameof(presentTick));
        _shouldRenderFrontend = shouldRenderFrontend ??
            throw new ArgumentNullException(nameof(shouldRenderFrontend));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _onPermanentFailure = onPermanentFailure ??
            throw new ArgumentNullException(nameof(onPermanentFailure));
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
        if (_disposed)
            throw new ObjectDisposedException(nameof(RtssSafeImguiHookDx11));

        var presentPointer =
            (long)DX11Hook.DXGIVTable[(int)IDXGISwapChain.Present].FunctionPointer;
        var hookTarget = DxgiPresentBridge.ResolveHookChainTarget(
            unchecked((ulong)presentPointer),
            MaxPresentHookChainJumps,
            out var existingJumpCount,
            out var resolveStatus);
        if (hookTarget == 0)
        {
            throw new InvalidOperationException(
                $"DX11 Present hook-chain resolution failed with status {resolveStatus} " +
                $"after {existingJumpCount} jump(s).");
        }

        if (existingJumpCount > 0)
        {
            TryLog(
                $"DX11 Present hook chaining followed {existingJumpCount} existing entry " +
                $"jump(s); installing at chain tail 0x{hookTarget:X}.");
        }

        s_instance = this;
        try
        {
            _presentHook = SDK.Hooks.CreateHook<DX11Hook.Present>(
                typeof(RtssSafeImguiHookDx11),
                nameof(PresentImplStatic),
                unchecked((long)hookTarget));
            Volatile.Write(ref _originalPresentAddress, _presentHook.OriginalFunctionAddress);
            Volatile.Write(
                ref s_fallbackOriginalPresentAddress,
                _presentHook.OriginalFunctionAddress);
            _presentHook.Activate();
            TryLog(
                "DX11 Present-only backend enabled with a native original-Present boundary; " +
                "frame-local render targets replace the ResizeBuffers hook.");
        }
        catch
        {
            try
            {
                _presentHook?.Disable();
            }
            catch
            {
            }

            if (ReferenceEquals(s_instance, this))
                s_instance = null;
            throw;
        }
    }

    private nint PresentImpl(nint swapChainPointer, int syncInterval, PresentFlags flags)
    {
        _presentLifetimeLock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _presentStopping) != 0 || s_presentRecursionLock)
                return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);
            if (Interlocked.CompareExchange(ref s_renderLease, 1, 0) != 0)
                return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);

            s_presentRecursionLock = true;
            try
            {
                try
                {
                    // This is a borrowed native pointer. Disposing the wrapper
                    // would release the game's swap chain, so it intentionally
                    // lives only as a non-owning managed view of the callback.
                    var swapChain = new SwapChain(swapChainPointer);
                    var windowHandle = swapChain.Description.OutputHandle;
                    if (!ImguiHook.CheckWindowHandle(windowHandle))
                        return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);

                    // Keep native/game upkeep on the established Present cadence.
                    // Only the frontend frame sleeps while no peer wants to render.
                    _presentTick();
                    var shouldRenderFrontend = _shouldRenderFrontend();
                    if (_initialized && !shouldRenderFrontend)
                    {
                        _frontendRenderedLastPresent = false;
                        return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);
                    }

                    using var device = swapChain.GetDevice<Device>();
                    var devicePointer = device.NativePointer.ToInt64();
                    if (_initialized &&
                        Volatile.Read(ref _initializedDevicePointer) != devicePointer)
                    {
                        ImGui.ImGuiImplDX11Shutdown();
                        _initialized = false;
                        Volatile.Write(ref _initializedDevicePointer, 0);
                        TryLog("DX11 device changed; the ImGui device backend was rebuilt in-place.");
                    }
                    if (!_initialized)
                    {
                        ImguiHook.InitializeWithHandle(windowHandle);
                        var wndProcHook = WndProcHook.Instance;
                        if (wndProcHook is null || wndProcHook.WindowHandle != windowHandle)
                        {
                            throw new InvalidOperationException(
                                "Reloaded.Imgui.Hook did not provide a WndProc hook for the active game window.");
                        }

                        // Reloaded.Imgui.Hook keeps WndProcHook.Instance after Destroy(),
                        // so a recovered Broker host must explicitly re-enable it.
                        wndProcHook.Enable();
                        ImGui.ImGuiImplDX11Init(
                            (void*)device.NativePointer,
                            (void*)device.ImmediateContext.NativePointer);
                        _initialized = true;
                        Volatile.Write(ref _initializedDevicePointer, devicePointer);
                    }

                    if (!shouldRenderFrontend)
                    {
                        _frontendRenderedLastPresent = false;
                        return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);
                    }

                    var frontendWakeFrame = IsFrontendWakeFrame(
                        _frontendRenderedLastPresent,
                        shouldRenderFrontend);
                    ImGui.ImGuiImplDX11NewFrame();
                    var io = ImGui.GetIO();
                    var previousInputTrickle = io.ConfigInputTrickleEventQueue;
                    var mouseResetRequested = ImGuiInputResetGate.Consume();
                    var resetFrontendInput = frontendWakeFrame || mouseResetRequested;
                    if (resetFrontendInput)
                    {
                        io.ConfigInputTrickleEventQueue = false;
                        if (frontendWakeFrame)
                            PrepareImGuiInputForFrontendWake(io, windowHandle);
                        else
                            PrepareImGuiMouseInputReset(io, windowHandle);
                    }
                    try
                    {
                        ImguiHook.NewFrame();
                        _frontendRenderedLastPresent = true;
                    }
                    finally
                    {
                        if (resetFrontendInput)
                            io.ConfigInputTrickleEventQueue = previousInputTrickle;
                    }
                    using var drawData = ImGui.GetDrawData();
                    if (drawData.CmdListsCount > 0 && drawData.TotalVtxCount > 0)
                        RenderFrame(swapChain, device, drawData);
                }
                catch (Exception exception)
                {
                    ReportFailure("Present overlay", exception);
                }

                return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);
            }
            catch (Exception exception)
            {
                ReportFailure("Present callback", exception);
                return InvokeOriginalPresent(swapChainPointer, syncInterval, flags);
            }
            finally
            {
                s_presentRecursionLock = false;
                Volatile.Write(ref s_renderLease, 0);
            }
        }
        finally
        {
            _presentLifetimeLock.ExitReadLock();
        }
    }

    private static void RenderFrame(SwapChain swapChain, Device device, ImDrawData drawData)
    {
        using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
        using var renderTarget = new RenderTargetView(device, backBuffer);
        var context = device.ImmediateContext;
        DepthStencilView? previousDepthStencil = null;
        RenderTargetView[] previousRenderTargets = [];
        var outputMergerStateCaptured = false;
        try
        {
            previousRenderTargets = context.OutputMerger.GetRenderTargets(
                8,
                out previousDepthStencil);
            outputMergerStateCaptured = true;
            context.OutputMerger.SetRenderTargets(renderTarget);
            ImGui.ImGuiImplDX11RenderDrawData(drawData);
        }
        finally
        {
            try
            {
                if (outputMergerStateCaptured)
                {
                    context.OutputMerger.SetRenderTargets(
                        previousDepthStencil,
                        previousRenderTargets);
                }
            }
            finally
            {
                foreach (var previousRenderTarget in previousRenderTargets)
                    previousRenderTarget?.Dispose();
                previousDepthStencil?.Dispose();
            }
        }
    }

    internal static bool IsFrontendWakeFrame(
        bool renderedLastPresent,
        bool shouldRenderFrontend) =>
        shouldRenderFrontend && !renderedLastPresent;

    private static void PrepareImGuiInputForFrontendWake(ImGuiIO io, nint windowHandle)
    {
        PrepareImGuiMouseInputReset(io, windowHandle);
        ImGui.ImGuiIO_ClearInputKeys(io);
        if (windowHandle != nint.Zero &&
            GetCursorPos(out var cursorPosition) &&
            ScreenToClient(windowHandle, ref cursorPosition))
        {
            ImGui.ImGuiIO_AddMousePosEvent(
                io,
                cursorPosition.X,
                cursorPosition.Y);
        }
        ImGui.ClearActiveID();
    }

    private static void PrepareImGuiMouseInputReset(ImGuiIO io, nint windowHandle)
    {
        ImGuiInputResetGate.ResetWin32MouseButtons(windowHandle);
        for (var button = 0; button < 5; button++)
            ImGui.ImGuiIO_AddMouseButtonEvent(io, button, false);
    }

    private nint InvokeOriginalPresent(
        nint swapChainPointer,
        int syncInterval,
        PresentFlags flags)
    {
        try
        {
            var originalPresentAddress = Volatile.Read(ref _originalPresentAddress);
            if (originalPresentAddress == 0)
                return FailureResult;

            var result = DxgiPresentBridge.InvokeOriginalPresent(
                unchecked((ulong)originalPresentAddress),
                swapChainPointer,
                syncInterval,
                unchecked((uint)flags),
                out var exceptionCode);
            if (exceptionCode != 0)
                HandleNativePresentFailure(exceptionCode);
            return new nint(result);
        }
        catch (Exception exception)
        {
            ReportFailure("original Present", exception);
            return FailureResult;
        }
    }

    private void HandleNativePresentFailure(uint exceptionCode)
    {
        if (Interlocked.Exchange(ref _nativePresentFailureHandled, 1) != 0)
            return;

        Volatile.Write(ref _presentStopping, 1);
        TryLog(
            $"DX11 original Present native boundary caught SEH 0x{exceptionCode:X8}; " +
            "the overlay hook will be disabled off the graphics callback thread.");
        if (!ThreadPool.QueueUserWorkItem(
                static state => ((RtssSafeImguiHookDx11)state!).DisableAfterNativePresentFailure(),
                this))
        {
            TryLog("DX11 could not queue the native-Present failure fallback.");
            NotifyPermanentFailure();
        }
    }

    private void DisableAfterNativePresentFailure()
    {
        try
        {
            Disable();
            TryLog(
                "DX11 overlay hook disabled after a native Present failure; " +
                "later frames now use the game's Present directly.");
        }
        catch (Exception exception)
        {
            ReportFailure("hook disable after native Present failure", exception);
        }
        finally
        {
            NotifyPermanentFailure();
        }
    }

    private void NotifyPermanentFailure()
    {
        try
        {
            _onPermanentFailure();
        }
        catch (Exception exception)
        {
            ReportFailure("permanent-failure callback", exception);
        }
    }

    private void ReportFailure(string stage, Exception exception)
    {
        var failureNumber = Interlocked.Increment(ref _presentFailureCount);
        if (failureNumber > 3 && (failureNumber & (failureNumber - 1)) != 0)
            return;
        TryLog(
            $"DX11 {stage} failed (occurrence {failureNumber}); " +
            $"the game callback was contained instead of crashing: {exception}");
    }

    private void TryLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Logging must never make an unmanaged graphics callback fail.
        }
    }

    public void Disable()
    {
        lock (_hookStateLock)
            _presentHook?.Disable();
    }

    public void Enable()
    {
        lock (_hookStateLock)
        {
            if (_disposed || Volatile.Read(ref _nativePresentFailureHandled) != 0)
                return;
            _presentHook?.Enable();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Volatile.Write(ref _presentStopping, 1);

        try
        {
            Disable();
        }
        catch (Exception exception)
        {
            ReportFailure("hook disable", exception);
        }

        var lifetimeLockEntered = false;
        try
        {
            if (!s_presentRecursionLock)
            {
                lifetimeLockEntered = _presentLifetimeLock.TryEnterWriteLock(
                    TimeSpan.FromSeconds(2));
            }

            if (!lifetimeLockEntered)
            {
                TryLog(
                    "DX11 shutdown skipped graphics resource release because a Present callback " +
                    "was still active; process teardown will reclaim those resources.");
            }
            else if (_initialized)
            {
                ImGui.ImGuiImplDX11Shutdown();
                _initialized = false;
                Volatile.Write(ref _initializedDevicePointer, 0);
            }
        }
        catch (Exception exception)
        {
            ReportFailure("shutdown", exception);
        }
        finally
        {
            if (ReferenceEquals(s_instance, this))
                s_instance = null;
            if (lifetimeLockEntered)
                _presentLifetimeLock.ExitWriteLock();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint PresentImplStatic(
        nint swapChainPointer,
        int syncInterval,
        PresentFlags flags)
    {
        try
        {
            var instance = s_instance;
            return instance is null
                ? InvokeFallbackOriginalPresent(swapChainPointer, syncInterval, flags)
                : instance.PresentImpl(swapChainPointer, syncInterval, flags);
        }
        catch (Exception exception)
        {
            var instance = s_instance;
            instance?.ReportFailure("Present unmanaged boundary", exception);
            return InvokeFallbackOriginalPresent(swapChainPointer, syncInterval, flags);
        }
    }

    private static nint InvokeFallbackOriginalPresent(
        nint swapChainPointer,
        int syncInterval,
        PresentFlags flags)
    {
        try
        {
            var address = Volatile.Read(ref s_fallbackOriginalPresentAddress);
            if (address == 0)
                return FailureResult;
            var result = DxgiPresentBridge.InvokeOriginalPresent(
                unchecked((ulong)address),
                swapChainPointer,
                syncInterval,
                unchecked((uint)flags),
                out _);
            return new nint(result);
        }
        catch
        {
            return FailureResult;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint window, ref NativePoint point);
}
