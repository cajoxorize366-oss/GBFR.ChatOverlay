# Relink 2.0.4 smoke test

A developer build is copied to the directory selected by `RELOADEDIIMODS`; a packaged build stays isolated and must be imported and enabled for the Granblue Fantasy: Relink profile before launch.

## Expected startup log

The Reloaded-II log should contain messages equivalent to:

```text
[gbfr.qol.chatoverlay] Startup phase=required-byte-rva-preflight-chat state=complete elapsed_ms=....
[gbfr.qol.chatoverlay] Startup phase=required-byte-rva-preflight-party-hud state=complete elapsed_ms=....
[gbfr.qol.chatoverlay] Startup phase=input-user32-iat state=complete hooks=All active=false; cursor interception was installed before other game hooks.
[gbfr.qol.chatoverlay] Reloaded-II load source=launcher (...) ...
[gbfr.qol.chatoverlay] Relink 2.0.4 native chat bridge attached: send=..., receive=....
[gbfr.qol.chatoverlay] Relink incoming player-name resolver attached: senderSlot=..., memberLookup=...; opaque RPC member keys are mapped to verified four-party member slots before lobby-name lookup.
[gbfr.qol.chatoverlay] Relink 2.0.4 native party-HUD tracker attached; lobby/battle mode, resolution, aspect ratio and HUD scale now follow the game's live UI node transforms.
[gbfr.qol.chatoverlay] Startup phase=directinput-broker-hooks state=complete elapsed_ms=....
[gbfr.qol.chatoverlay] DirectInput keyboard/mouse interception initialized through the game-local IAT broker; the dinput8/ReShade export entry was not modified and controllers remain pass-through.
[gbfr.qol.chatoverlay] CJK font loaded before DX11 hook initialization: ..., 9 glyph ranges.
[gbfr.qol.chatoverlay] DX11 Present-only backend enabled with a native original-Present boundary; frame-local render targets replace the ResizeBuffers hook.
[gbfr.qol.chatoverlay] DirectX 11 ImGui hook initialized with the Extra Sigil Present-only hook-chain and native SEH compatibility path.
[gbfr.qol.chatoverlay] DirectInput broker readiness: iat=True, factory=True, keyboard=True, mouse=True, controllers=pass-through.
[gbfr.qol.chatoverlay] First Direct3D11 Present callback: OS TID ....
```

`source=asi-bootstrapper` is the expected alternative when using the official Deploy ASI Loader. `source=unknown` must be preserved with its evidence string because it can identify a duplicate or conflicting injection path. The `relink-executable-sha256` and `partywin-sha256` begin/complete lines may appear later; both must state `diagnostic_only=true` and must never delay the hook phases above.

