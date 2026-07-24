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

## Party lifecycle foundation

The PlayFab Party lifecycle probe is enabled by default. The probe itself is observation-only; the separately configured Stage 2/3 ChatControl canary makes the Party calls described below. Restart the Mod after changing either Party option.

Expected startup evidence:

```text
Party lifecycle/Stage 3 voice test attached at 0x...; one ChatControl may join the existing PartyNetwork. Microphone stays muted unless U is held.
Party manager captured from PartyInitialize: 0x....
```

If Party initialized before the Mod attached, the manager can instead be captured from `PartyStartProcessingStateChanges`. During host/join/leave, preserve the ordered lifecycle lines. The host should include `CreateNewNetworkCompleted`; a joining client should include `ConnectToNetworkCompleted`. Both sides should then show authentication and endpoint lifecycle events such as:

```text
Party lifecycle state AuthenticateLocalUserCompleted (4).
Party lifecycle state CreateEndpointCompleted (10).
Party lifecycle state EndpointCreated (12).
```

Leaving should produce endpoint, device and network leave/destroy events. `EndpointMessageReceived`, text and transcription payload events are deliberately filtered and must not appear in the probe log.

The one-time `PartyStartProcessingStateChanges returned error 0x00001000; further errors are suppressed.` line at startup is not by itself a failed test. It can occur before Relink supplies the authenticated session. Judge the run by the later authenticated, endpoint, ChatControl and permission success lines. If enabling the Mod changes matchmaking, causes a crash, or prevents normal chat, disable it and preserve the full Reloaded-II log.

## Stage 2 ChatControl lifecycle

The current validation build enables `Enable Muted Party ChatControl Canary` by default. Test only in a private two-client session with the same package installed on both sides. Before launching the game, open the Mod configuration and choose `Voice Microphone` and `Voice Playback Device` independently. These two rows must be real ComboBox lists rather than endpoint-ID text fields. Each list must contain an explicit `Default (Windows system default)` item plus the active Windows endpoints. A new or migrated configuration selects `Default` for both roles. Save and restart after changing either value. The canary applies those selections only after synchronously setting and verifying input mute. Voice permissions are not granted until the Stage 2 join sequence is complete and a remote Mod ChatControl has been observed on the same PartyNetwork.

Each client must first log one result for each selected role. A manual choice logs its friendly name and stable endpoint ID; the default choice logs that it follows Windows. If a saved device was unplugged or disabled, the log must explicitly say it is not active and that the Mod is falling back:

```text
Stage 3 voice microphone: selected "..." with manual Windows endpoint ID ....
Stage 3 voice playback: following the Windows default communications device.
```

On each client, the local path should include lines equivalent to:

```text
Party lifecycle/Stage 3 voice test attached at 0x...; one ChatControl may join the existing PartyNetwork. Microphone stays muted unless U is held.
Stage 2 captured authenticated existing session: network=0x..., localUser=0x....
Stage 2 confirmed Relink's existing gameplay endpoint before canary creation: endpoint=0x....
Stage 2 canary creation queued on existing manager/network/device: ... Input mute was set and verified before audio selection; microphone="..." (Manual), playback="..." (SystemDefault); microphone permissions remain None until a remote Mod ChatControl joins this network.
Stage 2 CreateChatControlCompleted: result=0, ...
Stage 2 ChatControlCreated (local canary): chatControl=0x....
Stage 2 SetChatAudioInputCompleted: result=0, ... selectionType=3, device="...".
Stage 2 SetChatAudioOutputCompleted: result=0, ... selectionType=1, device="...".
Stage 2 ConnectChatControlCompleted: result=0, ...
Stage 2 ChatControlJoinedNetwork (local canary): network=0x..., chatControl=0x....
Stage 2 muted ChatControl canary joined the existing PartyNetwork. Input remains muted; Stage 3 microphone permissions wait for a remote Mod ChatControl on this same network.
```

After both clients are present, each side must observe its peer:

```text
Stage 2 ChatControlCreated (remote/other): chatControl=0x....
Stage 2 ChatControlJoinedNetwork (remote/other): network=0x..., chatControl=0x....
```

## Stage 3 two-client realtime voice test

Before arranging a second tester, wear headphones and run the local preflight from the main menu twice in immediate succession: hold `I`, speak, release, then press and hold `I` again without waiting for endpoint cleanup. The overlay should show `本地监听中`, then `本地自检通过` after a signal is detected on both holds. You should hear the selected microphone through the selected playback device only while `I` is held; release must silence it immediately, and the second hold must not remain stuck at `本地监听中`. Expected logs are:

```text
Local microphone monitor started: input="...", output="...", volume=35%. Audio remains on this PC and is not sent through Party.
Local microphone monitor detected input signal (peak ...%).
Local microphone monitor release acknowledged; local playback was gated off and endpoint cleanup continues in the background.
Local microphone monitor result: PASS — microphone signal was detected and sent to the selected local playback path.
Local microphone monitor cleanup queued (stop requested); the playback gate is already closed.
Local microphone monitor RecordingStopped event observed=True after ... ms.
Local microphone monitor playback stopped after ... ms; PlaybackStopped event observed=True.
Local microphone monitor cleanup complete after ... ms.
```

Cleanup lines from the first and second holds may interleave because cleanup is deliberately asynchronous. If it exceeds two seconds, the log reports the exact phase (`requesting microphone stop`, `stopping local playback`, `draining audio callbacks`, or `disposing endpoints`); playback must nevertheless remain silent and another `I` hold must still start. No authenticated Party session, remote ChatControl or microphone permission is required for this check. If no signal is observed, fix the Windows privacy/input meter, selected microphone or gain before a two-client run. If `I` passes but Party later reports `NoAudioInput`, the physical Windows path is working and the remaining fault is inside Party selection/integration. Using speakers can create acoustic feedback; the self-monitor volume defaults to 35% and is capped at 50%.

