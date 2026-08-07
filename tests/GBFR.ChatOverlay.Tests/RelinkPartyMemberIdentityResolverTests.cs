using System.Buffers.Binary;
using System.Text;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkPartyMemberIdentityResolverTests
{
    private static readonly nint ManagerSlot = (nint)0x1000;
    private static readonly nint Manager = (nint)0x100000;

    [Theory]
    [InlineData(0, 0, 0x1C128)]
    [InlineData(1, 2, 0x1C288)]
    public void TryResolveSlot_ReadsEntityIdFromTheNativeSelectedBank(
        byte onlineState,
        int memberSlot,
        int bankOffset)
    {
        var memory = new TestMemoryReader();
        memory.WritePointer(ManagerSlot, Manager);
        memory.WriteByte(Manager + 0x6CCE8, onlineState);
        var expected = $"entity-player-{memberSlot + 1}";
        WriteInlineString(
            memory,
            Manager + bankOffset + memberSlot * 0x58 + 0x28,
            expected);
        var resolver = new RelinkPartyMemberIdentityResolver(ManagerSlot, memory);

        Assert.True(resolver.TryResolveSlot(memberSlot, out var entityId));
        Assert.Equal(expected, entityId);
    }

    [Fact]
    public void TryResolveSlot_ReadsHeapBackedUtf8EntityId()
    {
        var memory = new TestMemoryReader();
        memory.WritePointer(ManagerSlot, Manager);
        memory.WriteByte(Manager + 0x6CCE8, 1);
        var nativeString = Manager + 0x1C288 + 0x58 + 0x28;
        var data = (nint)0x200000;
        var expected = "0123456789abcdef-玩家-entity-id";
        WriteHeapString(memory, nativeString, data, expected);
        var resolver = new RelinkPartyMemberIdentityResolver(ManagerSlot, memory);

        Assert.True(resolver.TryResolveSlot(1, out var entityId));
        Assert.Equal(expected, entityId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void TryResolveSlot_RejectsSlotsOutsideTheVerifiedFourMemberTable(int memberSlot)
    {
        var memory = new TestMemoryReader();
        memory.WritePointer(ManagerSlot, Manager);
        memory.WriteByte(Manager + 0x6CCE8, 1);
        var resolver = new RelinkPartyMemberIdentityResolver(ManagerSlot, memory);

        Assert.False(resolver.TryResolveSlot(memberSlot, out _));
    }

    [Fact]
    public void TryResolveSnapshot_ReadsOneCoherentFourMemberBank()
    {
        var memory = new TestMemoryReader();
        memory.WritePointer(ManagerSlot, Manager);
        memory.WriteByte(Manager + 0x6CCE8, 1);
        for (var memberSlot = 0; memberSlot < 4; memberSlot++)
        {
            WriteInlineString(
                memory,
                Manager + 0x1C288 + memberSlot * 0x58 + 0x28,
                $"entity-{memberSlot + 1}");
        }
        var resolver = new RelinkPartyMemberIdentityResolver(ManagerSlot, memory);

        Assert.True(resolver.TryResolveSnapshot(out var entityIds));
        Assert.Equal(["entity-1", "entity-2", "entity-3", "entity-4"], entityIds);
    }

    [Fact]
    public void TryResolveSnapshot_AllowsUnoccupiedMemberSlots()
    {
        var memory = new TestMemoryReader();
        memory.WritePointer(ManagerSlot, Manager);
        memory.WriteByte(Manager + 0x6CCE8, 1);
        WriteInlineString(memory, Manager + 0x1C288 + 0x28, "owner");
        for (var memberSlot = 1; memberSlot < 4; memberSlot++)
            WriteEmptyString(memory, Manager + 0x1C288 + memberSlot * 0x58 + 0x28);
        var resolver = new RelinkPartyMemberIdentityResolver(ManagerSlot, memory);

        Assert.True(resolver.TryResolveSnapshot(out var entityIds));
        Assert.Equal(["owner", string.Empty, string.Empty, string.Empty], entityIds);
    }

    private static void WriteInlineString(TestMemoryReader memory, nint address, string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value);
        Assert.InRange(encoded.Length, 1, 15);
        var layout = new byte[0x20];
        encoded.CopyTo(layout, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(0x10), (ulong)encoded.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(0x18), 0x0F);
        memory.Write(address, layout);
    }

    private static void WriteHeapString(
        TestMemoryReader memory,
        nint address,
        nint data,
        string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value);
        Assert.InRange(encoded.Length, 16, RelinkPartyMemberIdentityResolver.MaximumEntityIdBytes);
        var layout = new byte[0x20];
        BinaryPrimitives.WriteInt64LittleEndian(layout, data);
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(0x10), (ulong)encoded.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(0x18), (ulong)encoded.Length);
        memory.Write(address, layout);
        var terminated = new byte[encoded.Length + 1];
        encoded.CopyTo(terminated, 0);
        memory.Write(data, terminated);
    }

    private static void WriteEmptyString(TestMemoryReader memory, nint address)
    {
        var layout = new byte[0x20];
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(0x18), 0x0F);
        memory.Write(address, layout);
    }

    private sealed class TestMemoryReader : IRelinkMemoryReader
    {
        private readonly Dictionary<nint, byte> _bytes = [];

        public bool TryReadPointer(nint address, out nint value)
        {
            Span<byte> bytes = stackalloc byte[nint.Size];
            if (!TryReadBytes(address, bytes))
            {
                value = nint.Zero;
                return false;
            }

            value = nint.Size == sizeof(long)
                ? (nint)BinaryPrimitives.ReadInt64LittleEndian(bytes)
                : (nint)BinaryPrimitives.ReadInt32LittleEndian(bytes);
            return true;
        }

        public bool TryReadBytes(nint address, Span<byte> destination)
        {
            for (var index = 0; index < destination.Length; index++)
            {
                if (!_bytes.TryGetValue(address + index, out destination[index]))
                    return false;
            }
            return true;
        }

        internal void WritePointer(nint address, nint value)
        {
            Span<byte> bytes = stackalloc byte[nint.Size];
            if (nint.Size == sizeof(long))
                BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            else
                BinaryPrimitives.WriteInt32LittleEndian(bytes, checked((int)value));
            Write(address, bytes);
        }

        internal void WriteByte(nint address, byte value) => _bytes[address] = value;

        internal void Write(nint address, ReadOnlySpan<byte> source)
        {
            for (var index = 0; index < source.Length; index++)
                _bytes[address + index] = source[index];
        }
    }
}
