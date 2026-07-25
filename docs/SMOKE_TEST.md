# Relink 2.0.2 smoke test

The current build is automatically copied to the Reloaded-II Mods directory, but it must be enabled for the Granblue Fantasy: Relink profile before launch.

## Expected startup log

The Reloaded-II log should contain messages equivalent to:

```text
[gbfr.qol.chatoverlay] Relink 2.0.2 native chat bridge attached: send=..., receive=....
[gbfr.qol.chatoverlay] Relink incoming player-name resolver attached: senderSlot=..., memberLookup=...; empty RPC sender labels now use the verified four-slot lobby member table.
[gbfr.qol.chatoverlay] Relink 2.0.2 native party-HUD tracker attached; lobby/battle mode, resolution, aspect ratio and HUD scale now follow the game's live UI node transforms.
[gbfr.qol.chatoverlay] DirectInput8 keyboard interception initialized.
[gbfr.qol.chatoverlay] CJK font loaded before DX11 hook initialization: ..., 9 glyph ranges.
[gbfr.qol.chatoverlay] DirectX 11 ImGui hook initialized with the Extra Sigil compatibility path.
[gbfr.qol.chatoverlay] IDirectInput8::CreateDevice hooked (...).
[gbfr.qol.chatoverlay] DirectInput system keyboard device detected.
[gbfr.qol.chatoverlay] IDirectInputDevice8::GetDeviceState hooked.
[gbfr.qol.chatoverlay] First Direct3D11 Present callback: OS TID ....
```

The `CreateDevice`, keyboard-device and `GetDeviceState` lines appear only after the game initializes DirectInput.

The ImGui backend and Relink's native chat manager may both initialize before an online room exists. They are not room-readiness signals. The Overlay itself and its `Y/U/I` interception must remain inactive on the title, save-selection, loading and solo-town flows. It opens only after Relink authenticates the local user on an existing PartyNetwork and successfully creates the matching local gameplay endpoint. A host does not need to wait for a remote player. The log should then contain exactly one transition line:

```text
[gbfr.qol.chatoverlay] Relink online Party room became active; overlay rendering and Y/U/I hotkeys are now enabled.
[gbfr.qol.chatoverlay] Dear ImGui platform IME bridge bound to game window 0x...; platform callback available=True; IMM32 composition and candidate positioning follow the active text caret.
```

Calling `PartyNetworkLeaveNetwork`, or observing the matching endpoint, local user, Network or Party manager being destroyed, must hide the Overlay again and release any open composer or held voice/local-monitor input.

After the chat field first becomes active, the log should also contain exactly one line equivalent to:

```text
[gbfr.qol.chatoverlay] Win32 IME compatibility active for the ANSI/code page 936 game window; committed text is normalized to UTF-8 and candidate placement follows the chat input.
```

`Unicode` is also a valid window-kind result. On a Chinese ANSI window using Sogou or Microsoft Pinyin, code page `936` is expected. If Windows reactivates the game IME context while the chat field is open, one additional `Win32 IME candidate UI enabled ...` line is expected; its forwarded `WM_IME_SETCONTEXT` value must end with candidate bits `F`. It is supplementary evidence, not required on every chat open because the game keeps one top-level HWND active.

When the IME opens its first readable candidate page, the log should contain:

```text
[gbfr.qol.chatoverlay] Win32 IME candidate fallback captured list 0: count=..., selection=..., pageStart=..., pageSize=...; candidates are now drawn inside the Overlay.
```

If the composition ends with `without an IMM32 candidate list`, preserve that complete line: it means this input method exposed only an external TSF/Qt UI and the fallback received no words to draw.

## Visual and input checks

1. Stay on the title screen, save-selection screen, loading screen and a solo town with no online room. Confirm that the chat window is not drawn and pressing `Y`, `U` or `I` is left to the game. The separate default `Show All Slots` position test may draw microphone icons only when live party-HUD rows exist; disable that switch when checking the strict no-visual baseline.
2. Create an online room as host. After `AuthenticateLocalUserCompleted` and the matching successful `CreateEndpointCompleted`, confirm that the readiness transition log above appears even before a guest joins.
3. On a second client, join that room and confirm the same transition occurs after its own authentication/endpoint sequence. The lower-left system message should say the native Relink 2.0.2 bridge is connected.
4. Press `Y` once. The input field should open without inserting the activation key itself.
5. Enter `ABC123`, then use Microsoft Pinyin and Sogou to commit `我是`. The field must contain exactly `ABC123我是` once: `我` must never become `ÎÒ`, and Latin characters must not duplicate.
6. While composing with Sogou, confirm that the Overlay displays `候选：1.…` directly above the chat field, including brackets around the selected word. The candidate row must reserve only its actual wrapped text height: a short row must not create a blank line below the status text, and a long row may wrap without covering or pushing the input/status rows outside the window. Select candidates with number keys, Space and normal IME paging; the fallback is display-only and intentionally does not simulate keys or mouse selection.
7. Press Escape during an unfinished composition, reopen with `Y`, and type `我是` again. No pending lead byte, old composition or candidate window may leak into the new input session.
8. In the online room, press Enter and confirm the other client receives the message.
9. Have the second client send a free-text reply. Confirm it appears once in the Overlay history and uses that client's real online display name, not `Player 00000000`, `Player 00000001`, `Player 00000002` or `Player 00000003`.
10. Confirm the local message appears once as `You`; a server echo with identical text should not add a duplicate line.
11. While the input field is open, press movement and combat keys. The game should not respond to them.
12. Press Escape. The input field should close, and controls should resume after held keys have been released.
13. Leave or disband the online room. Confirm the Overlay disappears immediately and `Y/U/I` return to the game before returning to title.
14. Disable `Enable Overlay` and confirm that the Mod no longer captures `Y`.

