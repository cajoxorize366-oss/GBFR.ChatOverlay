using System.Diagnostics;
using System.Text;

namespace GBFR.ChatOverlay.SttWorker;

internal sealed class WhisperCliTranscriber
{
    private const int MaximumDiagnosticCharacters = 4_000;
    private static readonly TimeSpan InferenceTimeout = TimeSpan.FromSeconds(120);

    private readonly WorkerOptions _options;
    private readonly WorkerDiagnostics _diagnostics;

    public WhisperCliTranscriber(WorkerOptions options, WorkerDiagnostics diagnostics)
    {
        _options = options;
        _diagnostics = diagnostics;
    }

    public async Task<string> TranscribeAsync(
        string audioPath,
        long requestId,
        CancellationToken cancellationToken)
    {
        var outputStem = Path.Combine(_diagnostics.WorkDirectory, $"request-{requestId}.whisper");
        var outputPath = outputStem + ".txt";
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.WhisperExecutable,
            WorkingDirectory = _diagnostics.WorkDirectory,
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
        if (_diagnostics.PreserveArtifacts)
        {
            startInfo.ArgumentList.Add("-ojf");
            startInfo.ArgumentList.Add("-pp");
            startInfo.ArgumentList.Add("--print-confidence");
        }
        else
        {
            startInfo.ArgumentList.Add("-np");
        }
        startInfo.ArgumentList.Add("-otxt");
        AddArgument(startInfo, "-of", outputStem);

        _diagnostics.WriteText(
            $"request-{requestId}.whisper-command.txt",
            FormatCommand(startInfo));
        _diagnostics.Log(
            $"request={requestId} whisper start language={_options.Language} " +
            $"threads={_options.ThreadCount} audio=\"{audioPath}\"");

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

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
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
            var cancelledStdout = await ReadDiagnosticTaskAsync(stdoutTask).ConfigureAwait(false);
            var cancelledStderr = await ReadDiagnosticTaskAsync(stderrTask).ConfigureAwait(false);
            WriteProcessDiagnostics(requestId, cancelledStdout, cancelledStderr, exitCode: null);
            DeleteOutputsUnlessPreserved(outputPath, outputStem + ".json");
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("Whisper base inference exceeded 120 seconds.");
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        WriteProcessDiagnostics(requestId, stdout, stderr, process.ExitCode);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            DeleteOutputsUnlessPreserved(outputPath, outputStem + ".json");
            throw new InvalidOperationException(
                $"whisper-cli.exe exited with code {process.ExitCode}: {Truncate(detail.Trim())}");
        }
        if (!File.Exists(outputPath))
        {
            DeleteOutputsUnlessPreserved(outputPath, outputStem + ".json");
            throw new InvalidDataException("whisper-cli.exe did not create its transcript file.");
        }

        try
        {
            var transcript = (await File.ReadAllTextAsync(outputPath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false)).Trim();
            _diagnostics.Log(
                $"request={requestId} whisper complete exitCode={process.ExitCode} text=\"{Truncate(transcript)}\"");
            return transcript;
        }
        finally
        {
            if (!_diagnostics.PreserveArtifacts)
            {
                TryDelete(outputPath);
                TryDelete(outputStem + ".json");
            }
        }
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private void WriteProcessDiagnostics(long requestId, string stdout, string stderr, int? exitCode)
    {
        if (!_diagnostics.PreserveArtifacts)
            return;

        _diagnostics.WriteText($"request-{requestId}.whisper-stdout.log", stdout);
        _diagnostics.WriteText($"request-{requestId}.whisper-stderr.log", stderr);
        _diagnostics.WriteJson(
            $"request-{requestId}-whisper-process.json",
            new
            {
                requestId,
                exitCode,
                stdoutCharacters = stdout.Length,
                stderrCharacters = stderr.Length,
                language = _options.Language,
                threads = _options.ThreadCount,
            });
    }

    private void DeleteOutputsUnlessPreserved(params string[] paths)
    {
        if (_diagnostics.PreserveArtifacts)
            return;
        foreach (var path in paths)
            TryDelete(path);
    }

    private static async Task<string> ReadDiagnosticTaskAsync(Task<string> task)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            return "<diagnostic stream did not finish>";
        }
    }

    private static string FormatCommand(ProcessStartInfo startInfo) =>
        string.Join(
            " ",
            new[] { startInfo.FileName }
                .Concat(startInfo.ArgumentList)
                .Select(QuoteArgument));

    private static string QuoteArgument(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? '"' + value.Replace("\"", "\\\"") + '"'
            : value;

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
