using System.Diagnostics;

namespace GBFR.ChatOverlay.Native;

/// <summary>
/// Resolves the verified Relink 2.0.2 native chat manager used as the first
/// argument of the send bridge. Online-room UI lifetime is tracked separately
/// from PlayFab Party state changes.
/// </summary>
public sealed class RelinkGameContextProbe
{
    private readonly IRelinkMemoryReader _memory;
    private readonly Action<string> _log;
    private int _readFailureLogged;

    private RelinkGameContextProbe(
        nint moduleBase,
        RelinkChatRvas chatRvas,
        IRelinkMemoryReader memory,
        Action<string> log)
    {
        ModuleBase = moduleBase;
        ChatRvas = chatRvas;
        ManagerSlot = moduleBase + chatRvas.ManagerSlot;
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal nint ModuleBase { get; }

    internal RelinkChatRvas ChatRvas { get; }

    internal nint ManagerSlot { get; }

    public static RelinkGameContextProbe CreateForCurrentProcess(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        using var process = Process.GetCurrentProcess();
        var mainModule = process.MainModule ??
            throw new InvalidOperationException("The game module is unavailable.");
        var chatRvas = RelinkBuildLocator.Resolve(mainModule.FileName);
        return new RelinkGameContextProbe(
            mainModule.BaseAddress,
            chatRvas,
            new CurrentProcessRelinkMemoryReader(),
            log);
    }

    internal static RelinkGameContextProbe CreateForTesting(
        nint moduleBase,
        RelinkChatRvas chatRvas,
        IRelinkMemoryReader memory,
        Action<string>? log = null) =>
        new(moduleBase, chatRvas, memory, log ?? (_ => { }));

    internal bool TryGetHudChatManager(out nint manager)
    {
        manager = nint.Zero;
        if (ManagerSlot == nint.Zero)
            return false;

        try
        {
            return _memory.TryReadPointer(ManagerSlot, out manager) && manager != nint.Zero;
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _readFailureLogged, 1) == 0)
            {
                try
                {
                    _log(
                        $"Relink HUD chat manager probe failed closed; further read failures are suppressed: " +
                        $"{exception.GetType().Name}: {exception.Message}.");
                }
                catch
                {
                    // Never allow a logger failure to escape the native send path.
                }
            }

            manager = nint.Zero;
            return false;
        }
    }
}
