using System.IO;
using System.Reflection;
using GBFR.ChatOverlay.Runtime;
using GBFR.ChatOverlay.Runtime.Diagnostics;

namespace GBFR.ChatOverlay.Tests;

public sealed class DebugLogFanoutContractTests
{
    private const string ModId = "gbfr.qol.chatoverlay";
    [Fact]
    public void ModContext_ExposesUnifiedLogWithoutLoggerOrModConfig()
    {
        var contextType = typeof(ModContext);
        Assert.NotNull(contextType.GetProperty(
            "Log",
            BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(contextType.GetProperty(
            "Logger",
            BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(contextType.GetProperty(
            "ModConfig",
            BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void Mod_UsesOnlyContextLogAndDoesNotPrefixMessages()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Mod.cs"));

        Assert.Contains("_log = context.Log;", source, StringComparison.Ordinal);
        Assert.Contains("Action<string> moduleLog = _log;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ILogger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_logger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_modConfig", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[{_modConfig.ModId}]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_OwnsFileSinkAndAppliesLoggingOnEveryUpdatePath()
    {
        var source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Runtime", "Startup.cs"));

        Assert.Contains("_debugFileLog = CreateDebugFileLog();", source, StringComparison.Ordinal);
        Assert.Contains("DebugFileLog.FileName", source, StringComparison.Ordinal);
        Assert.Contains("GetDirectoryForModId(_modConfig.ModId)", source, StringComparison.Ordinal);
        Assert.Contains("new DebugFileLog(", source, StringComparison.Ordinal);
        Assert.Contains("ApplyDebugLogging(_configuration)", source, StringComparison.Ordinal);
        Assert.Contains("ApplyDebugLogging(configuration)", source, StringComparison.Ordinal);
        Assert.Contains("_moduleLog = CreateModuleLog();", source, StringComparison.Ordinal);
        Assert.Contains("CreateLogFanout", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupFanout_ReloadedSinkFailureStillWritesFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, DebugFileLog.FileName);
            using var debugLog = new DebugFileLog(path, ModId, _ => { });
            debugLog.ApplyEnabled(true);

            var fanout = Startup.CreateLogFanout(
                _ => throw new InvalidOperationException("reloaded failed"),
                debugLog,
                ModId);
            fanout("file-only-message");

            debugLog.Dispose();
            Assert.Contains(
                "file-only-message",
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StartupFanout_FileSinkUnavailableStillCallsReloaded()
    {
        var received = new List<string>();
        using var debugLog = new DebugFileLog(null, ModId, _ => { });
        debugLog.ApplyEnabled(true);

        var fanout = Startup.CreateLogFanout(received.Add, debugLog, ModId);
        fanout("reloaded-only-message");

        Assert.Contains(
            "[gbfr.qol.chatoverlay] reloaded-only-message",
            received,
            StringComparer.Ordinal);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"gbfr-debug-fanout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GBFR.ChatOverlay.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Unable to locate the GBFR.ChatOverlay repository root from the test output directory.");
    }
}
