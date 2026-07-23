using GBFR.ChatOverlay.Input;

namespace GBFR.ChatOverlay.Tests;

public sealed class DirectInputKeyboardStateFilterTests
{
    [Fact]
    public void Process_ActivatesOnlyOnKeyDownEdge()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var state = new byte[256];
        var activationCount = 0;
        state[0x15] = 0x80;

        filter.Process(state, Activate, () => false);
        filter.Process(state, Activate, () => false);
        state[0x15] = 0;
        filter.Process(state, Activate, () => false);
        state[0x15] = 0x80;
        filter.Process(state, Activate, () => false);

        Assert.Equal(2, activationCount);
        return;

        bool Activate()
        {
            activationCount++;
            return true;
        }
    }

    [Fact]
    public void Process_ClearsEntireStateWhenChatCapturesKeyboard()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var state = Enumerable.Repeat((byte)0x80, 256).ToArray();

        var filtered = filter.Process(state, () => true, () => true);

        Assert.True(filtered);
        Assert.All(state, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Process_LeavesStateUntouchedWhenCaptureIsDisabled()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var state = new byte[256];
        state[42] = 0x80;

        var filtered = filter.Process(state, () => false, () => false);

        Assert.False(filtered);
        Assert.Equal(0x80, state[42]);
    }

    [Fact]
    public void Process_DrainsHeldKeysBeforeReturningControlToGame()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var state = new byte[256];
        state[42] = 0x80;

        Assert.True(filter.Process(state, () => false, () => true));
        state[42] = 0x80;
        Assert.True(filter.Process(state, () => false, () => false));
        Assert.All(state, value => Assert.Equal(0, value));
        Assert.True(filter.Process(state, () => false, () => false));

        state[42] = 0x80;
        Assert.False(filter.Process(state, () => false, () => false));
        Assert.Equal(0x80, state[42]);
    }

    [Fact]
    public void Process_HoldUStartsAndReleaseStopsVoiceCapture()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var state = new byte[256];
        var starts = 0;
        var stops = 0;
        state[0x16] = 0x80;

        Assert.True(filter.Process(state, () => false, () => false, Begin, () => stops++, () => true));
        Assert.Equal(0, state[0x16]);
        state[0x16] = 0x80;
        filter.Process(state, () => false, () => false, Begin, () => stops++, () => true);
        state[0x16] = 0;
        filter.Process(state, () => false, () => false, Begin, () => stops++, () => true);

        Assert.Equal(1, starts);
        Assert.Equal(1, stops);
        return;

        bool Begin()
        {
            starts++;
            return true;
        }
    }

    [Fact]
    public void Process_DoesNotConsumeUWhenVoiceRequestIsRejected()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var state = new byte[256];
        state[0x16] = 0x80;

        var filtered = filter.Process(
            state,
            () => false,
            () => false,
            () => false,
            () => { },
            () => true);

        Assert.False(filtered);
        Assert.Equal(0x80, state[0x16]);
    }
}
