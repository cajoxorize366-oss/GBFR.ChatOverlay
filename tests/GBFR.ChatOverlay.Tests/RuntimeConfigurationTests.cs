using System.IO;
using System.Text.Json;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Runtime.Configuration;

namespace GBFR.ChatOverlay.Tests;

public sealed class RuntimeConfigurationTests
{
    [Fact]
    public void TryReadFromWithRetry_LoadsValidConfiguration()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "Config.json");
        try
        {
            var expected = new Config { PushToTalkKeyboardBinding = "F8" };
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(expected, Configurable<Config>.SerializerOptions));

            var loaded = Configurable<Config>.TryReadFromWithRetry(
                path,
                "Default Config",
                out var configuration,
                timeoutMilliseconds: 50,
                retryDelayMilliseconds: 1);

            Assert.True(loaded);
            Assert.NotNull(configuration);
            Assert.Equal("F8", configuration.PushToTalkKeyboardBinding);
            configuration.DisposeEvents();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryReadFromWithRetry_IgnoresRetiredQuickActionPanelBindings()
    {
        Assert.Null(typeof(Config).GetProperty("QuickActionsKeyboardBinding"));
        Assert.Null(typeof(Config).GetProperty("QuickActionsControllerBinding"));

        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "Config.json");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "PushToTalkKeyboardBinding": "F8",
                  "QuickActionsKeyboardBinding": "I",
                  "QuickActionsControllerBinding": "X"
                }
                """);

            var loaded = Configurable<Config>.TryReadFromWithRetry(
                path,
                "Default Config",
                out var configuration,
                timeoutMilliseconds: 50,
                retryDelayMilliseconds: 1);

            Assert.True(loaded);
            Assert.NotNull(configuration);
            Assert.Equal("F8", configuration.PushToTalkKeyboardBinding);

            var rewritten = JsonSerializer.Serialize(
                configuration,
                Configurable<Config>.SerializerOptions);
            Assert.DoesNotContain("QuickActionsKeyboardBinding", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("QuickActionsControllerBinding", rewritten, StringComparison.Ordinal);
            configuration.DisposeEvents();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryReadFromWithRetry_LeavesCurrentConfigurationUntouchedForPartialJson()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "Config.json");
        try
        {
            File.WriteAllText(path, "{ \"PushToTalkKeyboardBinding\":");

            var loaded = Configurable<Config>.TryReadFromWithRetry(
                path,
                "Default Config",
                out var configuration,
                timeoutMilliseconds: 10,
                retryDelayMilliseconds: 1);

            Assert.False(loaded);
            Assert.Null(configuration);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryReadFromWithRetry_LeavesCurrentConfigurationUntouchedWhenFileIsMissing()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "Config.json");
        try
        {
            var loaded = Configurable<Config>.TryReadFromWithRetry(
                path,
                "Default Config",
                out var configuration,
                timeoutMilliseconds: 10,
                retryDelayMilliseconds: 1);

            Assert.False(loaded);
            Assert.Null(configuration);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gbfr-chat-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
