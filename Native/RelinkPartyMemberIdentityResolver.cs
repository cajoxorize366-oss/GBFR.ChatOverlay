using System.Buffers.Binary;
using System.Text;

namespace GBFR.ChatOverlay.Native;

internal interface IRelinkPartyMemberIdentityResolver
{
    bool TryResolveSlot(int memberSlot, out string entityId);
}

internal interface IRelinkPartyMemberIdentitySnapshotResolver
{
    bool TryResolveSnapshot(out string[] entityIds);
}

/// <summary>
/// Reads the verified 2.0.3 four-member identity table used by Relink's own online-member
/// serializer. The selected bank and <c>member_entity_id</c> field are taken directly from
/// the validated native lookup callsite at RVA 0x003C773C.
/// </summary>
internal sealed class RelinkPartyMemberIdentityResolver :
    IRelinkPartyMemberIdentityResolver,
    IRelinkPartyMemberIdentitySnapshotResolver
{
    internal const int MaximumEntityIdBytes = 512;

    private const int MemberCount = 4;
    private const int MemberStride = 0x58;
    private const int OfflineMemberBankOffset = 0x1C128;
    private const int OnlineMemberBankOffset = 0x1C288;
    // The lookup's R9 predicate resolves to 0x1403AA460, which returns
    // byte [manager+0x6CCE8] and selects the 0x1C288 bank when nonzero.
    private const int OnlineStateOffset = 0x6CCE8;
    // 0x14026C960 copies the second MSVC std::string from source+0x28;
    // Relink serializes that copied field under the member_entity_id key.
    private const int EntityIdStringOffset = 0x28;
    private const int NativeStringBytes = 0x20;
    private const int NativeStringLengthOffset = 0x10;
    private const int NativeStringCapacityOffset = 0x18;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly nint _managerSlot;
    private readonly IRelinkMemoryReader _memory;

    internal RelinkPartyMemberIdentityResolver(nint managerSlot, IRelinkMemoryReader memory)
    {
        if (managerSlot == nint.Zero)
            throw new ArgumentException("The Relink party-member identity manager slot is null.", nameof(managerSlot));

        _managerSlot = managerSlot;
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    internal static RelinkPartyMemberIdentityResolver CreateForCurrentProcess(
        nint moduleBase,
        RelinkChatRvas rvas)
    {
        if (moduleBase == nint.Zero || rvas.PartyMemberIdentityManagerSlot <= 0)
            throw new InvalidOperationException("Relink party-member identity RVAs are unavailable.");

        return new RelinkPartyMemberIdentityResolver(
            moduleBase + rvas.PartyMemberIdentityManagerSlot,
            new CurrentProcessRelinkMemoryReader());
    }

    public bool TryResolveSlot(int memberSlot, out string entityId)
    {
        entityId = string.Empty;
        if (memberSlot is < 0 or >= MemberCount ||
            !_memory.TryReadPointer(_managerSlot, out var manager) ||
            manager == nint.Zero ||
            !TryReadByte(manager, OnlineStateOffset, out var onlineState))
        {
            return false;
        }

        var bankOffset = onlineState == 0 ? OfflineMemberBankOffset : OnlineMemberBankOffset;
        if (!TryAdd(manager, checked(bankOffset + memberSlot * MemberStride + EntityIdStringOffset), out var nativeString) ||
            !TryReadNativeString(nativeString, out entityId))
        {
            entityId = string.Empty;
            return false;
        }

        // The game may switch identity banks while a room is changing. Only publish a value
        // if both the manager pointer and the exact bank selector remained stable for the read.
        if (!_memory.TryReadPointer(_managerSlot, out var managerAfter) ||
            managerAfter != manager ||
            !TryReadByte(manager, OnlineStateOffset, out var onlineStateAfter) ||
            onlineStateAfter != onlineState)
        {
            entityId = string.Empty;
            return false;
        }

        return true;
    }

    public bool TryResolveSnapshot(out string[] entityIds)
    {
        entityIds = [];
        if (!_memory.TryReadPointer(_managerSlot, out var manager) ||
            manager == nint.Zero ||
            !TryReadByte(manager, OnlineStateOffset, out var onlineState))
        {
            return false;
        }

        var bankOffset = onlineState == 0 ? OfflineMemberBankOffset : OnlineMemberBankOffset;
        var snapshot = new string[MemberCount];
        for (var memberSlot = 0; memberSlot < MemberCount; memberSlot++)
        {
            if (!TryAdd(
                    manager,
                    checked(bankOffset + memberSlot * MemberStride + EntityIdStringOffset),
                    out var nativeString) ||
                !TryReadNativeString(nativeString, out snapshot[memberSlot], allowEmpty: true))
            {
                return false;
            }
        }

        // A room transition can replace the manager or switch the online/offline bank
        // between individual slot reads. Publish only one coherent four-member snapshot.
        if (!_memory.TryReadPointer(_managerSlot, out var managerAfter) ||
            managerAfter != manager ||
            !TryReadByte(manager, OnlineStateOffset, out var onlineStateAfter) ||
            onlineStateAfter != onlineState)
        {
            return false;
        }

        entityIds = snapshot;
        return true;
    }

    private bool TryReadNativeString(nint nativeString, out string value, bool allowEmpty = false)
    {
        value = string.Empty;
        Span<byte> layout = stackalloc byte[NativeStringBytes];
        if (!_memory.TryReadBytes(nativeString, layout))
            return false;

        var length = BinaryPrimitives.ReadUInt64LittleEndian(layout[NativeStringLengthOffset..]);
        var capacity = BinaryPrimitives.ReadUInt64LittleEndian(layout[NativeStringCapacityOffset..]);
        if (length > MaximumEntityIdBytes || capacity < length || capacity > 0x10000)
            return false;

        if (length == 0)
        {
            Span<byte> emptyLayoutAfter = stackalloc byte[NativeStringBytes];
            if (!allowEmpty ||
                !_memory.TryReadBytes(nativeString, emptyLayoutAfter) ||
                !layout.SequenceEqual(emptyLayoutAfter))
            {
                return false;
            }

            return true;
        }

        nint data;
        if (capacity < 0x10)
        {
            if (capacity != 0x0F)
                return false;
            data = nativeString;
        }
        else
        {
            data = nint.Size switch
            {
                sizeof(long) => (nint)BinaryPrimitives.ReadInt64LittleEndian(layout),
                sizeof(int) => (nint)BinaryPrimitives.ReadInt32LittleEndian(layout),
                _ => nint.Zero,
            };
            if (data == nint.Zero)
                return false;
        }

        var byteCount = checked((int)length + 1);
        Span<byte> utf8 = byteCount <= 513 ? stackalloc byte[byteCount] : new byte[byteCount];
        if (!_memory.TryReadBytes(data, utf8) || utf8[^1] != 0)
            return false;
        Span<byte> layoutAfter = stackalloc byte[NativeStringBytes];
        if (!_memory.TryReadBytes(nativeString, layoutAfter) || !layout.SequenceEqual(layoutAfter))
            return false;

        try
        {
            value = StrictUtf8.GetString(utf8[..^1]);
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

    private bool TryReadByte(nint address, int offset, out byte value)
    {
        value = 0;
        if (!TryAdd(address, offset, out var target))
            return false;

        Span<byte> buffer = stackalloc byte[1];
        if (!_memory.TryReadBytes(target, buffer))
            return false;

        value = buffer[0];
        return true;
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
}
