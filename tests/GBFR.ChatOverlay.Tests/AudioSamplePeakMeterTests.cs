using GBFR.ChatOverlay.Audio;
using NAudio.Wave;

namespace GBFR.ChatOverlay.Tests;

public sealed class AudioSamplePeakMeterTests
{
    [Fact]
    public void Measure_DetectsFloatMicrophoneSignal()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        var samples = new[] { 0f, -0.25f, 0.75f, 0.1f };
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        var peak = AudioSamplePeakMeter.Measure(bytes, format);

        Assert.InRange(peak, 0.749f, 0.751f);
    }

    [Fact]
    public void Measure_DetectsPcm16MicrophoneSignal()
    {
        var format = new WaveFormat(48_000, 16, 1);
        var samples = new short[] { 0, -16_384, 8_192 };
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        var peak = AudioSamplePeakMeter.Measure(bytes, format);

        Assert.InRange(peak, 0.499f, 0.501f);
    }

    [Fact]
    public void Measure_RejectsUnsupportedCompressedFormat()
    {
        var format = WaveFormat.CreateCustomFormat(
            WaveFormatEncoding.MpegLayer3,
            48_000,
            channels: 1,
            averageBytesPerSecond: 16_000,
            blockAlign: 1,
            bitsPerSample: 0);

        Assert.Throws<NotSupportedException>(() =>
            AudioSamplePeakMeter.Measure(new byte[] { 1, 2, 3 }, format));
    }
}
