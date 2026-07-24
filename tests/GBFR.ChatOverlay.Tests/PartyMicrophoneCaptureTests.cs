using System.Buffers.Binary;
using GBFR.ChatOverlay.Audio;
using NAudio.Wave;

namespace GBFR.ChatOverlay.Tests;

public sealed class PartyMicrophoneCaptureTests
{
    [Fact]
    public void Decoder_DownmixesPcm16StereoToMono()
    {
        var format = new WaveFormat(48_000, 16, 2);
        var bytes = new byte[format.BlockAlign * 2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(0), 16_384);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2), -8_192);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), -16_384);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), 8_192);

        var samples = new WaveToMonoSampleDecoder(format).Decode(bytes);

        Assert.Equal(2, samples.Length);
        Assert.Equal(0.125f, samples[0], precision: 5);
        Assert.Equal(-0.125f, samples[1], precision: 5);
    }

    [Fact]
    public void Decoder_ReadsSignedPcm24AndDropsIncompleteFrame()
    {
        var format = new WaveFormat(44_100, 24, 1);
        var bytes = new byte[]
        {
            0x00, 0x00, 0x40,
            0x00, 0x00, 0xC0,
            0x7F,
        };

        var samples = new WaveToMonoSampleDecoder(format).Decode(bytes);

        Assert.Equal(new[] { 0.5f, -0.5f }, samples);
    }

    [Fact]
    public void Decoder_SanitizesNonFiniteFloatSamples()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        var values = new[] { 0.25f, float.NaN, float.PositiveInfinity, -2f };
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);

        var samples = new WaveToMonoSampleDecoder(format).Decode(bytes);

        Assert.Equal(0.25f, samples[0]);
        Assert.Equal(0f, samples[1]);
        Assert.Equal(0f, samples[2]);
        Assert.Equal(-1f, samples[3]);
    }

    [Fact]
    public void Decoder_RejectsUnsupportedCompressedFormat()
    {
        var format = WaveFormat.CreateALawFormat(8_000, 1);

        Assert.Throws<NotSupportedException>(() => new WaveToMonoSampleDecoder(format));
    }

    [Fact]
    public void BoundedProvider_PreservesFifoAcrossWraparound()
    {
        var provider = new BoundedMonoSampleProvider(24_000, capacitySamples: 5);
        provider.Add(new[] { 1f, 2f, 3f, 4f });
        var first = new float[3];
        Assert.Equal(3, provider.Read(first, 0, first.Length));
        provider.Add(new[] { 5f, 6f, 7f });

        var remaining = new float[4];
        var read = provider.Read(remaining, 0, remaining.Length);

        Assert.Equal(4, read);
        Assert.Equal(new[] { 4f, 5f, 6f, 7f }, remaining);
    }

    [Fact]
    public void BoundedProvider_OverflowKeepsNewestSamples()
    {
        var provider = new BoundedMonoSampleProvider(24_000, capacitySamples: 4);
        provider.Add(new[] { 1f, 2f, 3f });
        provider.Add(new[] { 4f, 5f, 6f });

        var output = new float[4];
        var read = provider.Read(output, 0, output.Length);

        Assert.Equal(4, read);
        Assert.Equal(new[] { 3f, 4f, 5f, 6f }, output);
    }

    [Fact]
    public void BoundedProvider_ClearDropsBufferedAudio()
    {
        var provider = new BoundedMonoSampleProvider(24_000, capacitySamples: 4);
        provider.Add(new[] { 1f, 2f, 3f });

        provider.Clear();

        Assert.Equal(0, provider.Read(new float[4], 0, 4));
    }

    [Fact]
    public void FrameCadence_RequiresFullIntervalAndNeverCatchesUpAfterDelay()
    {
        var cadence = new PartyFrameCadence(
            timestampFrequency: 1_000,
            frameDurationMilliseconds: 40);

        Assert.Equal(0, cadence.GetRemainingTicks(timestamp: 100));
        cadence.MarkPublished(timestamp: 100);
        Assert.Equal(40, cadence.GetRemainingTicks(timestamp: 100));
        Assert.Equal(1, cadence.GetRemainingTicks(timestamp: 139));
        Assert.Equal(0, cadence.GetRemainingTicks(timestamp: 140));

        // A thread that wakes up late establishes a new 40 ms deadline. It must not emit several
        // accumulated frames immediately in an attempt to catch up with the old deadline.
        cadence.MarkPublished(timestamp: 500);
        Assert.Equal(40, cadence.GetRemainingTicks(timestamp: 500));
        Assert.Equal(0, cadence.GetRemainingTicks(timestamp: 540));
    }

    [Fact]
    public void FrameCadence_RoundsUpForTimestampFrequenciesNotDivisibleByOneThousand()
    {
        var cadence = new PartyFrameCadence(
            timestampFrequency: 1_001,
            frameDurationMilliseconds: 40);

        cadence.MarkPublished(timestamp: 100);

        Assert.Equal(41, cadence.GetRemainingTicks(timestamp: 100));
        Assert.Equal(1, cadence.GetRemainingTicks(timestamp: 140));
        Assert.Equal(0, cadence.GetRemainingTicks(timestamp: 141));
    }
}