## 0.4.0 voice-indicator position preview

This first 0.4 package validates HUD placement without requiring another player. `Enable Party Voice Indicators` and `Voice Indicator Debug: Show All Slots` are enabled by default. The debug override intentionally draws an idle microphone icon at 70% opacity for every active CPU/player HUD row; it is not proof that those rows use the Mod. Unlike chat and input, this explicit position-test override may render in a CPU party without an authenticated online room.

1. Form a four-character CPU party in town. Confirm the log reports `Native party-HUD microphone anchors are live: layout=OnlineLobby, activeRows=4, viewport=...`; no manual layout selector exists. All four icons should sit immediately to the right of each compact party information row and must not cover portraits, names, level text or the CPU/platform badge area.
2. Enter a quest with the battle party HUD. Confirm the same log reports `layout=Battle`; the local-player icon must follow the far-right edge of the long local HP row while the other three follow the separate right edge of the shorter teammate HP rows.
3. Repeat at another resolution, HUD scale or ultrawide aspect if available. The icon must remain attached to the same native row edge because its center and size come from that live UI node's final transform; there is no screenshot reference resolution or uniform image scale to tune.
4. If aggregate Party voice reaches `Speaking`, the debug local slot becomes bright and 100% opaque; idle preview slots retain 70% Alpha with a deliberately muted palette so the two states are visually distinct.
5. Disable `Voice Indicator Debug: Show All Slots`. Preview.2 must hide every icon because secure remote ChatControl-to-party-slot identity mapping is deliberately not enabled yet. A CPU or vanilla player must never receive an inferred Mod badge.

Remote per-slot talking state and Mod capability negotiation remain deferred until the native placements are approved. Automatic lobby/battle detection is already supplied by the two controller types. Record the game resolution, HUD scale, reported native layout/row count and a screenshot for every position correction. A real platform icon should remain in the portrait/name/badge region because the microphone anchors use the party-info/HP right edge; if it overlaps, preserve the log and screenshot before changing the selected child node.

## Failure handling

- If `Native chat bridge validation failed` appears, preserve the reported executable SHA-256. The Overlay should remain usable as a local preview.
- If `Native party-HUD anchor tracking unavailable` appears, preserve the complete signature-validation error. Chat and voice transport may continue, but microphone icons must remain hidden rather than fall back to screenshot coordinates.
- If the game fails before `DirectX 11 ImGui hook initialized with the Extra Sigil compatibility path`, disable the Mod and preserve the Reloaded-II log.
- If the log reaches `[WndProcHook]` but not `First Direct3D11 Present callback`, collect the Windows Application Error/WER entry. That boundary distinguishes native backend or WndProc initialization from managed Overlay rendering.
- If `Render callback recovered from an exception` appears, preserve the complete line. The callback guard released chat input capture, but the visual Overlay is degraded for that session.
- Do not mix individual DLLs from older packages. This build intentionally uses the same official `ImguiHookDx11`, prebuilt pinned CJK atlas and cached original WndProc as the proven Extra Sigil frontend; its fallback now selects `DefWindowProcA` or `DefWindowProcW` to match the actual game window.
- If the Overlay renders but controls still respond, preserve the three DirectInput log lines; their presence distinguishes a state-filter bug from a missed hook.
- If Chinese characters render as boxes, record the `CJK font loaded before DX11 hook initialization` line and Windows display language. If they become Latin-1 text such as `ÎÒ`, preserve the new `Win32 IME compatibility active` line, input-method name and complete Reloaded-II log.
- If composition text appears but the in-overlay candidate row does not, preserve either `candidate notification did not expose a readable IMM32 list` or `composition ended without an IMM32 candidate list`. Their presence distinguishes an IMM32 parsing error from a TSF/Qt-only input method.
- If sending closes the input but the second client receives nothing, record whether the current state is an online lobby, town, quest or results screen; the original native function retains Relink's own state validation.
- If an incoming line still uses `Player XXXXXXXX`, preserve the one-time `Relink player-name resolver could not map sender ...` line and record the sender's slot, displayed in-game name and current lobby/quest transition state. The fallback is intentional and must not crash or drop the message.
- Hashed quick-chat/stamp records are intentionally ignored by the incoming bridge until their text resolver is hooked.