The readiness line may transition through `factory=False`, `keyboard=False` or `mouse=False`; the final keyboard/mouse flags appear only after the game creates and polls those DirectInput devices. No line should report patching the `dinput8.dll` export itself.

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
3. On a second client, join that room and confirm the same transition occurs after its own authentication/endpoint sequence. The lower-left system message should say the native Relink 2.0.4 bridge is connected.
4. Press `Y` once. The input field should open without inserting the activation key itself.
5. Enter `ABC123`, then use Microsoft Pinyin and Sogou to commit `我是`. The field must contain exactly `ABC123我是` once: `我` must never become `ÎÒ`, and Latin characters must not duplicate.
6. While composing with Sogou, confirm that the Overlay displays `候选：1.…` directly above the chat field, including brackets around the selected word. The candidate row must reserve only its actual wrapped text height: a short row must not create a blank line below the status text, and a long row may wrap without covering or pushing the input/status rows outside the window. Select candidates with number keys, Space and normal IME paging; the fallback is display-only and intentionally does not simulate keys or mouse selection.
7. Press Escape during an unfinished composition, reopen with `Y`, and type `我是` again. No pending lead byte, old composition or candidate window may leak into the new input session.
8. In the online room, press Enter and confirm the other client receives the message.
9. Have the second client send a free-text reply. Confirm it appears once in the Overlay history and uses that client's real online display name, not `Player 00000000`, `Player 00000001`, `Player 00000002` or `Player 00000003`. Recreate the room with the host/join order reversed and repeat so that the local native member index is not assumed to be zero; the other client's line and the local client's next line must keep different correct names and slot colors. A temporary Party endpoint/lifecycle reset must not clear existing history, and incoming RPC records observed while the room gate is temporarily inactive must remain queued until the gate becomes active again.
10. Confirm the local message appears once immediately after Relink's native send call returns, including messages entered through the game's official chat UI and Custom Text actions sent by the Mod. It must not depend on the server returning an RPC to the sender, must use the local player's actual online display name and UI Player 1 color, and must not be forced to `You`. It may be marked as host only when the local Party role is authoritatively `Created`; a joining client must never infer itself as host from a local lobby-owner candidate. A synchronous or delayed authoritative echo may refine identity but must not add a duplicate line.
11. While the input field is open, press movement and combat keys. The game should not respond to them.
12. Press Escape. The input field should close, and controls should resume after held keys have been released.
13. Leave or disband the online room. Confirm the Overlay disappears immediately and `Y/U/I` return to the game before returning to title.
14. Disable `Enable Overlay` and confirm that the Mod no longer captures `Y`.
15. Open `F10`, select `快捷动作 / Quick Actions`, change one action to `自定义文 / Custom Text`, click its text editor, and enter `ABC我是`. Backspace must delete the final Chinese character and continue deleting Latin text normally. Microsoft Pinyin and Sogou commits must appear once as UTF-8, while configured Mod hotkeys remain blocked from firing behind the settings window. Close `F10`, press that action's keyboard hotkey, and confirm the text is sent once through the normal Relink chat path; a Broker guest must queue the hotkey to the next Render/Present callback instead of calling `sendMessage` directly from WndProc.
16. Close `F10` while a keyboard key or mouse button is held. WndProc, DirectInput and cursor capture must remain suppressed until the native held-input drain reaches neutral, then all three paths must release together without a stuck cursor, leaked key-up or gameplay click.

## 0.5.0-preview.23 chat attribution regression

Use two clients with deliberately different online names, for example client A `Kuro` and client B `trick`. Keep both Reloaded-II logs.

1. On A, send distinguishable ordinary text through the game's own chat UI and through a Mod Custom Text action. A's Overlay must label both lines `Kuro` with UI Player 1 color; B must label them `Kuro` with the correct remote color. Neither line may become `trick`.
2. On A, trigger the game's automatic All-Potion communication. The automatic multilingual sentence must follow the same ownership result as step 1. Its presentation field may be empty or carry a communication value, but it must not update the cached local name.
3. On A, trigger a victory communication such as `vo_CMM_win_3`. Both clients must retain `Kuro` as sender and may append the localized `胜利 / Victory` cue. No `vo_CMM_*` key may appear as the displayed name.
4. Repeat ordinary text, All-Potion and victory from B. Every line must now belong to `trick`; A's own preceding and following lines must remain `Kuro`.
5. Have A send `same text`, then have B send exactly `same text` inside the echo lifetime. Both distinct lines must remain. Only an RPC whose opaque member key resolves to A's proven local member index may consume A's echo token.
6. Reverse host/join order and repeat steps 1 through 5. Local history must remain UI Player 1 on each machine even when its native member index is `1`, `2` or `3`; remote UI Players 2 through 4 must follow the relative mapping around that local index.
7. Inspect the first attribution diagnostics in both logs. Each line must include `member_key`, `member_index`, `local_index`, `relation` and `ui_player`, contain no chat text, and agree with the visible sender. An unresolved key must keep `Player XXXXXXXX` and must not invent `Kuro`, `trick`, host ownership or a remote color.

## 0.5.0-preview.24 host identification regression

Install the same preview.26 ZIP on two clients and restart both processes so no earlier in-memory Party role or lobby-owner binding survives.

