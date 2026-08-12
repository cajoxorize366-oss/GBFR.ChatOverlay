using System.Buffers.Binary;
using System.Text;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkLobbyOwnerTrackerTests
{
    private static readonly nint OwnerOutput = (nint)0x1000;
    private static readonly nint OwnerEntityKey = (nint)0x2000;
    private static readonly nint OwnerEntityId = (nint)0x3000;

    [Fact]
    public void TryReadOwnerEntityId_FollowsDocumentedEntityKeyDoublePointer()
    {
        var memory = new TestMemoryReader();
        memory.WritePointer(OwnerOutput, OwnerEntityKey);
        memory.WritePointer(OwnerEntityKey, OwnerEntityId);
        memory.WriteUtf8Z(OwnerEntityId, "playfab-owner-entity-id");

        Assert.True(RelinkLobbyOwnerTracker.TryReadOwnerEntityId(memory, OwnerOutput, out var entityId));
        Assert.Equal("playfab-owner-entity-id", entityId);
    }

    [Fact]
    public void TryReadOwnerEntityId_RejectsNullLibraryOwnedOwner()
    {
        var memory = new TestMemoryReader();
        memory.WritePointer(OwnerOutput, nint.Zero);

        Assert.False(RelinkLobbyOwnerTracker.TryReadOwnerEntityId(memory, OwnerOutput, out var entityId));
        Assert.Empty(entityId);
    }

    [Fact]
    public void TryReadOwnerEntityId_DoesNotTreatOutputStorageAsTheEntityKey()
    {
        var memory = new TestMemoryReader();
        memory.WritePointer(OwnerOutput, OwnerEntityId);
        memory.WriteUtf8Z(OwnerEntityId, "direct-struct-would-be-wrong");

        Assert.False(RelinkLobbyOwnerTracker.TryReadOwnerEntityId(memory, OwnerOutput, out _));
    }

    [Fact]
    public void TryReadOwnerEntityId_RejectsUnterminatedOversizedId()
    {
        var memory = new TestMemoryReader();
        memory.WritePointer(OwnerOutput, OwnerEntityKey);
        memory.WritePointer(OwnerEntityKey, OwnerEntityId);
        memory.Write(OwnerEntityId, Enumerable.Repeat((byte)'a', RelinkLobbyOwnerTracker.MaximumEntityIdBytes).ToArray());

        Assert.False(RelinkLobbyOwnerTracker.TryReadOwnerEntityId(memory, OwnerOutput, out _));
    }

    [Fact]
    public void TryRefreshHostPlayerNumber_ReReadsSnapshotAndDoesNotReuseOwnerAfterMissing()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner((nint)0x5000, "owner");
        var resolver = new SequenceSnapshotResolver(
            new RelinkPartyMemberIdentitySnapshot(["owner", "", "", ""], LocalMemberSlot: 1),
            new RelinkPartyMemberIdentitySnapshot(["", "", "", ""], LocalMemberSlot: 1));

        Assert.True(RelinkLobbyOwnerHostRefresh.TryRefreshHostPlayerNumber(
            resolver,
            binding,
            PartyNetworkLocalRole.Connected,
            out var first));
        Assert.Equal(2, first);

        Assert.False(RelinkLobbyOwnerHostRefresh.TryRefreshHostPlayerNumber(
            resolver,
            binding,
            PartyNetworkLocalRole.Connected,
            out var second));
        Assert.Equal(0, second);
    }

    [Fact]
    public void TryRefreshHostPlayerNumber_ConnectedRoleExcludesLocalCandidate()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner((nint)0x5000, "local-owner");
        binding.ObserveOwner((nint)0x5001, "remote-owner");
        var resolver = new SequenceSnapshotResolver(
            new RelinkPartyMemberIdentitySnapshot(
                ["remote-owner", "", "local-owner", ""],
                LocalMemberSlot: 2));

        Assert.True(RelinkLobbyOwnerHostRefresh.TryRefreshHostPlayerNumber(
            resolver,
            binding,
            PartyNetworkLocalRole.Connected,
            out var playerNumber));
        Assert.Equal(2, playerNumber);
    }

    [Fact]
    public void TryRefreshHostPlayerNumber_ConnectedOnlyLocalCandidate_ReturnsFalse()
    {
        var binding = new PartyLobbyOwnerBinding();
        binding.ObserveOwner((nint)0x5000, "local-owner");
        var resolver = new SequenceSnapshotResolver(
            new RelinkPartyMemberIdentitySnapshot(
                ["", "", "local-owner", ""],
                LocalMemberSlot: 2));

        Assert.False(RelinkLobbyOwnerHostRefresh.TryRefreshHostPlayerNumber(
            resolver,
            binding,
            PartyNetworkLocalRole.Connected,
            out var playerNumber));
        Assert.Equal(0, playerNumber);
    }

    [Fact]
    public void TryRefreshHostPlayerNumber_CreatedRoleReturnsLocalHostWithoutCandidate()
    {
        var binding = new PartyLobbyOwnerBinding();
        var resolver = new SequenceSnapshotResolver(
            new RelinkPartyMemberIdentitySnapshot(
                ["first", "", "third", ""],
                LocalMemberSlot: 1));

        Assert.True(RelinkLobbyOwnerHostRefresh.TryRefreshHostPlayerNumber(
            resolver,
            binding,
            PartyNetworkLocalRole.Created,
            out var playerNumber));
        Assert.Equal(1, playerNumber);
    }

    private sealed class SequenceSnapshotResolver : IRelinkPartyMemberIdentitySnapshotResolver
    {
        private readonly RelinkPartyMemberIdentitySnapshot[] _snapshots;
        private int _index;

        internal SequenceSnapshotResolver(params RelinkPartyMemberIdentitySnapshot[] snapshots)
        {
            _snapshots = snapshots;
        }

        public bool TryResolveSnapshot(out string[] entityIds)
        {
            entityIds = _index < _snapshots.Length ? _snapshots[_index].EntityIds : [];
            return true;
        }

        public bool TryResolveCoherentSnapshot(out RelinkPartyMemberIdentitySnapshot snapshot)
        {
            if (_index >= _snapshots.Length)
            {
                snapshot = default;
                return false;
            }

            snapshot = _snapshots[_index++];
            return true;
        }
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

        internal void WriteUtf8Z(nint address, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + '\0');
            Write(address, bytes);
        }

        internal void Write(nint address, ReadOnlySpan<byte> source)
        {
            for (var index = 0; index < source.Length; index++)
                _bytes[address + index] = source[index];
        }
    }
}
