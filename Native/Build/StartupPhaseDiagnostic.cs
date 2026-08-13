using System.Diagnostics;

namespace GBFR.ChatOverlay.Native;

internal static class StartupPhaseDiagnostic
{
    internal static T Run<T>(string phase, Action<string> log, Func<T> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(action);

        var startedAt = Stopwatch.GetTimestamp();
        SafeLog(log, $"Startup phase={phase} state=begin.");
        try
        {
            var result = action();
            SafeLog(
                log,
                $"Startup phase={phase} state=complete " +
                $"elapsed_ms={(long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds}.");
            return result;
        }
        catch (Exception exception)
        {
            SafeLog(
                log,
                $"Startup phase={phase} state=failed " +
                $"elapsed_ms={(long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds} " +
                $"error={exception.GetType().Name} message=\"{Sanitize(exception.Message)}\".");
            throw;
        }
    }

    internal static void Run(string phase, Action<string> log, Action action) =>
        Run(
            phase,
            log,
            () =>
            {
                action();
                return true;
            });

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
            // Startup diagnostics must never affect hook installation.
        }
    }
}
