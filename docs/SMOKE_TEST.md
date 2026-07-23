# Relink 2.0.2 smoke test

The current build is automatically copied to the Reloaded-II Mods directory, but it must be enabled for the Granblue Fantasy: Relink profile before launch.

## Expected startup log

The Reloaded-II log should contain messages equivalent to:

```text
[gbfr.qol.chatoverlay] DirectInput8 keyboard interception initialized.
[gbfr.qol.chatoverlay] IDirectInput8::CreateDevice hooked (...).
[gbfr.qol.chatoverlay] DirectInput system keyboard device detected.
[gbfr.qol.chatoverlay] IDirectInputDevice8::GetDeviceState hooked.
[gbfr.qol.chatoverlay] Loaded CJK font: ...
[gbfr.qol.chatoverlay] DirectX 11 ImGui hook initialized.
```

The `CreateDevice`, keyboard-device and `GetDeviceState` lines appear only after the game initializes DirectInput.

## Visual and input checks

1. Confirm that two local-preview system messages appear at the lower-left of the game window.
2. Press `Y` once. The input field should open without inserting the activation key itself.
3. Enter Latin and Chinese text. The IME candidate window and final text should remain usable in borderless and fullscreen modes.
4. Press Enter. The message should be added as `You`; it must not be sent to other players in this milestone.
5. While the input field is open, press movement and combat keys. The game should not respond to them.
6. Press Escape. The input field should close, and controls should resume after held keys have been released.
7. Disable `Enable Overlay` in the Reloaded-II config and confirm that the Mod no longer captures `Y`.

## Failure handling

- If the game fails before `DirectX 11 ImGui hook initialized`, disable the Mod and preserve the Reloaded-II log.
- If the Overlay renders but controls still respond, preserve the three DirectInput log lines; their presence distinguishes a state-filter bug from a missed hook.
- If Chinese characters render as boxes, record the `Loaded CJK font` path and Windows display language.
- This milestone contains no game addresses or network hooks, so a game-version mismatch should fail at the generic ImGui/DirectInput layer rather than call an unknown Relink function.
