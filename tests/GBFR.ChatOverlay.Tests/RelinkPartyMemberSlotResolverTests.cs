using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class RelinkPartyMemberSlotResolverTests
{
    [Fact]
    public void TryResolveSlot_MapsOpaqueMemberKeyThroughNativeApi()
    {
        var native = new TestMemberSlotNativeApi
        {
            Handler = _ => 2,
        };
        var resolver = new RelinkPartyMemberSlotResolver(native, _ => { });

        Assert.True(resolver.TryResolveSlot(0x1234, out var memberSlot));
        Assert.Equal(2, memberSlot);
        Assert.Equal(1, native.CallCount);
    }

    [Fact]
    public void TryResolveSlot_FailsClosedWhenNativeResolverReturnsFalse()
    {
        var native = new TestMemberSlotNativeApi();
        var resolver = new RelinkPartyMemberSlotResolver(native, _ => { });

        Assert.False(resolver.TryResolveSlot(0x1234, out var memberSlot));
        Assert.Equal(-1, memberSlot);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void TryResolveSlot_RejectsSlotsOutsideFourPartyMembers(int invalidSlot)
    {
        var native = new TestMemberSlotNativeApi
        {
            Handler = _ => invalidSlot,
        };
        var resolver = new RelinkPartyMemberSlotResolver(native, _ => { });

        Assert.False(resolver.TryResolveSlot(0, out var memberSlot));
        Assert.Equal(-1, memberSlot);
    }

    [Fact]
    public void TryResolveSlot_FailsClosedWhenNativeApiThrows()
    {
        var native = new TestMemberSlotNativeApi
        {
            ExceptionToThrow = new InvalidOperationException("native resolver unavailable"),
        };
        var resolver = new RelinkPartyMemberSlotResolver(native, _ => { });

        Assert.False(resolver.TryResolveSlot(1, out _));
    }

    [Fact]
    public void TryResolveSlot_LogsDiagnosticOnlyOnce()
    {
        var logs = new List<string>();
        var native = new TestMemberSlotNativeApi();
        var resolver = new RelinkPartyMemberSlotResolver(native, logs.Add);

        Assert.False(resolver.TryResolveSlot(0x1234, out _));
        Assert.False(resolver.TryResolveSlot(0x1234, out _));

        var log = Assert.Single(logs);
        Assert.Contains("0x00001234", log, StringComparison.Ordinal);
    }
}

internal sealed class TestMemberSlotResolver : IRelinkPartyMemberSlotResolver
{
    internal Dictionary<uint, int> Slots { get; } = [];

    internal bool Fail { get; set; }

    internal Action<uint>? OnResolve { get; set; }

    internal int CallCount { get; private set; }

    internal uint LastMemberKey { get; private set; }

    public bool TryResolveSlot(uint memberKey, out int memberSlot)
    {
        memberSlot = -1;
        CallCount++;
        LastMemberKey = memberKey;
        OnResolve?.Invoke(memberKey);
        if (Fail || !Slots.TryGetValue(memberKey, out var candidate) || candidate is < 0 or >= 4)
            return false;

        memberSlot = candidate;
        return true;
    }
}

internal sealed class TestMemberSlotNativeApi : IRelinkPartyMemberSlotNativeApi
{
    internal Func<uint, int?>? Handler { get; set; }

    internal Exception? ExceptionToThrow { get; set; }

    internal int CallCount { get; private set; }

    public bool TryResolveMemberSlot(uint memberKey, out int memberSlot)
    {
        CallCount++;
        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        if (Handler is null)
        {
            memberSlot = -1;
            return false;
        }

        var candidate = Handler(memberKey);
        memberSlot = candidate ?? -1;
        return candidate is >= 0 and < 4;
    }
}
