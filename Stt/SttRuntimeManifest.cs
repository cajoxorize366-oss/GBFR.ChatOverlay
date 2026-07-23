namespace GBFR.ChatOverlay.Stt;

public sealed record SttWorkerLaunchOptions(
    string RuntimeRoot,
    string WorkerExecutable,
    string WhisperExecutable,
    string ModelFile,
    string ModelSha256,
    string Language,
    string MicrophoneSelector,
    bool DiagnosticsEnabled,
    string DiagnosticsDirectory,
    int ThreadCount,
    int MaximumCaptureSeconds);

public static class SttRuntimeManifest
{
    public const string RuntimeDirectoryName = "SttRuntime";
    public const string WhisperVersion = "1.9.1";
    public const string ModelName = "base";
    public const string DefaultLanguageCode = "zh";

    public const string ModelSha256 = "60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE";

    public static bool TryResolve(
        string modDirectory,
        string language,
        int threadCount,
        int maximumCaptureSeconds,
        out SttWorkerLaunchOptions? options,
        out string? error) =>
        TryResolve(
            modDirectory,
            language,
            AudioCaptureDeviceSelector.DefaultSelector,
            diagnosticsEnabled: false,
            Path.Combine(modDirectory, "STT-Debug"),
            threadCount,
            maximumCaptureSeconds,
            out options,
            out error);

    public static bool TryResolve(
        string modDirectory,
        string language,
        string? microphoneSelector,
        bool diagnosticsEnabled,
        string diagnosticsDirectory,
        int threadCount,
        int maximumCaptureSeconds,
        out SttWorkerLaunchOptions? options,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsDirectory);

        var runtimeRoot = Path.GetFullPath(Path.Combine(modDirectory, RuntimeDirectoryName));
        var workerExecutable = Path.Combine(runtimeRoot, "worker", "GBFR.ChatOverlay.SttWorker.exe");
        var whisperExecutable = Path.Combine(runtimeRoot, "whisper", "whisper-cli.exe");
        var modelFile = Path.Combine(runtimeRoot, "models", "ggml-base.bin");

        var missing = new List<string>();
        if (!File.Exists(workerExecutable))
            missing.Add(workerExecutable);
        if (!File.Exists(whisperExecutable))
            missing.Add(whisperExecutable);
        if (!File.Exists(modelFile))
            missing.Add(modelFile);

        if (missing.Count > 0)
        {
            options = null;
            error = "STT runtime is incomplete. Missing: " + string.Join(", ", missing.Select(Path.GetFileName));
            return false;
        }

        options = new SttWorkerLaunchOptions(
            runtimeRoot,
            workerExecutable,
            whisperExecutable,
            modelFile,
            ModelSha256,
            NormalizeLanguage(language),
            NormalizeMicrophoneSelector(microphoneSelector),
            diagnosticsEnabled,
            Path.GetFullPath(diagnosticsDirectory),
            Math.Clamp(threadCount, 1, 16),
            Math.Clamp(maximumCaptureSeconds, 3, 30));
        error = null;
        return true;
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return DefaultLanguageCode;

        var value = language.Trim().ToLowerInvariant();
        return value.Length <= 16 && value.All(character => char.IsAsciiLetter(character) || character is '-' or '_')
            ? value
            : DefaultLanguageCode;
    }

    private static string NormalizeMicrophoneSelector(string? selector)
    {
        var value = selector?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 2_048
            ? AudioCaptureDeviceSelector.DefaultSelector
            : value;
    }
}
