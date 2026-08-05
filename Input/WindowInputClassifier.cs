using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Input;

internal static class WindowInputClassifier
{
    internal static bool IsAlwaysCaptured(uint message, InputCaptureDevices devices)
    {
        const uint WmNcMouseFirst = 0x00A0;
        const uint WmNcMouseLast = 0x00AD;
        const uint WmKeyDown = 0x0100;
        const uint WmKeyUp = 0x0101;
        const uint WmChar = 0x0102;
        const uint WmDeadChar = 0x0103;
        const uint WmSysKeyDown = 0x0104;
        const uint WmSysKeyUp = 0x0105;
        const uint WmSysChar = 0x0106;
        const uint WmSysDeadChar = 0x0107;
        const uint WmUniChar = 0x0109;
        const uint WmImeStartComposition = 0x010D;
        const uint WmImeEndComposition = 0x010E;
        const uint WmImeComposition = 0x010F;
        const uint WmMouseFirst = 0x0200;
        const uint WmMouseLast = 0x020E;
        const uint WmImeChar = 0x0286;

        var captureMouse = (devices & InputCaptureDevices.Mouse) != 0;
        var captureKeyboard = (devices & InputCaptureDevices.Keyboard) != 0;
        var captureText = (devices & InputCaptureDevices.Text) != 0;
        return captureMouse &&
                   (message is >= WmNcMouseFirst and <= WmNcMouseLast ||
                    message is >= WmMouseFirst and <= WmMouseLast) ||
               captureKeyboard &&
                   message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp ||
               captureText &&
                   (message is WmChar or WmDeadChar or WmSysChar or WmSysDeadChar or WmUniChar ||
                    message is WmImeStartComposition or WmImeEndComposition or WmImeComposition ||
                    message == WmImeChar);
    }

    internal static bool ShouldCapture(
        uint message,
        nint lParam,
        InputCaptureDevices devices)
    {
        const uint WmInput = 0x00FF;
        return IsAlwaysCaptured(message, devices) ||
               (message == WmInput && IsCapturedRawInput(lParam, devices));
    }

    private static bool IsCapturedRawInput(
        nint rawInputHandle,
        InputCaptureDevices devices)
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
        if (copied == uint.MaxValue || copied < headerSize)
            return false;
        return header.Type switch
        {
            0 => (devices & InputCaptureDevices.Mouse) != 0,
            1 => (devices & InputCaptureDevices.Keyboard) != 0,
            _ => false,
        };
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
