# Relink native chat bridge and sender attribution

The Overlay stays independent from version-specific native code through `IChatTransport` and `IIncomingChatSource`. Runtime hooks target only the verified Relink 2.0.4 executable:

```text
SHA-256 f827f3c13caa90b290fab2fe7e28165a80448fde0a3f7a96d79dac6b8343ff2a
```

Initialization validates the required instruction ranges and every derived RIP-relative target before installing hooks. The full 123 MB image hash is collected later for diagnostics and never blocks Reloaded-II's synchronous loader path.

## Cross-version function map

Relink 2.0.2 is not a supported runtime profile. It was independently disassembled only to verify that the 2.0.4 attribution semantics were not an accidental single-build interpretation.

| Function or callsite | Relink 2.0.4 RVA | Relink 2.0.2 RVA |
| --- | ---: | ---: |
| `ui::hud::Manager::sendMessage` | `0x9049F0` | `0x90A2E0` |
| automatic raw-text caller | `0x3EA8D5` | `0x3F1595` |
| typed-chat caller | `0x25F6383` | `0x25FAD73` |
| `ui::hud::Manager::rpcMessage` | `0xB97950` | `0xB9D230` |
| member-key resolver | `0x6CD520` | `0x6D2EE0` |
| resolver call inside `rpcMessage` | `0xB979B0` | `0xB9D290` |

The exact 2.0.2 comparison image has SHA-256 `63340832bcf731fbc97796f686b05c988418e83d451d4a49b2244a85d00e297f`. Both resolver functions have 30 direct callsites and the same loop/write-result structure. Older notes that listed 2.0.2 chat RVAs `0x903A50` and `0xB969B0` do not match this executable and must not be used.

## Outbound text paths

The verified machine ABI is:

```text
ui::hud::Manager::sendMessage
(Manager*, message_string_view*, uint32 hash, presentation_string_view*, int category)
```

The typed-chat caller supplies hash `0x887AE0B0`, an empty presentation view and category `-1`. The Mod's custom text path reproduces that call, preserving the game's own availability checks, length limit, cooldown, filtering and network transmission.

Relink's automatic communication dispatcher begins at RVA `0x3EA670`. It accepts event values `0..19`, selects a row from the loaded automatic communication data, and reaches the same `sendMessage` function at `0x3EA8D5`. Static inspection shows:

- seven direct dispatcher callsites;
- direct event constants `1`, `2`, `5`, `6` and `7`, plus two queue-driven event values;
- an event row stride of `0x40` from manager field `+0xED0`;
- raw text at row `+0x0C`, a mode byte at `+0x39`, and up to three optional presentation choices at `+0x48`, `+0x4C` and `+0x50`;
- the registered resource path `system/table/communication_autoFixedPhrase.tbl` at RVA `0x620DF4C`.

The resource rows themselves are packed outside the executable, so the exact event number for the localized All-Potion sentence is not statically proven. That uncertainty does not affect attribution: automatic potion, victory and other raw-text events all enter the same send hook, and the fourth argument is treated only as a presentation or communication cue. It is never accepted as a player name.

Stamps, selectable fixed phrases and emotions use separate verified functions (`sendStamp`, `sendFixedPhrase` and `sendEmotion`). They do not change the raw-text identity contract.

## Incoming packet layout

`ui::hud::Manager::rpcMessage` receives a `network::protocol::behavior::Chat` record. For raw text, both 2.0.4 and 2.0.2 access the same fields:

| Offset | Meaning |
| ---: | --- |
| `+0x18` | opaque member key |
| `+0x1C` | bounded `0x160`-byte text buffer |
| `+0x17C` | hash/discriminator; `0x887AE0B0` means literal text |
| `+0x180` | bounded `0x18`-byte presentation/communication field |
| `+0x198` | category |
| `+0x19C` | metadata |

The decisive instruction sequence is inside `rpcMessage`: it reads `Chat+0x18`, passes that value to the member-key resolver, checks the resolver result, and only then continues with the message, presentation field and metadata. Therefore `+0x18` is not a direct `0..3` Party index. The presentation field is copied separately and is not identity, even when it contains a normal-looking value such as `Djeeta` or `trick`.

The resolver walks four active member records, compares the candidate record's key at `record+0x18` with the opaque input key, and writes the matching loop index `0..3` to the output pointer. Failure produces no trusted member index. The bridge calls this same native resolver before name lookup, blacklist selection, local/remote comparison, room-host mapping or UI color assignment.

## Local identity and player names

