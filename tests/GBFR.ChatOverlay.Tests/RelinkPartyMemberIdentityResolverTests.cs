using System.Buffers.Binary;
using System.Text;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkPartyMemberIdentityResolverTests
{
    private static readonly nint ManagerSlot = (nint)0x1000;
    private static readonly nint Manager = (nint)0x100000;
    private const uint LocalMemberKeyBase = 0xA1000000;

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
        var resolver = CreateResolver(memory);

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
        var resolver = CreateResolver(memory);

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
        var resolver = CreateResolver(memory);

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
        var resolver = CreateResolver(memory);

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
        var resolver = CreateResolver(memory);

        Assert.True(resolver.TryResolveSnapshot(out var entityIds));
        Assert.Equal(["owner", string.Empty, string.Empty, string.Empty], entityIds);
    }

    [Theory]
    [InlineData(0, 0x6C828, 2)]
    [InlineData(1, 0x6C82C, 3)]
    public void TryResolveLocalMemberSlot_ReadsSelectedTable(
        byte onlineState,
        int tableOffset,
        int expectedSlot)
    {
        var memory = CreateLocalSlotMemory(onlineState, expectedSlot, tableOffset);
        var resolver = CreateResolver(memory, expectedSlot);

        Assert.True(resolver.TryResolveLocalMemberSlot(out var localMemberSlot));
        Assert.Equal(expectedSlot, localMemberSlot);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void TryResolveLocalMemberSlot_RejectsSlotsOutsideTheFourMemberTable(int localMemberSlot)
    {
        var memory = CreateLocalSlotMemory(1, localMemberSlot, 0x6C82C);
        var resolver = CreateResolver(memory, localMemberSlot);

        Assert.False(resolver.TryResolveLocalMemberSlot(out _));
    }

    [Fact]
    public void TryResolveLocalMemberSlot_RejectsUnsupportedOnlineState()
    {
        var memory = CreateLocalSlotMemory(2, 1, 0x6C830);
        var resolver = CreateResolver(memory, 1);

        Assert.False(resolver.TryResolveLocalMemberSlot(out _));
        Assert.False(resolver.TryResolveCoherentSnapshot(out _));
    }

    [Fact]
    public void TryResolveLocalMemberSlot_FailsWhenManagerChangesMidRead()
    {
        var memory = CreateLocalSlotMemory(1, 3, 0x6C82C);
        var resolver = CreateResolver(memory, 3);
        memory.OnRead = (address, length) =>
        {
            if (address == ManagerSlot && length == nint.Size)
                memory.WritePointer(ManagerSlot, Manager + 0x1000);
        };

        Assert.False(resolver.TryResolveLocalMemberSlot(out _));
    }

    [Fact]
    public void TryResolveLocalMemberSlot_FailsWhenOnlineStateChangesMidRead()
    {
        var memory = CreateLocalSlotMemory(1, 3, 0x6C82C);
        var resolver = CreateResolver(memory, 3);
        var stateReads = 0;
        memory.OnRead = (address, length) =>
        {
            if (address == Manager + 0x6CCE8 && length == 1 && ++stateReads == 1)
                memory.WriteByte(address, 0);
        };

        Assert.False(resolver.TryResolveLocalMemberSlot(out _));
    }

    [Fact]
    public void TryResolveLocalMemberSlot_FailsWhenLocalSlotChangesMidRead()
    {
        var memory = CreateLocalSlotMemory(1, 3, 0x6C82C);
        var resolver = CreateResolver(memory, 3, 2);
        var slotReads = 0;
        memory.OnRead = (address, length) =>
        {
            if (address == Manager + 0x6C82C && length == 4 && ++slotReads == 1)
                memory.WriteUInt32(address, LocalMemberKey(2));
        };

        Assert.False(resolver.TryResolveLocalMemberSlot(out _));
    }

    [Fact]
    public void TryResolveCoherentSnapshot_ReturnsFourEntityIdsAndSameBatchLocalSlot()
    {
        var memory = CreateCoherentSnapshotMemory(1, 3, 0x1C288);
        var resolver = CreateResolver(memory, 3);

        Assert.True(resolver.TryResolveCoherentSnapshot(out var snapshot));
        Assert.Equal(["entity-1", "entity-2", "entity-3", "entity-4"], snapshot.EntityIds);
        Assert.Equal(3, snapshot.LocalMemberSlot);
    }

    [Fact]
    public void TryResolveCoherentSnapshot_FailsWhenManagerChangesMidRead()
    {
        var memory = CreateCoherentSnapshotMemory(1, 3, 0x1C288);
        var resolver = CreateResolver(memory, 3);
        memory.OnRead = (address, length) =>
        {
            if (address == ManagerSlot && length == nint.Size)
                memory.WritePointer(ManagerSlot, Manager + 0x1000);
        };

        Assert.False(resolver.TryResolveCoherentSnapshot(out _));
    }

    [Fact]
    public void TryResolveCoherentSnapshot_FailsWhenOnlineStateChangesMidRead()
    {
        var memory = CreateCoherentSnapshotMemory(1, 3, 0x1C288);
        var resolver = CreateResolver(memory, 3);
        var stateReads = 0;
        memory.OnRead = (address, length) =>
        {
            if (address == Manager + 0x6CCE8 && length == 1 && ++stateReads == 1)
                memory.WriteByte(address, 0);
        };

        Assert.False(resolver.TryResolveCoherentSnapshot(out _));
    }

    [Fact]
    public void TryResolveCoherentSnapshot_FailsWhenLocalSlotChangesMidRead()
    {
        var memory = CreateCoherentSnapshotMemory(1, 3, 0x1C288);
        var resolver = CreateResolver(memory, 3, 2);
        var slotReads = 0;
        memory.OnRead = (address, length) =>
        {
            if (address == Manager + 0x6C82C && length == 4 && ++slotReads == 1)
                memory.WriteUInt32(address, LocalMemberKey(2));
        };

        Assert.False(resolver.TryResolveCoherentSnapshot(out _));
    }

    private static TestMemoryReader CreateLocalSlotMemory(
        byte onlineState,
        int localMemberSlot,
        int tableOffset)
    {
        var memory = new TestMemoryReader();
        memory.WritePointer(ManagerSlot, Manager);
        memory.WriteByte(Manager + 0x6CCE8, onlineState);
        memory.WriteUInt32(Manager + tableOffset, LocalMemberKey(localMemberSlot));
        return memory;
    }

    private static RelinkPartyMemberIdentityResolver CreateResolver(
        TestMemoryReader memory,
        params int[] mappedSlots)
    {
        var memberSlotResolver = new TestMemberSlotResolver();
        foreach (var mappedSlot in mappedSlots)
            memberSlotResolver.Slots[LocalMemberKey(mappedSlot)] = mappedSlot;
        return new RelinkPartyMemberIdentityResolver(ManagerSlot, memory, memberSlotResolver);
    }

    private static uint LocalMemberKey(int memberSlot) =>
        unchecked(LocalMemberKeyBase + (uint)(memberSlot + 16));

    private static TestMemoryReader CreateCoherentSnapshotMemory(
        byte onlineState,
        int localMemberSlot,
        int bankOffset)
    {
        var memory = CreateLocalSlotMemory(
            onlineState,
            localMemberSlot,
            onlineState == 0 ? 0x6C828 : 0x6C82C);
        for (var memberSlot = 0; memberSlot < 4; memberSlot++)
        {
            WriteInlineString(
                memory,
                Manager + bankOffset + memberSlot * 0x58 + 0x28,
                $"entity-{memberSlot + 1}");
        }
        return memory;
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

        internal Action<nint, int>? OnRead { get; set; }

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

            OnRead?.Invoke(address, destination.Length);
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

        internal void WriteUInt32(nint address, uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            Write(address, bytes);
        }

        internal void Write(nint address, ReadOnlySpan<byte> source)
        {
            for (var index = 0; index < source.Length; index++)
                _bytes[address + index] = source[index];
        }
    }
}