1. Client A creates the online room and client B joins. In A's log, the host diagnostic must settle on `local_role=Created, host_ui_player=1`; in B's log it must settle on `local_role=Connected` and the remote UI player that represents A.
2. Send ordinary text, a Mod Custom Text action, the automatic All-Potion sentence and a victory communication from both clients. Both overlays must mark only A's lines with `[房主]`; B's local lines must never receive the label. Names and colors must remain those verified by the independent sender-attribution path.
3. Reverse the roles: B creates a new room and A joins. Both overlays must now mark only B. This must remain true when the creator's native member index is nonzero because UI player 1 is local-relative, not the raw member slot.
4. On the joining client, a captured owner candidate equal to its own EntityId must be ignored. If no unique remote candidate is available yet, `host_ui_player=0` and no line is marked until the remote owner can be proven.
5. If two distinct remote candidates match active members, the Party role is unknown, or the member snapshot is malformed or changes mid-read, the label must disappear rather than selecting the first candidate.
6. Leave and recreate the room once more. The previous role, owner candidate, room name and cached host slot must not leak into the new session.

## 0.5.0-preview.25 room-member and voice-header regression

Use two clients with different online names and keep both complete Reloaded-II logs. Test once with A hosting and once with B hosting.

1. Enter the room before the second client joins. Existing baseline members must not produce a synthetic `joined the room` line when the Overlay becomes active.
2. Have the second client join after the first client is already active. The first client's chat history must add exactly one localized system line naming that player. A member represented by multiple Party endpoints must still produce only one join line.
3. When the local side has a Party-confirmed unmuted input or a remote side reports `ChatIndicator.Talking`, the first row of the chat box must change from `[语音] 已就绪` to `[语音] <name> 正在使用语音`. Simultaneous local and remote users must both be listed once using their actual online names. Releasing `U` or returning every remote indicator to non-talking must restore the normal ready row.
4. Destroy and recreate the same remote endpoint while the member remains in the coherent four-slot identity snapshot. No leave or duplicate join line may appear.
5. Leave normally. The remaining client must add exactly one line naming the departed player with `主动离开`; the name must come from the EntityId-bound cache even though the live member-name slot has already disappeared.
6. Repeat with a network interruption and a kick where practical. The remaining client must report `连接中断` or `被踢出房间` from the official Party destroy reason. Authentication loss, endpoint creation failure and unknown values must use their explicit localized fallbacks instead of guessing another reason.
7. While the identity snapshot is unavailable or still contains the member after `EndpointDestroyed`, no leave line may be shown. It may appear only after the game's original `PartyFinishProcessingStateChanges` has completed and a coherent snapshot confirms absence.
8. Leave/disband the room and create another one. Member-name cache, pending endpoint events and speaker names from the previous room must not leak into the new room.

## 0.5.0-preview.26 local room-exit reason regression

Use the same preview.26 ZIP on both clients and preserve the complete Reloaded-II logs around each leave.

1. Complete a quest, reach the normal results/settlement flow, then return through the game's ordinary leave-room path. The leaving client must add exactly one local system line with `主动离开`; it must never say `网络波动`, even when the room identity snapshot has already become unavailable and the message can name only `当前房间`.
2. Repeat from a town/lobby room using the normal leave or disband command. A successful `PartyNetworkLeaveNetwork` call must remain authoritative evidence of a graceful local request when the later `PartyCleanup` hook runs.
3. On a joining client, make the remote host disappear without a proven local leave request. The existing `房主掉线` classification must remain unchanged.
4. Reproduce a genuine disconnect where Party reports its native disconnected destroyed/removed reason. That path must still report `网络波动`; the new graceful-leave rule must not mask an authoritative disconnect event.
5. Force or observe teardown without a preceding successful `PartyNetworkLeaveNetwork`. The fail-closed fallback may report `网络波动`, and duplicate cleanup/destroy callbacks must not add a second exit line.

## Push-to-talk and controller hotkeys