## Party lifecycle foundation

The observation-only PlayFab Party lifecycle hook is always active because it is the Overlay's online-room gate. `Log Party Lifecycle Diagnostics` controls only event logging; the separately configured Stage 2/3 ChatControl canary makes the Party calls described below. Restart the Mod after changing either Party option.

Expected startup evidence:

```text
Party lifecycle/Stage 3 voice test attached at 0x...; one ChatControl may join the existing PartyNetwork. U unmutes Party's native selected microphone path directly; no audio-manipulation capture stream is configured, and input stays muted unless U is held.
Party manager captured from PartyInitialize: 0x....
Party work modes captured from PartyInitialize: Audio=Manual (1), Networking=Automatic (0).
Party Audio work mode is Manual; started the Mod-owned PartyDoWork(Audio) pump at 40 ms intervals. The global work mode was not changed.
```

`Audio=Automatic (0)` plus `Party's internal real-time audio thread remains the sole owner` is equally healthy and must not be followed by a Mod pump. `Audio=Manual (1)` requires the 40 ms pump line above. Any `PartyGetWorkMode(Audio)` or `PartyDoWork(Audio)` error is a fail-closed voice failure for that manager.

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
Party lifecycle/Stage 3 voice test attached at 0x...; one ChatControl may join the existing PartyNetwork. U unmutes Party's native selected microphone path directly; no audio-manipulation capture stream is configured, and input stays muted unless U is held.
Stage 2 captured authenticated existing session: network=0x..., localUser=0x....
Stage 2 confirmed Relink's existing gameplay endpoint before canary creation: endpoint=0x....
Stage 2 canary creation queued on existing manager/network/device: ... Input mute was set and verified before audio selection; microphone="..." (Manual), playback="..." (SystemDefault); Party's native selected microphone path remains active; no audio-manipulation capture stream is configured, and microphone permissions remain None until a remote Mod ChatControl joins this network.
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

Cleanup lines from the first and second holds may interleave because cleanup is deliberately asynchronous. If it exceeds two seconds, the log reports the exact phase (`requesting microphone stop`, `stopping local playback`, `draining audio callbacks`, or `disposing endpoints`); playback must nevertheless remain silent and another `I` hold must still start. No authenticated Party session, remote ChatControl or microphone permission is required for this check. If no signal is observed, fix the Windows privacy/input meter, selected microphone or gain before a two-client run. Preview.12 keeps I as this separate WASAPI preflight, while preview.13 also supplies Party's audio work only when the runtime reports `Audio=Manual`. Using speakers can create acoustic feedback; the self-monitor volume defaults to 35% and is capped at 50%.

Prerequisites for `U`: both testers must use the exact same ZIP, leave both Party options enabled, keep `Experimental Voice (U Party / I Local Test)` enabled, select the intended microphone and playback device in the two Mod configuration lists, save, and restart before testing. The two choices may be different devices and do not have to be the Windows defaults. Use a private two-client room and label the saved logs as client A and client B. Do not begin the voice test unless each client has successful `SetChatAudio...Completed` lines with the expected `selectionType` (`1` for default or `3` for manual), initialized Party input/output states, and the startup line explicitly saying that no audio-manipulation capture stream is configured.

Before touching `U`, both logs must contain a grant for the remote control discovered above:

```text
Stage 3 voice test permissions granted for remote ChatControl=0x... on network=0x...: SendMicrophoneAudio|ReceiveMicrophoneAudio (0x0005). Input remains muted until U is held.
```

The voice row at the top of the chat overlay should progress from `[VOICE] 等待进入联机房间 · 可按住 I 本地监听` to `[VOICE] 等待队友语音通道 · 按住 I 本地监听`, then `[VOICE] 已就绪 · U 队友通话 / I 本地监听` after the permission line. A key-down alone must not display the speaking state before Party confirms the native unmute.

Run this exact test in both directions:

