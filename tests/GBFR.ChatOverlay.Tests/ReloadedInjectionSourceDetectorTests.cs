using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class ReloadedInjectionSourceDetectorTests
{
    [Fact]
    public void ClassifyCandidates_RecognizesLauncherDllByExactNameAndExport()
    {
        var source = ReloadedInjectionSourceDetector.ClassifyCandidates(
            ["C:\\Reloaded\\Reloaded.Mod.Loader.Bootstrapper.dll"],
            [true]);

        Assert.Equal(ReloadedInjectionKind.Launcher, source.Kind);
        Assert.Contains("InitializeASI", source.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyCandidates_RecognizesOfficialAsiBootstrapper()
    {
        var source = ReloadedInjectionSourceDetector.ClassifyCandidates(
            ["C:\\Game\\RELOADED.MOD.LOADER.BOOTSTRAPPER.ASI"],
            [true]);

        Assert.Equal(ReloadedInjectionKind.AsiBootstrapper, source.Kind);
    }

    [Fact]
    public void ClassifyCandidates_RejectsSimilarNamesAndMissingExports()
    {
        var source = ReloadedInjectionSourceDetector.ClassifyCandidates(
            [
                "C:\\Game\\Reloaded.Mod.Loader.Bootstrapper.backup.dll",
                "C:\\Game\\Reloaded.Mod.Loader.Bootstrapper.dll",
            ],
            [true, false]);

        Assert.Equal(ReloadedInjectionKind.Unknown, source.Kind);
        Assert.Contains("lacks", source.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyCandidates_RejectsConflictingLauncherAndAsiModules()
    {
        var source = ReloadedInjectionSourceDetector.ClassifyCandidates(
            [
                "C:\\Reloaded\\Reloaded.Mod.Loader.Bootstrapper.dll",
                "C:\\Game\\Reloaded.Mod.Loader.Bootstrapper.asi",
            ],
            [true, true]);

        Assert.Equal(ReloadedInjectionKind.Unknown, source.Kind);
        Assert.Contains("both", source.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatLogMessage_IdentifiesChineseLauncherDescription()
    {
        var message = ReloadedInjectionSourceDetector.FormatLogMessage(
            new ReloadedInjectionSource(
                ReloadedInjectionKind.Launcher,
                "C:\\Reloaded\\Reloaded.Mod.Loader.Bootstrapper.dll",
                "test"));

        Assert.Contains("source=launcher", message, StringComparison.Ordinal);
        Assert.Contains("Launcher 注入", message, StringComparison.Ordinal);
    }
}
