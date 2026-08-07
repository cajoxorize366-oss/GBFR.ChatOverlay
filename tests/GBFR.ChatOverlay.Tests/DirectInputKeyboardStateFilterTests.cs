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
    public void Process_ActivationCanEnableCaptureForTheSameStateBuffer()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var state = new byte[256];
        state[0x15] = 0x80;
        state[42] = 0x80;
        var capture = false;

        var filtered = filter.Process(
            state,
            () => capture = true,
            () => capture);

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
    public void Process_DoesNotKeepASeparateReleaseLatch()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var state = new byte[256];
        state[42] = 0x80;

        Assert.True(filter.Process(state, () => false, () => true));
        state[42] = 0x80;
        Assert.False(filter.Process(state, () => false, () => false));
        Assert.Equal(0x80, state[42]);
    }

    [Fact]
    public void Process_ReportsAndFiltersVoicePushToTalkWhileEnabled()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var reports = new List<bool>();
        var state = new byte[256];
        state[0x16] = 0x80;

        var filtered = filter.Process(
            state,
            () => false,
            () => false,
            () => true,
            reports.Add);

        Assert.True(filtered);
        Assert.Equal(0, state[0x16]);
        Assert.Equal(new[] { true }, reports);

        filtered = filter.Process(
            state,
            () => false,
            () => false,
            () => true,
            reports.Add);

        Assert.False(filtered);
        Assert.Equal(new[] { true, false }, reports);
    }

    [Fact]
    public void Process_DoesNotOpenMicrophoneWhileChatCapturesKeyboard()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var reports = new List<bool>();
        var state = new byte[256];
        state[0x16] = 0x80;

        filter.Process(
            state,
            () => false,
            () => true,
            () => true,
            reports.Add);

        Assert.Equal(new[] { false }, reports);
        Assert.All(state, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Process_LeavesVoiceKeyForGameWhenVoiceTestIsDisabled()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var reports = new List<bool>();
        var state = new byte[256];
        state[0x16] = 0x80;

        var filtered = filter.Process(
            state,
            () => false,
            () => false,
            () => false,
            reports.Add);

        Assert.False(filtered);
        Assert.Equal(0x80, state[0x16]);
        Assert.Equal(new[] { false }, reports);
    }

    [Fact]
    public void Process_ReportsAndFiltersF10SettingsMenuKey()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var reports = new List<bool>();
        var state = new byte[256];
        state[0x44] = 0x80;

        var filtered = filter.Process(
            state,
            () => false,
            () => false,
            isSettingsMenuAvailable: () => true,
            reportSettingsMenuKey: reports.Add);

        Assert.True(filtered);
        Assert.Equal(0, state[0x44]);
        Assert.Equal(new[] { true }, reports);

        state[0x44] = 0;
        filter.Process(
            state,
            () => false,
            () => false,
            isSettingsMenuAvailable: () => true,
            reportSettingsMenuKey: reports.Add);
        Assert.Equal(new[] { true, false }, reports);
    }

    [Fact]
    public void Process_IKeyIsNoLongerReservedForSelfTest()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var state = new byte[256];
        state[0x17] = 0x80;

        var filtered = filter.Process(state, () => false, () => false);

        Assert.False(filtered);
        Assert.Equal(0x80, state[0x17]);
    }

    [Fact]
    public void Process_HeldKeyCannotActivateWhenChannelBecomesReadyMidHold()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var reports = new List<bool>();
        var state = new byte[256];
        state[0x16] = 0x80;
        var enabled = false;

        filter.Process(
            state,
            () => false,
            () => false,
            () => enabled,
            reports.Add);

        enabled = true;
        state[0x16] = 0x80;
        filter.Process(
            state,
            () => false,
            () => false,
            () => enabled,
            reports.Add);

        state[0x16] = 0;
        filter.Process(state, () => false, () => false, () => enabled, reports.Add);
        state[0x16] = 0x80;
        filter.Process(state, () => false, () => false, () => enabled, reports.Add);

        Assert.Equal(new[] { false, false, false, true }, reports);
    }

    [Fact]
    public void Process_LosingEligibilityMidHoldRequiresReleaseBeforeReopen()
    {
        var filter = new DirectInputKeyboardStateFilter();
        var reports = new List<bool>();
        var state = new byte[256];
        var capture = false;
        state[0x16] = 0x80;

        filter.Process(
            state,
            () => false,
            () => capture,
            isVoicePushToTalkEnabled: () => true,
            reportVoicePushToTalk: reports.Add);

        capture = true;
        state[0x16] = 0x80;
        filter.Process(
            state,
            () => false,
            () => capture,
            isVoicePushToTalkEnabled: () => true,
            reportVoicePushToTalk: reports.Add);

        capture = false;
        state[0x16] = 0x80;
        filter.Process(
            state,
            () => false,
            () => capture,
            isVoicePushToTalkEnabled: () => true,
            reportVoicePushToTalk: reports.Add);

        state[0x16] = 0;
        filter.Process(
            state,
            () => false,
            () => capture,
            isVoicePushToTalkEnabled: () => true,
            reportVoicePushToTalk: reports.Add);
        state[0x16] = 0x80;
        filter.Process(
            state,
            () => false,
            () => capture,
            isVoicePushToTalkEnabled: () => true,
            reportVoicePushToTalk: reports.Add);

        Assert.Equal(new[] { true, false, false, false, true }, reports);
    }
}
