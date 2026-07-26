# One-run Party voice troubleshooting matrix

This is the required two-client test for `0.4.0-preview.12`. It follows Microsoft PlayFab Party's native audio troubleshooting flow. The online U path does not configure an audio-manipulation capture stream: Party owns microphone capture, encoding, transmission and playback. When Relink has configured Party's Audio task as `Manual`, the Mod supplies only the required 40 ms `PartyDoWork(Audio)` pump.

## What the package measures

The diagnostic sampler is read-only. It never changes permissions, mute, volume, device selection or connection state. It runs only after Relink's original `PartyFinishProcessingStateChanges` returns, approximately four times per second while a Mod peer is connected, plus a 500 ms heartbeat while `U` remains held. Unchanged snapshots are suppressed.

Each client records:

- Party input and output initialization state, numeric `errorDetail`, and a deferred `PartyGetErrorMessage` translation when an error is nonzero.
- Party's selected input and output endpoint, including selection type, context and resolved device ID.
- `PartyChatControlGetAudioInputMuted` and `GetLocalChatIndicator`, proving whether U opened the native input and whether Party reports `Silent`, `Talking`, `AudioInputMuted` or `NoAudioInput` locally.
- For every remote Mod ChatControl independently: permission readback, `GetChatIndicator`, incoming-audio mute and render volume.
- A per-U native-input result and a terminal session summary. Online success requires the same remote ChatControl to reach `Talking`; evidence from different peers is never combined into one pass.

The separate I monitor still uses local WASAPI capture and playback. It proves the selected Windows devices can capture and render locally, but it does not prove Party transport.

## One test run

1. Install the exact same ZIP on client A and client B. Record the ZIP SHA-256. Do not mix the main and Configurator DLLs from different packages.
2. On both clients, choose the intended `Voice Microphone` and `Voice Playback Device`, save, and restart. `Default` follows the Windows default communications endpoint; a named entry saves that endpoint ID.
3. Before joining a room, each client wears headphones, holds `I`, speaks, and confirms it hears its own selected microphone. The overlay must reach `本地自检通过`; the log must contain `Local microphone monitor detected input signal` and a `result: PASS` line after release.
4. Create a private room and wait until both overlays say `[VOICE] 已就绪 · U 队友通话 / I 本地监听`.
5. A holds `U`, speaks continuously for at least three seconds, then releases it. B listens.
6. B holds `U`, speaks continuously for at least three seconds, then releases it. A listens.
7. Once, hold `U` and switch focus away from the game. Confirm the 350 ms watchdog forces mute, then return and complete one normal hold/release.
8. Leave the room normally so both logs receive the diagnostic summary and cleanup chain.
9. Preserve both complete Reloaded-II logs, labelled A/B, plus the approximate I-preflight, A-talk, B-talk, focus-loss and leave times.

## Healthy evidence

Before U, each client should show the native route and initialized devices:

```text
Party lifecycle/Stage 3 voice test attached at 0x...; ... U unmutes Party's native selected microphone path directly; no audio-manipulation capture stream is configured, and input stays muted unless U is held.
Party work modes captured from ...: Audio=Manual (1), Networking=Automatic (0).
Party Audio work mode is Manual; started the Mod-owned PartyDoWork(Audio) pump at 40 ms intervals. The global work mode was not changed.
Stage 2 canary creation queued ... Party's native selected microphone path remains active; no audio-manipulation capture stream is configured ...
Stage 3 Party audio input state: Initialized (1); errorDetail=0x00000000.
Stage 3 Party audio output state: Initialized (1); errorDetail=0x00000000.
```

While A speaks with U held, A should contain:

```text
Stage 3 push-to-talk microphone UNMUTED while U is held; Party is capturing the configured Windows microphone directly.
Stage 3 voice diagnostics LOCAL: ... pttKeyHeld=True, nativeInputUnmuted=True, inputMuted=False, localIndicator=Talking (1), ... audioPath=PartyNativeInput, captureSink=enabled:False,... diagnosis=PASS_LOCAL_MICROPHONE_SIGNAL_CAPTURED.
Stage 3 local microphone capture result for the completed U hold: PASS - Party GetLocalChatIndicator reached Talking.
```

