using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Tests;

public sealed class RecentEchoSuppressorTests
{
    [Fact]
    public void TryConsume_ConsumesOneMatchingEcho()
    {
        var suppressor = new RecentEchoSuppressor(TimeSpan.FromSeconds(5));
        var now = DateTimeOffset.UtcNow;
        var token = suppressor.Register("hello", now);

        Assert.True(suppressor.TryConsume("hello", now.AddSeconds(1), out var firstWasLocal));
        Assert.True(firstWasLocal);
        Assert.False(suppressor.TryConsume("hello", now.AddSeconds(1), out var duplicateWasLocal));
        Assert.True(duplicateWasLocal);
        Assert.False(suppressor.TryComplete(token, now.AddSeconds(1)));
    }

    [Fact]
    public void TryConsume_DoesNotConsumeExpiredOrDifferentMessage()
    {
        var suppressor = new RecentEchoSuppressor(TimeSpan.FromSeconds(1));
        var now = DateTimeOffset.UtcNow;
        suppressor.Register("hello", now);

        Assert.False(suppressor.TryConsume("other", now.AddMilliseconds(100)));
        Assert.False(suppressor.TryConsume("hello", now.AddSeconds(2)));
    }

    [Fact]
    public void TryComplete_PublishesFallbackAndSuppressesALateEcho()
    {
        var suppressor = new RecentEchoSuppressor(TimeSpan.FromSeconds(5));
        var now = DateTimeOffset.UtcNow;
        var token = suppressor.Register("hello", now);

        Assert.True(suppressor.TryComplete(token, now.AddMilliseconds(1)));
        Assert.False(suppressor.TryConsume("hello", now.AddSeconds(1), out var wasLocalEcho));
        Assert.True(wasLocalEcho);
        Assert.False(suppressor.TryConsume("hello", now.AddSeconds(1), out wasLocalEcho));
        Assert.False(wasLocalEcho);
    }

    [Fact]
    public void TryComplete_DoesNotFallbackAfterASynchronousEcho()
    {
        var suppressor = new RecentEchoSuppressor(TimeSpan.FromSeconds(5));
        var now = DateTimeOffset.UtcNow;
        var token = suppressor.Register("hello", now);

        Assert.True(suppressor.TryConsume("hello", now, out var wasLocalEcho));
        Assert.True(wasLocalEcho);
        Assert.False(suppressor.TryComplete(token, now.AddMilliseconds(1)));
        Assert.False(suppressor.TryConsume("hello", now.AddMilliseconds(2), out wasLocalEcho));
        Assert.False(wasLocalEcho);
    }

    [Fact]
    public void Cancel_RemovesFailedSendToken()
    {
        var suppressor = new RecentEchoSuppressor();
        var now = DateTimeOffset.UtcNow;
        var token = suppressor.Register("hello", now);

        Assert.True(suppressor.Cancel(token));
        Assert.False(suppressor.TryConsume("hello", now, out var wasLocalEcho));
        Assert.False(wasLocalEcho);
    }
}
