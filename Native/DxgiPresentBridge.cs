using System.Reflection;
using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Native;

/// <summary>
/// Binds the small native boundary used to follow pre-existing DXGI Present
/// entry jumps and to contain access violations raised by the next hook.
/// </summary>
internal static class DxgiPresentBridge
{
    internal const string LibraryName = "GBFR.ChatOverlay.Native.dll";

    private static readonly object ResolverLock = new();
    private static string? _libraryPath;
    private static nint _libraryHandle;
    private static int _resolverConfigured;

    internal enum HookChainResolveStatus : uint
    {
        Ok = 0,
        InvalidArgument = 1,
        Unreadable = 2,
        NonExecutable = 3,
        Cycle = 4,
        DepthExceeded = 5,
        UnsupportedJump = 6,
    }

    internal static void Configure(string modDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
        var path = Path.GetFullPath(Path.Combine(modDirectory, LibraryName));
        lock (ResolverLock)
        {
            if (_libraryPath is not null &&
                !string.Equals(_libraryPath, path, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Native Present bridge was already bound to a different path: {_libraryPath}");
            }

            _libraryPath = path;
            if (Interlocked.Exchange(ref _resolverConfigured, 1) == 0)
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(DxgiPresentBridge).Assembly,
                    ResolveLibrary);
            }
        }
    }

    internal static int InvokeOriginalPresent(
        ulong originalFunctionAddress,
        nint swapChain,
        int syncInterval,
        uint presentFlags,
        out uint exceptionCode) =>
        GBFRChatOverlay_InvokeOriginalPresent(
            originalFunctionAddress,
            swapChain,
            unchecked((uint)syncInterval),
            presentFlags,
            out exceptionCode);

    internal static ulong ResolveHookChainTarget(
        ulong functionAddress,
        uint maxJumpCount,
        out uint jumpCount,
        out HookChainResolveStatus status) =>
        GBFRChatOverlay_ResolveHookChainTarget(
            functionAddress,
            maxJumpCount,
            out jumpCount,
            out status);

    private static nint ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase))
            return nint.Zero;

        lock (ResolverLock)
        {
            if (_libraryHandle != nint.Zero)
                return _libraryHandle;
            if (_libraryPath is null || !File.Exists(_libraryPath))
                throw new DllNotFoundException($"Native Present bridge not found: {_libraryPath}");

            _libraryHandle = NativeLibrary.Load(_libraryPath);
            return _libraryHandle;
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GBFRChatOverlay_InvokeOriginalPresent(
        ulong originalFunctionAddress,
        nint swapChain,
        uint syncInterval,
        uint presentFlags,
        out uint exceptionCode);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern ulong GBFRChatOverlay_ResolveHookChainTarget(
        ulong functionAddress,
        uint maxJumpCount,
        out uint jumpCount,
        out HookChainResolveStatus status);
}
