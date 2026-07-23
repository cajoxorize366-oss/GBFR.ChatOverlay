namespace GBFR.ChatOverlay.Stt;

public sealed class UnavailableSttWorkerClient : ISttWorkerClient
{
    public UnavailableSttWorkerClient(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        UnavailableReason = reason;
    }

    public bool IsAvailable => false;
    public string UnavailableReason { get; }

    public bool TrySend(SttCommand command) => false;

    public bool TryRead(out SttEvent message)
    {
        message = null!;
        return false;
    }

    public void Dispose() { }
}
