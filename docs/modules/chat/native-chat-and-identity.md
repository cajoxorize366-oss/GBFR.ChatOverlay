# Native Chat and Identity

## Scope

This module carries room text through Relink's own chat channel and resolves every displayed sender from game-owned identity state. It does not use display-name matching as an identity key and it does not create a second chat network.

Primary sources:

- `Core/ChatSession.cs`, `Core/ChatHistory.cs`, and `Core/ChatBlacklist.cs`
- `Native/Chat/RelinkChatBridge.cs`
- `Native/Chat/RelinkChatPacketDecoder.cs`
- `Native/Chat/RelinkFilteredChatCallbackDecoder.cs`
- `Native/Chat/RelinkChatMessageAttribution.cs`
- `Native/Chat/RelinkIncomingChatModerationPolicy.cs`
- `Native/Chat/RelinkOutgoingChatPolicy.cs`
- `Core/PendingFilteredChatQueue.cs`
- `Native/Identity/RelinkPartyMemberSlotResolver.cs`
- `Native/Identity/RelinkPlayerNameResolver.cs`
- `Native/Identity/RelinkPartyMemberIdentityResolver.cs`
- `Native/Identity/RelinkLobbyOwnerTracker.cs`

## Build gate

`RelinkGameContextProbe` asks `RelinkBuildLocator` to validate the Relink 2.0.4 executable before any chat delegate or hook is created. The locator checks the executable SHA-256, raw send/receive entry points, both WordFilter completion callbacks, required instruction bytes, relative call targets, manager-slot derivation, and the PlayFab lobby-owner import thunk. A mismatch disables the native bridge instead of scanning for a nearby function.

## Incoming message flow

```text
Relink rpcMessage
  -> view the bounded 0x1A0-byte packet while the native pointer is valid
  -> read the opaque sender key and resolve it to an actual four-member slot
  -> read the slot's verified lobby profile name and PlayFab EntityId
  -> compare the actual slot with the coherent local slot
  -> decode the bounded message/category/metadata fields
  -> classify self, party member, automatic communication, or unknown
  -> apply persistent/room blocks before Relink accepts the packet
  -> apply custom and optional Steam supplementary filtering
  -> copy the packet only when rewriting its raw-text field
  -> call Relink's original handler with the original or rewritten packet
  -> Relink applies its own Block/Mute gates and native WordFilter
  -> filtered-receive callback forwards the final text to Relink's official UI
  -> only after that original callback succeeds, enqueue the final sanitized text
  -> ChatSession drains it into ChatHistory on the overlay tick
```

The packet's sender label is presentation evidence only. Sender ownership comes from the sender-key resolver and the current member tables. If the slot, local key, active bank, or profile cannot be read coherently, attribution fails closed rather than assigning the line to the host or to the local player.

The same attribution path is used for ordinary text, Relink automatic communication lines, and victory lines. Generic labels beginning with `vo_CMM_` and character voice resources shaped as `PL<digits>_VO_CMM_*` are communication cues. Known actions retain their victory, link-attack, or thanks label; unknown valid communication actions use the generic official label. Embedded text such as a player name containing `vo_CMM_`, nonnumeric `PL` prefixes, and `_vo_CMM_emo_*` resources are not protocol cues. Automatic communication bypasses custom moderation, Steam supplementary filtering, hit counters, and automatic blocking. Raw communication lines still complete through Relink's native WordFilter callback so the mod displays the same final text as the official UI. This prevents automatic potion, link-attack, SBA, and victory messages from changing the ownership or moderation state of later user text.

Only decoded raw-text packets enter the word-filter pipeline. A player block is broader: it is checked before decoding and suppresses that participant's raw text, stamps, and fixed phrases in the same authoritative receive gate.

## Outgoing message flow

Custom text is normalized and limited to Relink's `0x15D` UTF-8 byte payload. Before `RelinkChatBridge.Send` calls the original native send function, it records a bounded pending association because the native WordFilter may complete synchronously or on a worker thread. The filtered-send callback reads Relink's final sanitized string, registers deduplication against that final text, forwards the callback so Relink performs the actual send, and only then publishes the local history entry. The later filtered RPC copy is reconciled against the same final text so the sender sees one message rather than a local line plus a duplicate network echo.

Relink's automatic communication dispatcher uses the same raw-text send function but marks those lines with a `vo_CMM_*` presentation label and a category from `0` through `19`. The game sends that packet to the active party members, then uses the category again when routing the received line through its communication and official-chat UI. A local speech bubble or mod-history entry therefore proves that the automatic cue ran, but it does not prove that every receiver's official chat history accepted the automatic category. For compatibility with receivers that omit those categories, the bridge forwards verified raw `vo_CMM_*` lines as normal category `-1` text while preserving the presentation label and WordFilter path. Manual text, non-raw official actions, unlabelled raw text, and categories outside the verified automatic range are unchanged. Normalized automatic lines use Relink's ordinary text rate limit.

Official actions do not synthesize chat packets:

| Action | Native path |
| --- | --- |
| Stamp | `SendStamp(manager, nativeValue)` |
| Fixed phrase | `SendFixedPhrase(manager, nativeValue, -1, 0)` plus local playback |
| Emotion | `SendEmotion(nativeValue)` plus local playback |

`-1` is Relink's explicit no-character-voice sentinel for fixed phrases. Custom text always uses the normal chat transport.

## Local player and host identity

The local player is resolved from Relink's current local-member key and actual slot, then cached only after a verified name read. The literal UI word `You` is never used as an identity source.

Host detection has a separate authoritative chain:

```text
PFLobbyGetOwner import thunk
  -> PFEntityKey owner EntityId
  -> coherent four-slot member EntityId snapshot
  -> current Party network role (created or connected)
  -> UI player number 1-4
```

For a room creator, the local player is Player 1 in the overlay. For a joiner, the owner EntityId is mapped through the joiner's local-slot-relative ordering. Ambiguous, duplicate, stale, or missing EntityIds clear the host marker; they never force `You` to become the host.

## Queue and callback rules

Native detours copy only bounded data and call the original function in the expected order. The WordFilter callbacks can run on the current stack for cache hits or on a worker thread for queued work, so callback state and string-view pointers are decoded only while that callback is active. Managed rendering never reads a transient packet or closure pointer. Pending sends, pending receives, incoming messages, and diagnostics have fixed capacities. Each accepted raw RPC registers a receive association keyed by sender key, category, and metadata and retains the communication cue decoded from that original packet. The callback supplies the final filtered text and may supplement a missing cue, but it cannot erase a cue already proven by the packet. A filtered callback without a current association is forwarded to Relink but is not published by the mod. Room reset and suspension atomically clear both association queues and echo state so a late callback cannot normally publish into a new room. Logger failures are contained only at native callback boundaries where unwinding into Relink would be unsafe.

## Failure behavior

- Unsupported executable: native chat remains unavailable.
- Chat manager not ready: sends return `Unavailable`; no address fallback is attempted.
- Sender identity ambiguous: the line is retained without inventing host/local ownership.
- Moderation attribution ambiguous: the original packet is passed through and no player is counted or blocked.
- Steamworks supplementary filter unavailable or throws: custom rules continue and the supplementary step fails open.
- A confirmed filtered replacement cannot be encoded into Relink's bounded packet: that message is dropped instead of exposing the original matched text.
- Invalid UTF-8 length or NUL: the send is rejected before entering native code.
- Lobby-owner tracking unavailable: chat continues, but host labels and room-owner naming fail closed.
