using GBFR.ChatOverlay.Stt;

namespace GBFR.ChatOverlay.SttWorker;

internal sealed class ProtocolWriter
{
    private readonly object _sync = new();
    private int _closed;

    public bool Write(SttEvent message)
    {
        if (Volatile.Read(ref _closed) != 0)
            return false;

        var line = SttProtocol.Serialize(message);
        lock (_sync)
        {
            if (_closed != 0)
                return false;

            try
            {
                Console.Out.WriteLine(line);
                Console.Out.Flush();
                return true;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                Volatile.Write(ref _closed, 1);
                return false;
            }
        }
    }
}
