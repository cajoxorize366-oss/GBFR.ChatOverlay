using System.Diagnostics;
using System.Text;

namespace GBFR.ChatOverlay.SttWorker;

internal sealed class WhisperCliTranscriber
{
    private const int MaximumDiagnosticCharacters = 4_000;
    private static readonly TimeSpan InferenceTimeout = TimeSpan.FromSeconds(120);

    private readonly WorkerOptions _options;

    public WhisperCliTranscriber(WorkerOptions options) => _options = options;

    public async Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        var outputStem = Path.Combine(_options.WorkDirectory, $"result-{Guid.NewGuid():N}");
        var outputPath = outputStem + ".txt";
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.WhisperExecutable,
            WorkingDirectory = _options.WorkDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        AddArgument(startInfo, "-m", _options.ModelFile);
        AddArgument(startInfo, "-f", audioPath);
        AddArgument(startInfo, "-l", _options.Language);
        AddArgument(startInfo, "-t", _options.ThreadCount.ToString());
        startInfo.ArgumentList.Add("-ng");
        startInfo.ArgumentList.Add("-nt");
        startInfo.ArgumentList.Add("-np");
        startInfo.ArgumentList.Add("-otxt");
        AddArgument(startInfo, "-of", outputStem);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("whisper-cli.exe did not start.");
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(InferenceTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            TryKill(process);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("Whisper base inference exceeded 120 seconds.");
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(
                $"whisper-cli.exe exited with code {process.ExitCode}: {Truncate(detail.Trim())}");
        }
        if (!File.Exists(outputPath))
            throw new InvalidDataException("whisper-cli.exe did not create its transcript file.");

        try
        {
            return (await File.ReadAllTextAsync(outputPath, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false))
                .Trim();
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaximumDiagnosticCharacters
            ? value
            : value[..MaximumDiagnosticCharacters] + "...";
}
