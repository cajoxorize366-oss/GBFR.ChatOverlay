using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace GBFR.ChatOverlay.Native;

internal interface IRelinkPlayerNameNativeApi
{
    bool TryResolveMemberSlot(uint senderId, out int memberSlot);

    nint GetLobbyMember(nint manager, int memberSlot);
}

/// <summary>
/// Resolves the opaque sender identifier carried by Relink's chat RPC through the
/// same four-slot member table used by the verified 2.0.4 executable when it exports
/// <c>member_name</c> to the online UI.
/// </summary>
internal sealed class RelinkPlayerNameResolver
{
    internal const int MaximumPlayerNameBytes = 64;

    private const int MemberProfileOffset = 0x5E60;
    private const int MemberActiveOffset = 0x5EBC;
    private const int ProfileNameOffset = 0x208;
    private const int NativeStringBytes = 0x20;
    private const int NativeStringLengthOffset = 0x10;
    private const int NativeStringCapacityOffset = 0x18;
    private const ulong NativeStringInlineCapacity = 0x0F;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly nint _memberManagerSlot;
    private readonly IRelinkMemoryReader _memory;
    private readonly IRelinkPlayerNameNativeApi _native;
    private readonly Action<string> _log;
    private int _failureLogged;

    internal RelinkPlayerNameResolver(
        nint memberManagerSlot,
        IRelinkMemoryReader memory,
        IRelinkPlayerNameNativeApi native,
        Action<string> log)
    {
        _memberManagerSlot = memberManagerSlot;
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal static RelinkPlayerNameResolver CreateForCurrentProcess(
        nint moduleBase,
        RelinkChatRvas rvas,
        Action<string> log)
    {
        if (moduleBase == nint.Zero ||
            rvas.SenderSlotResolver <= 0 ||
            rvas.LobbyMemberLookup <= 0 ||
            rvas.LobbyMemberManagerSlot <= 0)
        {
            throw new InvalidOperationException("Relink player-name resolver RVAs are unavailable.");
        }

        return new RelinkPlayerNameResolver(
            moduleBase + rvas.LobbyMemberManagerSlot,
            new CurrentProcessRelinkMemoryReader(),
            new CurrentProcessRelinkPlayerNameNativeApi(moduleBase, rvas),
            log);
    }

    internal bool TryResolve(uint senderId, out string playerName)
    {
        playerName = string.Empty;
        try
        {
            if (!TryResolveMemberSlot(senderId, out var memberSlot))
                return false;
            return TryResolveName(memberSlot, senderId, out playerName);
        }
        catch (Exception exception)
        {
            playerName = string.Empty;
            LogFailureOnce(
                senderId,
                $"the resolver failed closed with {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal bool TryResolveMemberSlot(uint senderId, out int memberSlot)
    {
        memberSlot = -1;
        try
        {
            if (!_native.TryResolveMemberSlot(senderId, out memberSlot) || memberSlot is < 0 or >= 4)
            {
                memberSlot = -1;
                LogFailureOnce(senderId, "the member slot was unavailable");
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            memberSlot = -1;
            LogFailureOnce(
                senderId,
                $"the member-slot resolver failed closed with {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal bool TryResolveName(int memberSlot, uint senderId, out string playerName)
    {
        playerName = string.Empty;
        try
        {
            if (memberSlot is < 0 or >= 4 ||
                !_memory.TryReadPointer(_memberManagerSlot, out var memberManager) ||
                memberManager == nint.Zero)
            {
                LogFailureOnce(senderId, "the member slot or lobby manager was unavailable");
                return false;
            }

            var member = _native.GetLobbyMember(memberManager, memberSlot);
            if (member == nint.Zero ||
                !TryAdd(member, MemberActiveOffset, out var memberActiveAddress))
            {
                LogFailureOnce(senderId, "the mapped lobby member was unavailable");
                return false;
            }

            Span<byte> active = stackalloc byte[1];
            if (!_memory.TryReadBytes(memberActiveAddress, active) || active[0] == 0 ||
                !TryAdd(member, MemberProfileOffset, out var memberProfileAddress) ||
                !_memory.TryReadPointer(memberProfileAddress, out var memberProfile) ||
                memberProfile == nint.Zero ||
                !TryAdd(memberProfile, ProfileNameOffset, out var playerNameAddress) ||
                !TryReadNativeString(playerNameAddress, out playerName))
            {
                playerName = string.Empty;
                LogFailureOnce(senderId, "the lobby member name was empty or unreadable");
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            playerName = string.Empty;
            LogFailureOnce(
                senderId,
                $"the resolver failed closed with {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private bool TryReadNativeString(nint nativeString, out string value)
    {
        value = string.Empty;
        Span<byte> layout = stackalloc byte[NativeStringBytes];
        if (!_memory.TryReadBytes(nativeString, layout))
            return false;

        var length = BinaryPrimitives.ReadUInt64LittleEndian(layout[NativeStringLengthOffset..]);
        var capacity = BinaryPrimitives.ReadUInt64LittleEndian(layout[NativeStringCapacityOffset..]);
        if (length is 0 or > MaximumPlayerNameBytes || capacity < length)
            return false;

        nint data;
        if (capacity < 0x10)
        {
            if (capacity != NativeStringInlineCapacity)
                return false;
            data = nativeString;
        }
        else
        {
            if (capacity > 0x10000)
                return false;

            data = IntPtr.Size switch
            {
                sizeof(long) => (nint)BinaryPrimitives.ReadInt64LittleEndian(layout),
                sizeof(int) => (nint)BinaryPrimitives.ReadInt32LittleEndian(layout),
                _ => nint.Zero,
            };
            if (data == nint.Zero)
                return false;
        }

        Span<byte> utf8 = stackalloc byte[MaximumPlayerNameBytes + 1];
        var bytesToRead = checked((int)length + 1);
        var encodedName = utf8[..bytesToRead];
        if (!_memory.TryReadBytes(data, encodedName) || encodedName[^1] != 0)
            return false;

        try
        {
            value = StrictUtf8.GetString(encodedName[..^1]).Trim();
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            value = string.Empty;
            return false;
        }

        return true;
    }

    private void LogFailureOnce(uint senderId, string reason)
    {
        if (Interlocked.Exchange(ref _failureLogged, 1) != 0)
            return;

        try
        {
            _log(
                $"Relink player-name resolver could not map sender 0x{senderId:X8}; " +
                $"the stable Player fallback was kept because {reason}. Further failures are suppressed.");
        }
        catch
        {
            // Never allow a logger failure to escape the native receive hook.
        }
    }

    private static bool TryAdd(nint address, int offset, out nint result)
    {
        result = nint.Zero;
        if (address == nint.Zero)
            return false;

        try
        {
            result = checked(address + offset);
            return result != nint.Zero;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private sealed class CurrentProcessRelinkPlayerNameNativeApi : IRelinkPlayerNameNativeApi
    {
        private readonly SenderSlotResolverDelegate _senderSlotResolver;
        private readonly LobbyMemberLookupDelegate _lobbyMemberLookup;

        internal CurrentProcessRelinkPlayerNameNativeApi(nint moduleBase, RelinkChatRvas rvas)
        {
            _senderSlotResolver = Marshal.GetDelegateForFunctionPointer<SenderSlotResolverDelegate>(
                moduleBase + rvas.SenderSlotResolver);
            _lobbyMemberLookup = Marshal.GetDelegateForFunctionPointer<LobbyMemberLookupDelegate>(
                moduleBase + rvas.LobbyMemberLookup);
        }

        public bool TryResolveMemberSlot(uint senderId, out int memberSlot) =>
            _senderSlotResolver(senderId, out memberSlot);

        public nint GetLobbyMember(nint manager, int memberSlot) =>
            _lobbyMemberLookup(manager, memberSlot);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SenderSlotResolverDelegate(uint senderId, out int memberSlot);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint LobbyMemberLookupDelegate(nint manager, int memberSlot);
    }
}
