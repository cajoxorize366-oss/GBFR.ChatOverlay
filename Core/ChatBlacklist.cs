namespace GBFR.ChatOverlay.Core;

/// <summary>
/// Room-scoped receive blacklist. Player numbers use the UI convention (1-4),
/// while Relink's native member slots use zero-based values (0-3).
/// </summary>
public sealed class ChatBlacklist
{
    private const int FirstRemotePlayer = 2;
    private const int LastRemotePlayer = 4;
    private const int AllRemotePlayersMask = 0b111;
    private int _mutedPlayerMask;

    public bool IsMuted(int playerNumber)
    {
        if (playerNumber is < FirstRemotePlayer or > LastRemotePlayer)
            return false;

        return (Volatile.Read(ref _mutedPlayerMask) & GetMask(playerNumber)) != 0;
    }

    public bool IsMemberSlotMuted(int memberSlot) => IsMuted(memberSlot + 1);

    public bool SetMuted(int playerNumber, bool muted)
    {
        if (playerNumber is < FirstRemotePlayer or > LastRemotePlayer)
            return false;

        var mask = GetMask(playerNumber);
        if (muted)
            Interlocked.Or(ref _mutedPlayerMask, mask);
        else
            Interlocked.And(ref _mutedPlayerMask, ~mask);
        return true;
    }

    public bool Toggle(int playerNumber)
    {
        if (playerNumber is < FirstRemotePlayer or > LastRemotePlayer)
            return false;

        var mask = GetMask(playerNumber);
        while (true)
        {
            var current = Volatile.Read(ref _mutedPlayerMask);
            var updated = current ^ mask;
            if (Interlocked.CompareExchange(ref _mutedPlayerMask, updated, current) == current)
                return (updated & mask) != 0;
        }
    }

    public bool AreAllRemotePlayersMuted
    {
        get => (Volatile.Read(ref _mutedPlayerMask) & AllRemotePlayersMask) == AllRemotePlayersMask;
    }

    public bool ToggleAllRemotePlayers()
    {
        while (true)
        {
            var current = Volatile.Read(ref _mutedPlayerMask);
            var muted = (current & AllRemotePlayersMask) != AllRemotePlayersMask;
            var updated = muted ? AllRemotePlayersMask : 0;
            if (Interlocked.CompareExchange(ref _mutedPlayerMask, updated, current) == current)
                return muted;
        }
    }

    public void Clear()
    {
        Interlocked.Exchange(ref _mutedPlayerMask, 0);
    }

    private static int GetMask(int playerNumber) => 1 << (playerNumber - FirstRemotePlayer);
}
