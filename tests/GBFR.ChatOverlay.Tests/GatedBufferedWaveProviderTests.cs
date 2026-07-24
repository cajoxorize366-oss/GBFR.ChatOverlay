using GBFR.ChatOverlay.Audio;
using NAudio.Wave;

namespace GBFR.ChatOverlay.Tests;

public sealed class GatedBufferedWaveProviderTests
{
    [Fact]
    public void Disable_OverwritesAlreadyBufferedAudioWithSilence()
    {
        var provider = new GatedBufferedWaveProvider(
            new WaveFormat(48_000, 16, 1),
            TimeSpan.FromMilliseconds(250));
        var microphoneAudio = Enumerable.Repeat((byte)0x7F, 128).ToArray();
        provider.AddSamples(microphoneAudio, 0, microphoneAudio.Length);

        provider.Disable();
        var playback = Enumerable.Repeat((byte)0xCC, 128).ToArray();
        var read = provider.Read(playback, 0, playback.Length);

        Assert.Equal(playback.Length, read);
        Assert.All(playback, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Disable_DropsMicrophoneAudioSubmittedAfterRelease()
    {
        var provider = new GatedBufferedWaveProvider(
            new WaveFormat(48_000, 16, 1),
            TimeSpan.FromMilliseconds(250));
        provider.Disable();
        var microphoneAudio = Enumerable.Repeat((byte)0x7F, 128).ToArray();

        provider.AddSamples(microphoneAudio, 0, microphoneAudio.Length);
        var playback = new byte[128];
        provider.Read(playback, 0, playback.Length);

        Assert.All(playback, value => Assert.Equal(0, value));
    }
}
