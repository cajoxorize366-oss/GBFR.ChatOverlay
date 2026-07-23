# Relink chat bridge plan

The Overlay is deliberately independent from Relink's version-specific chat implementation. The bridge should be added behind the existing `IChatTransport` contract and must remain disabled unless every required signature has been resolved for the running executable.

## Outbound messages

Start from the original text-entry UI, not the network layer:

1. Open the vanilla text box through the normal Tab/communication-menu flow.
2. Submit a distinctive UTF-8/UTF-16 test string while tracing calls made by the UI confirmation handler.
3. Identify the narrowest function that accepts the message and still performs the game's normal length, state and rate checks.
4. Wrap that function in an `IChatTransport` implementation.
5. Reject sends when the game is not in a chat-capable state; never construct or replay a network packet directly.

The first fallback may automate the vanilla text box with clipboard paste and confirmation. It is suitable for proving the flow but should not become the final transport because it depends on focus and UI state.

## Incoming messages

Trace from the point where the vanilla UI appends a received line. The desired hook is after decoding and player-name resolution but before the original UI discards its display record. Copy only stable values into a managed event record:

- sender display name;
- sender/player identifier when available;
- message text;
- message category/channel;
- local receive timestamp.

The hook callback must enqueue records and return immediately. The ImGui render callback can drain the queue into `ChatHistory`; it must not traverse transient game objects on a later frame.

## Version and online safety

- Use signature scans with validation of surrounding instructions; never commit absolute executable addresses.
- Gate inbound and outbound features independently so one missing signature does not disable the local Overlay.
- Validate string encoding, maximum length and null termination before entering native code.
- Preserve the game's own send cooldown and validation path.
- Disable the bridge on unknown executable versions or ambiguous scans.
- Do not modify quest state, matchmaking state or network packets.

## Completion criteria

The bridge milestone is complete only when a vanilla client can receive a message sent from the Overlay, the Overlay records a teammate message once without duplicate local echo, and all failure paths leave the local preview usable.
