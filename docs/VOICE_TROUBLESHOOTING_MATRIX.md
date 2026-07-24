# One-run Party voice troubleshooting matrix

This is the required test for `0.3.0-preview.7`. It follows Microsoft PlayFab Party's official audio troubleshooting flow and is designed to extract the useful evidence from one two-client session. Do not repeat isolated device-switch experiments before collecting this run.

## What the package now measures

The sampler is read-only. It never changes permissions, mute, volume, device selection or connection state. It runs only after Relink's original `PartyFinishProcessingStateChanges` returns, approximately four times per second while a Mod peer is connected, plus a 500 ms heartbeat while `U` remains held. Unchanged snapshots are suppressed from the log.

Each client records:

- `PartyLocalChatAudioInputChanged` and `PartyLocalChatAudioOutputChanged`, including the official state enum, numeric `errorDetail`, and a deferred `PartyGetErrorMessage` translation when the error is nonzero.
- `PartyChatControlGetAudioInput` and `GetAudioOutput`: selection type, selection context and the device identifier Party says is selected. `SystemDefault` means the Windows default **communications** endpoint.
- `PartyChatControlGetAudioInputMuted` and `GetLocalChatIndicator`: whether Party accepted push-to-talk and whether Party sees `Silent`, `Talking`, `AudioInputMuted` or `NoAudioInput` locally.
- For every remote Mod ChatControl independently: permission readback, `GetChatIndicator`, incoming-audio mute and render volume.
- A per-`U` local-capture result and a terminal session summary. Evidence from different remote ChatControls is never combined into one pass.

## One test run

1. Install the exact same ZIP on client A and client B. Record the ZIP SHA-256. Do not mix the main and Configurator DLLs from different packages.
2. On both clients, choose the intended `Voice Microphone` and `Voice Playback Device`, save, and restart. `Default` follows the Windows default communications device; a named entry uses Party `Manual` selection with that endpoint ID.
3. Before joining a room, each client wears headphones, holds `I`, speaks, and confirms it hears its own selected microphone. The overlay must reach `本地自检通过`; the log must contain `Local microphone monitor detected input signal` and a `result: PASS` line after release. This is local Windows/WASAPI evidence only, not a Party pass.
4. Create a private room and wait until both overlays say `[VOICE] 已就绪 · U 队友通话 / I 本地监听`.
5. A holds `U`, speaks continuously for at least three seconds, then releases it. B listens.
6. B holds `U`, speaks continuously for at least three seconds, then releases it. A listens.
7. Once, hold `U` and switch focus away from the game. Confirm the 350 ms watchdog forces mute, then return and complete one normal hold/release.
8. Leave the room normally so both logs receive the diagnostic summary and cleanup chain.
9. Preserve both complete Reloaded-II logs, labelled A/B, plus the approximate I-preflight, A-talk, B-talk, focus-loss and leave times. This single pair of logs should be sufficient for the next diagnosis.

## Healthy evidence

Each client should contain all of these forms:

```text
Stage 3 Party audio input state: Initialized (1); errorDetail=0x00000000.
Stage 3 Party audio output state: Initialized (1); errorDetail=0x00000000.
Stage 3 voice diagnostics LOCAL: ... localIndicator=Talking (1) ... diagnosis=PASS_LOCAL_MICROPHONE_SIGNAL_CAPTURED.
Stage 3 voice diagnostics PEER 0x...: permissions=0x0005 ... remoteIndicator=Talking (1), incomingMuted=False, renderVolume=... diagnosis=PASS_REMOTE_AUDIO_RECEIVED_BY_PARTY.
Stage 3 local microphone capture result for the completed U hold: PASS - Party GetLocalChatIndicator reached Talking.
Stage 3 voice diagnostic SUMMARY (...): verdict=PASS_PARTY_BIDIRECTIONAL_SIGNAL_PATH; ... completePeer=0x....
```

The summary pass means that, during this session, Party observed local microphone activity and one specific remote peer independently satisfied permission `0x0005`, incoming-unmuted, positive finite render volume and remote `Talking`. It does not prove that a physical speaker produced audible sound; Windows endpoint routing, per-device mixer state and hardware remain outside Party's indicator API.

## Decision matrix