1. On an Overlay Broker guest client, hold `U` after voice reaches Ready. The status must immediately show `正在通话中 / Transmitting`; repeated key-down messages while held must not reopen the microphone. Releasing `U`, losing focus, suspending the Mod or losing voice eligibility must close it once.
2. Hold a configured per-action keyboard key. It must dispatch once on the physical press edge. Release and press again: the Mod must call the native send path again immediately and leave cooldown rejection to the game instead of imposing its own two-second gate. Individual quick actions must expose no controller binding and must ignore legacy action-level `ControllerBinding` values.
3. Attempt to bind `DPadDown` alone or in a chord. Capture must remain active, the binding must stay unchanged, and the row must explain that the game reserves `DPadDown` for its official quick phrase.
4. For Flydigi Vader 5 Pro extra buttons, disable Steam Input for this game and enable the Flydigi Space Station option that allows third-party applications to take over controller mappings. The binding capture must recognize `C`, `Z`, `LM`, `RM`, `M1`, `M2`, `M3`, `M4` and `Circle` only after the status reply allows takeover and an Acquire reply confirms success. A raw input packet alone must never mark the controller ready.
5. With Steam Input kept enabled, leave Flydigi third-party takeover disabled and map the nine extra buttons to unused keyboard keys such as `F13-F21` in Space Station. Bind them through the Mod's Keyboard buttons and confirm each physical extra button triggers only its assigned action. If the HID interface or acquisition is occupied, the controller binding prompt must describe this fallback.

## Player 2/3/4 mute

1. In a private room with at least one remote Party voice peer, open the settings menu and select `02 玩家禁言 / Player Mute`. The page must always contain exactly `玩家 2`, `玩家 3` and `玩家 4`; it must not rename or reorder rows from ChatControl join timing.
2. An occupied row becomes actionable only after its Relink slot EntityId exactly matches a joined remote Party ChatControl. Its detail must say `EntityId 已精确匹配`; an empty slot or a peer without a matching ChatControl remains unavailable.
3. While the matching remote player is speaking, click `禁言 / Mute`. Their incoming Party audio must stop and the row must change to `当前已禁言 / currently muted`. The log should say `Player N incoming Party audio muted after exact Relink-slot/EntityId correlation.`
4. Click `取消禁言 / Unmute`. Audio must resume only for the same player, the Party readback must confirm the change, and the other occupied rows must retain their previous state.
5. Have that player leave. Their row must become unavailable before another click can reach the departed ChatControl. Rejoining or replacing the slot must require a fresh EntityId match; the implementation must never reuse the old pointer or fall back to join order.

## 0.5.0-preview.22 voice indicators

The normal path now combines exact Relink party-slot EntityIds with permissioned Party ChatControls before drawing. `Voice Indicator Debug: Show All Slots` remains a diagnostics-only position preview: it intentionally draws every active CPU/player HUD row and is not proof that the formal channel loop is active or that those rows use this Mod.

1. Install the same preview.26 ZIP on clients A and B, join one online room and wait for both overlays to reach `Ready`. With debug show-all disabled, A and B must each show a muted 70%-opacity microphone only on the local row and the exact remote member row whose permissioned ChatControl supplied the matching EntityId. CPU, empty and unresolved rows must remain blank. This is an identity/channel assertion, not authenticated Mod capability negotiation.
2. Test the two-person case with CPU or empty party slots. The Mod member icon must stay attached to that member's stable remote ordinal; the unused rows must not receive an inferred icon. If the live HUD row cardinality cannot be reconciled with the exact party snapshot, the formal path must hide rather than guess.
3. Have A hold `U` and speak continuously. A's local icon and B's matching remote icon must become bright at 100% opacity only when Party reports `Talking`; release must return both to the muted established state. Repeat from B to A.
4. In town, confirm the tracker reports `layout=OnlineLobby`; enter a quest and confirm `layout=Battle`. The same identities must follow the compact town rows and the battle HP rows without swapping. Repeat once with the main chat overlay disabled or Compact Mode closed; voice icons must continue rendering independently.
5. Repeat at another resolution, HUD scale or ultrawide aspect if available. The icon must remain attached to the same native row edge because its center and size come from that live UI node's final transform; there is no screenshot reference resolution or uniform image scale to tune.
6. Verify the native party-HUD whitelist and timing: icons appear only after the HP HUD reaches stable visibility state `2`, disappear as soon as it starts closing, and remain absent on loading, menus and results. Trigger a Full Chain: all microphone icons must disappear for the entire opening/visible/closing sequence and return only after `ControllerChainburst` closes.
7. Enable debug show-all only for position diagnosis. Every valid active HUD row may then display an idle icon even without a Mod peer. Disable it before judging formal identity behavior.

