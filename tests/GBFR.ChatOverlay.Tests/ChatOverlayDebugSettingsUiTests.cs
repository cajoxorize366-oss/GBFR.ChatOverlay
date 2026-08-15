using System.IO;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatOverlayDebugSettingsUiTests
{
    [Fact]
    public void GeneralSettingsPage_RendersDiagnosticsSection_AndPreservesTabNumbering()
    {
        var source = ReadPeerSource();
        var generalBody = ExtractMethodBody(source, "private void DrawGeneralSettingsTab()");
        var settingsMenuBody = ExtractMethodBody(source, "private void DrawSettingsMenu()");

        Assert.Contains(
            "ImGui.Text(T(\"诊断\", \"Diagnostics\"));",
            generalBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "DrawDiagnosticsSettingsSection();",
            generalBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "DrawSettingsTab(T(\"00 通用设置\", \"00 General\"), DrawGeneralSettingsTab);",
            settingsMenuBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "DrawSettingsTab(T(\"01 语音\", \"01 Voice\"), DrawVoiceSettingsTab);",
            settingsMenuBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "DrawSettingsTab(T(\"02 快捷动作\", \"02 Quick Actions\"), DrawQuickActionSettingsTab);",
            settingsMenuBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "DrawSettingsTab(T(\"04 聊天过滤\", \"04 Chat Filter\"), DrawChatFilterSettingsTab);",
            settingsMenuBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "DrawSettingsTab(T(\"05 屏蔽管理\", \"05 Block Management\"), DrawChatBlockManagementTab);",
            settingsMenuBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsSection_ReadsLiveDebugSettingAndWritesThroughSafeUpdater()
    {
        var source = ReadPeerSource();
        var diagnosticsBody = ExtractMethodBody(
            source,
            "private void DrawDiagnosticsSettingsSection()");
        var checkboxBody = ExtractMethodBody(
            source,
            "private void DrawConfigurationCheckbox(");

        Assert.Contains(
            "var configuration = _getConfiguration();",
            diagnosticsBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "T(\"调试日志\", \"Debug Log\")",
            diagnosticsBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "configuration.EnableDebugLogging",
            diagnosticsBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "DrawConfigurationCheckbox(",
            diagnosticsBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "value.EnableDebugLogging = enabled",
            diagnosticsBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "UpdateConfigurationSafely(configuration => apply(configuration, value));",
            checkboxBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "GBFR.ChatOverlay.debug.log",
            diagnosticsBody,
            StringComparison.Ordinal);
    }

    private static string ReadPeerSource() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Overlay", "ChatOverlayPeer.cs"));

    private static string ExtractMethodBody(string source, string signature)
    {
        var methodStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Method signature was not found: {signature}");
        if (methodStart < 0)
            return string.Empty;

        var bodyStart = source.IndexOf('{', methodStart);
        Assert.True(bodyStart >= 0, $"Method body was not found: {signature}");
        if (bodyStart < 0)
            return string.Empty;

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
                continue;
            }

            if (source[index] != '}' || --depth != 0)
                continue;

            return source[(bodyStart + 1)..index];
        }

        throw new InvalidOperationException($"Method body was not closed: {signature}");
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