using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkGameContextProbeTests
{
    [Fact]
    public void ChatManagerPointerIsReadIndependentlyForNativeSend()
    {
        var memory = new TestRelinkMemoryReader();
        var probe = RelinkGameContextProbe.CreateForTesting(
            (nint)0x1000,
            new RelinkChatRvas(0x10, 0x20, 0x300),
            memory);

        Assert.False(probe.TryGetHudChatManager(out _));

        memory.Pointers[(nint)0x1300] = (nint)0x12345678;
        Assert.True(probe.TryGetHudChatManager(out var manager));
        Assert.Equal((nint)0x12345678, manager);
    }

    [Fact]
    public void PointerReadFailureFailsClosedAndLogsOnce()
    {
        var logs = new List<string>();
        var memory = new TestRelinkMemoryReader
        {
            Exception = new InvalidOperationException("test read failure"),
        };
        var probe = RelinkGameContextProbe.CreateForTesting(
            (nint)0x1000,
            new RelinkChatRvas(0x10, 0x20, 0x300),
            memory,
            logs.Add);

        Assert.False(probe.TryGetHudChatManager(out _));
        Assert.False(probe.TryGetHudChatManager(out _));

        var log = Assert.Single(logs);
        Assert.Contains("failed closed", log, StringComparison.Ordinal);
        Assert.Contains("test read failure", log, StringComparison.Ordinal);
    }

    [Fact]
    public void NullManagerSlotNeverInvokesTheReader()
    {
        var memory = new TestRelinkMemoryReader();
        var probe = RelinkGameContextProbe.CreateForTesting(
            nint.Zero,
            new RelinkChatRvas(0, 0, 0),
            memory);

        Assert.False(probe.TryGetHudChatManager(out _));
        Assert.Equal(0, memory.ReadCount);
    }

    private sealed class TestRelinkMemoryReader : IRelinkMemoryReader
    {
        internal Dictionary<nint, nint> Pointers { get; } = new();

        internal Exception? Exception { get; init; }

        internal int ReadCount { get; private set; }

        public bool TryReadPointer(nint address, out nint value)
        {
            ReadCount++;
            if (Exception is not null)
                throw Exception;
            return Pointers.TryGetValue(address, out value);
        }

        public bool TryReadBytes(nint address, Span<byte> destination)
        {
            ReadCount++;
            if (Exception is not null)
                throw Exception;
            return false;
        }
    }
}
