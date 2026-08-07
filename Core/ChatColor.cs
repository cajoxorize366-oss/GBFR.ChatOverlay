using System.Globalization;

namespace GBFR.ChatOverlay.Core;

public static class ChatColor
{
    public static bool TryParseRgb(string? value, out float[] rgba)
    {
        rgba = [1.0f, 1.0f, 1.0f, 1.0f];
        if (!TryParseHex(value, out var rgb))
            return false;

        rgba =
        [
            ((rgb >> 16) & 0xFF) / 255.0f,
            ((rgb >> 8) & 0xFF) / 255.0f,
            (rgb & 0xFF) / 255.0f,
            1.0f,
        ];
        return true;
    }

    public static bool TryParseImGuiColor(string? value, out uint color)
    {
        color = 0xFFFFFFFF;
        if (!TryParseHex(value, out var rgb))
            return false;

        var red = rgb >> 16 & 0xFF;
        var green = rgb >> 8 & 0xFF;
        var blue = rgb & 0xFF;
        color = red | green << 8 | blue << 16 | 0xFF000000;
        return true;
    }

    public static string ToHex(float[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length < 3)
            throw new ArgumentException("RGB color requires at least three components.", nameof(rgba));

        var red = ToByte(rgba[0]);
        var green = ToByte(rgba[1]);
        var blue = ToByte(rgba[2]);
        return $"#{red:X2}{green:X2}{blue:X2}";
    }

    public static uint ToImGuiColor(float[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length < 3)
            throw new ArgumentException("RGB color requires at least three components.", nameof(rgba));

        var red = ToByte(rgba[0]);
        var green = ToByte(rgba[1]);
        var blue = ToByte(rgba[2]);
        var alpha = rgba.Length > 3 ? ToByte(rgba[3]) : byte.MaxValue;
        return (uint)(red | green << 8 | blue << 16 | alpha << 24);
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f), 0, 255);

    private static bool TryParseHex(string? value, out uint rgb)
    {
        rgb = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        return hex.Length == 6 &&
               uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }
}
