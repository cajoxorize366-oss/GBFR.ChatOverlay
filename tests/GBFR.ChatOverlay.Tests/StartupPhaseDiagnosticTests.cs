using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class StartupPhaseDiagnosticTests
{
    [Fact]
    public void Run_LogsBeginAndComplete()
    {
        var logs = new List<string>();

        var result = StartupPhaseDiagnostic.Run("test-phase", logs.Add, () => 42);

        Assert.Equal(42, result);
        Assert.Contains(logs, message => message.Contains("state=begin", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("state=complete", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_LogsFailureAndRethrows()
    {
        var logs = new List<string>();

        Assert.Throws<InvalidOperationException>(() =>
            StartupPhaseDiagnostic.Run(
                "test-phase",
                logs.Add,
                () => throw new InvalidOperationException("failed")));

        Assert.Contains(logs, message =>
            message.Contains("state=failed", StringComparison.Ordinal) &&
            message.Contains("InvalidOperationException", StringComparison.Ordinal));
    }
}