Relink's own local-member callsite reads the Party manager, selects `manager+0x6C828` or `manager+0x6C82C` using byte `manager+0x6CCE8`, and sends that stored opaque key through the same resolver. The bridge repeats this coherent read and verifies that the manager, selector, key and resolved member index remain stable before publication.

After an index is proven, the display name comes from the active lobby member profile:

```text
member manager global RVA 0x7C21AB8
member lookup RVA 0x3760A0
active flag member+0x5EBC
profile pointer member+0x5E60
MSVC std::string member_name profile+0x208
```

The local user is always UI Player 1. Remote UI Players 2 through 4 are assigned by ascending actual member index while skipping the proven local index. Native member indices and UI player numbers are intentionally separate concepts.

## End-to-end echo and attribution flow

1. A typed or automatic raw-text send enters the send hook. The hook records the text for echo correlation and classifies only known presentation cues such as `vo_CMM_win_*`; it obtains the sender from the verified local member index and lobby profile.
2. The original game function runs unchanged. If a synchronous local RPC arrives first, its opaque key must resolve to the proven local index before it can consume the pending echo.
3. If no synchronous RPC arrives, the successful send is published immediately as local UI Player 1. A later proven-local RPC is deduplicated.
4. A remote player sending identical text never consumes the local echo token, because text equality is considered only after the RPC member key proves that the packet is local.
5. A remote RPC is mapped from opaque key to member index, then to the verified lobby name and relative UI Player 2 through 4. A failed mapping keeps `Player XXXXXXXX` and UI player `0` rather than guessing.

This flow covers the user's normal text, automatic All-Potion sentence and victory sentence. A presentation value can add a localized cue to the line, but cannot replace its sender.

## Host authority and UI labels

Sender attribution and host authority use the same coherent four-member EntityId snapshot, but they are separate decisions. A correct Steam/display name does not prove that the same member owns the room.

The host decision is gated by PlayFab Party lifecycle state:

- `CreateNewNetworkCompleted` records that the local user created the Party network. Party's documented flow then connects that same local device with `ConnectToNetwork`; when both completions precede authentication, `Created` remains the stronger role signal and the local UI player is the host.
- A client that only completes `ConnectToNetwork` is a joiner. Its own EntityId is excluded from all captured `PFLobbyGetOwner` candidates. A host label is published only when exactly one remaining candidate matches a current remote member.
- Missing lifecycle evidence, a local-only candidate, multiple matching remote candidates, malformed member data or a role change clears the cached host and fails closed with no `[房主]` label.

The overlay performs a final defensive check and renders the label only for an authoritative player number in `1..4` that equals the resolved message player. Runtime diagnostics report only `local_role` and `host_ui_player`; they never log EntityIds or chat text.

## Regression and fix

Commit `eb205c1d5472638fa3ccf3b7f8c1518703fb1e03` removed the native key-to-index step and treated `Chat+0x18` as a direct Party index. It also allowed native member position to leak into the local UI player number. Earlier send-hook code additionally attempted to learn the local name from the fourth `sendMessage` string; filtering only `vo_CMM_*` still allowed other presentation values to poison the cache.

Together, those assumptions explain the regression where an automatic communication could establish another member's name (for example `trick`) and subsequent ordinary or victory lines continued under that name. `0.5.0-preview.23` removes both identity shortcuts:

- every opaque member key uses Relink's native resolver;
- the short/presentation field never supplies identity;
- local history always uses UI Player 1 and a verified local lobby name;
- authoritative echo suppression requires a proven local member index;
- capped attribution diagnostics record keys, indices and decisions without logging chat text.

`0.5.0-preview.24` separately fixes the host-label regression introduced by binding the first matching lobby-owner candidate on every client. The creator/joiner Party role now controls whether local UI player 1 can be a host at all, and joiners can bind only a unique non-local owner candidate.

## Diagnostics and remaining runtime criteria

For the first 32 messages in each room, logs include `member_key`, resolved `member_index`, `local_index`, local/remote relation, UI player number, cue, category and metadata. Chat text is intentionally omitted. Resolver failures are logged once and fail closed to the stable fallback.

Static function location, ABI inspection, cross-version structure comparison, C# integration and automated regression tests are complete. The following remain `UNVERIFIED` until an actual two-client run:

- the exact packed resource row/event number for the localized All-Potion sentence;
- both clients observing ordinary text, All-Potion and victory events with correct names after reversing host/join order;
- live logs proving a remote same-text message is retained while only the proven-local RPC echo is deduplicated.
- both clients marking only the actual creator as host after reversing host/join order, with `Created` on the creator and `Connected` on the joiner.

The bridge never creates or edits a network packet and never guesses identity from message text, presentation text, host state or slot zero.
