using System.Text.Json;
using GBFR.ChatOverlay.Configuration;

namespace GBFR.ChatOverlay.Tests;

public sealed class VoiceLanguageConfigTests
{
    [Fact]
    public void DefaultsToChinese()
    {
        var configuration = new Config();

        Assert.Equal(VoiceLanguageOption.Chinese, configuration.VoiceLanguage);
        Assert.Equal("zh", configuration.VoiceLanguageCode);
        Assert.Equal("default", configuration.VoiceMicrophone);
        Assert.True(configuration.EnableVoiceDiagnostics);
    }

    [Theory]
    [InlineData(VoiceLanguageOption.Chinese, "zh")]
    [InlineData(VoiceLanguageOption.Japanese, "ja")]
    [InlineData(VoiceLanguageOption.English, "en")]
    [InlineData(VoiceLanguageOption.Korean, "ko")]
    [InlineData(VoiceLanguageOption.Automatic, "auto")]
    public void ListSelectionMapsToWhisperCode(VoiceLanguageOption option, string expectedCode)
    {
        var configuration = new Config { VoiceLanguage = option };

        Assert.Equal(expectedCode, configuration.VoiceLanguageCode);
    }

    [Fact]
    public void LegacyAutomaticDefaultMigratesToChineseOnce()
    {
        var configuration = JsonSerializer.Deserialize<Config>(
            """{"VoiceLanguage":"auto"}""",
            Config.SerializerOptions)!;

        Assert.True(configuration.ApplyVoiceLanguageDefaultMigration());
        Assert.Equal(VoiceLanguageOption.Chinese, configuration.VoiceLanguage);
        Assert.Equal("zh", configuration.VoiceLanguageCode);
        Assert.False(configuration.ApplyVoiceLanguageDefaultMigration());
    }

    [Fact]
    public void ExplicitAutomaticSelectionIsPreservedAfterMigration()
    {
        var configuration = new Config();
        configuration.ApplyVoiceLanguageDefaultMigration();
        configuration.VoiceLanguage = VoiceLanguageOption.Automatic;

        Assert.False(configuration.ApplyVoiceLanguageDefaultMigration());
        Assert.Equal("auto", configuration.VoiceLanguageCode);
    }

    [Fact]
    public void JsonKeepsTheExistingVoiceLanguageKey()
    {
        var configuration = new Config { VoiceLanguage = VoiceLanguageOption.Japanese };

        var json = JsonSerializer.Serialize(configuration, Config.SerializerOptions);

        Assert.Contains("\"VoiceLanguage\": \"ja\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("VoiceLanguageCode", json, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonKeepsMicrophoneAndDiagnosticsSettings()
    {
        var configuration = new Config
        {
            VoiceMicrophone = "USB Microphone",
            EnableVoiceDiagnostics = false,
        };

        var json = JsonSerializer.Serialize(configuration, Config.SerializerOptions);
        var roundTrip = JsonSerializer.Deserialize<Config>(json, Config.SerializerOptions)!;

        Assert.Equal("USB Microphone", roundTrip.VoiceMicrophone);
        Assert.False(roundTrip.EnableVoiceDiagnostics);
    }
}