Prerequisites for `U`: both testers must use the exact same ZIP, leave both Party options enabled, keep `Experimental Voice (U Party / I Local Test)` enabled, select the intended microphone and playback device in the two Mod configuration lists, save, and restart before testing. The two choices may be different devices and do not have to be the Windows defaults. Use a private two-client room and label the saved logs as client A and client B. Do not begin the voice test unless each client has both successful `SetChatAudio...Completed` lines with the expected `selectionType` (`1` for default or `3` for manual) and displayed device name.

Before touching `U`, both logs must contain a grant for the remote control discovered above:

```text
Stage 3 voice test permissions granted for remote ChatControl=0x... on network=0x...: SendMicrophoneAudio|ReceiveMicrophoneAudio (0x0005). Input remains muted until U is held.
```

The voice row at the top of the chat overlay should progress from `[VOICE] 等待进入联机房间 · 可按住 I 本地监听` to `[VOICE] 等待队友语音通道 · 按住 I 本地监听`, then `[VOICE] 已就绪 · U 队友通话 / I 本地监听` after the permission line. A key-down alone must not display the speaking state before Party confirms the native unmute.

Run this exact test in both directions:

1. Client A holds `U`, speaks a short phrase, then releases `U`.
2. After Party confirms unmute, A's overlay must show `>>> [VOICE] 正在语音 · 松开 U 静音 <<<` and log `Stage 3 push-to-talk microphone UNMUTED while U is held.`
3. On release, A's overlay must return to `[VOICE] 已就绪 · U 队友通话 / I 本地监听` and log `Stage 3 push-to-talk microphone muted.`
4. Client B must hear the phrase only during A's hold interval. B's log must also transition its matching `Stage 3 voice diagnostics PEER` line to `remoteIndicator=Talking (1)` and `diagnosis=PASS_REMOTE_AUDIO_RECEIVED_BY_PARTY`. This proves Party observed remote voice; physical audibility remains a separate manual observation.
5. Repeat with B speaking and A listening. Both directions must pass; one-way audio is a failed test.
6. While holding `U`, switch focus away from the game without delivering a normal key-up. Within roughly 350 ms, the speaker should log `Stage 3 push-to-talk heartbeat timed out; microphone mute was forced.` and the peer must stop hearing audio. After focus returns, the overlay must no longer show `正在语音`. Repeat a normal hold/release once to prove recovery.

Speak continuously for at least three seconds during each direction so the low-noise diagnostic poll cannot miss the indicator transition. The expected LOCAL/PEER lines, terminal summary meanings, every official Party indicator state and the complete decision matrix are documented in [VOICE_TROUBLESHOOTING_MATRIX.md](VOICE_TROUBLESHOOTING_MATRIX.md). One labelled A/B log pair from that procedure should replace repeated one-setting-at-a-time tests.

`I` is consumed whenever local monitoring is available, and `U` is consumed only after the remote Mod voice path is ready. `I` and `U` cannot be active together; `U` wins, and an interrupted held `I` must be released before it can start again. The Party microphone must remain silent before the permission log, while `U` is released, after focus loss, and after the last remote Mod ChatControl leaves.

## Session-exit and cleanup test

After bidirectional voice passes, have A leave the room normally while B remains. For the strongest boundary check, A may hold `U` and use mouse/controller navigation to trigger leave; the pre-leave detour must mute before destruction. For each successfully joined local canary, the leave path should first include a line equivalent to:

```text
Stage 2 pre-leave DestroyChatControl queued before Relink PartyNetworkLeaveNetwork: network=0x..., chatControl=0x...; awaiting local left/completed/destroyed events from the game's state-change pump.
```

Then preserve `ChatControlLeftNetwork (local canary)`, `DestroyChatControlCompleted: result=0`, `ChatControlDestroyed (local canary)`, `Stage 2 cleanup complete`, and `PartyCleanup completed`. Event interleaving can differ, but the handles in the local completion lines must match the locally owned canary. B should independently observe A as `ChatControlLeftNetwork (remote/other)` and/or `ChatControlDestroyed (remote/other)` and must stop hearing A immediately. Repeat by recreating the room and swapping host/guest roles if possible.

If `Stage 2 manager cleanup reached before local ChatControl teardown completed` appears, preserve the full diagnostic fields. `PartyCleanup completed` still proves the manager's safety fallback ran, but the strict Stage 2 teardown-event check has not passed.

The test fails if either client lacks the `0x0005` permission line, logs `Stage 3 voice test failed closed`, logs `Stage 2 canary disabled (fail-closed)`, reports a nonzero Party operation, or cannot complete the local cleanup chain. It also fails for one-way/no audio, audio while `U` is released, local monitor audio after `I` is released, audio continuing after focus loss/peer departure, a manager ownership conflict, a second local ChatControl, changed matchmaking, broken native text chat or rendering. The Mod must not call `PartyEndpointSendMessage`, initialize a second Party manager or create another gameplay endpoint. Disable `Experimental Voice (U Party / I Local Test)`, restart, and preserve both complete logs plus approximate key-down/key-up/leave times after any failure.

If either audio row is a plain text field, or opening Mod configuration reports that the audio-device UI is missing, verify that `GBFR.ChatOverlay.dll` and `GBFR.ChatOverlay.ConfiguratorUI.dll` came from the same ZIP. The second DLL is launcher-only; it must be present beside the main Mod DLL but is deliberately not referenced or loaded by the game-side assembly.
