# Relink native chat bridge

The Overlay remains independent from Relink's version-specific implementation through `IChatTransport` and `IIncomingChatSource`. The first native bridge targets one verified Relink 2.0.2 executable only:

```text
SHA-256 63340832bcf731fbc97796f686b05c988418e83d451d4a49b2244a85d00e297f
```

The RVAs below are reverse-engineering anchors, not runtime constants. Runtime initialization scans the executable `.text` section for unique validated signatures and disables the bridge on any hash, count or RVA mismatch.

## Outbound messages

The identified source method is:

```text
ui::hud::Manager::sendMessage
RVA 0x90A2E0 in the verified image
machine ABI: (Manager*, string_view*, uint32, string_view*, int)
```

The typed-chat caller supplies the raw-text discriminator `0x887AE0B0`, a valid empty short string view and category `-1`. The bridge reproduces that call and lets `sendMessage` retain the game's state, length, cooldown, filtering and network path. It never creates a packet. Text is UTF-8, NUL-terminated for downstream safety, and rejected above `0x15D` bytes.

## Incoming messages

The identified receive method is:

```text
ui::hud::Manager::rpcMessage
RVA 0xB9D230 in the verified image
optimized machine ABI: (network::protocol::behavior::Chat const*)
```

For raw free-text records, the verified `Chat` layout exposes:

- `+0x18`: sender/player identifier used by the game's lookup and filtering path;
- `+0x1C`: bounded `0x160`-byte text buffer;
- `+0x17C`: text hash/discriminator (`0x887AE0B0` means literal text);
- `+0x180`: bounded `0x18`-byte sender label/short field;
- `+0x198` and `+0x19C`: category/metadata retained for later classification.

The callback copies `0x1A0` bytes immediately, calls the original function, strictly validates UTF-8 and enqueues an immutable record. The ImGui render callback drains a bounded queue into `ChatHistory`. Hashed quick messages are currently ignored because displaying them requires the game's text resolver.

## Version and online safety

- Use signature scans with validation of surrounding instructions; analysis RVAs are never used as runtime targets.
- Gate inbound and outbound features independently so one missing signature does not disable the local Overlay.
- Validate executable hash, signature uniqueness, string encoding, maximum length and null termination before entering native code.
- Preserve the game's own send cooldown and validation path.
- Disable the bridge on unknown executable versions or ambiguous scans.
- Do not modify quest state, matchmaking state or network packets.

## Remaining runtime criteria

Static location, C# ABI integration and unit tests are complete. The milestone still requires a two-client online test proving that a vanilla client receives an Overlay message, the Overlay records a teammate free-text message once without duplicate local echo, and unsupported game states leave the local preview usable.
