# Relink native chat bridge

The Overlay remains independent from Relink's version-specific implementation through `IChatTransport` and `IIncomingChatSource`. The native bridge targets one verified Relink 2.0.4 executable only:

```text
SHA-256 f827f3c13caa90b290fab2fe7e28165a80448fde0a3f7a96d79dac6b8343ff2a
```

The hash above remains a deferred diagnostic identifier. Runtime initialization does not read or hash the complete 123 MB executable on Reloaded-II's synchronous loader path. Instead it reads only the required instruction ranges at the known RVAs, validates every exact/wildcard signature and recomputes each RIP-relative target before installing hooks. Any required byte, RVA or derived target mismatch disables the affected bridge.

## Outbound messages

The identified source method is:

```text
ui::hud::Manager::sendMessage
RVA 0x9049F0 in the verified image
machine ABI: (Manager*, string_view*, uint32, string_view*, int)
```

The typed-chat caller supplies the raw-text discriminator `0x887AE0B0`, a valid empty short string view and category `-1`. The bridge reproduces that call and lets `sendMessage` retain the game's state, length, cooldown, filtering and network path. It never creates a packet. Text is UTF-8, NUL-terminated for downstream safety, and rejected above `0x15D` bytes.

## Incoming messages

The identified receive method is:

```text
ui::hud::Manager::rpcMessage
RVA 0xB97950 in the verified image
optimized machine ABI: (network::protocol::behavior::Chat const*)
```

For raw free-text records, the verified `Chat` layout exposes:

- `+0x18`: verified four-party member slot (`0..3`) used directly by the game's identity and filtering path;
- `+0x1C`: bounded `0x160`-byte text buffer;
- `+0x17C`: text hash/discriminator (`0x887AE0B0` means literal text);
- `+0x180`: bounded `0x18`-byte sender label/short field;
- `+0x198` and `+0x19C`: category/metadata retained for later classification.

The short sender-label field is empty for ordinary online free-text messages in the verified build. Native inspection confirms that the game feeds `+0x18` directly into its four-entry Party identity bank, so the bridge uses it directly as the member slot. RVA `0x6CD520` instead returns the traversal index of a separate network-object pointer array; it is retained only as a validated native boundary and is never used for display names, player colors, blacklist selection or local identity. Name resolution then follows the same lobby member lookup used by the online UI:

```text
member manager global RVA 0x7C21AB8
member lookup RVA 0x3760A0
active flag member+0x5EBC
profile pointer member+0x5E60
MSVC std::string member_name profile+0x208
```

The authoritative local Party slot is read from the same Party manager used by the identity bank: byte `manager+0x6CCE8` selects the local-slot entry at `manager+0x6C828` or `manager+0x6C82C`. The manager pointer, selector, local slot and all four EntityIds are rechecked as one coherent snapshot before publication; slot 0 is never assumed to be local. The name path accepts only active slots `0..3`, a valid profile pointer, a bounded NUL-terminated MSVC string and strict UTF-8. A normal non-empty RPC sender label remains authoritative. Labels beginning with `vo_CMM_` are machine communication cues rather than player names: the bridge resolves the player through `+0x18`, maps `chance`, `win_*` and `thanks` to structured link-attack, victory and thanks cues, and never publishes the raw cue key as a sender. If any lookup or validation fails, the immutable record keeps the stable `Player XXXXXXXX` fallback and emits at most one diagnostic line.

The callback copies `0x1A0` bytes immediately, calls the original function, strictly validates UTF-8, resolves an empty or machine-cue sender label and enqueues an immutable record. It never dereferences the packet again after the original callback returns. The outbound short sender view is also treated as untrusted identity input: `vo_CMM_*` values cannot update the cached local player name, and every incoming record is sanitized once more immediately before enqueue. The ImGui render callback drains a bounded queue into `ChatHistory`, preserving the real player slot and the optional communication cue separately. Hashed quick messages are currently ignored because displaying them requires the game's text resolver.

## Version and online safety

- Validate exact surrounding instructions at every required RVA and recompute RIP-relative targets before using them.
- Gate inbound and outbound features independently so one missing signature does not disable the local Overlay.
- Record the executable hash asynchronously for diagnostics; synchronously validate required bytes/RVAs, member-slot range, member activity, string encoding, maximum length and null termination before using native data.
- Preserve the game's own send cooldown and validation path.
- Disable the bridge on any required-byte/RVA mismatch; an unknown full-file hash alone is diagnostic and never bypasses those checks.
- Do not modify quest state, matchmaking state or network packets.

## Remaining runtime criteria

Static location, native ABI inspection, C# integration and unit tests are complete. The `0.5.0-preview.14` local echo behavior is retained, `0.5.0-preview.15` moves every native bridge profile to the verified Relink 2.0.4 image, and `0.5.0-preview.20` separates Party identity slots from network-object traversal slots while reading the authoritative local Party slot. The remaining criterion is a two-client run with reversed host/join order proving that free text and `vo_CMM_*` cues retain each sender's real name, and that an unresolved lookup safely keeps `Player XXXXXXXX` without inventing a host.
