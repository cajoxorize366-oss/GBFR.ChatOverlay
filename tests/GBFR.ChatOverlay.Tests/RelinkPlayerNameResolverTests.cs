using System.Buffers.Binary;
using System.Text;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkPlayerNameResolverTests
{
    private static readonly nint MemberManagerSlot = (nint)0x1000;
    private static readonly nint MemberManager = (nint)0x2000;
    private static readonly nint Member = (nint)0x3000;
    private static readonly nint Profile = (nint)0xA000;

    [Fact]
    public void TryResolve_ReadsInlineLobbyMemberNameForMappedSender()
    {
        var memory = CreateActiveMemberMemory();
        WriteInlineName(memory, "Djeeta");
        var native = new TestPlayerNameNativeApi(Member);
        var resolver = CreateResolver(memory, native, memberKey: 0x1234, memberSlot: 2);

        Assert.True(resolver.TryResolve(0x1234, out var playerName));
        Assert.Equal("Djeeta", playerName);
        Assert.Equal(MemberManager, native.LastManager);
        Assert.Equal(2, native.LastMemberSlot);
    }

    [Fact]
    public void TryResolve_ReadsHeapBackedUtf8LobbyMemberName()
    {
        var memory = CreateActiveMemberMemory();
        WriteHeapName(memory, "VeryLong骑空士Persona");
        var native = new TestPlayerNameNativeApi(Member);
        var resolver = CreateResolver(memory, native, memberKey: 0xAABBCCDD, memberSlot: 3);

        Assert.True(resolver.TryResolve(0xAABBCCDD, out var playerName));
        Assert.Equal("VeryLong骑空士Persona", playerName);
        Assert.Equal(3, native.LastMemberSlot);
    }

    [Theory]
    [InlineData(0u, 3)]
    [InlineData(1u, 0)]
    [InlineData(2u, 1)]
    [InlineData(3u, 2)]
    public void TryResolve_MapsEvenSmallOpaqueKeysBeforeLobbyLookup(
        uint memberKey,
        int resolvedMemberSlot)
    {
        var memory = CreateActiveMemberMemory();
        WriteInlineName(memory, $"Member {resolvedMemberSlot}");
        var native = new TestPlayerNameNativeApi(Member);
        var resolver = CreateResolver(memory, native, memberKey, resolvedMemberSlot);

        Assert.True(resolver.TryResolve(memberKey, out var playerName));
        Assert.Equal($"Member {resolvedMemberSlot}", playerName);
        Assert.Equal(resolvedMemberSlot, native.LastMemberSlot);
    }

    [Fact]
    public void TryResolve_FailsClosedForInactiveOrMalformedMemberAndLogsOnce()
    {
        var logs = new List<string>();
        var memory = new TestRelinkMemoryReader();
        memory.WritePointer(MemberManagerSlot, MemberManager);
        memory.WriteByte(Member + 0x5EBC, 0);
        var native = new TestPlayerNameNativeApi(Member);
        var resolver = CreateResolver(memory, native, memberKey: 0xDEAD, memberSlot: 0, logs.Add);

        Assert.False(resolver.TryResolve(0xDEAD, out _));
        Assert.False(resolver.TryResolve(0xDEAD, out _));

        var log = Assert.Single(logs);
        Assert.Contains("could not map", log, StringComparison.Ordinal);
        Assert.Contains("Player fallback", log, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_RejectsInvalidUtf8AndMissingTerminator()
    {
        var memory = CreateActiveMemberMemory();
        var nativeString = Profile + 0x208;
        var layout = new byte[0x20];
        layout[0] = 0xC3;
        layout[1] = 0x28;
        layout[2] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(0x10), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(0x18), 0x0F);
        memory.Write(nativeString, layout);
        var resolver = new RelinkPlayerNameResolver(
            MemberManagerSlot,
            memory,
            new TestPlayerNameNativeApi(Member),
            CreateSlotResolver((0xBAAD, 1)),
            _ => { });

        Assert.False(resolver.TryResolve(0xBAAD, out _));
    }

    [Fact]
    public void TryResolve_FailsClosedWhenOpaqueMemberKeyCannotBeMapped()
    {
        var memory = CreateActiveMemberMemory();
        WriteInlineName(memory, "Djeeta");
        var native = new TestPlayerNameNativeApi(Member);
        var resolver = new RelinkPlayerNameResolver(
            MemberManagerSlot,
            memory,
            native,
            new TestMemberSlotResolver(),
            _ => { });

        Assert.False(resolver.TryResolve(0xCAFEBABE, out _));
        Assert.Equal(0, native.MemberLookupCount);
    }

    private static RelinkPlayerNameResolver CreateResolver(
        TestRelinkMemoryReader memory,
        TestPlayerNameNativeApi native,
        uint memberKey,
        int memberSlot,
        Action<string>? log = null) =>
        new(
            MemberManagerSlot,
            memory,
            native,
            CreateSlotResolver((memberKey, memberSlot)),
            log ?? (_ => { }));

    private static TestMemberSlotResolver CreateSlotResolver(
        params (uint MemberKey, int MemberSlot)[] mappings)
    {
        var resolver = new TestMemberSlotResolver();
        foreach (var (memberKey, memberSlot) in mappings)
            resolver.Slots[memberKey] = memberSlot;
        return resolver;
    }

    private static TestRelinkMemoryReader CreateActiveMemberMemory()
    {
        var memory = new TestRelinkMemoryReader();
        memory.WritePointer(MemberManagerSlot, MemberManager);
        memory.WriteByte(Member + 0x5EBC, 1);
        memory.WritePointer(Member + 0x5E60, Profile);
        return memory;
    }

    private static void WriteInlineName(TestRelinkMemoryReader memory, string name) =>
        WriteInlineName(memory, Profile + 0x208, name);

    private static void WriteInlineName(TestRelinkMemoryReader memory, nint address, string name)
    {
        var encoded = Encoding.UTF8.GetBytes(name);
        Assert.InRange(encoded.Length, 1, 15);
        var layout = new byte[0x20];
        encoded.CopyTo(layout, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(0x10), (ulong)encoded.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(0x18), 0x0F);
        memory.Write(address, layout);
    }

    private static void WriteHeapName(TestRelinkMemoryReader memory, string name)
    {
        var encoded = Encoding.UTF8.GetBytes(name);
        Assert.InRange(
            encoded.Length,
            16,
            RelinkPlayerNameResolver.MaximumPlayerNameBytes);
        var data = (nint)0xC000;
        var layout = new byte[0x20];
        BinaryPrimitives.WriteInt64LittleEndian(layout, data);
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(0x10), (ulong)encoded.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(0x18), (ulong)encoded.Length);
        memory.Write(Profile + 0x208, layout);

        var terminated = new byte[encoded.Length + 1];
        encoded.CopyTo(terminated, 0);
        memory.Write(data, terminated);
    }

    private sealed class TestPlayerNameNativeApi(
        nint member) : IRelinkPlayerNameNativeApi
    {
        internal nint LastManager { get; private set; }

        internal int LastMemberSlot { get; private set; } = -1;

        internal int MemberLookupCount { get; private set; }

        public nint GetLobbyMember(nint manager, int resolvedMemberSlot)
        {
            LastManager = manager;
            LastMemberSlot = resolvedMemberSlot;
            MemberLookupCount++;
            return member;
        }
    }


    private sealed class TestRelinkMemoryReader : IRelinkMemoryReader
    {
        private readonly Dictionary<nint, byte> _bytes = new();

        public bool TryReadPointer(nint address, out nint value)
        {
            Span<byte> encoded = stackalloc byte[IntPtr.Size];
            if (!TryReadBytes(address, encoded))
            {
                value = nint.Zero;
                return false;
            }

            value = IntPtr.Size switch
            {
                sizeof(long) => (nint)BinaryPrimitives.ReadInt64LittleEndian(encoded),
                sizeof(int) => (nint)BinaryPrimitives.ReadInt32LittleEndian(encoded),
                _ => nint.Zero,
            };
            return value != nint.Zero;
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
            Span<byte> encoded = stackalloc byte[IntPtr.Size];
            if (IntPtr.Size == sizeof(long))
                BinaryPrimitives.WriteInt64LittleEndian(encoded, value);
            else
                BinaryPrimitives.WriteInt32LittleEndian(encoded, checked((int)value));
            Write(address, encoded);
        }

        internal void WriteByte(nint address, byte value) => _bytes[address] = value;

        internal void Write(nint address, ReadOnlySpan<byte> source)
        {
            for (var index = 0; index < source.Length; index++)
                _bytes[address + index] = source[index];
        }
    }
}
