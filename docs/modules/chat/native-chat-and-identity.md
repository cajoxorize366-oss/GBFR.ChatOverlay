# Native Chat and Identity

## Scope

This module carries room text through Relink's own chat channel and resolves every displayed sender from game-owned identity state. It does not use display-name matching as an identity key and it does not create a second chat network.

Primary sources:

- `Core/ChatSession.cs`, `Core/ChatHistory.cs`, and `Core/ChatBlacklist.cs`
- `Native/Chat/RelinkChatBridge.cs`
- `Native/Chat/RelinkChatPacketDecoder.cs`
- `Native/Chat/RelinkChatMessageAttribution.cs`
- `Native/Identity/RelinkPartyMemberSlotResolver.cs`
- `Native/Identity/RelinkPlayerNameResolver.cs`
- `Native/Identity/RelinkPartyMemberIdentityResolver.cs`
- `Native/Identity/RelinkLobbyOwnerTracker.cs`

## Build gate

`RelinkGameContextProbe` asks `RelinkBuildLocator` to validate the Relink 2.0.4 executable before any chat delegate or hook is created. The locator checks the executable SHA-256, required instruction bytes, relative call targets, manager-slot derivation, and the PlayFab lobby-owner import thunk. A mismatch disables the native bridge instead of scanning for a nearby function.

## Incoming message flow

```text
Relink rpcMessage
  -> copy the fixed 0x1A0-byte packet while the native pointer is valid
  -> decode message/category/metadata and opaque sender key
  -> resolve sender key to an actual four-member slot
  -> read the slot's verified lobby profile name
  -> compare the actual slot with the coherent local slot
  -> classify self, party member, automatic communication, or unknown
  -> enqueue an immutable IncomingChatMessage
  -> ChatSession drains it into ChatHistory on the overlay tick
```

The packet's sender label is presentation evidence only. Sender ownership comes from the sender-key resolver and the current member tables. If the slot, local key, active bank, or profile cannot be read coherently, attribution fails closed rather than assigning the line to the host or to the local player.

The same attribution path is used for ordinary text, Relink automatic communication lines, and victory lines. This prevents automatic potion, link-attack, SBA, and victory messages from changing the ownership of later user text.

## Outgoing message flow

Custom text is normalized and limited to Relink's `0x15D` UTF-8 byte payload. `RelinkChatBridge.Send` resolves the live HUD chat manager and calls the original native send function. After the native call returns, the bridge publishes an authoritative local entry immediately and registers an echo token. The later RPC copy is reconciled against that token so the sender sees one message rather than a local line plus a duplicate network echo.

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

Native detours copy only bounded data and call the original function in the expected order. Managed rendering never reads a transient RPC packet pointer. Incoming queues and diagnostic queues have fixed capacities so a broken producer cannot grow memory without limit. Logger failures are contained only at native callback boundaries where unwinding into Relink would be unsafe.

## Failure behavior

- Unsupported executable: native chat remains unavailable.
- Chat manager not ready: sends return `Unavailable`; no address fallback is attempted.
- Sender identity ambiguous: the line is retained without inventing host/local ownership.
- Invalid UTF-8 length or NUL: the send is rejected before entering native code.
- Lobby-owner tracking unavailable: chat continues, but host labels and room-owner naming fail closed.
