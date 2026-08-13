# Party Voice

## Scope

Party voice joins the authenticated PlayFab Party session already owned by Relink. PlayFab Party remains responsible for microphone capture, codec work, transport, jitter handling, and playback. The mod owns one local `PartyChatControl`, permissions, selected endpoint IDs, push-to-talk mute state, and presentation snapshots.

Primary sources:

- `Native/Party/PartyLifecycleProbe.cs`
- `Native/Party/PartyVoiceSession.cs`
- `Native/Party/PartyNativeApi.cs`
- `Native/Party/PartyAudioWorkPump.cs`
- `Input/VoicePushToTalkSafetyGate.cs`
- `Input/VoiceInputModeCoordinator.cs`

The NAudio path under `Audio/` is only the local settings-page self-test. It is not an online voice transport.

## PartyWin compatibility gate

The lifecycle probe accepts only `PartyWin.dll` with SHA-256 `3f0c6abbb735d81fa766a105982bda73f1d2c2cf01109fa2e7cf64813a52ce55`, file version `1.10.2509.24002`, and product version `1.10.12`. It then binds required exports by exact name. A version or export mismatch leaves room observation or voice unavailable instead of calling an unknown ABI.

## Session state machine

```text
WaitingForAuthenticatedSession
  -> Creating
  -> ConfiguringMutedAudio
  -> Connecting
  -> JoinedMuted
  -> VoiceReady
  -> Disconnecting / Destroying
  -> Completed
```

Any ownership mismatch, unknown state change, failed required call, or unsafe teardown enters `Disabled`. A disabled session stays muted and does not retry against uncertain Party state.

## Establishment flow

1. `PartyLifecycleProbe` observes the manager, network, authenticated local user, local device, and local gameplay endpoint from Relink's normal state-change batches.
2. `PartyVoiceSession` inventories existing device and network ChatControls. It refuses to take ownership of a pre-existing local control.
3. It creates one local ChatControl with an operation-specific async token.
4. It mutes audio input before selecting the configured input and output endpoints.
5. It connects the control to the existing Party network.
6. It discovers compatible remote ChatControls and grants only `SendMicrophoneAudio | ReceiveMicrophoneAudio` permissions.
7. It exposes `VoiceReady` only after the owned local control is joined, required remote permissions exist, and the microphone is still muted.

Text-to-speech and Party text permissions are not granted. The mod's text chat continues to use Relink's native room channel.

## Push-to-talk

Physical keyboard or controller state is sampled from the input module. `VoicePushToTalkSafetyGate` converts repeated physical snapshots into edges and a heartbeat:

- press: unmute only when a compatible remote voice participant is established;
- hold: refresh the heartbeat and request bounded diagnostic samples;
- release: mute immediately;
- missing heartbeat for 350 ms, focus/input suspension, host loss, or disposal: force mute.

This watchdog is a microphone safety boundary, not an arbitrary chat cooldown. Party's own state remains the authority for whether an unmute call can proceed.

## State-change batch fencing

Relink owns each `PartyStartProcessingStateChanges` / `PartyFinishProcessingStateChanges` critical section. The session records snapshots during the batch but does not issue Party mutation calls until the original finish call has completed. Work is serialized through one scheduled reconciliation loop and operation-specific `GCHandle` tokens, preventing stale completions from satisfying a newer operation.

`PartyAudioWorkPump` services Party's manual work mode outside the active state-change batch. A work-pump failure disables voice fail closed while room observation remains available.

## Teardown

Before Relink's original `PartyNetworkLeaveNetwork` executes, the session forces the microphone muted and queues destruction of its owned ChatControl. Party then reports disconnect/destroy completions through the normal state-change pump. Manager cleanup, suspension, and disposal follow the same mute-first rule.

The mod never destroys a ChatControl it did not create, never destroys the game's local user or network, and never continues native calls after Party ownership becomes uncertain.

## UI snapshots

The session publishes:

- a coarse `PartyVoiceUiStatus` for the chat header;
- established remote EntityIds;
- currently talking remote EntityIds;
- the established participant count.

EntityId-to-player mapping is performed later against a coherent Relink member snapshot. Voice transport does not guess display names or HUD rows.
