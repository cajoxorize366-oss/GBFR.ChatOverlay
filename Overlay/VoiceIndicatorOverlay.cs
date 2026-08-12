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
        Func<float, float, float, float, IReadOnlyList<PartyHudAnchor>> getNativeAnchors,
        IReadOnlyList<int> establishedRemotePlayers,
        IReadOnlyList<int> occupiedRemotePlayers,
        IReadOnlyList<int> talkingRemotePlayers,
        bool snapshotsValid = true)
    {
        if (!configuration.EnableVoiceIndicators)
            return;

        var viewport = ImGui.GetMainViewport();
        var workPosition = viewport.WorkPos;
        var workSize = viewport.WorkSize;
        IReadOnlyList<PartyHudAnchor> nativeAnchors;
        try
        {
            nativeAnchors = getNativeAnchors(
                workPosition.X,
                workPosition.Y,
                workSize.X,
                workSize.Y) ?? Array.Empty<PartyHudAnchor>();
        }
        catch
        {
            return;
        }

        var placements = CreatePlacements(
            configuration.EffectiveShowAllVoiceIndicatorSlots,
            voiceStatus,
            nativeAnchors,
            establishedRemotePlayers,
            occupiedRemotePlayers,
            talkingRemotePlayers,
            snapshotsValid);
        if (placements.Count == 0)
            return;

        var drawList = ImGui.GetForegroundDrawListViewportPtr(viewport);
        foreach (var placement in placements)
            DrawMicrophone(drawList, placement);
    }

    internal static IReadOnlyList<VoiceIndicatorPlacement> CreatePlacements(
        bool showAllSlots,
        PartyVoiceUiStatus voiceStatus,
        IReadOnlyList<PartyHudAnchor> nativeAnchors) =>
        CreatePlacements(
            showAllSlots,
            voiceStatus,
            nativeAnchors,
            Array.Empty<int>(),
            Array.Empty<int>(),
            Array.Empty<int>());

    internal static IReadOnlyList<VoiceIndicatorPlacement> CreatePlacements(
        bool showAllSlots,
        PartyVoiceUiStatus voiceStatus,
        IReadOnlyList<PartyHudAnchor> nativeAnchors,
        IReadOnlyList<int> establishedRemotePlayers,
        IReadOnlyList<int> occupiedRemotePlayers,
        IReadOnlyList<int> talkingRemotePlayers,
        bool snapshotsValid = true)
    {
        ArgumentNullException.ThrowIfNull(nativeAnchors);
        ArgumentNullException.ThrowIfNull(establishedRemotePlayers);
        ArgumentNullException.ThrowIfNull(occupiedRemotePlayers);
        ArgumentNullException.ThrowIfNull(talkingRemotePlayers);

        var validAnchors = nativeAnchors
            .Where(IsValidAnchor)
            .ToArray();
        if (validAnchors.Length == 0)
            return Array.Empty<VoiceIndicatorPlacement>();

        var normalizedTalkingRemotePlayers = NormalizeRemotePlayers(talkingRemotePlayers);
        if (showAllSlots)
        {
            var debugPlacements = new List<VoiceIndicatorPlacement>(validAnchors.Length);
            var debugRemoteSpeaking = ResolveDebugRemoteSpeaking(
                validAnchors,
                occupiedRemotePlayers,
                normalizedTalkingRemotePlayers);
            foreach (var anchor in nativeAnchors)
            {
                if (!IsValidAnchor(anchor))
                    continue;

                var isSpeaking = anchor.IsLocal
                    ? voiceStatus.State == PartyVoiceUiState.Speaking
                    : debugRemoteSpeaking.Contains(anchor.SlotIndex);
                debugPlacements.Add(CreatePlacement(anchor, isSpeaking));
            }

            return debugPlacements;
        }

        if (!snapshotsValid ||
            voiceStatus.State is not PartyVoiceUiState.Ready and not PartyVoiceUiState.Speaking)
        {
            return Array.Empty<VoiceIndicatorPlacement>();
        }

        var normalizedEstablishedRemotePlayers = NormalizeRemotePlayers(establishedRemotePlayers);
        var normalizedOccupiedRemotePlayers = NormalizeRemotePlayers(occupiedRemotePlayers);
        if (normalizedEstablishedRemotePlayers.Length == 0 ||
            normalizedEstablishedRemotePlayers.Any(
                remotePlayer => !normalizedOccupiedRemotePlayers.Contains(remotePlayer)))
        {
            return Array.Empty<VoiceIndicatorPlacement>();
        }

        if (validAnchors.Length != nativeAnchors.Count)
            return Array.Empty<VoiceIndicatorPlacement>();
        if (validAnchors.Any(anchor => anchor.Layout != validAnchors[0].Layout))
            return Array.Empty<VoiceIndicatorPlacement>();

        var localAnchors = validAnchors
            .Where(static anchor => anchor.IsLocal)
            .ToArray();
        var remoteAnchors = OrderRemoteAnchors(validAnchors);
        if (localAnchors.Length != 1)
            return Array.Empty<VoiceIndicatorPlacement>();

        if (!TryResolveRemotePlayerMap(
                remoteAnchors,
                normalizedOccupiedRemotePlayers,
                out var mappedRemotePlayers))
            return Array.Empty<VoiceIndicatorPlacement>();

        var established = normalizedEstablishedRemotePlayers.ToHashSet();
        var talking = normalizedTalkingRemotePlayers.ToHashSet();
        var placements = new List<VoiceIndicatorPlacement>(1 + remoteAnchors.Length)
        {
            CreatePlacement(
                localAnchors[0],
                voiceStatus.State == PartyVoiceUiState.Speaking),
        };

        for (var index = 0; index < remoteAnchors.Length; index++)
        {
            var remotePlayer = mappedRemotePlayers[index];
            if (!established.Contains(remotePlayer))
                continue;

            placements.Add(CreatePlacement(
                remoteAnchors[index],
                talking.Contains(remotePlayer)));
        }

        return placements;
    }

    private static HashSet<int> ResolveDebugRemoteSpeaking(
        IReadOnlyList<PartyHudAnchor> validAnchors,
        IReadOnlyList<int> occupiedRemotePlayers,
        IReadOnlyList<int> talkingRemotePlayers)
    {
        var normalizedOccupiedRemotePlayers = NormalizeRemotePlayers(occupiedRemotePlayers);
        var remoteAnchors = OrderRemoteAnchors(validAnchors);
        if (!TryResolveRemotePlayerMap(
                remoteAnchors,
                normalizedOccupiedRemotePlayers,
                out var mappedRemotePlayers))
            return [];

        var talking = NormalizeRemotePlayers(talkingRemotePlayers).ToHashSet();
        var speakingSlots = new HashSet<int>();
        for (var index = 0; index < remoteAnchors.Length; index++)
        {
            if (talking.Contains(mappedRemotePlayers[index]))
                speakingSlots.Add(remoteAnchors[index].SlotIndex);
        }

        return speakingSlots;
    }

    private static PartyHudAnchor[] OrderRemoteAnchors(IReadOnlyList<PartyHudAnchor> anchors) =>
        anchors
            .Where(static anchor => !anchor.IsLocal)
            .OrderBy(static anchor => anchor.CenterY)
            .ThenBy(static anchor => anchor.CenterX)
            .ThenBy(static anchor => anchor.SlotIndex)
            .ToArray();

    private static bool TryResolveRemotePlayerMap(
        IReadOnlyList<PartyHudAnchor> remoteAnchors,
        IReadOnlyList<int> normalizedOccupiedRemotePlayers,
        out int[] mappedRemotePlayers)
    {
        if (remoteAnchors.Count == 3)
        {
            mappedRemotePlayers = [1, 2, 3];
            return true;
        }

        if (remoteAnchors.Count == normalizedOccupiedRemotePlayers.Count)
        {
            mappedRemotePlayers = normalizedOccupiedRemotePlayers.ToArray();
            return true;
        }

        mappedRemotePlayers = Array.Empty<int>();
        return false;
    }

    private static VoiceIndicatorPlacement CreatePlacement(
        PartyHudAnchor anchor,
        bool isSpeaking) =>
        new(
            anchor.SlotIndex,
            anchor.CenterX,
            anchor.CenterY,
            anchor.IconSize,
            isSpeaking ? SpeakingOpacity : IdleOpacity,
            isSpeaking);

    private static bool IsValidAnchor(PartyHudAnchor anchor) =>
        float.IsFinite(anchor.CenterX) &&
        float.IsFinite(anchor.CenterY) &&
        float.IsFinite(anchor.IconSize) &&
        anchor.IconSize > 0.0f;

    private static int[] NormalizeRemotePlayers(IReadOnlyList<int> remotePlayers) =>
        remotePlayers
            .Where(static remotePlayer => remotePlayer is >= 1 and <= 3)
            .Distinct()
            .OrderBy(static remotePlayer => remotePlayer)
            .ToArray();

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
