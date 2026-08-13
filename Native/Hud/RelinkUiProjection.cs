using System.Numerics;

namespace GBFR.ChatOverlay.Native;

internal static class RelinkUiProjection
{
    private const float MinimumClipW = 0.0001f;
    private const float MaximumReasonableNdc = 8.0f;

    internal static bool TryProject(
        Matrix4x4 nativeFinalTransform,
        Vector2 nativeLocalPoint,
        float viewportX,
        float viewportY,
        float viewportWidth,
        float viewportHeight,
        out Vector2 screenPoint)
    {
        screenPoint = default;
        if (!IsFinite(nativeFinalTransform) ||
            !float.IsFinite(nativeLocalPoint.X) ||
            !float.IsFinite(nativeLocalPoint.Y) ||
            !float.IsFinite(viewportX) ||
            !float.IsFinite(viewportY) ||
            !float.IsFinite(viewportWidth) ||
            !float.IsFinite(viewportHeight) ||
            viewportWidth <= 0.0f ||
            viewportHeight <= 0.0f)
        {
            return false;
        }

        var clip = Vector4.Transform(
            new Vector4(nativeLocalPoint.X, nativeLocalPoint.Y, 0.0f, 1.0f),
            nativeFinalTransform);
        if (!float.IsFinite(clip.X) ||
            !float.IsFinite(clip.Y) ||
            !float.IsFinite(clip.W) ||
            MathF.Abs(clip.W) < MinimumClipW)
        {
            return false;
        }

        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        if (!float.IsFinite(ndcX) || !float.IsFinite(ndcY))
        {
            return false;
        }

        if (MathF.Abs(ndcX) <= MaximumReasonableNdc &&
            MathF.Abs(ndcY) <= MaximumReasonableNdc)
        {
            // Relink's render-ready UI transforms use Direct3D clip coordinates.
            screenPoint = new Vector2(
                viewportX + ((ndcX + 1.0f) * 0.5f * viewportWidth),
                viewportY + ((1.0f - ndcY) * 0.5f * viewportHeight));
        }
        else
        {
            // Some controller updates expose the pre-projection UI world matrix.
            // Its basis already contains the game's resolution/HUD scale and its
            // conventional positive-Y basis points upward from the top-left anchor.
            var yDirection = nativeFinalTransform.M22 >= 0.0f ? -1.0f : 1.0f;
            screenPoint = new Vector2(
                viewportX + ndcX,
                viewportY + (ndcY * yDirection));
        }

        return float.IsFinite(screenPoint.X) && float.IsFinite(screenPoint.Y);
    }

    internal static bool TryMeasureLogicalLength(
        Matrix4x4 nativeFinalTransform,
        float logicalLength,
        float viewportX,
        float viewportY,
        float viewportWidth,
        float viewportHeight,
        out float screenLength)
    {
        screenLength = 0.0f;
        if (!float.IsFinite(logicalLength) || logicalLength <= 0.0f ||
            !TryProject(
                nativeFinalTransform,
                Vector2.Zero,
                viewportX,
                viewportY,
                viewportWidth,
                viewportHeight,
                out var origin) ||
            !TryProject(
                nativeFinalTransform,
                new Vector2(0.0f, logicalLength),
                viewportX,
                viewportY,
                viewportWidth,
                viewportHeight,
                out var endpoint))
        {
            return false;
        }

        screenLength = Vector2.Distance(origin, endpoint);
        return float.IsFinite(screenLength) && screenLength > 0.0f;
    }

    private static bool IsFinite(Matrix4x4 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
        float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
        float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
        float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) &&
        float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
        float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) &&
        float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);
}