For every failure, preserve both clients' logs, especially `Voice indicator membership snapshot changed` and `Native party-HUD microphone anchors changed`, the game resolution/HUD scale, the reported native layout, native member types and active row count, whether debug show-all was enabled, and screenshots from town plus battle. A real platform icon remains in the portrait/name/badge region because microphone anchors use the party-info/HP right edge.

## 0.5.0-preview.1 RTSS compatibility

This preview replaces the stock Reloaded DX11 implementation with the Present-only compatibility path proven by GBFR Extra Sigil Slots. It does not install a `ResizeBuffers` hook. Start RTSS before Reloaded-II and keep the same RTSS profile/overlay settings that previously reproduced the conflict.

1. Launch with RTSS active. If RTSS already owns an entry jump, confirm a line such as `DX11 Present hook chaining followed ... existing entry jump(s); installing at chain tail ...` appears before the Present-only enabled line. Zero existing jumps is valid only when RTSS did not patch this DXGI entry.
2. Enter town, resize or switch window mode if available, open/close the main menu, then enter and leave a quest. The chat and microphone UI must render normally whenever their own room/HUD gates allow it; RTSS must keep rendering and the game must not hang or crash.
3. Confirm no log mentions installing or recovering a `ResizeBuffers` hook. The backend uses frame-local render targets, so swap-chain resize does not require a second managed hook chain.
4. If the native original-Present boundary catches `SEH 0xC0000005`, preserve the complete log. It must be followed by `overlay hook disabled after a native Present failure` and `Overlay graphics backend failed closed`. The game and RTSS should continue; `Y` must no longer be captured and no chat/voice UI may remain for that session.
5. Repeat once with RTSS disabled. Both runs must reach `First Direct3D11 Present callback`; behavior outside the graphics compatibility layer must remain identical.
6. Open F10, switch focus away from the game, then return and close the menu. `WM_ACTIVATE`, `WM_KILLFOCUS`, `WM_ACTIVATEAPP`, `WM_CANCELMODE` and `WM_CAPTURECHANGED` must continue to reach the game; the Overlay must not flicker, repeatedly recapture the cursor or leave movement/mouse input stuck.

## Failure handling

- If `Native chat bridge validation failed` appears, preserve the complete required-byte/RVA preflight error and the later deferred executable SHA-256 diagnostic, if it completes. The Overlay should remain usable as a local preview.
- If `Native party-HUD anchor tracking unavailable` appears, preserve the complete signature-validation error. Chat and voice transport may continue, but microphone icons must remain hidden rather than fall back to screenshot coordinates.
- If the game fails before `DirectX 11 ImGui hook initialized with the Extra Sigil Present-only hook-chain and native SEH compatibility path`, disable the Mod and preserve the Reloaded-II log.
- If the log reaches `[WndProcHook]` but not `First Direct3D11 Present callback`, collect the Windows Application Error/WER entry. That boundary distinguishes native backend or WndProc initialization from managed Overlay rendering.
- If `Render callback recovered from an exception` appears, preserve the complete line. The callback guard released chat input capture, but the visual Overlay is degraded for that session.
- Do not mix individual DLLs from older packages. `GBFR.ChatOverlay.Native.dll`, `GBFR.ChatOverlay.dll` and the remaining dependencies must come from the same archive. The current backend ports Extra Sigil's Present-only hook-chain/SEH boundary while retaining the prebuilt pinned CJK atlas and cached ANSI/Unicode WndProc fallback.
- If the Overlay renders but controls still respond, preserve the `directinput-broker-hooks` phase and every DirectInput broker readiness transition; they distinguish policy/filter bugs from a missed game-local IAT or device-method hook.
- If Chinese characters render as boxes, record the `CJK font loaded before DX11 hook initialization` line and Windows display language. If they become Latin-1 text such as `ÎÒ`, preserve the new `Win32 IME compatibility active` line, input-method name and complete Reloaded-II log.
- If composition text appears but the in-overlay candidate row does not, preserve either `candidate notification did not expose a readable IMM32 list` or `composition ended without an IMM32 candidate list`. Their presence distinguishes an IMM32 parsing error from a TSF/Qt-only input method.
- If sending closes the input but the second client receives nothing, record whether the current state is an online lobby, town, quest or results screen; the original native function retains Relink's own state validation.
- If an incoming line still uses `Player XXXXXXXX`, preserve the one-time member-key/player-name resolver failure and the capped `Relink chat attribution #...` line. Record the opaque `member_key`, resolved `member_index` if any, displayed in-game name and current lobby/quest transition state. The fallback is intentional and must not crash or drop the message.
- Trigger messages whose native sender label is `vo_CMM_chance`, `vo_CMM_win_3` and `vo_CMM_thanks`. Each line must retain the actual player name and slot color while adding the localized `连携攻击`, `胜利` or `感谢` cue. No `vo_CMM_*` key may appear as the displayed sender or overwrite the cached local identity.
- Restart the game with `0.5.0-preview.26`. On both clients trigger ordinary text, automatic All-Potion, `vo_CMM_chance`, `vo_CMM_win_3` and `vo_CMM_thanks`. Every line must retain its actual sender even when the local member index is nonzero; neither identity nor any room name may contain a presentation key or the other client's name. Only the actual Party creator may carry `[房主]`. A process that already loaded an older DLL must be restarted because its in-memory identity and lobby binding may already be stale.
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