At the same time B must independently contain:

```text
Stage 3 voice diagnostics PEER 0x...: permissions=0x0005 ... remoteIndicator=Talking (1), incomingMuted=False, renderVolume=... diagnosis=PASS_REMOTE_AUDIO_RECEIVED_BY_PARTY.
```

After both directions and normal leave, a complete run may end with:

```text
Stage 3 voice diagnostic SUMMARY (...): verdict=PASS_PARTY_BIDIRECTIONAL_SIGNAL_PATH; ... localTalkingObserved=True, captureSinkEnabled=False, completePeer=0x....
```

This pass means Party observed native local capture plus one specific remote peer with permission `0x0005`, incoming audio unmuted, positive finite render volume and remote `Talking`. The listener must still confirm physical audibility because Windows endpoint routing, mixer state and hardware are outside Party's indicator API.

## Decision matrix

| Log evidence | Layer identified | Interpretation / next action |
| --- | --- | --- |
| Log contains `ConfigureAudioManipulationCaptureStreamCompleted`, `official Party capture sink acquired`, `SubmitBuffer`, or `0x000010D8` | Wrong/old package | Preview.14 and later never enter the replacement-sink path. Remove the old Mod folder, install the complete ZIP and confirm both DLL/version pairs match. |
| `Audio=Automatic (0)` followed by `sole owner` | Party automatic audio work | Healthy: Party owns its internal audio thread; no Mod `DoWork` pump should start. |
| `Audio=Manual (1)` but no `started ... PartyDoWork(Audio) ... 40 ms` line | Missing/failed audio work pump | Preserve the immediately adjacent mode-query/fail-closed log and confirm 0.4.0-preview.12 is installed as a complete package. |
| `PartyGetWorkMode(Audio)` or `PartyDoWork(Audio)` returns an error | Party work scheduling | Voice is deliberately fail-closed for that manager. Preserve the exact error and the complete lifecycle; do not judge microphone or transport from that run. |
| `I` reports `result: PASS` and self-audio is audible | Windows capture/render outside Party | The selected Windows mic and playback device work in shared mode. This is preflight evidence, not an online pass. |
| `I` starts but reports no microphone signal | Windows capture before Party | Check the physical mic, Windows privacy/input meter, selected endpoint and gain. |
| Input state other than `Initialized (1)` | Party native capture device | Party cannot establish the selected microphone. Use the state and translated `errorDetail`, then verify the endpoint and Windows privacy settings. |
| Output state other than `Initialized (1)` | Party render device | The receiving client cannot establish its selected playback endpoint. Check the endpoint, exclusive use and format support. |
| U held but `nativeInputUnmuted=False` or `inputMuted=True` | Push-to-talk mute transition | Party did not open the ChatControl input. Preserve the Set/Get mute logs and lifecycle immediately before U. |
| U held and `localIndicator=NoAudioInput` after a healthy Automatic owner or active Manual pump | Party native input | Party still has no usable audio input on that ChatControl. Compare the logged selected device ID with Windows and the successful I endpoint. |
| U held and local remains `Silent` despite speech | Party native capture/signal | Party initialized and unmuted the input but did not detect voice. Verify mic gain/privacy and speak continuously for at least three seconds. |
| U held and `localIndicator=Talking` | Local Party send evidence | Party is capturing the native microphone. This alone does not prove the remote client received it. |
| Permission readback lacks send or receive microphone bits | Chat permission | The live local-to-remote relation is not `0x0005`; transport cannot be ready. |
| Remote `IncomingVoiceDisabled` | Receive permission/policy | Party says incoming voice is disabled. Compare both clients' permission readbacks. |
| Remote `IncomingCommunicationsMuted` or `incomingMuted=True` | Receiver-side mute | The receiving client is muting that peer. |
| Remote `NoRemoteInput` throughout a proven local `Talking` interval | Speaker/Party network relation | The sender captured speech but Party did not expose a remote input. Preserve both complete logs and exact peer handles. |
| Remote `RemoteAudioInputMuted` | Speaker push-to-talk state | Expected outside the peer's U hold. If it remains during speech, inspect the speaker's mute transition. |
| Remote `Talking` | Party network receive | Network voice reached Party on the receiver. If no sound is audible, focus on output state, selected playback device, render volume, Windows mixer and hardware. |
| `renderVolume=0`, negative, `NaN` or infinity | Party render control | Party's render path is not at a usable positive finite volume. |
| Remote remains `Silent` while the speaker logs local `Talking` | Network/peer relation | Compare permissions, incoming mute, exact peer handles and both complete logs. |
| `FAIL_LOCAL_TALKING_NOT_OBSERVED` | Local native send path | No U hold produced Party `Talking`; resolve this before judging remote transport. |
| `FAIL_REMOTE_TALKING_NOT_OBSERVED` | Remote receive path | Local native capture passed, but no peer reached `Talking` on this client. |
| `FAIL_NO_SINGLE_PEER_COMPLETED_REMOTE_PATH` | Multi-peer evidence | Different peers supplied only partial evidence; no individual remote relation completed the receive path. |
| `INCONCLUSIVE_*` | Missing evidence/API getter | The run ended too early, no three-second phrase occurred, an audio-state event was absent, or a read-only getter failed. |
| `PASS_PARTY_BIDIRECTIONAL_SIGNAL_PATH` but no physical sound | Windows/output after Party | Party's logical signal path passed. Check the selected output ID, default communications role, app/device mixer, headset routing and hardware. |

