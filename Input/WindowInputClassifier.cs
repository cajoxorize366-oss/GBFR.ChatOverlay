using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Input;

internal static class WindowInputClassifier
{
    internal static bool IsAlwaysCaptured(uint message)
    {
        const uint WmNcMouseFirst = 0x00A0;
        const uint WmNcMouseLast = 0x00AD;
        const uint WmKeyFirst = 0x0100;
        const uint WmKeyLast = 0x0109;
        const uint WmImeStartComposition = 0x010D;
        const uint WmImeEndComposition = 0x010E;
        const uint WmImeComposition = 0x010F;
        const uint WmMouseFirst = 0x0200;
        const uint WmMouseLast = 0x020E;
        const uint WmImeChar = 0x0286;

        return message is >= WmNcMouseFirst and <= WmNcMouseLast ||
               message is >= WmKeyFirst and <= WmKeyLast ||
               message is WmImeStartComposition or WmImeEndComposition or WmImeComposition ||
               message is >= WmMouseFirst and <= WmMouseLast ||
               message == WmImeChar;
    }

    internal static bool ShouldCapture(uint message, nint lParam)
    {
        const uint WmInput = 0x00FF;
        return IsAlwaysCaptured(message) ||
               (message == WmInput && IsKeyboardOrMouseRawInput(lParam));
    }

    private static bool IsKeyboardOrMouseRawInput(nint rawInputHandle)
    {
        const uint RidHeader = 0x10000005;
        if (rawInputHandle == nint.Zero)
            return false;

        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        var dataSize = headerSize;
        var copied = GetRawInputData(
            rawInputHandle,
            RidHeader,
            out var header,
            ref dataSize,
            headerSize);
        return copied != uint.MaxValue && copied >= headerSize && header.Type <= 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        internal uint Type;
        internal uint Size;
        internal nint Device;
        internal nint WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint rawInputHandle,
        uint command,
        out RawInputHeader data,
        ref uint dataSize,
        uint headerSize);
}
