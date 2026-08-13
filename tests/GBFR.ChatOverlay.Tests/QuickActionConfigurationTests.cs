using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Native;
using System.Text.Json;

namespace GBFR.ChatOverlay.Tests;

public sealed class QuickActionConfigurationTests
{
    [Fact]
    public void LegacyTextOnlyAction_RemainsConfiguredAsCustomText()
    {
        var action = new QuickActionConfiguration { Text = "Ready!" };

        Assert.Equal(QuickActionKind.CustomText, action.Kind);
        Assert.True(action.IsConfigured);
    }

    [Fact]
    public void OfficialAction_DoesNotRequireCustomText()
    {
        var action = new QuickActionConfiguration
        {
            Kind = QuickActionKind.Stamp,
            OfficialId = 16,
            Text = string.Empty,
            KeyboardBinding = "Alt+1",
        };
        var configuration = new Config
        {
            QuickActions = [action],
            OpenChatKeyboardBinding = string.Empty,
            SettingsMenuKeyboardBinding = string.Empty,
            PushToTalkKeyboardBinding = string.Empty,
            QuickActionsKeyboardBinding = string.Empty,
        };

        var snapshot = HotkeyConfigurationSnapshot.Create(configuration);

        Assert.True(action.IsConfigured);
        Assert.True(snapshot.QuickActions[0].IsConfigured);
        Assert.Contains(
            snapshot.NativeBindings,
            binding => binding.PolicyFlag == (byte)DirectInputBrokerPolicy.SuppressQuickActions);
    }

    [Fact]
    public void OfficialCatalog_MatchesRelink202CommunicationTables()
    {
        Assert.Equal(94, CommunicationCatalog.GetEntries(QuickActionKind.Stamp).Count);
        Assert.Equal(62, CommunicationCatalog.GetEntries(QuickActionKind.FixedPhrase).Count);
        Assert.Equal(23, CommunicationCatalog.GetEntries(QuickActionKind.Emotion).Count);

        Assert.True(CommunicationCatalog.TryGetEntry(QuickActionKind.Stamp, 16, out var stamp));
        Assert.Equal("谢谢", stamp.ChineseName);
        Assert.Equal("谢谢", stamp.GetDisplayName(UiLanguage.SimplifiedChinese));
        Assert.Equal("Thanks!", stamp.GetDisplayName(UiLanguage.English));
        Assert.True(CommunicationCatalog.TryGetEntry(QuickActionKind.FixedPhrase, 5, out var phrase));
        Assert.Equal("请多关照！", phrase.ChineseName);
        Assert.True(CommunicationCatalog.TryGetEntry(QuickActionKind.Emotion, 12, out var emotion));
        Assert.Equal(17, emotion.NativeValue);
    }

    [Fact]
    public void OfficialCatalog_DefaultIdFailsClosedForUnsupportedKinds()
    {
        Assert.Equal(16, CommunicationCatalog.GetDefaultId(QuickActionKind.Stamp));
        Assert.Equal(-1, CommunicationCatalog.GetDefaultId(QuickActionKind.CustomText));
        Assert.Equal(-1, CommunicationCatalog.GetDefaultId((QuickActionKind)int.MaxValue));
    }

    [Fact]
    public void UnknownOfficialId_IsNotConfigured()
    {
        var action = new QuickActionConfiguration
        {
            Kind = QuickActionKind.Emotion,
            OfficialId = int.MaxValue,
        };

        Assert.False(action.IsConfigured);
    }

    [Fact]
    public void LegacyControllerBinding_IsIgnoredWithoutADeadRuntimeProperty()
    {
        var action = JsonSerializer.Deserialize<QuickActionConfiguration>(
            "{\"Text\":\"Ready!\",\"ControllerBinding\":\"X\"}")!;
        var json = JsonSerializer.Serialize(action);
        var snapshot = HotkeyConfigurationSnapshot.Create(new Config
        {
            QuickActions = [action],
        });

        Assert.DoesNotContain("ControllerBinding", json, StringComparison.Ordinal);
        Assert.Null(typeof(QuickActionConfiguration).GetProperty("ControllerBinding"));
        Assert.False(snapshot.QuickActions[0].Keyboard.IsBound);
    }
}