Before arranging a second tester, wear headphones and press `F10` from the main menu. Confirm the settings window captures both keyboard and mouse: game selection, camera and character controls must not react while it is open. Select a microphone and speaker, then click `麦克风测试`, speak, and confirm the live input-level bar moves. Click `停止麦克风测试`, then immediately start it a second time without waiting for endpoint cleanup. Local playback must stop immediately on each stop and the second test must not remain stuck at `正在启动所选音频设备`. The old `I` key must pass through to the game and must never start the self-test. Expected logs are:

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

Cleanup lines from the first and second tests may interleave because cleanup is deliberately asynchronous. If it exceeds two seconds, the log reports the exact phase (`requesting microphone stop`, `stopping local playback`, `draining audio callbacks`, or `disposing endpoints`); playback must nevertheless remain silent and another menu test must still start. No authenticated Party session, remote ChatControl or microphone permission is required for this check. If no signal is observed, fix Windows privacy, the selected microphone, or the menu input gain before a two-client run. Using speakers can create acoustic feedback; self-test playback defaults to 35% and is capped at 50%.

While the `F10` menu is open, drag the cyan top edge of the chat preview and drag the cyan triangle in its bottom-right corner. Close the menu, reopen it, and confirm position/size persist. With Compact Mode disabled, the preview is the full history frame. With Compact Mode enabled, the preview must immediately switch to the compact voice-row-plus-disabled-input shape; it must not show history, focus the input, change the draft or overwrite the saved full-frame height. Press `Y` in a room and confirm the live compact shape matches that preview, including the voice row above the input. Repeat once at another resolution or window size: the chat box should retain the same relative placement inside the usable viewport and remain fully on-screen. Hold a keyboard key or mouse button while closing the menu; Win32/Raw Input, DirectInput and the native cursor-release hook must continue using the same effective device mask until two physically neutral frames are observed. The held input must not leak into the game, and normal input must resume after physical release.

Prerequisites for `U`: both testers must use the exact same ZIP, leave both Party options enabled, keep `Experimental Voice (U Party / F10 Settings)` enabled, select the intended microphone and playback device in the Mod configuration or F10 menu, save, and restart before testing Party voice. The two choices may be different devices and do not have to be the Windows defaults. Use a private two-client room and label the saved logs as client A and client B. Do not begin the voice test unless each client has successful `SetChatAudio...Completed` lines with the expected `selectionType` (`1` for default or `3` for manual), initialized Party input/output states, and the startup line explicitly saying that no audio-manipulation capture stream is configured.

Before touching `U`, both logs must contain a grant for the remote control discovered above:

