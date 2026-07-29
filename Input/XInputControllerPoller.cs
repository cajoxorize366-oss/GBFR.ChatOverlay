using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Input;

internal readonly record struct XInputControllerSnapshot(
    bool ApiAvailable,
    bool IsConnected,
    ControllerButtons Buttons,
    ulong Sequence);

internal interface IXInputControllerBackend
{
    bool IsAvailable { get; }

    bool TryGetButtons(uint userIndex, out ControllerButtons buttons);
}

internal sealed class XInputControllerPoller
{
    private const uint MaximumUsers = 4;
    private readonly IXInputControllerBackend _backend;
    private bool _lastApiAvailable;
    private bool _lastConnected;
    private ControllerButtons _lastButtons;
    private ulong _sequence;

    internal XInputControllerPoller()
        : this(new NativeXInputControllerBackend())
    {
    }

    internal XInputControllerPoller(IXInputControllerBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    internal XInputControllerSnapshot Poll()
    {
        var available = _backend.IsAvailable;
        var connected = false;
        var buttons = ControllerButtons.None;
        if (available)
        {
            for (uint userIndex = 0; userIndex < MaximumUsers; userIndex++)
            {
                if (!_backend.TryGetButtons(userIndex, out var userButtons))
                    continue;
                connected = true;
                buttons |= userButtons;
            }
        }

        if (available != _lastApiAvailable ||
            connected != _lastConnected ||
            buttons != _lastButtons)
        {
            _lastApiAvailable = available;
            _lastConnected = connected;
            _lastButtons = buttons;
            _sequence++;
        }

        return new XInputControllerSnapshot(available, connected, buttons, _sequence);
    }
}

internal sealed class NativeXInputControllerBackend : IXInputControllerBackend
{
    private const uint ErrorSuccess = 0;
    private readonly XInputGetStateDelegate? _getState;

    internal NativeXInputControllerBackend()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string[] libraryNames =
        [
            "xinput1_4.dll",
            "xinput1_3.dll",
            "xinput9_1_0.dll",
            "xinputuap.dll",
        ];
        foreach (var libraryName in libraryNames)
        {
            if (!NativeLibrary.TryLoad(libraryName, out var library))
                continue;
            if (!NativeLibrary.TryGetExport(library, "XInputGetState", out var address))
            {
                NativeLibrary.Free(library);
                continue;
            }

            _getState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(address);
            break;
        }
    }

    public bool IsAvailable => _getState is not null;

    public bool TryGetButtons(uint userIndex, out ControllerButtons buttons)
    {
        buttons = ControllerButtons.None;
        if (_getState is null)
            return false;

        try
        {
            var result = _getState(userIndex, out var state);
            if (result != ErrorSuccess)
                return false;
            buttons = state.Gamepad.Buttons;
            return true;
        }
        catch
        {
            return false;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint XInputGetStateDelegate(uint userIndex, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        internal uint PacketNumber;
        internal XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        internal ControllerButtons Buttons;
        internal byte LeftTrigger;
        internal byte RightTrigger;
        internal short ThumbLeftX;
        internal short ThumbLeftY;
        internal short ThumbRightX;
        internal short ThumbRightY;
    }
}
