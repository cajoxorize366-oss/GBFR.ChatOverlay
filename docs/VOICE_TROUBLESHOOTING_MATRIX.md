# One-run Party voice troubleshooting matrix

This is the required test for `0.3.0-preview.9`. It combines Microsoft PlayFab Party's official audio troubleshooting flow with direct evidence from Party's official audio-manipulation capture sink. It is designed to extract the useful evidence from one two-client session. Do not repeat isolated device-switch experiments before collecting this run.

## What the package now measures

The diagnostic sampler is read-only. It never changes permissions, mute, volume, device selection or connection state. It runs only after Relink's original `PartyFinishProcessingStateChanges` returns, approximately four times per second while a Mod peer is connected, plus a 500 ms heartbeat while `U` remains held. Unchanged snapshots are suppressed from the log. Separately, the U bridge captures the selected Windows microphone and deliberately submits PCM through Party's documented manipulation sink; that is the voice transport under test.

Each client records:

- Capture-stream configure completion plus sink format readback. A valid Windows sink is 24,000 Hz, channel mask `0`, mono, 32-bit float and non-interleaved.
- For each U hold: selected WASAPI source format, accepted 40 ms frame count, submitted audio duration, peak, submit failures and frames skipped while Relink owned a Party state batch.
- `PartyLocalChatAudioOutputChanged`, including the official state enum, numeric `errorDetail`, and a deferred `PartyGetErrorMessage` translation when the error is nonzero. The legacy automatic-input state and device getters remain context, but they do not override successful manipulation-sink submission.
- `PartyChatControlGetAudioInputMuted` and `GetLocalChatIndicator`: whether Party accepted push-to-talk and whether Party additionally reports `Silent`, `Talking`, `AudioInputMuted` or `NoAudioInput` locally.
- For every remote Mod ChatControl independently: permission readback, `GetChatIndicator`, incoming-audio mute and render volume.
- A per-`U` capture-bridge result and a terminal session summary. Local sink acceptance is send-path evidence only; online success requires the same remote ChatControl to reach `Talking`. Evidence from different remote ChatControls is never combined into one pass.

## One test run

1. Install the exact same ZIP on client A and client B. Record the ZIP SHA-256. Do not mix the main and Configurator DLLs from different packages.
2. On both clients, choose the intended `Voice Microphone` and `Voice Playback Device`, save, and restart. `Default` follows the Windows default communications device; a named entry saves that endpoint ID. U captures the microphone through WASAPI and hands PCM to Party; Party renders received voice through the playback selection.
3. Before joining a room, each client wears headphones, holds `I`, speaks, and confirms it hears its own selected microphone. The overlay must reach `本地自检通过`; the log must contain `Local microphone monitor detected input signal` and a `result: PASS` line after release. This is local Windows/WASAPI evidence only, not a Party pass.
4. Create a private room and wait until both overlays say `[VOICE] 已就绪 · U 队友通话 / I 本地监听`.
5. A holds `U`, speaks continuously for at least three seconds, then releases it. B listens.
6. B holds `U`, speaks continuously for at least three seconds, then releases it. A listens.
7. Once, hold `U` and switch focus away from the game. Confirm the 350 ms watchdog forces mute, then return and complete one normal hold/release.
8. Leave the room normally so both logs receive the diagnostic summary and cleanup chain.
9. Preserve both complete Reloaded-II logs, labelled A/B, plus the approximate I-preflight, A-talk, B-talk, focus-loss and leave times. This single pair of logs should be sufficient for the next diagnosis.

## Healthy evidence

Each speaking client should contain these local forms:

```text
Stage 3 ConfigureAudioManipulationCaptureStreamCompleted: result=0, error=0x00000000, chatControl=0x....
Stage 3 official Party capture sink acquired: stream=0x..., format=24000 Hz, channelMask=0x0, channels=1, bits=32, sampleType=Float, interleaved=False. ...
Stage 3 Party audio output state: Initialized (1); errorDetail=0x00000000.
Stage 3 Party microphone capture started for U: ... PartySinkFormat=24000 Hz mono float32, frame=40 ms.
Stage 3 Party capture sink accepted the first 40 ms microphone frame (3840 bytes).
Stage 3 Party capture sink accepted microphone signal (peak ...); this PCM is now on the Party voice transport path.
Stage 3 Party capture bridge result (completed U hold): verdict=PASS_PARTY_CAPTURE_SINK_ACCEPTED_MICROPHONE_SIGNAL, submittedFrames=..., submittedAudioMs=..., peak=..., submitFailures=0, backpressureDrops=0, ...
```

