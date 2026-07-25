using DearImguiSharp;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Overlay;

internal readonly record struct VoiceIndicatorPlacement(
    int SlotIndex,
    float CenterX,
    float CenterY,
    float Size,
    float Opacity,
    bool IsSpeaking);

internal readonly record struct VoiceIndicatorPalette(
    uint Accent,
    uint Foreground,
    uint Background);

internal static class VoiceIndicatorOverlay
{
    internal const float IdleOpacity = 0.70f;
    internal const float SpeakingOpacity = 1.00f;

    internal static void Draw(
        Config configuration,
        PartyVoiceUiStatus voiceStatus,
        Func<float, float, float, float, IReadOnlyList<PartyHudAnchor>> getNativeAnchors)
    {
        if (!configuration.EnableVoiceIndicators)
            return;

        var viewport = ImGui.GetMainViewport();
        var workPosition = viewport.WorkPos;
        var workSize = viewport.WorkSize;
        var nativeAnchors = getNativeAnchors(
            workPosition.X,
            workPosition.Y,
            workSize.X,
            workSize.Y);
        var placements = CreatePlacements(
            configuration.ShowAllVoiceIndicatorSlots,
            voiceStatus,
            nativeAnchors);
        if (placements.Count == 0)
            return;

        var drawList = ImGui.GetForegroundDrawListViewportPtr(viewport);
        foreach (var placement in placements)
            DrawMicrophone(drawList, placement);
    }

    internal static IReadOnlyList<VoiceIndicatorPlacement> CreatePlacements(
        bool showAllSlots,
        PartyVoiceUiStatus voiceStatus,
        IReadOnlyList<PartyHudAnchor> nativeAnchors)
    {
        ArgumentNullException.ThrowIfNull(nativeAnchors);

        // Remote Party ChatControl-to-Relink-slot identity is not verified yet.
        // Fail closed instead of marking a CPU or vanilla player as a Mod user when
        // the explicit position-test override is disabled.
        if (!showAllSlots || nativeAnchors.Count == 0)
            return Array.Empty<VoiceIndicatorPlacement>();

        var placements = new List<VoiceIndicatorPlacement>(nativeAnchors.Count);
        foreach (var anchor in nativeAnchors)
        {
            if (!float.IsFinite(anchor.CenterX) ||
                !float.IsFinite(anchor.CenterY) ||
                !float.IsFinite(anchor.IconSize) ||
                anchor.IconSize <= 0.0f)
            {
                continue;
            }

            var isSpeaking = anchor.IsLocal && voiceStatus.State == PartyVoiceUiState.Speaking;
            placements.Add(new VoiceIndicatorPlacement(
                anchor.SlotIndex,
                anchor.CenterX,
                anchor.CenterY,
                anchor.IconSize,
                isSpeaking ? SpeakingOpacity : IdleOpacity,
                isSpeaking));
        }

        return placements;
    }

    internal static uint PackColor(byte red, byte green, byte blue, float opacity)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0.0f, 1.0f) * byte.MaxValue);
        return red |
               ((uint)green << 8) |
               ((uint)blue << 16) |
               ((uint)alpha << 24);
    }

    internal static VoiceIndicatorPalette CreatePalette(bool isSpeaking, float opacity)
    {
        if (isSpeaking)
        {
            return new VoiceIndicatorPalette(
                PackColor(105, 224, 255, opacity),
                PackColor(245, 252, 255, opacity),
                PackColor(6, 15, 23, opacity * 0.72f));
        }

        // Keep the requested 70% alpha, but use a muted idle palette as well.
        // A bright white 70%-alpha glyph still looked nearly opaque against
        // Relink's dark battle backgrounds, making the state change too subtle.
        return new VoiceIndicatorPalette(
            PackColor(72, 137, 154, opacity),
            PackColor(164, 181, 188, opacity),
            PackColor(6, 15, 23, opacity * 0.50f));
    }

    private static void DrawMicrophone(ImDrawList drawList, VoiceIndicatorPlacement placement)
    {
        var size = placement.Size;
        var opacity = placement.Opacity;
        var centerX = placement.CenterX;
        var centerY = placement.CenterY;
        var radius = size * 0.5f;
        var lineWidth = Math.Max(1.0f, size * 0.065f);
        var palette = CreatePalette(placement.IsSpeaking, opacity);

        using var center = CreateVector2(centerX, centerY);
        ImGui.ImDrawListAddCircleFilled(drawList, center, radius, palette.Background, 24);
        ImGui.ImDrawListAddCircle(
            drawList,
            center,
            radius - (lineWidth * 0.5f),
            palette.Accent,
            24,
            lineWidth);

        var capsuleHalfWidth = size * 0.12f;
        var capsuleTop = centerY - (size * 0.28f);
        var capsuleBottom = centerY + (size * 0.08f);
        using var capsuleMinimum = CreateVector2(centerX - capsuleHalfWidth, capsuleTop);
        using var capsuleMaximum = CreateVector2(centerX + capsuleHalfWidth, capsuleBottom);
        ImGui.ImDrawListAddRectFilled(
            drawList,
            capsuleMinimum,
            capsuleMaximum,
            palette.Foreground,
            capsuleHalfWidth,
            0);

        using var receiverCenter = CreateVector2(centerX, capsuleBottom - (size * 0.01f));
        ImGui.ImDrawListPathClear(drawList);
        ImGui.ImDrawListPathArcTo(
            drawList,
            receiverCenter,
            size * 0.21f,
            0.0f,
            MathF.PI,
            12);
        ImGui.ImDrawListPathStroke(drawList, palette.Foreground, 0, lineWidth);

        var stemTop = centerY + (size * 0.18f);
        var stemBottom = centerY + (size * 0.29f);
        using var stemStart = CreateVector2(centerX, stemTop);
        using var stemEnd = CreateVector2(centerX, stemBottom);
        ImGui.ImDrawListAddLine(drawList, stemStart, stemEnd, palette.Foreground, lineWidth);
        using var baseStart = CreateVector2(centerX - (size * 0.13f), stemBottom);
        using var baseEnd = CreateVector2(centerX + (size * 0.13f), stemBottom);
        ImGui.ImDrawListAddLine(drawList, baseStart, baseEnd, palette.Foreground, lineWidth);
    }

    private static ImVec2 CreateVector2(float x, float y)
    {
        var vector = new ImVec2();
        vector.X = x;
        vector.Y = y;
        return vector;
    }
}
