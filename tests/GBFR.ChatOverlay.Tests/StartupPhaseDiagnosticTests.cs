using System.IO;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class StartupPhaseDiagnosticTests
{
    [Fact]
    public void Run_LogsBeginAndComplete()
    {
        var logs = new List<string>();

        var result = StartupPhaseDiagnostic.Run("test-phase", logs.Add, () => 42);

        Assert.Equal(42, result);
        Assert.Contains(logs, message => message.Contains("state=begin", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("state=complete", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_LogsFailureAndRethrows()
    {
        var logs = new List<string>();

        Assert.Throws<InvalidOperationException>(() =>
            StartupPhaseDiagnostic.Run(
                "test-phase",
                logs.Add,
                () => throw new InvalidOperationException("failed")));

        Assert.Contains(logs, message =>
            message.Contains("state=failed", StringComparison.Ordinal) &&
            message.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public void ModStartup_AppliesChatFilterWithoutRefreshingOfficialFilter()
    {
        var modSource = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Mod.cs"));
        var constructorStart = modSource.IndexOf(
            "internal Mod(ModContext context)",
            StringComparison.Ordinal);
        Assert.True(constructorStart >= 0, "Mod constructor was not found.");

        var constructorEnd = modSource.IndexOf(
            "public void ConfigurationUpdated",
            constructorStart,
            StringComparison.Ordinal);

        Assert.True(constructorEnd > constructorStart, "Mod constructor boundary was not found.");
        var constructorSource = modSource[constructorStart..constructorEnd];

        Assert.Contains(
            "_chatModeration = new ChatModerationService(new SteamOfficialTextFilter());",
            constructorSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_chatModeration.ApplyConfiguration(_configuration.ChatFilter);",
            constructorSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_chatModeration.RefreshOfficialFilter();",
            constructorSource,
            StringComparison.Ordinal);
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