## Cleanup evidence

Every U release must log `Stage 3 push-to-talk microphone muted.` and the next LOCAL snapshot must show `nativeInputUnmuted=False` and `inputMuted=True`. The session summary should be followed by the local teardown chain: pre-leave destroy queued, local left, destroy completion with result `0`, local destroyed, Stage 2 cleanup complete and Party cleanup. The remote client should observe the departing ChatControl leave or be destroyed. Audio continuing after release, focus loss or peer departure is always a failed safety test.

The one-time startup line `PartyStartProcessingStateChanges returned error 0x00001000` is not a voice diagnosis. It is ignorable only when the later authenticated session, endpoint, local/remote ChatControl, audio-state, permission and diagnostic lines all appear.

## Official references

- [Microsoft: Troubleshoot PlayFab Party audio and chat](https://learn.microsoft.com/en-us/xbox/playfab/community/voice-communications/concepts-audio-troubleshooting)
- [Microsoft: PartyLocalChatControl reference](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/reference/classes/partylocalchatcontrol/partylocalchatcontrol)
- [Microsoft: PartyLocalChatControl::SetAudioInput](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/reference/classes/partylocalchatcontrol/methods/partylocalchatcontrol_setaudioinput)
- [Microsoft: PartyLocalChatControl::SetAudioInputMuted](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/reference/classes/partylocalchatcontrol/methods/partylocalchatcontrol_setaudioinputmuted)
- [Microsoft: PartyLocalChatControl::GetChatIndicator](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/reference/classes/partylocalchatcontrol/methods/partylocalchatcontrol_getchatindicator)
- [Microsoft: PartyChatPermissionOptions](https://learn.microsoft.com/en-us/xbox/playfab/multiplayer/networking/reference/enums/partychatpermissionoptions)
- [Microsoft: Real-time audio manipulation](https://learn.microsoft.com/en-us/xbox/playfab/community/voice-communications/concepts-realtime-audio-manipulation)
- [Microsoft: Party quickstart](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/quickstart)
- [Microsoft: PartyManager::SetWorkMode](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/reference/classes/partymanager/methods/partymanager_setworkmode)
- [Microsoft: PartyManager::DoWork](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/reference/classes/partymanager/methods/partymanager_dowork)
