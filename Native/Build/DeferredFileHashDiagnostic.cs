using System.Diagnostics;
using System.Security.Cryptography;

namespace GBFR.ChatOverlay.Native;

internal static class DeferredFileHashDiagnostic
{
    internal static Task Start(
        string label,
        string? path,
        string expectedSha256,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentNullException.ThrowIfNull(log);
        return StartCore(
            token => ComputeSha256Async(path, token),
            label,
            path,
            expectedSha256,
            log,
            cancellationToken);
    }

    internal static Task StartCore(
        Func<CancellationToken, Task<string>> computeHash,
        string label,
        string? path,
        string expectedSha256,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(computeHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentNullException.ThrowIfNull(log);

        return Task.Run(async () =>
        {
            var startedAt = Stopwatch.GetTimestamp();
            var pathDetail = string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : $" path=\"{Sanitize(path)}\"";
            SafeLog(
                log,
                $"Startup phase={label}-sha256 state=begin mode=deferred-diagnostic " +
                $"diagnostic_only=true{pathDetail}.");
            try
            {
                var sha256 = await computeHash(cancellationToken).ConfigureAwait(false);
                var expectedHashMatch = string.Equals(
                    sha256,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase);
                SafeLog(
                    log,
                    $"Startup phase={label}-sha256 state=complete " +
                    $"elapsed_ms={ElapsedMilliseconds(startedAt)} sha256={sha256} " +
                    $"expected_hash_match={(expectedHashMatch ? "true" : "false")} " +
                    "diagnostic_only=true.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SafeLog(
                    log,
                    $"Startup phase={label}-sha256 state=failed " +
                    $"elapsed_ms={ElapsedMilliseconds(startedAt)} reason=cancelled " +
                    "diagnostic_only=true.");
            }
            catch (Exception exception)
            {
                SafeLog(
                    log,
                    $"Startup phase={label}-sha256 state=failed " +
                    $"elapsed_ms={ElapsedMilliseconds(startedAt)} " +
                    $"error={exception.GetType().Name} message=\"{Sanitize(exception.Message)}\" " +
                    "diagnostic_only=true.");
            }
        }, CancellationToken.None);
    }

    internal static async Task<string> ComputeSha256Async(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new FileNotFoundException("The diagnostic file path is unavailable.");

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest);
    }

    private static long ElapsedMilliseconds(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');

    private static void SafeLog(Action<string> log, string message)
    {
        try
        {
            log(message);
        }
        catch
        {
            // Diagnostics must never affect loader or hook lifetime.
        }
    }
}
