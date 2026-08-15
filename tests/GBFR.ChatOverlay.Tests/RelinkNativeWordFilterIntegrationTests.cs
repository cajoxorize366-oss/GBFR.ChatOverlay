using System.IO;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkNativeWordFilterIntegrationTests
{
    [Fact]
    public void NativeBridge_PublishesRawTextOnlyFromWordFilterCompletionCallbacks()
    {
        var source = NormalizeLineEndings(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Native",
            "Chat",
            "RelinkChatBridge.cs")));

        Assert.Contains(
            "CreateHook<WordFilterCallbackDelegate>(\n                    FilteredSendMessage,\n                    moduleBase + rvas.FilteredSendCallback)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateHook<WordFilterCallbackDelegate>(\n                    FilteredReceiveMessage,\n                    moduleBase + rvas.FilteredReceiveCallback)",
            source,
            StringComparison.Ordinal);

        var sendTransport = Slice(
            source,
            "public ChatSendResult Send(string message)",
            "public ChatSendResult SendOfficialQuickAction");
        Assert.Contains("EnqueuePendingFilteredSend(", sendTransport, StringComparison.Ordinal);
        Assert.DoesNotContain("_echoSuppressor.Register", sendTransport, StringComparison.Ordinal);
        Assert.DoesNotContain("CompleteLocalSend", sendTransport, StringComparison.Ordinal);

        var sendDetour = Slice(
            source,
            "private void SendMessage(",
            "private static bool TryReadOutgoingText");
        Assert.Contains("EnqueuePendingFilteredSend(", sendDetour, StringComparison.Ordinal);
        Assert.DoesNotContain("_echoSuppressor.Register", sendDetour, StringComparison.Ordinal);
        Assert.DoesNotContain("CompleteLocalSend", sendDetour, StringComparison.Ordinal);

        var filteredSend = Slice(
            source,
            "private void FilteredSendMessage(",
            "private void FilteredReceiveMessage(");
        Assert.Contains("_echoSuppressor.Register(finalText", filteredSend, StringComparison.Ordinal);
        Assert.Contains("CompleteLocalSend(", filteredSend, StringComparison.Ordinal);

        var receiveDetour = Slice(
            source,
            "private void RpcMessage(",
            "private void PublishFilteredIncoming(");
        Assert.DoesNotContain("EnqueueIncoming(", receiveDetour, StringComparison.Ordinal);
        Assert.Contains("EnqueuePendingFilteredReceive(", receiveDetour, StringComparison.Ordinal);

        var filteredReceive = Slice(
            source,
            "private void FilteredReceiveMessage(",
            "private static bool TryDecodeFilteredReceive(");
        var originalIndex = filteredReceive.IndexOf("OriginalFunction", StringComparison.Ordinal);
        var publishIndex = filteredReceive.IndexOf("PublishFilteredIncoming", StringComparison.Ordinal);
        Assert.True(originalIndex >= 0, "The native filtered-receive callback is not forwarded.");
        Assert.True(publishIndex > originalIndex, "Overlay publication must happen after Relink accepts the final text.");
        Assert.Contains("_pendingFilteredReceives.TryTake(", filteredReceive, StringComparison.Ordinal);

        var sendQueueHelper = Slice(
            source,
            "private long EnqueuePendingFilteredSend(",
            "private long EnqueuePendingFilteredReceive(");
        Assert.Contains("lock (_filterPipelineSync)", sendQueueHelper, StringComparison.Ordinal);
        Assert.Contains("_pendingFilteredSends.Enqueue", sendQueueHelper, StringComparison.Ordinal);
    }

    [Fact]
    public void SendDetour_UsesPolicyNormalizedCategoryBeforeOriginalFunction()
    {
        var source = NormalizeLineEndings(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Native",
            "Chat",
            "RelinkChatBridge.cs")));
        var sendDetour = Slice(
            source,
            "private void SendMessage(",
            "private static bool TryReadOutgoingText");

        var policyIndex = sendDetour.IndexOf("RelinkOutgoingChatPolicy.", StringComparison.Ordinal);
        var originalCallIndex = sendDetour.IndexOf("_sendHook!.OriginalFunction(", StringComparison.Ordinal);

        Assert.True(policyIndex >= 0, "SendMessage detour must use RelinkOutgoingChatPolicy.");
        Assert.True(originalCallIndex > policyIndex, "OriginalFunction must be called after the outgoing category policy.");
        Assert.Contains("forwardedCategory", sendDetour[originalCallIndex..], StringComparison.Ordinal);
    }

    [Fact]
    public void Mod_ExposesNativeWordFilterSynchronizationToPageFour()
    {
        var source = NormalizeLineEndings(
            File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Mod.cs")));

        Assert.Contains(
            "isRelinkWordFilterSynchronized: () =>\n                _nativeChatBridge?.IsNativeWordFilterSynchronized == true",
            source,
            StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker was not found: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker was not found: {endMarker}");
        return source[start..end];
    }

    private static string NormalizeLineEndings(string source) =>
        source.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GBFR.ChatOverlay.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Unable to locate the GBFR.ChatOverlay repository root from the test output directory.");
    }
}
