using System.IO;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatOverlayDebugSettingsUiTests
{
    [Fact]
    public void GeneralSettingsPage_UsesLiveDebugLoggingSettingAndSafeUpdater()
    {
        var source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Overlay", "ChatOverlayPeer.cs"));
        var generalBody = SliceBetween(
            source,
            "private void DrawGeneralSettingsTab()",
            "private void DrawDiagnosticsSettingsSection()");
        var diagnosticsBody = SliceBetween(
            source,
            "private void DrawDiagnosticsSettingsSection()",
            "private void DrawLanguageSetting()");
        var checkboxBody = SliceBetween(
            source,
            "private void DrawConfigurationCheckbox(",
            "private void DrawBindingRow(");

        Assert.Contains("DrawDiagnosticsSettingsSection();", generalBody, StringComparison.Ordinal);
        Assert.Contains("var configuration = _getConfiguration();", diagnosticsBody, StringComparison.Ordinal);
        Assert.Contains("T(\"调试日志\", \"Debug Log\")", diagnosticsBody, StringComparison.Ordinal);
        Assert.Contains("configuration.EnableDebugLogging", diagnosticsBody, StringComparison.Ordinal);
        Assert.Contains("value.EnableDebugLogging = enabled", diagnosticsBody, StringComparison.Ordinal);
        Assert.Contains("GBFR.ChatOverlay.debug.log", diagnosticsBody, StringComparison.Ordinal);
        Assert.Contains(
            "UpdateConfigurationSafely(configuration => apply(configuration, value));",
            checkboxBody,
            StringComparison.Ordinal);
    }

    private static string SliceBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Source marker was not found: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Source marker was not found after {startMarker}: {endMarker}");
        return source[start..end];
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
