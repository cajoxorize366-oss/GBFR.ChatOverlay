using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GBFR.ChatOverlay.SttWorker;

internal sealed class WorkerDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly object _fileSync = new();

    public WorkerDiagnostics(WorkerOptions options)
    {
        RootDirectory = options.DiagnosticsDirectory;
        WorkDirectory = ResolveWritableDirectory(
            options.WorkDirectory,
            Path.Combine(Path.GetTempPath(), "GBFR.ChatOverlay", "STT-Work"),
            "worker scratch");

        if (options.DiagnosticsEnabled)
        {
            RootDirectory = ResolveWritableDirectory(
                options.DiagnosticsDirectory,
                GetFallbackDiagnosticsDirectory(),
                "diagnostics");
            try
            {
                var sessionDirectory = Path.Combine(
                    RootDirectory,
                    $"session-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(sessionDirectory);
                SessionDirectory = sessionDirectory;
                WorkDirectory = sessionDirectory;
                PreserveArtifacts = true;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[diagnostics] Could not create session directory: {exception.Message}");
            }
        }

        WriteMicrophoneSnapshot(writeFile: PreserveArtifacts);
        Log(
            $"worker started; diagnostics={PreserveArtifacts}; language={options.Language}; " +
            $"deviceSelector={options.DeviceSelector}; session={SessionDirectory ?? "disabled"}");
        if (PreserveArtifacts)
        {
            WriteJson(
                "session.json",
                new
                {
                    startedAt = DateTimeOffset.Now,
                    processId = Environment.ProcessId,
                    options.Language,
                    options.DeviceSelector,
                    options.ThreadCount,
                    options.MaximumCaptureSeconds,
                    options.WhisperExecutable,
                    options.ModelFile,
                    options.ModelSha256,
                });
        }
    }

    public string RootDirectory { get; }
    public string? SessionDirectory { get; }
    public string WorkDirectory { get; }
    public bool PreserveArtifacts { get; }

    public void Log(string message)
    {
        var line = $"[{DateTimeOffset.Now:O}] {message}";
        Console.Error.WriteLine(line);
        if (SessionDirectory is null)
            return;

        try
        {
            lock (_fileSync)
            {
                File.AppendAllText(
                    Path.Combine(SessionDirectory, "debug.log"),
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[diagnostics] Could not append debug.log: {exception.Message}");
        }
    }

    public void WriteText(string fileName, string text)
    {
        if (SessionDirectory is null)
            return;

        try
        {
            File.WriteAllText(
                Path.Combine(SessionDirectory, fileName),
                text,
                new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            Log($"Could not write diagnostic file {fileName}: {exception.Message}");
        }
    }

    public void WriteJson<T>(string fileName, T value)
    {
        if (SessionDirectory is null)
            return;
        WriteText(fileName, JsonSerializer.Serialize(value, JsonOptions));
    }

    private void WriteMicrophoneSnapshot(bool writeFile)
    {
        try
        {
            var snapshot = AudioDeviceCatalog.GetSnapshot();
            var json = JsonSerializer.Serialize(
                new { generatedAt = DateTimeOffset.Now, devices = snapshot },
                JsonOptions);
            if (writeFile)
            {
                File.WriteAllText(
                    Path.Combine(RootDirectory, "microphones.json"),
                    json,
                    new UTF8Encoding(false));
            }
            foreach (var device in snapshot)
            {
                Console.Error.WriteLine(
                    $"[microphone] default={device.IsDefault} name=\"{device.Name}\" id=\"{device.Id}\"");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[microphone] Enumeration failed: {exception.Message}");
        }
    }

    private static string ResolveWritableDirectory(
        string preferred,
        string fallback,
        string purpose)
    {
        Exception? lastError = null;
        foreach (var candidate in new[] { preferred, fallback }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                Directory.CreateDirectory(fullPath);
                var probe = Path.Combine(fullPath, $".write-test-{Guid.NewGuid():N}");
                using (new FileStream(
                           probe,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 1,
                           FileOptions.DeleteOnClose))
                {
                }
                return fullPath;
            }
            catch (Exception exception)
            {
                lastError = exception;
                Console.Error.WriteLine(
                    $"[diagnostics] {purpose} directory unavailable at \"{candidate}\": {exception.Message}");
            }
        }

        throw new IOException($"No writable {purpose} directory was available.", lastError);
    }

    private static string GetFallbackDiagnosticsDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData) ? Path.GetTempPath() : localAppData;
        return Path.Combine(root, "GBFR.ChatOverlay", "STT-Debug");
    }
}
