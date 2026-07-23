using NAudio.Wave;

namespace GBFR.ChatOverlay.SttWorker;

internal sealed record AudioFileMetrics(
    string Format,
    long FileBytes,
    double DurationMilliseconds,
    long SampleValueCount,
    double Peak,
    double Rms,
    double SilenceRatio,
    double ClippingRatio)
{
    public bool LikelySilent => Peak < 0.005 || SilenceRatio >= 0.995;
    public bool LikelyClipping => ClippingRatio >= 0.01;
}

internal static class AudioFileDiagnostics
{
    private const float SilenceThreshold = 0.005f;
    private const float ClippingThreshold = 0.99f;

    public static AudioFileMetrics Analyze(string path)
    {
        using var reader = new WaveFileReader(path);
        var provider = reader.ToSampleProvider();
        var buffer = new float[4_096];
        long sampleCount = 0;
        long silentSamples = 0;
        long clippedSamples = 0;
        double sumSquares = 0;
        float peak = 0;

        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                var absolute = Math.Abs(buffer[index]);
                peak = Math.Max(peak, absolute);
                sumSquares += buffer[index] * buffer[index];
                if (absolute < SilenceThreshold)
                    silentSamples++;
                if (absolute >= ClippingThreshold)
                    clippedSamples++;
            }
            sampleCount += read;
        }

        return new AudioFileMetrics(
            reader.WaveFormat.ToString(),
            new FileInfo(path).Length,
            reader.TotalTime.TotalMilliseconds,
            sampleCount,
            peak,
            sampleCount == 0 ? 0 : Math.Sqrt(sumSquares / sampleCount),
            sampleCount == 0 ? 1 : (double)silentSamples / sampleCount,
            sampleCount == 0 ? 0 : (double)clippedSamples / sampleCount);
    }
}
