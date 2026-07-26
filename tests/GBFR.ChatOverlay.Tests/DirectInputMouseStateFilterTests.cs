using GBFR.ChatOverlay.Input;

namespace GBFR.ChatOverlay.Tests;

public sealed class DirectInputMouseStateFilterTests
{
    [Fact]
    public void Capture_ClearsMovementAndButtons()
    {
        var filter = new DirectInputMouseStateFilter();
        var state = Enumerable.Repeat((byte)0x80, 20).ToArray();

        var filtered = filter.Process(state, capture: true);

        Assert.True(filtered);
        Assert.All(state, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Close_DrainsHeldButtonBeforeReturningMouseToGame()
    {
        var filter = new DirectInputMouseStateFilter();
        var state = new byte[20];
        state[12] = 0x80;

        Assert.True(filter.Process(state, capture: true));
        state[12] = 0x80;
        Assert.True(filter.Process(state, capture: false));
        Assert.True(filter.IsSuppressing);

        Assert.True(filter.Process(state, capture: false));
        Assert.False(filter.IsSuppressing);

        state[4] = 1;
        Assert.False(filter.Process(state, capture: false));
        Assert.Equal(1, state[4]);
    }

    [Theory]
    [InlineData(0x0100)]
    [InlineData(0x0201)]
    [InlineData(0x00A1)]
    [InlineData(0x010F)]
    public void WindowClassifier_AlwaysCapturesKeyboardMouseAndIme(uint message)
    {
        Assert.True(WindowInputClassifier.IsAlwaysCaptured(message));
    }
}