The receiving client must independently contain:

```text
Stage 3 voice diagnostics PEER 0x...: permissions=0x0005 ... remoteIndicator=Talking (1), incomingMuted=False, renderVolume=... diagnosis=PASS_REMOTE_AUDIO_RECEIVED_BY_PARTY.
Stage 3 voice diagnostic SUMMARY (...): verdict=PASS_PARTY_BIDIRECTIONAL_SIGNAL_PATH; ... completePeer=0x....
```

The summary pass means that, during this session, Party accepted local microphone PCM through its official send sink and one specific remote peer independently satisfied permission `0x0005`, incoming-unmuted, positive finite render volume and remote `Talking`. It does not prove that a physical speaker produced audible sound; the listener must still confirm audibility because Windows endpoint routing, per-device mixer state and hardware remain outside Party's indicator API.

## Decision matrix

| Log evidence | Layer identified | Interpretation / next action |
| --- | --- | --- |
| Configure completion `result=0` followed by exact sink format | Party capture-sink setup | The ChatControl has the documented manipulation sink before it connects. Continue to a U hold. |
| Configure fails, sink is null, or format differs | Party capture-sink ABI/setup | The Mod fails closed before connection. Preserve the exact result/format and do not test voice with that build. |
| `I` local monitor logs `result: PASS` and self-audio is audible | Windows capture/render outside Party | The selected Windows mic and playback device work in shared mode. This is useful preflight evidence, not an online pass. |
| `I` starts but reports no microphone signal | Windows capture before Party | Check the physical mic, Windows privacy/input meter, selected endpoint and gain before arranging another two-client test. |
| Output state other than `Initialized (1)` | Party render device | The receiving client cannot establish its selected playback endpoint. Check the endpoint, exclusive use and format support. |
| `Party microphone capture started` but no first accepted frame | Windows capture/conversion or SubmitBuffer | Preserve the source format and any capture fault/submit error. Party never accepted a complete 40 ms frame. |
| First frames accepted but bridge verdict is `NO_SPEECH_SIGNAL_OBSERVED` | Local microphone signal | The sink is live but the measured PCM stayed below the signal threshold. Check the selected endpoint, Windows meter and gain. |
| `PASS_PARTY_CAPTURE_SINK_ACCEPTED_MICROPHONE_SIGNAL` | Local Party send path | The selected Windows mic was converted and Party synchronously accepted signal-bearing PCM. This alone does not prove network receipt. |
| `Party capture sink backpressure 0x000010D8` | Recoverable Party sink queue pressure | The current 40 ms frame was dropped because Party's bounded queue had no space. Capture must remain active and a later paced frame must succeed. Use `backpressureDrops` in the hold summary; repeated fail-closed teardown for this code indicates an old build. |
| Three consecutive non-`0x10D8` `SubmitBuffer` failures | Party capture sink/API state | The bridge closes its gate, mutes and tears down fail-closed. Preserve all three HRESULTs and the session lifecycle immediately before them. |
| U held but `nativeInputUnmuted=False` or `inputMuted=True` | Push-to-talk/native mute | Party did not open the ChatControl even though WASAPI may have started. No captured frame is allowed through the submission gate. |
| Legacy LOCAL indicator `NoAudioInput` while capture-sink frames are accepted | Party automatic input, bypassed by manipulation | Do not fail the local path on this field. Use sink acceptance locally and the peer's remote `Talking` to judge the real bridge. |
| Local `Talking` while the sink signal is accepted | Supplemental Party evidence | Helpful confirmation, but not required because the receiver's indicator is the authoritative online check. |
| Permission readback lacks send or receive microphone bits | Chat permission | `SetPermissions` was accepted or logged but the live relation is not `0x0005`; transport cannot be considered ready. |
| Remote `IncomingVoiceDisabled` | Receive permission/policy | Party says incoming voice is disabled. Compare both clients' permission readbacks. |
| Remote `IncomingCommunicationsMuted` or `incomingMuted=True` | Receiver-side mute | The receiving client is muting that peer. |
| Remote `NoRemoteInput` throughout the speaker's U hold | Speaker/Party send relation | Inspect the speaker's capture-sink result and permission readback. If its sink accepted signal, preserve both logs because Party did not expose that PCM as remote input. |
| Remote `RemoteAudioInputMuted` | Speaker push-to-talk state | Expected outside the peer's U hold. If it remains throughout speech, inspect the speaker's mute transition. |
| Remote `Talking` | Party network receive | The receiver sees the peer's voice activity. If no sound is audible, transport succeeded far enough to focus on the receiver's output state, selected playback device, render volume, Windows mixer and hardware. |
| `renderVolume=0`, negative, `NaN` or infinity | Party render control | Party's render path is not at a usable positive finite volume. |
| Remote remains `Silent` while the speaker logs sink-accepted signal | Network/peer relation | Party accepted the speaker's PCM but the receiver never observes remote voice; compare permission, incoming mute, exact peer handles and both complete logs. |
| Local sink evidence passes but summary says `FAIL_REMOTE_TALKING_NOT_OBSERVED` | One-way path | This client submitted speech but never observed the peer talking. Use the other client's local bridge result and this client's PEER lines to identify which direction failed. |
| `FAIL_NO_SINGLE_PEER_COMPLETED_REMOTE_PATH` | Multi-peer evidence | Different peers supplied only partial evidence; no individual remote relation completed the receive path. The per-peer fields show which component is missing. |
| `INCONCLUSIVE_*` | Missing evidence/API getter | The run ended too early, no three-second phrase occurred, an audio-state event was absent, or a read-only getter failed. Keep the translated getter errors and repeat only after addressing that concrete gap. |
| `PASS_PARTY_BIDIRECTIONAL_SIGNAL_PATH` but no physical sound | Windows/output after Party | Party's logical signal path passed. Check the logged selected output ID against Windows Sound, the default communications role, app/device mixer, headset routing and hardware. |

