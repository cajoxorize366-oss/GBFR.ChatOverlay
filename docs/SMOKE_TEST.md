# Relink 2.0.2 smoke test

The current build is automatically copied to the Reloaded-II Mods directory, but it must be enabled for the Granblue Fantasy: Relink profile before launch.

## Expected startup log

The Reloaded-II log should contain messages equivalent to:

```text
[gbfr.qol.chatoverlay] Relink 2.0.2 native chat bridge attached: send=..., receive=....
[gbfr.qol.chatoverlay] DirectInput8 keyboard interception initialized.
[gbfr.qol.chatoverlay] DX11 safety backend attached (guarded Present/ResizeBuffers callbacks).
[gbfr.qol.chatoverlay] IDirectInput8::CreateDevice hooked (...).
[gbfr.qol.chatoverlay] DirectInput system keyboard device detected.
[gbfr.qol.chatoverlay] IDirectInputDevice8::GetDeviceState hooked.
[gbfr.qol.chatoverlay] Loaded CJK font: ...
[gbfr.qol.chatoverlay] DirectX 11 ImGui hook initialized.
```

The `CreateDevice`, keyboard-device and `GetDeviceState` lines appear only after the game initializes DirectInput.

## Visual and input checks

1. Confirm that the lower-left system message says the native Relink 2.0.2 bridge is connected.
2. Press `Y` once. The input field should open without inserting the activation key itself.
3. Enter Latin and Chinese text. The IME candidate window and final text should remain usable in borderless and fullscreen modes.
4. In an online lobby or party, press Enter and confirm a second vanilla client receives the message.
5. Have the second client send a free-text reply. Confirm it appears once in the Overlay history.
6. Confirm the local message appears once as `You`; a server echo with identical text should not add a duplicate line.
7. While the input field is open, press movement and combat keys. The game should not respond to them.
8. Press Escape. The input field should close, and controls should resume after held keys have been released.
9. Disable `Enable Overlay` and confirm that the Mod no longer captures `Y`.

## Failure handling

- If `Native chat bridge validation failed` appears, preserve the reported executable SHA-256. The Overlay should remain usable as a local preview.
- If the game fails before `DirectX 11 ImGui hook initialized`, disable the Mod and preserve the Reloaded-II log.
- If `DX11 overlay disabled after a graphics error; game rendering will continue` appears, the guard worked but the Overlay was degraded for that session. Preserve the complete line: it identifies the exact Present/ResizeBuffers stage, dimensions and HRESULT needed for the next compatibility fix. This is not a successful visual test unless the Overlay remains visible.
- An unhandled exception whose stack ends in `ImguiHookDx11.ResizeBuffersImpl` means the guarded backend was not loaded. Confirm that `DX11 safety backend attached` appeared and that `GBFR.ChatOverlay.dll` came from the same package; do not mix individual DLLs from different builds.
- If the Overlay renders but controls still respond, preserve the three DirectInput log lines; their presence distinguishes a state-filter bug from a missed hook.
- If Chinese characters render as boxes, record the `Loaded CJK font` path and Windows display language.
- If sending closes the input but the second client receives nothing, record whether the current state is an online lobby, town, quest or results screen; the original native function retains Relink's own state validation.
- Hashed quick-chat/stamp records are intentionally ignored by the incoming bridge until their text resolver is hooked.

## Optional Party lifecycle probe

The PlayFab Party probe is enabled by default for this validation build and never sends Party data. Restart the Mod after changing `Enable Party Lifecycle Probe`; disable it after preserving the private-session capture.

Expected startup evidence:

```text
Party lifecycle probe attached at 0x...; observation only, no Party calls or sends.
Party manager captured from PartyInitialize: 0x....
```

If Party initialized before the Mod attached, the manager can instead be captured from `PartyStartProcessingStateChanges`. During host/join/leave, preserve the ordered lifecycle lines. The host should include `CreateNewNetworkCompleted`; a joining client should include `ConnectToNetworkCompleted`. Both sides should then show authentication and endpoint lifecycle events such as:

```text
Party lifecycle state AuthenticateLocalUserCompleted (4).
Party lifecycle state CreateEndpointCompleted (10).
Party lifecycle state EndpointCreated (12).
```

Leaving should produce endpoint, device and network leave/destroy events. `EndpointMessageReceived`, text and transcription payload events are deliberately filtered and must not appear in the probe log.

Disable the probe after collecting both logs. If enabling it changes matchmaking, causes a crash, or prevents normal chat, disable the Mod and preserve the Reloaded-II log; do not proceed to the ChatControl canary.

## Stage 2 muted ChatControl canary

The current validation build enables `Enable Muted Party ChatControl Canary` by default. Test only in a private two-client session with the same package installed on both sides. This stage selects the system-default input/output only after synchronously setting and verifying input mute. It never binds or calls `PartyChatControlSetPermissions`, so no peer is authorized to send or receive voice.

On each client, the local path should include lines equivalent to:

```text
Party lifecycle/Stage 2 canary attached at 0x...; one muted ChatControl may join the existing PartyNetwork, with no chat permissions granted.
Stage 2 captured authenticated existing session: network=0x..., localUser=0x....
Stage 2 confirmed Relink's existing gameplay endpoint before canary creation: endpoint=0x....
Stage 2 canary creation queued on existing manager/network/device: ... Input mute was set and verified before system-default I/O selection; PartyChatControlSetPermissions is not bound or called.
Stage 2 CreateChatControlCompleted: result=0, ...
Stage 2 ChatControlCreated (local canary): chatControl=0x....
Stage 2 SetChatAudioInputCompleted: result=0, ... selectionType=1.
Stage 2 SetChatAudioOutputCompleted: result=0, ... selectionType=1.
Stage 2 ConnectChatControlCompleted: result=0, ...
Stage 2 ChatControlJoinedNetwork (local canary): network=0x..., chatControl=0x....
Stage 2 muted ChatControl canary joined the existing PartyNetwork. Input remains muted and chat permissions remain None.
```

After both clients are present, each side must also observe its peer without changing permissions:

```text
Stage 2 ChatControlCreated (remote/other): chatControl=0x....
Stage 2 ChatControlJoinedNetwork (remote/other): network=0x..., chatControl=0x....
```

Leave the session normally on both clients. For each successfully joined local canary, the leave path should first include a line equivalent to:

```text
Stage 2 pre-leave DestroyChatControl queued before Relink PartyNetworkLeaveNetwork: network=0x..., chatControl=0x...; awaiting local left/completed/destroyed events from the game's state-change pump.
```

Then preserve `ChatControlLeftNetwork (local canary)`, `DestroyChatControlCompleted: result=0`, `ChatControlDestroyed (local canary)`, `Stage 2 cleanup complete`, and `PartyCleanup completed`. Event interleaving can differ, but the handles in the local completion lines must match the locally owned canary. The peer should independently observe the departing control as `remote/other`.

If `Stage 2 manager cleanup reached before local ChatControl teardown completed` appears, preserve the full diagnostic fields. `PartyCleanup completed` still proves the manager's safety fallback ran, but the strict Stage 2 teardown-event check has not passed.

The test fails if either client logs `Stage 2 canary disabled (fail-closed)`, a nonzero Stage 2 result/error, a manager ownership conflict, missing mute verification, or a second local ChatControl. It also fails if matchmaking, native text chat or rendering changes. There must be no permission grant, audio unmute, second `PartyInitialize`, new gameplay endpoint, or `PartyEndpointSendMessage` action from this Mod. Disable `Enable Muted Party ChatControl Canary`, restart, and preserve both full logs after any failure.
