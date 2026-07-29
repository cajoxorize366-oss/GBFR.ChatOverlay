using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Native;
using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Tests;

public sealed class HotkeyBindingTests
{
    [Theory]
    [InlineData("F10", "F10")]
    [InlineData("ctrl+1", "Ctrl+1")]
    [InlineData("Shift+Alt+Y", "Shift+Alt+Y")]
    [InlineData("PageDown", "PageDown")]
    [InlineData("VK_BA", "VK_BA")]
    public void KeyboardBinding_ParsesAndFormatsCanonicalText(string input, string expected)
    {
        Assert.True(KeyboardBinding.TryParse(input, out var binding));
        Assert.True(binding.IsBound);
        Assert.Equal(expected, binding.Format());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Y+U")]
    [InlineData("NotAKey")]
    public void KeyboardBinding_RejectsInvalidOrIncompleteValues(string input)
    {
        var parsed = KeyboardBinding.TryParse(input, out var binding);
        if (input.Length == 0)
        {
            Assert.True(parsed);
            Assert.False(binding.IsBound);
        }
        else
        {
            Assert.False(parsed);
        }
    }

    [Theory]
    [InlineData(0x15, KeyboardModifiers.None, "Y")]
    [InlineData(0x15, KeyboardModifiers.Control | KeyboardModifiers.Shift, "Ctrl+Shift+Y")]
    [InlineData(0xC8, KeyboardModifiers.None, "Up")]
    public void KeyboardBinding_ConvertsDirectInputScanCodes(
        byte scanCode,
        KeyboardModifiers modifiers,
        string expected)
    {
        Assert.True(KeyboardBinding.TryFromDirectInputScanCode(scanCode, modifiers, out var binding));
        Assert.Equal(expected, binding.Format());
        Assert.True(binding.TryGetDirectInputScanCode(out var roundTrip));
        Assert.Equal(scanCode, roundTrip);
    }

    [Theory]
    [InlineData("A", "A")]
    [InlineData("lb+dpad-up", "LB+DPadUp")]
    [InlineData("RightBumper+Y", "RB+Y")]
    public void ControllerBinding_ParsesAtMostTwoButtons(string input, string expected)
    {
        Assert.True(ControllerBinding.TryParse(input, out var binding));
        Assert.Equal(expected, binding.Format());
    }

    [Fact]
    public void ControllerBinding_RejectsThreeButtonChord()
    {
        Assert.False(ControllerBinding.TryParse("LB+RB+Y", out _));
    }

    [Fact]
    public void NativeHotkeyBinding_UsesFourByteStableAbi()
    {
        Assert.Equal(4, Marshal.SizeOf<DirectInputHotkeyBinding>());
    }

    [Fact]
    public void ConfigurationSnapshot_BuildsFixedAndDynamicNativeBindings()
    {
        var configuration = new Config
        {
            OpenChatKeyboardBinding = "X",
            SettingsMenuKeyboardBinding = "Ctrl+F10",
            PushToTalkKeyboardBinding = "U",
            QuickActionsKeyboardBinding = "Q",
            GlobalMuteKeyboardBinding = "M",
            GlobalMuteControllerBinding = "LB+X",
            QuickActions =
            [
                new QuickActionConfiguration
                {
                    Name = "Ready",
                    Text = "Ready!",
                    KeyboardBinding = "Alt+1",
                    ControllerBinding = "LB+Y",
                },
            ],
        };

        var snapshot = HotkeyConfigurationSnapshot.Create(configuration);

        Assert.Equal(6, snapshot.NativeBindings.Length);
        Assert.Single(snapshot.QuickActions);
        Assert.Equal("LB+Y", snapshot.QuickActions[0].Controller.Format());
        Assert.Equal("M", snapshot.GlobalMuteKeyboard.Format());
        Assert.Equal("LB+X", snapshot.GlobalMuteController.Format());
        Assert.Contains(
            snapshot.NativeBindings,
            binding => binding.Modifiers == KeyboardModifiers.Control &&
                       binding.PolicyFlag == (byte)DirectInputBrokerPolicy.SuppressSettings);
        Assert.Contains(
            snapshot.NativeBindings,
            binding => binding.Modifiers == KeyboardModifiers.Alt &&
                       binding.PolicyFlag == (byte)DirectInputBrokerPolicy.SuppressQuickActions);
    }

    [Fact]
    public void ConfigurationSnapshot_RecognizesControllerChordWithoutConsumingIt()
    {
        var configuration = new Config
        {
            PushToTalkKeyboardBinding = string.Empty,
            PushToTalkControllerBinding = "LB+Y",
        };
        var hotkeys = HotkeyConfigurationSnapshot.Create(configuration);
        var native = new DirectInputBrokerSnapshot
        {
            AbiVersion = DirectInputBrokerSnapshot.ExpectedAbiVersion,
            StructSize = DirectInputBrokerSnapshot.ExpectedStructSize,
            ControllerButtons = ControllerButtons.LeftBumper | ControllerButtons.Y,
        };

        Assert.True(HotkeyConfigurationSnapshot.IsPressed(
            native,
            hotkeys.PushToTalkKeyboard,
            hotkeys.PushToTalkController));
        Assert.Equal(
            ControllerButtons.LeftBumper | ControllerButtons.Y,
            native.ControllerButtons);
    }
}
