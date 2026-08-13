using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Overlay;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatModerationSettingsPresentationTests
{
    [Fact]
    public void FormatNotification_ReplacesSupportedVariables()
    {
        var result = ChatModerationSettingsPresentation.FormatNotification(
            "{player}:{count}/{threshold}",
            "小明",
            0,
            4,
            7,
            64);

        Assert.Equal("小明:4/7", result);
    }

    [Fact]
    public void FormatNotification_UsesDefaultTemplateForEmptyTemplate()
    {
        var result = ChatModerationSettingsPresentation.FormatNotification(
            " \r\n\0 ",
            "Alice",
            0,
            3,
            3,
            128);

        Assert.Equal(
            ChatFilterConfiguration.DefaultNotificationTemplate.Replace(
                "{player}",
                "Alice",
                StringComparison.Ordinal),
            result);
    }

    [Fact]
    public void FormatNotification_UsesPlayerNumberWhenNameIsEmpty()
    {
        var result = ChatModerationSettingsPresentation.FormatNotification(
            "{player}",
            "",
            3,
            1,
            2,
            64);

        Assert.Equal("玩家 3", result);
    }

    [Fact]
    public void FormatNotification_CleansLineBreaksAndNul()
    {
        var result = ChatModerationSettingsPresentation.FormatNotification(
            "a\r\nb\0c",
            "Player",
            0,
            1,
            2,
            64);

        Assert.Equal("a bc", result);
        Assert.DoesNotContain('\r', result);
        Assert.DoesNotContain('\n', result);
        Assert.DoesNotContain('\0', result);
    }

    [Fact]
    public void FormatNotification_LimitsChatComposerLength()
    {
        var result = ChatModerationSettingsPresentation.FormatNotification(
            "{player}",
            "1234567890",
            0,
            1,
            1,
            5);

        Assert.Equal("12345", result);
    }

    [Theory]
    [InlineData("short", "short")]
    [InlineData("1234567890123456", "…90123456")]
    public void ShortenIdentity_PreservesShortValueAndKeepsTail(string identity, string expected)
    {
        Assert.Equal(expected, ChatModerationSettingsPresentation.ShortenIdentity(identity));
    }

    [Fact]
    public void ParticipantLabel_FallsBackToPlayerNumberWhenNameIsEmpty()
    {
        var participant = new ChatModerationParticipant(3, "", null);

        Assert.Equal(
            "玩家 3",
            ChatModerationSettingsPresentation.ParticipantLabel(
                participant,
                UiLanguage.SimplifiedChinese));
    }
}