```text
Stage 3 voice test permissions granted for remote ChatControl=0x... on network=0x...: SendMicrophoneAudio|ReceiveMicrophoneAudio (0x0005). Input remains muted until U is held.
```

The voice row at the top of the chat overlay should progress from `[VOICE] 等待进入联机房间 · F10 设置 / 本地自检` to `[VOICE] 等待队友语音通道 · F10 设置 / 本地自检`, then `[VOICE] 已就绪 · U 队友通话 / F10 设置与自检` after the permission line. A key-down alone must not display the speaking state before Party confirms the native unmute.

Run this exact test in both directions:

1. Client A holds `U`, speaks a short phrase, then releases `U`.
2. After Party confirms unmute, A's overlay must show `>>> [VOICE] 正在语音 · 松开 U 静音 <<<` and the log must say `Party is capturing the configured Windows microphone directly`.
3. While A speaks continuously, A's LOCAL diagnostic must reach `nativeInputUnmuted=True`, `inputMuted=False`, `localIndicator=Talking (1)`, `audioPath=PartyNativeInput` and `diagnosis=PASS_LOCAL_MICROPHONE_SIGNAL_CAPTURED`. This is the local Party send-path evidence.
4. On release, A's overlay must return to `[VOICE] 已就绪 · U 队友通话 / F10 设置与自检`. The log must show the completed-U result with `PASS - Party GetLocalChatIndicator reached Talking`, followed by `Stage 3 push-to-talk microphone muted.` The next LOCAL diagnostic must show `nativeInputUnmuted=False` and `inputMuted=True`.
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

On room entry, chat history and Compact Mode's transient notice must show `已进入XXX的房间，X人成功建立语音通道`; `XXX` is the resolved room owner's player name and `X` counts the joined local ChatControl plus only remote ChatControls whose `0x0005` permissions succeeded. Join a room hosted by the other client while the joining client's authoritative local Party slot is nonzero: `XXX` must be the other client's name, never the joining client's name or a stale owner from the previous room. Repeat the exit path four ways and verify one non-duplicated localized notice: a normal leave reports `自行退房`; `LocalUserKicked` reports `你已被踢除房间`; a Party destroyed reason of `Disconnected` reports `网络波动已退出房间`; terminating a remote host reports `房主掉线` only when a coherent member snapshot proves that the same previously observed remote owner disappeared. Resolver failure, multiple matching lobby-owner candidates or a never-observed owner must remain unknown and must not be called a host disconnect. Each proven exit notice may retain only that bound owner's cached room name after native room identity is invalidated.

If `Stage 2 manager cleanup reached before local ChatControl teardown completed` appears, preserve the full diagnostic fields. `PartyCleanup completed` still proves the manager's safety fallback ran, but the strict Stage 2 teardown-event check has not passed.

The test fails if either client lacks the `0x0005` permission line, cannot initialize the selected Party input/output, never reaches local `Talking` during real speech, never observes the peer `Talking`, logs `Stage 3 voice test failed closed`, logs `Stage 2 canary disabled (fail-closed)`, or cannot complete the local cleanup chain. Any `ConfigureAudioManipulationCaptureStream`, capture-sink acquisition, `SubmitBuffer` or `0x000010D8` line proves an old package is still installed. It also fails for one-way/no audio, audio while `U` is released, local self-test audio after the F10 test is stopped, audio continuing after focus loss/peer departure, a manager ownership conflict, a second local ChatControl, changed matchmaking, broken native text chat or rendering. The Mod must not call `PartyEndpointSendMessage`, initialize a second Party manager or create another gameplay endpoint. Disable `Experimental Voice (U Party / F10 Settings)`, restart, and preserve both complete logs plus approximate key-down/key-up/leave times after any failure.

If either audio row is a plain text field, or opening Mod configuration reports that the audio-device UI is missing, verify that `GBFR.ChatOverlay.dll` and `GBFR.ChatOverlay.ConfiguratorUI.dll` came from the same ZIP. The second DLL is launcher-only; it must be present beside the main Mod DLL but is deliberately not referenced or loaded by the game-side assembly.
