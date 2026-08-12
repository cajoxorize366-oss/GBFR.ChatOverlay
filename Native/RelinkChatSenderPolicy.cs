using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Native;

internal static class RelinkChatSenderPolicy
{
    internal static bool IsLocalRpc(int partyMemberSlot, int localMemberSlot) =>
        localMemberSlot is >= 0 and <= 3 &&
        partyMemberSlot is >= 0 and <= 3 &&
        partyMemberSlot == localMemberSlot;

    internal static bool ShouldBlockBlacklistedRpc(
        int partyMemberSlot,
        int localMemberSlot,
        ChatBlacklist blacklist)
    {
        if (!PartyMemberSlotMap.IsValidSlot(partyMemberSlot) ||
            !PartyMemberSlotMap.IsValidSlot(localMemberSlot))
        {
            return false;
        }

        if (partyMemberSlot == localMemberSlot)
            return false;

        return blacklist.IsMemberSlotMuted(partyMemberSlot, localMemberSlot);
    }

    internal static bool TryConsumeAuthoritativeLocalEcho(
        RecentEchoSuppressor suppressor,
        bool isLocal,
        string text,
        DateTimeOffset now,
        out bool wasLocalEcho)
    {
        wasLocalEcho = false;
        if (!isLocal)
            return false;

        return suppressor.TryConsume(text, now, out wasLocalEcho);
    }
}

internal sealed class LocalChatIdentityCache
{
    private readonly object _sync = new();
    private readonly string _fallbackSender;
    private string? _sender;

    internal LocalChatIdentityCache(string fallbackSender = "Local")
    {
        _fallbackSender = string.IsNullOrWhiteSpace(fallbackSender) ? "Local" : fallbackSender.Trim();
    }

    internal LocalChatIdentity Read()
    {
        lock (_sync)
        {
            return new LocalChatIdentity(_sender ?? _fallbackSender, 1);
        }
    }

    internal void UpdateName(string? senderName)
    {
        if (string.IsNullOrWhiteSpace(senderName))
            return;

        lock (_sync)
        {
            _sender = senderName.Trim();
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _sender = null;
        }
    }
}
