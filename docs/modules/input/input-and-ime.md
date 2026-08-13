# Input and IME

## Scope

The input module separates three responsibilities:

- native keyboard/mouse interception for suppressing game input while an overlay owns it;
- standard and extended controller polling for mod hotkeys;
- Win32 text/IME routing into Dear ImGui.

Primary sources:

- `Input/DirectInputKeyboardHook.cs`
- `Input/HotkeyBinding.cs`
- `Input/HotkeyConfigurationSnapshot.cs`
- `Input/XInputControllerPoller.cs`
- `Input/FlydigiExtendedControllerPoller.cs`
- `Overlay/ChatOverlayPeer.cs`
- `Overlay/Win32ImeCompatibility.cs`
- `Overlay/Win32ImeCandidateReader.cs`
- `Native/Interop/DirectInputBrokerBridge.cs`
- `NativeBridge/directinput_broker.cpp`

## DirectInput broker

The native bridge patches only the game executable's imports for `DirectInput8Create` and `XInputGetState`. It does not replace the exported entry in `dinput8.dll` or a ReShade proxy. When the game creates a keyboard or mouse device, the bridge attaches to COM vtable entries `GetDeviceState` index `9` and `GetDeviceData` index `10`.

Native hooks never call managed code. They update an atomic 64-byte ABI v2 snapshot containing 256 keyboard scan-code bits, controller bits, readiness flags, policy flags, and a sequence number. `DirectInputKeyboardHook.Poll` reads that snapshot from the shared Present tick.

Capture policy can independently suppress activation, settings, push-to-talk, quick actions, keyboard, and mouse input. On release, the broker drains held keys/buttons before reporting capture inactive so a key used inside the overlay is not replayed into Relink.

## Hotkey behavior

Keyboard bindings support one primary key plus Ctrl, Shift, and Alt. Actions fire on a physical down edge and reset on release. Rebinding waits for all previous keys to be released before accepting a new edge.

Standard controller bindings use XInput buttons and may contain one or two buttons. User-level controller bindings remain available for settings, chat, push-to-talk, global chat mute, and player chat mute.

Individual quick actions are keyboard-only and run directly from their own binding; there is no separate quick-action panel hotkey. The obsolete panel bindings and per-action controller field are accepted as unknown legacy JSON and are not written back.

`DPadDown` is rejected by both parser and binding-capture UI because Relink uses it for the official quick-phrase path. This prevents a mod binding from sending an unrelated official communication line.

## Flydigi extended buttons

The Vader 5 Pro path reads HID vendor `0x37D7`, product `0x2401`, protocol interface `mi_01`. It supports `C`, `Z`, `LM`, `RM`, `M1`, `M2`, `M3`, `M4`, and `Circle`.

Input reports are accepted only after the device reports that third-party takeover is allowed and the acquisition command succeeds. The poller sends the vendor protocol's `SDL` acquisition token; `SDL` is protocol data, not a button the user presses. Steam Input or another owner may prevent raw extended-button acquisition. In that case the extended path reports unavailable instead of translating ordinary XInput buttons into guessed extended buttons.

## Text editing and Backspace

When the chat composer or settings text field owns text input, normal editing messages continue to Dear ImGui. The hotkey router consumes only configured mod hotkeys; it does not consume ordinary Backspace. Backspace has a special meaning only while the key-binding capture dialog is active, where it clears that binding.

## Chinese IME flow

Relink may expose an ANSI game window even when a Chinese IME is active. The text path handles both Unicode and ANSI/DBCS windows:

```text
WM_IME_SETCONTEXT / composition notifications
  -> enable candidate UI and notify ImGui's Win32 backend
WM_IME_CHAR on Unicode window
  -> AddInputCharacterUTF16
WM_IME_CHAR on ANSI window
  -> decode packed bytes with the active input locale code page
  -> AddInputCharactersUTF8
split ANSI WM_CHAR pair
  -> retain DBCS lead byte
  -> decode lead + trail together
  -> AddInputCharactersUTF8
```

The ANSI path consumes the original message after UTF-8 insertion so `DefWindowProcA` cannot split one Chinese character into two Latin-1-looking characters.

When enabled, the candidate fallback reads up to four IMM32 candidate lists, validates their bounded buffers, and renders the first readable list beside the active field. IMEs that draw candidates exclusively through external TSF/Qt UI still receive their normal default-window-procedure path.

## Focus and safety

Window focus loss clears pending binding capture, resets incomplete DBCS state, releases overlay input, and forces push-to-talk muted. Any broker ABI mismatch or native failure changes the broker to fail-open for game input and fail-closed for mod hotkeys.
