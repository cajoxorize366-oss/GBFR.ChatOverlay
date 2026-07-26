using System.IO;
using System.Runtime.InteropServices;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class DxgiPresentBridgeTests
{
    public DxgiPresentBridgeTests()
    {
        Assert.True(Environment.Is64BitProcess, "The native Present bridge is x64-only.");
        Assert.True(
            File.Exists(Path.Combine(AppContext.BaseDirectory, DxgiPresentBridge.LibraryName)),
            "The native Present bridge was not copied to the test output.");
        DxgiPresentBridge.Configure(AppContext.BaseDirectory);
    }

    [Fact]
    public void NativeLibraryExportsBothPresentCompatibilityEntrypoints()
    {
        var path = Path.Combine(AppContext.BaseDirectory, DxgiPresentBridge.LibraryName);
        var handle = NativeLibrary.Load(path);
        try
        {
            Assert.True(
                NativeLibrary.TryGetExport(
                    handle,
                    "GBFRChatOverlay_InvokeOriginalPresent",
                    out _));
            Assert.True(
                NativeLibrary.TryGetExport(
                    handle,
                    "GBFRChatOverlay_ResolveHookChainTarget",
                    out _));
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public void InvokeOriginalPresentReturnsNativeHResult()
    {
        using var code = ExecutableMemory.Allocate(16);
        code.Write(0, [0xB8, 0x78, 0x56, 0x34, 0x12, 0xC3]);

        var result = DxgiPresentBridge.InvokeOriginalPresent(
            unchecked((ulong)code.Address),
            new nint(1),
            1,
            0,
            out var exceptionCode);

        Assert.Equal(unchecked((int)0x12345678), result);
        Assert.Equal(0u, exceptionCode);
    }

    [Fact]
    public void InvokeOriginalPresentContainsAccessViolation()
    {
        using var code = ExecutableMemory.Allocate(16);
        code.Write(0, [0x31, 0xC0, 0x8B, 0x00, 0xC3]);

        var result = DxgiPresentBridge.InvokeOriginalPresent(
            unchecked((ulong)code.Address),
            new nint(1),
            0,
            0,
            out var exceptionCode);

        Assert.Equal(unchecked((int)0x80004005), result);
        Assert.Equal(0xC0000005u, exceptionCode);
    }

    [Fact]
    public void ResolveHookChainTargetFollowsTwoExistingEntryJumps()
    {
        using var code = ExecutableMemory.Allocate(128);
        var first = code.Address;
        var second = first + 32;
        var target = first + 96;

        var relative = checked((int)(second - (first + 5)));
        code.Write(0, [0xE9, .. BitConverter.GetBytes(relative)]);
        code.Write(32, [0xFF, 0x25, 0, 0, 0, 0]);
        Marshal.WriteIntPtr(second + 6, target);
        code.Write(96, [0xC3, 0x90]);

        var resolved = DxgiPresentBridge.ResolveHookChainTarget(
            unchecked((ulong)first),
            16,
            out var jumpCount,
            out var status);

        Assert.Equal(unchecked((ulong)target), resolved);
        Assert.Equal(2u, jumpCount);
        Assert.Equal(DxgiPresentBridge.HookChainResolveStatus.Ok, status);
    }

    [Fact]
    public void ResolveHookChainTargetRejectsInvalidArguments()
    {
        var resolved = DxgiPresentBridge.ResolveHookChainTarget(
            0,
            16,
            out var jumpCount,
            out var status);

        Assert.Equal(0ul, resolved);
        Assert.Equal(0u, jumpCount);
        Assert.Equal(DxgiPresentBridge.HookChainResolveStatus.InvalidArgument, status);
    }

    private sealed class ExecutableMemory : IDisposable
    {
        private const uint MemCommit = 0x1000;
        private const uint MemReserve = 0x2000;
        private const uint MemRelease = 0x8000;
        private const uint PageExecuteReadWrite = 0x40;

        private nint _address;

        private ExecutableMemory(nint address)
        {
            _address = address;
        }

        internal nint Address => _address;

        internal static ExecutableMemory Allocate(nuint size)
        {
            var address = VirtualAlloc(
                nint.Zero,
                size,
                MemCommit | MemReserve,
                PageExecuteReadWrite);
            if (address == nint.Zero)
                throw new InvalidOperationException($"VirtualAlloc failed: {Marshal.GetLastWin32Error()}.");
            return new ExecutableMemory(address);
        }

        internal void Write(int offset, byte[] bytes) =>
            Marshal.Copy(bytes, 0, _address + offset, bytes.Length);

        public void Dispose()
        {
            var address = Interlocked.Exchange(ref _address, nint.Zero);
            if (address != nint.Zero)
                VirtualFree(address, 0, MemRelease);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint VirtualAlloc(
            nint address,
            nuint size,
            uint allocationType,
            uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFree(nint address, nuint size, uint freeType);
    }
}
