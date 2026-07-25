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

The short sender-label field is empty for ordinary online free-text messages in the verified build; the `+0x18` value is an opaque sender identifier, not a display-name string. The bridge therefore uses the game's own sender-to-member-slot resolver at RVA `0x6D2EE0`, then follows the same lobby member lookup used by the online UI:

```text
member manager global RVA 0x7C23878
member lookup RVA 0x37CDD0
active flag member+0x5EBC
profile pointer member+0x5E60
MSVC std::string member_name profile+0x208
```

The name path accepts only active slots `0..3`, a valid profile pointer, a bounded NUL-terminated MSVC string and strict UTF-8. A non-empty RPC sender label remains authoritative. If any lookup or validation fails, the immutable record keeps the stable `Player XXXXXXXX` fallback and emits at most one diagnostic line.

The callback copies `0x1A0` bytes immediately, calls the original function, strictly validates UTF-8, resolves an empty sender label and enqueues an immutable record. The ImGui render callback drains a bounded queue into `ChatHistory`. Hashed quick messages are currently ignored because displaying them requires the game's text resolver.

## Version and online safety

- Use signature scans with validation of surrounding instructions; analysis RVAs are never used as runtime targets.
- Gate inbound and outbound features independently so one missing signature does not disable the local Overlay.
- Validate executable hash, signature uniqueness, member-slot range, member activity, string encoding, maximum length and null termination before using native data.
- Preserve the game's own send cooldown and validation path.
- Disable the bridge on unknown executable versions or ambiguous scans.
- Do not modify quest state, matchmaking state or network packets.

## Remaining runtime criteria

Static location, native ABI inspection, C# integration and unit tests are complete. Preview.17 still requires a two-client online smoke test proving that each teammate free-text line shows the same real player name as Relink's online UI, local echo remains `You` without duplication, and a failed name lookup safely retains `Player XXXXXXXX`.
