using GBFR.ChatOverlay.Input;
using GBFR.ChatOverlay.Configuration;
using GBFR.ChatOverlay.Native;
using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Tests;

public sealed class HotkeyBindingTests
{
    [Theory]
    [InlineData("F10", "F10")]
    [InlineData("1", "1")]
    [InlineData("ctrl+1", "Ctrl+1")]
    [InlineData("Num1", "Num1")]
    [InlineData("Ctrl+VK_61", "Ctrl+Num1")]
    [InlineData("Shift+Alt+Y", "Shift+Alt+Y")]
    [InlineData("PageDown", "PageDown")]
    [InlineData("VK_61", "Num1")]
    [InlineData("VK_BA", "VK_BA")]
    [InlineData("F13", "F13")]
    public void KeyboardBinding_ParsesAndFormatsCanonicalText(string input, string expected)
    {
        Assert.True(KeyboardBinding.TryParse(input, out var binding));
        Assert.True(binding.IsBound);
        Assert.Equal(expected, binding.Format());
    }

    [Theory]
    [InlineData("VK_60", "Num0")]
    [InlineData("VK_61", "Num1")]
    [InlineData("VK_62", "Num2")]
    [InlineData("VK_63", "Num3")]
    [InlineData("VK_64", "Num4")]
    [InlineData("VK_65", "Num5")]
    [InlineData("VK_66", "Num6")]
    [InlineData("VK_67", "Num7")]
    [InlineData("VK_68", "Num8")]
    [InlineData("VK_69", "Num9")]
    public void KeyboardBinding_FormatsNumpadVirtualKeysWithCanonicalNames(
        string legacyValue,
        string expected)
    {
        Assert.True(KeyboardBinding.TryParse(legacyValue, out var binding));
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
    [InlineData(0x64, KeyboardModifiers.None, "F13")]
    [InlineData(0x6C, KeyboardModifiers.None, "F21")]
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
    [InlineData("m4", "M4")]
    [InlineData("RM+Z", "Z+RM")]
    [InlineData("LeftMiddle+A", "A+LM")]
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

    [Theory]
    [InlineData("DPadDown")]
    [InlineData("LB+DPadDown")]
    public void ControllerBinding_RejectsGameReservedDPadDown(string input)
    {
        Assert.False(ControllerBinding.TryParse(input, out _));
        Assert.True(ControllerBinding.ContainsReservedDPadDown(input));
    }

    [Fact]
    public void ControllerBinding_MatchesStandardAndFlydigiButtonsTogether()
    {
        Assert.True(ControllerBinding.TryParse("LB+M2", out var binding));

        Assert.True(binding.IsPressed(
            ControllerButtons.LeftBumper,
            ExtendedControllerButtons.M2));
        Assert.False(binding.IsPressed(
            ControllerButtons.LeftBumper,
            ExtendedControllerButtons.None));
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
            GlobalMuteKeyboardBinding = "M",
            GlobalMuteControllerBinding = "LB+X",
            QuickActions =
            [
                new QuickActionConfiguration
                {
                    Name = "Ready",
                    Text = "Ready!",
                    KeyboardBinding = "Alt+1",
                },
            ],
        };

        var snapshot = HotkeyConfigurationSnapshot.Create(configuration);

        Assert.NotEmpty(snapshot.NativeBindings);
        Assert.Null(typeof(HotkeyConfigurationSnapshot).GetProperty(
            "QuickActionsKeyboard",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        Assert.Null(typeof(HotkeyConfigurationSnapshot).GetProperty(
            "QuickActionsController",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        Assert.Single(snapshot.QuickActions);
        Assert.Equal("Alt+1", snapshot.QuickActions[0].Keyboard.Format());
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