1. Client A holds `U`, speaks a short phrase, then releases `U`.
2. After Party confirms unmute, A's overlay must show `>>> [VOICE] 正在语音 · 松开 U 静音 <<<` and the log must say `Party is capturing the configured Windows microphone directly`.
3. While A speaks continuously, A's LOCAL diagnostic must reach `nativeInputUnmuted=True`, `inputMuted=False`, `localIndicator=Talking (1)`, `audioPath=PartyNativeInput` and `diagnosis=PASS_LOCAL_MICROPHONE_SIGNAL_CAPTURED`. This is the local Party send-path evidence.
4. On release, A's overlay must return to `[VOICE] 已就绪 · U 队友通话 / I 本地监听`. The log must show the completed-U result with `PASS - Party GetLocalChatIndicator reached Talking`, followed by `Stage 3 push-to-talk microphone muted.` The next LOCAL diagnostic must show `nativeInputUnmuted=False` and `inputMuted=True`.
5. Client B must hear the phrase only during A's hold interval. B's matching `Stage 3 voice diagnostics PEER` line must transition to `remoteIndicator=Talking (1)` and `diagnosis=PASS_REMOTE_AUDIO_RECEIVED_BY_PARTY`. This is the required online-transport evidence; physical audibility additionally confirms B's output route.
6. Repeat with B speaking and A listening. Both directions must pass; one-way audio is a failed test.
7. While holding `U`, switch focus away from the game without delivering a normal key-up. Within roughly 350 ms, the speaker should log `Stage 3 push-to-talk heartbeat timed out; microphone mute was forced.` and the peer must stop hearing audio. After focus returns, the overlay must no longer show `正在语音`. Repeat a normal hold/release once to prove recovery.

Speak continuously for at least three seconds during each direction so the low-noise diagnostic poll cannot miss the indicator transition. The expected LOCAL/PEER lines, terminal summary meanings, every official Party indicator state and the complete decision matrix are documented in [VOICE_TROUBLESHOOTING_MATRIX.md](VOICE_TROUBLESHOOTING_MATRIX.md). One labelled A/B log pair from that procedure should replace repeated one-setting-at-a-time tests.

`I` is consumed whenever local monitoring is available, and `U` is consumed only after the remote Mod voice path is ready. `I` and `U` cannot be active together; `U` wins, and an interrupted held `I` must be released before it can start again. The Party microphone must remain silent before the permission log, while `U` is released, after focus loss, and after the last remote Mod ChatControl leaves.

## Session-exit and cleanup test

After bidirectional voice passes, have A leave the room normally while B remains. For the strongest boundary check, A may hold `U` and use mouse/controller navigation to trigger leave; the pre-leave detour must mute before destruction. For each successfully joined local canary, the leave path should first include a line equivalent to:

```text
Stage 2 pre-leave DestroyChatControl queued before Relink PartyNetworkLeaveNetwork: network=0x..., chatControl=0x...; awaiting local left/completed/destroyed events from the game's state-change pump.
```

Then preserve `ChatControlLeftNetwork (local canary)`, `DestroyChatControlCompleted: result=0`, `ChatControlDestroyed (local canary)`, `Stage 2 cleanup complete`, and `PartyCleanup completed`. Event interleaving can differ, but the handles in the local completion lines must match the locally owned canary. B should independently observe A as `ChatControlLeftNetwork (remote/other)` and/or `ChatControlDestroyed (remote/other)` and must stop hearing A immediately. Repeat by recreating the room and swapping host/guest roles if possible.

If `Stage 2 manager cleanup reached before local ChatControl teardown completed` appears, preserve the full diagnostic fields. `PartyCleanup completed` still proves the manager's safety fallback ran, but the strict Stage 2 teardown-event check has not passed.

The test fails if either client lacks the `0x0005` permission line, cannot initialize the selected Party input/output, never reaches local `Talking` during real speech, never observes the peer `Talking`, logs `Stage 3 voice test failed closed`, logs `Stage 2 canary disabled (fail-closed)`, or cannot complete the local cleanup chain. Any `ConfigureAudioManipulationCaptureStream`, capture-sink acquisition, `SubmitBuffer` or `0x000010D8` line proves an old package is still installed. It also fails for one-way/no audio, audio while `U` is released, local monitor audio after `I` is released, audio continuing after focus loss/peer departure, a manager ownership conflict, a second local ChatControl, changed matchmaking, broken native text chat or rendering. The Mod must not call `PartyEndpointSendMessage`, initialize a second Party manager or create another gameplay endpoint. Disable `Experimental Voice (U Party / I Local Test)`, restart, and preserve both complete logs plus approximate key-down/key-up/leave times after any failure.

If either audio row is a plain text field, or opening Mod configuration reports that the audio-device UI is missing, verify that `GBFR.ChatOverlay.dll` and `GBFR.ChatOverlay.ConfiguratorUI.dll` came from the same ZIP. The second DLL is launcher-only; it must be present beside the main Mod DLL but is deliberately not referenced or loaded by the game-side assembly.