| Log evidence | Layer identified | Interpretation / next action |
| --- | --- | --- |
| Input `Initialized (1)` | Party capture device | Healthy device initialization. Continue to the local indicator. |
| `I` local monitor logs `result: PASS` and self-audio is audible | Windows capture/render outside Party | The selected Windows mic and playback device work in shared mode. If `U` still reports `NoAudioInput`, focus on Party device selection/integration rather than the physical mic. |
| `I` starts but reports no microphone signal | Windows capture before Party | Check the physical mic, Windows privacy/input meter, selected endpoint and gain before arranging another two-client test. |
| Input `NoInput`, `NotFound`, `UserConsentDenied`, `UnsupportedFormat`, `AlreadyInUse` or `UnknownError` | Local capture setup | The state plus translated `errorDetail` is the primary failure. Check Windows microphone privacy, the selected/default communications endpoint, exclusive use and format support. |
| Output state other than `Initialized (1)` | Party render device | The receiving client cannot establish its selected playback endpoint. Check the endpoint, exclusive use and format support. |
| `selectedDeviceId=<null>` or empty while state is not initialized | Party device selection | Party did not resolve a usable selected device. For `SystemDefault`, set the intended Windows default communications device; for `Manual`, reselect an active endpoint. |
| `pttKeyHeld=True`, `nativeInputUnmuted=True`, `inputMuted=False`, local `Talking` | Local capture signal | Party is receiving microphone activity. |
| U held, local `Silent` for the whole three-second phrase | Microphone signal before transport | Party opened input but did not detect speech. Check the physical mic, Windows input meter, gain and the selected endpoint. |
| U held but local `AudioInputMuted`, or `inputMuted=True` | Push-to-talk/native mute | The Party mute transition did not open capture. Preserve the full transition and getter errors. |
| Local `NoAudioInput` | Local capture device | Party has no usable audio input even if device selection completed earlier. |
| Permission readback lacks send or receive microphone bits | Chat permission | `SetPermissions` was accepted or logged but the live relation is not `0x0005`; transport cannot be considered ready. |
| Remote `IncomingVoiceDisabled` | Receive permission/policy | Party says incoming voice is disabled. Compare both clients' permission readbacks. |
| Remote `IncomingCommunicationsMuted` or `incomingMuted=True` | Receiver-side mute | The receiving client is muting that peer. |
| Remote `NoRemoteInput` | Speaker's capture setup | The peer has no usable Party input. Inspect the peer's LOCAL line and input state. |
| Remote `RemoteAudioInputMuted` | Speaker push-to-talk state | Expected outside the peer's U hold. If it remains throughout speech, inspect the speaker's mute transition. |
| Remote `Talking` | Party network receive | The receiver sees the peer's voice activity. If no sound is audible, transport succeeded far enough to focus on the receiver's output state, selected playback device, render volume, Windows mixer and hardware. |
| `renderVolume=0`, negative, `NaN` or infinity | Party render control | Party's render path is not at a usable positive finite volume. |
| Remote remains `Silent` while the peer's LOCAL side logs `Talking` | Network/peer relation | Capture works on the speaker but the receiver never observes remote voice; compare permission, incoming mute and both peer handles. |
| Local `Talking` passes but summary says `FAIL_REMOTE_TALKING_NOT_OBSERVED` | One-way path | This client sent local speech but never observed the peer talking. Use the other client's summary to identify which direction failed. |
| `FAIL_NO_SINGLE_PEER_COMPLETED_REMOTE_PATH` | Multi-peer evidence | Different peers supplied only partial evidence; no individual remote relation completed the receive path. The per-peer fields show which component is missing. |
| `INCONCLUSIVE_*` | Missing evidence/API getter | The run ended too early, no three-second phrase occurred, an audio-state event was absent, or a read-only getter failed. Keep the translated getter errors and repeat only after addressing that concrete gap. |
| `PASS_PARTY_BIDIRECTIONAL_SIGNAL_PATH` but no physical sound | Windows/output after Party | Party's logical signal path passed. Check the logged selected output ID against Windows Sound, the default communications role, app/device mixer, headset routing and hardware. |

## Cleanup evidence

The summary should be followed by the existing local teardown chain: pre-leave destroy queued, local left, destroy completion with result `0`, local destroyed, Stage 2 cleanup complete and Party cleanup. The remote client should observe the departing ChatControl leave or be destroyed. Audio continuing after release, focus loss or peer departure is always a failed safety test.

The one-time startup line `PartyStartProcessingStateChanges returned error 0x00001000` is not a voice diagnosis. It is ignorable only when the later authenticated session, endpoint, local/remote ChatControl, audio-state, permission and diagnostic lines all appear.

## Official references

- [Microsoft: Troubleshoot PlayFab Party audio and chat](https://learn.microsoft.com/en-us/xbox/playfab/community/voice-communications/concepts-audio-troubleshooting)
- [Microsoft: PartyLocalChatControl reference (including GetLocalChatIndicator)](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/reference/classes/partylocalchatcontrol/partylocalchatcontrol)
- [Microsoft: PartyLocalChatControl::GetChatIndicator](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/reference/classes/partylocalchatcontrol/methods/partylocalchatcontrol_getchatindicator)
- [Microsoft: PartyChatPermissionOptions](https://learn.microsoft.com/en-us/xbox/playfab/multiplayer/networking/reference/enums/partychatpermissionoptions)
- [Microsoft: Party quickstart](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/quickstart)
