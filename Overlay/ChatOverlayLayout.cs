using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Overlay;

internal readonly record struct ChatOverlayRect(float X, float Y, float Width, float Height);

internal static class ChatOverlayLayout
{
    internal const float MinimumWidth = 320.0f;
    internal const float MinimumHeight = 160.0f;
    internal const float DefaultInset = 24.0f;

    internal static ChatOverlayRect Resolve(
        Config configuration,
        float workX,
        float workY,
        float workWidth,
        float workHeight)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var safeWorkWidth = Math.Max(1.0f, workWidth);
        var safeWorkHeight = Math.Max(1.0f, workHeight);
        var width = Math.Clamp(
            configuration.OverlayWidth,
            Math.Min(MinimumWidth, safeWorkWidth),
            Math.Min(1_200.0f, safeWorkWidth));
        var height = Math.Clamp(
            configuration.OverlayHeight,
            Math.Min(MinimumHeight, safeWorkHeight),
            Math.Min(800.0f, safeWorkHeight));
        var travelX = Math.Max(0.0f, safeWorkWidth - width);
        var travelY = Math.Max(0.0f, safeWorkHeight - height);

        var xRatio = NormalizeRatio(configuration.OverlayPositionXRatio);
        var yRatio = NormalizeRatio(configuration.OverlayPositionYRatio);
        var x = xRatio is { } savedX
            ? workX + savedX * travelX
            : workX + Math.Min(DefaultInset, travelX);
        var y = yRatio is { } savedY
            ? workY + savedY * travelY
            : workY + Math.Max(0.0f, travelY - DefaultInset);
        return new ChatOverlayRect(x, y, width, height);
    }

    internal static ChatOverlayRect Move(
        ChatOverlayRect rect,
        float deltaX,
        float deltaY,
        float workX,
        float workY,
        float workWidth,
        float workHeight) =>
        Clamp(
            rect with { X = rect.X + deltaX, Y = rect.Y + deltaY },
            workX,
            workY,
            workWidth,
            workHeight);

    internal static ChatOverlayRect Resize(
        ChatOverlayRect rect,
        float deltaX,
        float deltaY,
        float workX,
        float workY,
        float workWidth,
        float workHeight)
    {
        var maximumWidth = Math.Max(1.0f, workX + workWidth - rect.X);
        var maximumHeight = Math.Max(1.0f, workY + workHeight - rect.Y);
        var width = Math.Clamp(
            rect.Width + deltaX,
            Math.Min(MinimumWidth, maximumWidth),
            Math.Min(1_200.0f, maximumWidth));
        var height = Math.Clamp(
            rect.Height + deltaY,
            Math.Min(MinimumHeight, maximumHeight),
            Math.Min(800.0f, maximumHeight));
        return new ChatOverlayRect(rect.X, rect.Y, width, height);
    }

    internal static (double XRatio, double YRatio) ToRatios(
        ChatOverlayRect rect,
        float workX,
        float workY,
        float workWidth,
        float workHeight)
    {
        var travelX = Math.Max(0.0f, workWidth - rect.Width);
        var travelY = Math.Max(0.0f, workHeight - rect.Height);
        var x = travelX <= 0.0f ? 0.0 : Math.Clamp((rect.X - workX) / travelX, 0.0f, 1.0f);
        var y = travelY <= 0.0f ? 0.0 : Math.Clamp((rect.Y - workY) / travelY, 0.0f, 1.0f);
        return (x, y);
    }

    private static ChatOverlayRect Clamp(
        ChatOverlayRect rect,
        float workX,
        float workY,
        float workWidth,
        float workHeight)
    {
        var maximumX = workX + Math.Max(0.0f, workWidth - rect.Width);
        var maximumY = workY + Math.Max(0.0f, workHeight - rect.Height);
        return rect with
        {
            X = Math.Clamp(rect.X, workX, maximumX),
            Y = Math.Clamp(rect.Y, workY, maximumY),
        };
    }

    private static float? NormalizeRatio(double value) =>
        double.IsFinite(value) && value >= 0.0
            ? (float)Math.Clamp(value, 0.0, 1.0)
            : null;
}
