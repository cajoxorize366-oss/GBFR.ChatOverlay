namespace GBFR.ChatOverlay.SttWorker;

internal sealed record WorkerOptions(
    string WhisperExecutable,
    string ModelFile,
    string ModelSha256,
    string Language,
    string DeviceSelector,
    bool DiagnosticsEnabled,
    string DiagnosticsDirectory,
    int ThreadCount,
    int MaximumCaptureSeconds,
    string WorkDirectory)
{
    public static WorkerOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Worker arguments must be --name value pairs.");
            values.Add(args[index], args[index + 1]);
        }

        var whisper = GetRequiredPath(values, "--whisper");
        var model = GetRequiredPath(values, "--model");
        var workDirectory = GetRequiredPath(values, "--work-directory");
        var modelSha256 = GetRequired(values, "--model-sha256");
        if (modelSha256.Length != 64 || !modelSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("--model-sha256 must be a 64-character hexadecimal SHA-256 value.");

        var language = GetRequired(values, "--language").Trim().ToLowerInvariant();
        if (language.Length is 0 or > 16 ||
            language.Any(character => !char.IsAsciiLetter(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("--language is invalid.");
        }

        var deviceSelector = GetRequired(values, "--device").Trim();
        if (deviceSelector.Length > 2_048)
            throw new ArgumentException("--device is too long.");
        var diagnosticsEnabled = ParseBoolean(values, "--diagnostics");
        var diagnosticsDirectory = GetRequiredPath(values, "--diagnostics-directory");

        var threadCount = ParseBoundedInt(values, "--threads", 1, 16);
        var maximumSeconds = ParseBoundedInt(values, "--max-seconds", 3, 30);

        return new WorkerOptions(
            whisper,
            model,
            modelSha256,
            language,
            deviceSelector,
            diagnosticsEnabled,
            diagnosticsDirectory,
            threadCount,
            maximumSeconds,
            workDirectory);
    }

    private static string GetRequired(Dictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Required worker argument {name} is missing.");
        return value;
    }

    private static string GetRequiredPath(Dictionary<string, string> values, string name) =>
        Path.GetFullPath(GetRequired(values, name));

    private static int ParseBoundedInt(
        Dictionary<string, string> values,
        string name,
        int minimum,
        int maximum)
    {
        var raw = GetRequired(values, name);
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {minimum} and {maximum}.");
        return value;
    }

    private static bool ParseBoolean(Dictionary<string, string> values, string name)
    {
        var raw = GetRequired(values, name);
        if (!bool.TryParse(raw, out var value))
            throw new ArgumentException($"{name} must be true or false.");
        return value;
    }
}
