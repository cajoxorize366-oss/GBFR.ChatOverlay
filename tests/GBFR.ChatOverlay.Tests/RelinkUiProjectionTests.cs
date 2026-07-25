using System.Numerics;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkUiProjectionTests
{
    [Fact]
    public void TryProject_ConvertsNativeClipTransformToViewportPixels()
    {
        var transform = new Matrix4x4(
            2.0f / 1920.0f, 0.0f, 0.0f, 0.0f,
            0.0f, -2.0f / 1080.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            -1.0f, 1.0f, 0.0f, 1.0f);

        var succeeded = RelinkUiProjection.TryProject(
            transform,
            new Vector2(960.0f, 540.0f),
            viewportX: 10.0f,
            viewportY: 20.0f,
            viewportWidth: 2560.0f,
            viewportHeight: 1440.0f,
            out var screenPoint);

        Assert.True(succeeded);
        Assert.Equal(1290.0f, screenPoint.X, precision: 3);
        Assert.Equal(740.0f, screenPoint.Y, precision: 3);
    }

    [Fact]
    public void TryProject_PreservesNativeUltrawideAnchorInsteadOfApplyingScreenshotScale()
    {
        var transform = new Matrix4x4(
            2.0f / 3440.0f, 0.0f, 0.0f, 0.0f,
            0.0f, -2.0f / 1440.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            -1.0f, 1.0f, 0.0f, 1.0f);

        Assert.True(RelinkUiProjection.TryProject(
            transform,
            new Vector2(132.0f, 184.0f),
            0.0f,
            0.0f,
            3440.0f,
            1440.0f,
            out var screenPoint));

        Assert.Equal(132.0f, screenPoint.X, precision: 3);
        Assert.Equal(184.0f, screenPoint.Y, precision: 3);
    }

    [Fact]
    public void TryMeasureLogicalLength_UsesNativeHudScale()
    {
        var transform = new Matrix4x4(
            2.0f / 1920.0f, 0.0f, 0.0f, 0.0f,
            0.0f, -2.0f / 1080.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            -1.0f, 1.0f, 0.0f, 1.0f);

        Assert.True(RelinkUiProjection.TryMeasureLogicalLength(
            transform,
            logicalLength: 36.0f,
            viewportX: 0.0f,
            viewportY: 0.0f,
            viewportWidth: 2560.0f,
            viewportHeight: 1440.0f,
            out var screenLength));

        Assert.Equal(48.0f, screenLength, precision: 3);
    }

    [Fact]
    public void TryProject_AcceptsPreProjectionUiWorldMatrixWithoutReferenceResolution()
    {
        var transform = Matrix4x4.CreateScale(4.0f / 3.0f, 4.0f / 3.0f, 1.0f);
        transform.M41 = 100.0f;
        transform.M42 = -200.0f;

        Assert.True(RelinkUiProjection.TryProject(
            transform,
            new Vector2(50.0f, 0.0f),
            0.0f,
            0.0f,
            2560.0f,
            1440.0f,
            out var screenPoint));

        Assert.Equal(166.6667f, screenPoint.X, precision: 3);
        Assert.Equal(200.0f, screenPoint.Y, precision: 3);
    }

    [Fact]
    public void TryProject_RejectsInvalidNativeTransform()
    {
        var transform = Matrix4x4.Identity;
        transform.M44 = 0.0f;

        Assert.False(RelinkUiProjection.TryProject(
            transform,
            Vector2.Zero,
            0.0f,
            0.0f,
            1920.0f,
            1080.0f,
            out _));
    }
}