## Cleanup evidence

Every U release must first log `Party microphone submission gate closed`; endpoint cleanup may finish asynchronously, but no old callback may increase that hold's submitted-frame count. The session summary should be followed by the existing local teardown chain: pre-leave destroy queued, local left, destroy completion with result `0`, local destroyed, Stage 2 cleanup complete and Party cleanup. The remote client should observe the departing ChatControl leave or be destroyed. Audio continuing after release, focus loss or peer departure is always a failed safety test.

The one-time startup line `PartyStartProcessingStateChanges returned error 0x00001000` is not a voice diagnosis. It is ignorable only when the later authenticated session, endpoint, local/remote ChatControl, audio-state, permission and diagnostic lines all appear.

## Official references

- [Microsoft: Troubleshoot PlayFab Party audio and chat](https://learn.microsoft.com/en-us/xbox/playfab/community/voice-communications/concepts-audio-troubleshooting)
- [Microsoft: PartyLocalChatControl reference (including GetLocalChatIndicator)](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/reference/classes/partylocalchatcontrol/partylocalchatcontrol)
- [Microsoft: PartyLocalChatControl::GetChatIndicator](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/reference/classes/partylocalchatcontrol/methods/partylocalchatcontrol_getchatindicator)
- [Microsoft: PartyChatPermissionOptions](https://learn.microsoft.com/en-us/xbox/playfab/multiplayer/networking/reference/enums/partychatpermissionoptions)
- [Microsoft: real-time audio manipulation](https://learn.microsoft.com/en-us/xbox/playfab/community/voice-communications/concepts-realtime-audio-manipulation)
- [Microsoft: configure an audio-manipulation capture stream](https://learn.microsoft.com/en-us/xbox/playfab/multiplayer/networking/reference/classes/partylocalchatcontrol/methods/partylocalchatcontrol_configureaudiomanipulationcapturestream)
- [Microsoft: submit a capture buffer](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/reference/classes/partyaudiomanipulationsinkstream/methods/partyaudiomanipulationsinkstream_submitbuffer)
- [Microsoft: Party quickstart](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/networking/quickstart)
