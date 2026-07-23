namespace GBFR.ChatOverlay.Stt;

public interface ISttWorkerClient : IDisposable
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }

    bool TrySend(SttCommand command);
    bool TryRead(out SttEvent message);
}
