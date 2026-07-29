using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Overlay;

internal static class OverlayUiScale
{
    private static readonly Lazy<float> s_systemScale = new(ReadSystemScale);

    internal static float SystemScale => s_systemScale.Value;

    internal static float FromDpi(uint dpi) =>
        Math.Clamp((dpi == 0 ? 96.0f : dpi) / 96.0f, 0.75f, 2.0f);

    internal static float Scale(float value) => value * SystemScale;

    private static float ReadSystemScale()
    {
        try
        {
            return FromDpi(GetDpiForSystem());
        }
        catch
        {
            return 1.0f;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
