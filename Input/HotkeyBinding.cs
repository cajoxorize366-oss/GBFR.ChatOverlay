using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Input;

[Flags]
public enum KeyboardModifiers : byte
{
    None = 0,
    Control = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
}

public readonly record struct KeyboardBinding(ushort VirtualKey, KeyboardModifiers Modifiers)
{
    private const uint MapVscToVkEx = 3;
    private const uint MapVkToVscEx = 4;

    public bool IsBound => VirtualKey != 0;

    public string Format()
    {
        if (!IsBound)
            return string.Empty;

        var parts = new List<string>(4);
        if ((Modifiers & KeyboardModifiers.Control) != 0)
            parts.Add("Ctrl");
        if ((Modifiers & KeyboardModifiers.Shift) != 0)
            parts.Add("Shift");
        if ((Modifiers & KeyboardModifiers.Alt) != 0)
            parts.Add("Alt");
        parts.Add(KeyboardKeyNames.Format(VirtualKey));
        return string.Join('+', parts);
    }

    public bool TryGetDirectInputScanCode(out byte scanCode)
    {
        scanCode = 0;
        if (!IsBound || !OperatingSystem.IsWindows())
            return false;

        var mapped = MapVirtualKeyW(VirtualKey, MapVkToVscEx);
        if (mapped == 0)
            return false;
        var low = (byte)(mapped & 0xFF);
        var prefix = mapped & 0xFF00;
        scanCode = prefix is 0xE000 or 0xE100 || IsExtendedVirtualKey(VirtualKey)
            ? (byte)(low | 0x80)
            : low;
        return scanCode != 0;
    }

    internal static bool TryFromDirectInputScanCode(
        byte scanCode,
        KeyboardModifiers modifiers,
        out KeyboardBinding binding)
    {
        binding = default;
        if (scanCode == 0 || !OperatingSystem.IsWindows())
            return false;

        var mappedScanCode = (scanCode & 0x80) != 0
            ? (uint)((scanCode & 0x7F) | 0xE000)
            : scanCode;
        var virtualKey = MapVirtualKeyW(mappedScanCode, MapVscToVkEx);
        if (virtualKey == 0 || virtualKey > ushort.MaxValue)
            return false;

        binding = new KeyboardBinding((ushort)virtualKey, modifiers);
        return binding.IsBound;
    }

    private static bool IsExtendedVirtualKey(ushort virtualKey) => virtualKey is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;

    public static bool TryParse(string? value, out KeyboardBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var modifiers = KeyboardModifiers.None;
        ushort primary = 0;
        foreach (var rawToken in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawToken.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                rawToken.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyboardModifiers.Control;
                continue;
            }
            if (rawToken.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyboardModifiers.Shift;
                continue;
            }
            if (rawToken.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyboardModifiers.Alt;
                continue;
            }
            if (primary != 0 || !KeyboardKeyNames.TryParse(rawToken, out primary))
                return false;
        }

        if (primary == 0)
            return false;
        binding = new KeyboardBinding(primary, modifiers);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);
}

[Flags]
public enum ControllerButtons : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftStick = 0x0040,
    RightStick = 0x0080,
    LeftBumper = 0x0100,
    RightBumper = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

[Flags]
public enum ExtendedControllerButtons : ushort
{
    None = 0,
    C = 0x0001,
    Z = 0x0002,
    M1 = 0x0004,
    M2 = 0x0008,
    M3 = 0x0010,
    M4 = 0x0020,
    LM = 0x0040,
    RM = 0x0080,
    Circle = 0x0100,
}

