namespace GBFR.ChatOverlay.Input;

[Flags]
public enum InputCaptureDevices
{
    None = 0,
    Keyboard = 1 << 0,
    Mouse = 1 << 1,
    Text = 1 << 2,
    All = Keyboard | Mouse | Text,
}
