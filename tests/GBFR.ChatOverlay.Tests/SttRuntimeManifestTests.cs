using GBFR.ChatOverlay.Stt;

namespace GBFR.ChatOverlay.Tests;

public sealed class SttRuntimeManifestTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zh!")]
    public void InvalidLanguageFallsBackToChinese(string language)
    {
        var modDirectory = CreateFakeRuntime();
        try
        {
            Assert.True(SttRuntimeManifest.TryResolve(
                modDirectory,
                language,
                4,
                15,
                out var options,
                out var error));
            Assert.Null(error);
            Assert.Equal("zh", options!.Language);
        }
        finally
        {
            Directory.Delete(modDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(" AUTO ", "auto")]
    [InlineData("JA", "ja")]
    [InlineData("ko", "ko")]
    public void ValidLanguageIsNormalized(string language, string expected)
    {
        var modDirectory = CreateFakeRuntime();
        try
        {
            Assert.True(SttRuntimeManifest.TryResolve(
                modDirectory,
                language,
                4,
                15,
                out var options,
                out _));
            Assert.Equal(expected, options!.Language);
        }
        finally
        {
            Directory.Delete(modDirectory, recursive: true);
        }
    }

    private static string CreateFakeRuntime()
    {
        var modDirectory = Path.Combine(
            Path.GetTempPath(),
            "gbfr-chat-overlay-tests",
            Guid.NewGuid().ToString("N"));
        var runtimeDirectory = Path.Combine(modDirectory, SttRuntimeManifest.RuntimeDirectoryName);
        var files = new[]
        {
            Path.Combine(runtimeDirectory, "worker", "GBFR.ChatOverlay.SttWorker.exe"),
            Path.Combine(runtimeDirectory, "whisper", "whisper-cli.exe"),
            Path.Combine(runtimeDirectory, "models", "ggml-base.bin"),
        };

        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, Array.Empty<byte>());
        }

        return modDirectory;
    }
}