public readonly record struct ControllerBinding(
    ControllerButtons Buttons,
    ExtendedControllerButtons ExtendedButtons)
{
    public ControllerBinding(ControllerButtons buttons)
        : this(buttons, ExtendedControllerButtons.None)
    {
    }

    private static readonly (ControllerButtons Button, string Name)[] OrderedButtons =
    [
        (ControllerButtons.LeftBumper, "LB"),
        (ControllerButtons.RightBumper, "RB"),
        (ControllerButtons.DPadUp, "DPadUp"),
        (ControllerButtons.DPadDown, "DPadDown"),
        (ControllerButtons.DPadLeft, "DPadLeft"),
        (ControllerButtons.DPadRight, "DPadRight"),
        (ControllerButtons.LeftStick, "LS"),
        (ControllerButtons.RightStick, "RS"),
        (ControllerButtons.Start, "Start"),
        (ControllerButtons.Back, "Back"),
        (ControllerButtons.A, "A"),
        (ControllerButtons.B, "B"),
        (ControllerButtons.X, "X"),
        (ControllerButtons.Y, "Y"),
    ];

    private static readonly (ExtendedControllerButtons Button, string Name)[] OrderedExtendedButtons =
    [
        (ExtendedControllerButtons.C, "C"),
        (ExtendedControllerButtons.Z, "Z"),
        (ExtendedControllerButtons.LM, "LM"),
        (ExtendedControllerButtons.RM, "RM"),
        (ExtendedControllerButtons.M1, "M1"),
        (ExtendedControllerButtons.M2, "M2"),
        (ExtendedControllerButtons.M3, "M3"),
        (ExtendedControllerButtons.M4, "M4"),
        (ExtendedControllerButtons.Circle, "Circle"),
    ];

    public bool IsBound =>
        Buttons != ControllerButtons.None || ExtendedButtons != ExtendedControllerButtons.None;

    public string Format()
    {
        if (!IsBound)
            return string.Empty;
        var buttons = Buttons;
        var extendedButtons = ExtendedButtons;
        var names = OrderedButtons
            .Where(item => (buttons & item.Button) != 0)
            .Select(item => item.Name)
            .Concat(OrderedExtendedButtons
                .Where(item => (extendedButtons & item.Button) != 0)
                .Select(item => item.Name));
        return string.Join('+', names);
    }

    public bool IsPressed(ControllerButtons pressed) =>
        IsPressed(pressed, ExtendedControllerButtons.None);

    public bool IsPressed(
        ControllerButtons pressed,
        ExtendedControllerButtons extendedPressed) =>
        IsBound &&
        (pressed & Buttons) == Buttons &&
        (extendedPressed & ExtendedButtons) == ExtendedButtons;

    public static bool TryParse(string? value, out ControllerBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var buttons = ControllerButtons.None;
        var extendedButtons = ExtendedControllerButtons.None;
        foreach (var rawToken in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseButton(rawToken, out var button))
            {
                if (button == ControllerButtons.DPadDown)
                    return false;
                if ((buttons & button) != 0)
                    return false;
                buttons |= button;
                continue;
            }
            if (TryParseExtendedButton(rawToken, out var extendedButton))
            {
                if ((extendedButtons & extendedButton) != 0)
                    return false;
                extendedButtons |= extendedButton;
                continue;
            }
            return false;
        }

        var buttonCount = BitOperations.PopCount((uint)buttons) +
                          BitOperations.PopCount((uint)extendedButtons);
        if (buttonCount is 0 or > 2)
            return false;
        binding = new ControllerBinding(buttons, extendedButtons);
        return true;
    }

    internal static bool ContainsReservedDPadDown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        foreach (var rawToken in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseButton(rawToken, out var button) && button == ControllerButtons.DPadDown)
                return true;
        }
        return false;
    }

    private static bool TryParseButton(string value, out ControllerButtons button)
    {
        foreach (var item in OrderedButtons)
        {
            if (item.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                button = item.Button;
                return true;
            }
        }

        var alias = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        button = alias.ToUpperInvariant() switch
        {
            "LEFTBUMPER" => ControllerButtons.LeftBumper,
            "RIGHTBUMPER" => ControllerButtons.RightBumper,
            "LEFTSTICK" => ControllerButtons.LeftStick,
            "RIGHTSTICK" => ControllerButtons.RightStick,
            "DPADUP" => ControllerButtons.DPadUp,
            "DPADDOWN" => ControllerButtons.DPadDown,
            "DPADLEFT" => ControllerButtons.DPadLeft,
            "DPADRIGHT" => ControllerButtons.DPadRight,
            _ => ControllerButtons.None,
        };
        return button != ControllerButtons.None;
    }

    private static bool TryParseExtendedButton(
        string value,
        out ExtendedControllerButtons button)
    {
        foreach (var item in OrderedExtendedButtons)
        {
            if (item.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                button = item.Button;
                return true;
            }
        }

        var alias = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        button = alias.ToUpperInvariant() switch
        {
            "LEFTMIDDLE" => ExtendedControllerButtons.LM,
            "RIGHTMIDDLE" => ExtendedControllerButtons.RM,
            "O" => ExtendedControllerButtons.Circle,
            _ => ExtendedControllerButtons.None,
        };
        return button != ExtendedControllerButtons.None;
    }
}

internal static class KeyboardKeyNames
{
    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D,
        ["Escape"] = 0x1B,
        ["Space"] = 0x20,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["End"] = 0x23,
        ["Home"] = 0x24,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
    };

    internal static bool TryParse(string value, out ushort virtualKey)
    {
        virtualKey = 0;
        if (NamedKeys.TryGetValue(value, out virtualKey))
            return true;
        if (value.Length == 1)
        {
            var character = char.ToUpperInvariant(value[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = character;
                return true;
            }
        }
        if (value.Length is >= 2 and <= 3 &&
            value[0] is 'F' or 'f' &&
            int.TryParse(value[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            virtualKey = checked((ushort)(0x70 + functionKey - 1));
            return true;
        }
        if (value.StartsWith("VK_", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(value[3..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out virtualKey) &&
            virtualKey != 0)
        {
            return true;
        }
        return false;
    }

    internal static string Format(ushort virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
            return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87)
            return $"F{virtualKey - 0x70 + 1}";
        foreach (var item in NamedKeys)
        {
            if (item.Value == virtualKey)
                return item.Key;
        }
        return $"VK_{virtualKey:X2}";
    }
}
