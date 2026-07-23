using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Input;

public interface IXInputStateSource
{
    bool TryGetButtons(int playerIndex, out ushort buttons);
}

public sealed class PushToTalkChordDetector
{
    public const ushort LeftShoulder = 0x0100;
    public const ushort RightThumb = 0x0080;
    public const ushort DefaultChord = LeftShoulder | RightThumb;

    private readonly ushort _chord;
    private int _activePlayerIndex = -1;
    private bool _chordWasDown;
    private bool _captureAccepted;

    public PushToTalkChordDetector(ushort chord = DefaultChord)
    {
        if (chord == 0)
            throw new ArgumentOutOfRangeException(nameof(chord));
        _chord = chord;
    }

    public void Poll(
        IXInputStateSource source,
        bool enabled,
        Func<bool> tryBeginCapture,
        Action endCapture)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tryBeginCapture);
        ArgumentNullException.ThrowIfNull(endCapture);

        if (!enabled)
        {
            EndAcceptedCapture(endCapture);
            _chordWasDown = false;
            _activePlayerIndex = -1;
            return;
        }

        var playerIndex = _activePlayerIndex;
        var chordIsDown = playerIndex >= 0
            ? source.TryGetButtons(playerIndex, out var activeButtons) && HasChord(activeButtons)
            : TryFindPressedChord(source, out playerIndex);

        if (chordIsDown && !_chordWasDown)
        {
            _activePlayerIndex = playerIndex;
            _captureAccepted = tryBeginCapture();
        }
        else if (!chordIsDown && _chordWasDown)
        {
            EndAcceptedCapture(endCapture);
            _activePlayerIndex = -1;
        }

        _chordWasDown = chordIsDown;
    }

    public void Reset()
    {
        _activePlayerIndex = -1;
        _chordWasDown = false;
        _captureAccepted = false;
    }

    private bool TryFindPressedChord(IXInputStateSource source, out int playerIndex)
    {
        for (var index = 0; index < 4; index++)
        {
            if (source.TryGetButtons(index, out var buttons) && HasChord(buttons))
            {
                playerIndex = index;
                return true;
            }
        }

        playerIndex = -1;
        return false;
    }

    private bool HasChord(ushort buttons) => (buttons & _chord) == _chord;

    private void EndAcceptedCapture(Action endCapture)
    {
        if (_captureAccepted)
            endCapture();
        _captureAccepted = false;
    }
}

public sealed class XInputStateSource : IXInputStateSource
{
    public bool TryGetButtons(int playerIndex, out ushort buttons)
    {
        try
        {
            var result = XInputGetState((uint)playerIndex, out var state);
            buttons = result == 0 ? state.Gamepad.Buttons : (ushort)0;
            return result == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            buttons = 0;
            return false;
        }
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint playerIndex, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLeftX;
        public short ThumbLeftY;
        public short ThumbRightX;
        public short ThumbRightY;
    }
}
