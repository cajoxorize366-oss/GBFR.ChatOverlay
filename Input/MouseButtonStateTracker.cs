namespace GBFR.ChatOverlay.Input;

internal static class MouseButtonStateTracker
{
    private static int s_pressedButtons;

    internal static uint PressedButtons => unchecked((uint)Volatile.Read(ref s_pressedButtons));

    internal static void ObserveWindowMessage(uint message, nint wParam)
    {
        uint setMask = 0;
        uint clearMask = 0;
        switch (message)
        {
            case 0x00A1:
            case 0x00A3:
            case 0x0201:
            case 0x0203:
                setMask = 1u << 0;
                break;
            case 0x00A2:
            case 0x0202:
                clearMask = 1u << 0;
                break;
            case 0x00A4:
            case 0x00A6:
            case 0x0204:
            case 0x0206:
                setMask = 1u << 1;
                break;
            case 0x00A5:
            case 0x0205:
                clearMask = 1u << 1;
                break;
            case 0x00A7:
            case 0x00A9:
            case 0x0207:
            case 0x0209:
                setMask = 1u << 2;
                break;
            case 0x00A8:
            case 0x0208:
                clearMask = 1u << 2;
                break;
            case 0x00AA:
            case 0x00AC:
            case 0x020B:
            case 0x020D:
                setMask = ExtraButtonMask(wParam);
                break;
            case 0x00AB:
            case 0x020C:
                clearMask = ExtraButtonMask(wParam);
                break;
            default:
                return;
        }

        while (true)
        {
            var observed = Volatile.Read(ref s_pressedButtons);
            var updated = (unchecked((uint)observed) | setMask) & ~clearMask;
            if (Interlocked.CompareExchange(ref s_pressedButtons, unchecked((int)updated), observed) == observed)
                return;
        }
    }

    internal static void Reset() => Volatile.Write(ref s_pressedButtons, 0);

    private static uint ExtraButtonMask(nint wParam) =>
        (unchecked((uint)(nuint)wParam) >> 16) switch
        {
            1 => 1u << 3,
            2 => 1u << 4,
            _ => 0,
        };
}
