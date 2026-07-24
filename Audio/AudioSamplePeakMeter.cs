using System.Buffers.Binary;
using NAudio.Wave;

namespace GBFR.ChatOverlay.Audio;

internal static class AudioSamplePeakMeter
{
    private static readonly Guid PcmSubFormat =
        new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid IeeeFloatSubFormat =
        new("00000003-0000-0010-8000-00AA00389B71");

    public static float Measure(ReadOnlySpan<byte> samples, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (samples.IsEmpty)
            return 0;

        var encoding = ResolveEncoding(format);
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0)
            throw new NotSupportedException($"Unsupported microphone sample size: {format.BitsPerSample} bits.");

        var peak = encoding switch
        {
            WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32 =>
                MeasureFloat32(samples),
            WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 64 =>
                MeasureFloat64(samples),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 8 =>
                MeasurePcm8(samples),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 16 =>
                MeasurePcm16(samples),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 24 =>
                MeasurePcm24(samples),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 32 =>
                MeasurePcm32(samples),
            _ => throw new NotSupportedException(
                $"Unsupported microphone format: {encoding}, {format.BitsPerSample} bits."),
        };

        return float.IsFinite(peak) ? Math.Clamp(peak, 0f, 1f) : 0f;
    }

    private static WaveFormatEncoding ResolveEncoding(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible)
            return format.Encoding;

        if (format is not WaveFormatExtensible extensible)
            return format.Encoding;

        if (extensible.SubFormat == PcmSubFormat)
            return WaveFormatEncoding.Pcm;
        if (extensible.SubFormat == IeeeFloatSubFormat)
            return WaveFormatEncoding.IeeeFloat;
        return format.Encoding;
    }

    private static float MeasureFloat32(ReadOnlySpan<byte> samples)
    {
        var peak = 0f;
        for (var offset = 0; offset + 4 <= samples.Length; offset += 4)
        {
            var value = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(samples[offset..]));
            if (float.IsFinite(value))
                peak = Math.Max(peak, Math.Abs(value));
        }

        return peak;
    }

    private static float MeasureFloat64(ReadOnlySpan<byte> samples)
    {
        var peak = 0d;
        for (var offset = 0; offset + 8 <= samples.Length; offset += 8)
        {
            var value = BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReadInt64LittleEndian(samples[offset..]));
            if (double.IsFinite(value))
                peak = Math.Max(peak, Math.Abs(value));
        }

        return (float)Math.Min(peak, 1d);
    }

    private static float MeasurePcm8(ReadOnlySpan<byte> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
            peak = Math.Max(peak, Math.Abs(sample - 128) / 128f);
        return peak;
    }

    private static float MeasurePcm16(ReadOnlySpan<byte> samples)
    {
        var peak = 0f;
        for (var offset = 0; offset + 2 <= samples.Length; offset += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(samples[offset..]);
            peak = Math.Max(peak, sample == short.MinValue ? 1f : Math.Abs(sample) / 32767f);
        }

        return peak;
    }

    private static float MeasurePcm24(ReadOnlySpan<byte> samples)
    {
        var peak = 0f;
        for (var offset = 0; offset + 3 <= samples.Length; offset += 3)
        {
            var sample = samples[offset] |
                         (samples[offset + 1] << 8) |
                         (samples[offset + 2] << 16);
            if ((sample & 0x00800000) != 0)
                sample |= unchecked((int)0xFF000000);
            peak = Math.Max(peak, sample == -8_388_608 ? 1f : Math.Abs(sample) / 8_388_607f);
        }

        return peak;
    }

    private static float MeasurePcm32(ReadOnlySpan<byte> samples)
    {
        var peak = 0d;
        for (var offset = 0; offset + 4 <= samples.Length; offset += 4)
        {
            var sample = BinaryPrimitives.ReadInt32LittleEndian(samples[offset..]);
            var absolute = sample == int.MinValue ? 1d : Math.Abs((double)sample) / int.MaxValue;
            peak = Math.Max(peak, absolute);
        }

        return (float)Math.Min(peak, 1d);
    }
}
