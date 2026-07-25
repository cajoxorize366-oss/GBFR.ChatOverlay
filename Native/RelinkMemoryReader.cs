using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Native;

internal interface IRelinkMemoryReader
{
    bool TryReadPointer(nint address, out nint value);

    bool TryReadBytes(nint address, Span<byte> destination);
}

/// <summary>
/// Reads the current process through the kernel copy boundary so an unavailable or
/// transitioning Relink object fails closed instead of raising an access violation in
/// the native chat-send path.
/// </summary>
internal sealed class CurrentProcessRelinkMemoryReader : IRelinkMemoryReader
{
    private static readonly nint CurrentProcessPseudoHandle = (nint)(-1);

    public bool TryReadPointer(nint address, out nint value)
    {
        if (IntPtr.Size == sizeof(long) && TryRead(address, out long rawValue))
        {
            value = (nint)rawValue;
            return true;
        }

        if (IntPtr.Size == sizeof(int) && TryRead(address, out int rawValue32))
        {
            value = (nint)rawValue32;
            return true;
        }

        value = nint.Zero;
        return false;
    }

    public unsafe bool TryReadBytes(nint address, Span<byte> destination)
    {
        if (address == nint.Zero)
            return false;
        if (destination.IsEmpty)
            return true;

        fixed (byte* buffer = destination)
        {
            return ReadProcessMemory(
                       CurrentProcessPseudoHandle,
                       address,
                       buffer,
                       (nuint)destination.Length,
                       out var bytesRead) &&
                   bytesRead == (nuint)destination.Length;
        }
    }

    private static unsafe bool TryRead<T>(nint address, out T value)
        where T : unmanaged
    {
        value = default;
        if (address == nint.Zero)
            return false;

        T candidate = default;
        if (!ReadProcessMemory(
                CurrentProcessPseudoHandle,
                address,
                &candidate,
                (nuint)sizeof(T),
                out var bytesRead) ||
            bytesRead != (nuint)sizeof(T))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadProcessMemory(
        nint process,
        nint baseAddress,
        void* buffer,
        nuint size,
        out nuint bytesRead);
}
