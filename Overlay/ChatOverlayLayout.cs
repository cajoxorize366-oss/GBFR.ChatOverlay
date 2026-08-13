using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Overlay;

internal readonly record struct ChatOverlayRect(float X, float Y, float Width, float Height);

internal enum ChatOverlayPresentationMode
{
    Full,
    Hidden,
    Compact,
}

internal static class ChatOverlayLayout
{
    internal const float MinimumWidth = 320.0f;
    internal const float MinimumHeight = 160.0f;
    internal const float DefaultInset = 24.0f;
    internal const float ComposerReservedHeight = 58.0f;
    internal const float ResizeHandleHitSize = 56.0f;
    internal const float ResizeHandleGripSize = 28.0f;
    internal const float ResizeHandleInset = 6.0f;
    internal const float ResizeHandleTopClearance = 28.0f;

    internal static ChatOverlayPresentationMode ResolvePresentation(
        bool compactMode,
        bool composerOpen,
        bool editMode)
    {
        if (!compactMode)
            return ChatOverlayPresentationMode.Full;

        return editMode || composerOpen
            ? ChatOverlayPresentationMode.Compact
            : ChatOverlayPresentationMode.Hidden;
    }

    internal static float ResolveCompactInputHeight(
        float candidateHeight,
        float statusHeight) =>
        ResolveCompactInputHeight(0.0f, candidateHeight, statusHeight, 1.0f);

    internal static float ResolveCompactInputHeight(
        float voiceHeight,
        float candidateHeight,
        float statusHeight,
        float fontScale)
    {
        var safeFontScale = NormalizeFontScale(fontScale);
        var safeVoiceHeight = NormalizeAdditionalHeight(voiceHeight);
        var safeCandidateHeight = NormalizeAdditionalHeight(candidateHeight);
        var safeStatusHeight = NormalizeAdditionalHeight(statusHeight);
        return (ComposerReservedHeight +
                safeVoiceHeight +
                safeCandidateHeight +
                safeStatusHeight) * safeFontScale;
    }

    internal static ChatOverlayRect ResolveCompactInputRect(
        ChatOverlayRect fullRect,
        float candidateHeight,
        float statusHeight,
        float minimumY) =>
        ResolveCompactInputRect(
            fullRect,
            voiceHeight: 0.0f,
            candidateHeight: candidateHeight,
            statusHeight: statusHeight,
            fontScale: 1.0f,
            minimumY: minimumY);

    internal static ChatOverlayRect ResolveCompactInputRect(
        ChatOverlayRect fullRect,
        float voiceHeight,
        float candidateHeight,
        float statusHeight,
        float fontScale,
        float minimumY)
    {
        var bottom = fullRect.Y + fullRect.Height;
        var requestedHeight = ResolveCompactInputHeight(
            voiceHeight,
            candidateHeight,
            statusHeight,
            fontScale);
        var y = Math.Max(minimumY, bottom - requestedHeight);
        return fullRect with
        {
            Y = y,
            Height = Math.Max(1.0f, bottom - y),
        };
    }

    internal static ChatOverlayRect ApplyCompactEditToFullRect(
        ChatOverlayRect fullRect,
        ChatOverlayRect compactRect,
        ChatOverlayRect editedCompactRect,
        float workX,
        float workY,
        float workWidth,
        float workHeight)
    {
        var moved = Move(
            fullRect,
            editedCompactRect.X - compactRect.X,
            editedCompactRect.Y - compactRect.Y,
            workX,
            workY,
            workWidth,
            workHeight);
        return ResizeWidth(
            moved,
            editedCompactRect.Width - compactRect.Width,
            workX,
            workWidth);
    }

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

    internal static ChatOverlayRect ResizeWidth(
        ChatOverlayRect rect,
        float deltaX,
        float workX,
        float workWidth)
    {
        var maximumWidth = Math.Max(1.0f, workX + workWidth - rect.X);
        var width = Math.Clamp(
            rect.Width + deltaX,
            Math.Min(MinimumWidth, maximumWidth),
            Math.Min(1_200.0f, maximumWidth));
        return rect with { Width = width };
    }

    internal static ChatOverlayRect ResolveResizeHandleHitRect(
        ChatOverlayRect rect,
        float hitSize,
        float topClearance)
    {
        var width = Math.Min(Math.Max(1.0f, hitSize), Math.Max(1.0f, rect.Width));
        var availableHeight = Math.Max(1.0f, rect.Height - Math.Max(0.0f, topClearance));
        var height = Math.Min(Math.Max(1.0f, hitSize), availableHeight);
        return new ChatOverlayRect(
            rect.X + rect.Width - width,
            rect.Y + rect.Height - height,
            width,
            height);
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

    private static float NormalizeAdditionalHeight(float value) =>
        float.IsFinite(value) && value > 0.0f
            ? value
            : 0.0f;

    private static float NormalizeFontScale(float value) =>
        float.IsFinite(value) && value > 0.0f
            ? value
            : 1.0f;
}
