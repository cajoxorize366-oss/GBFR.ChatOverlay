# Chat Filtering and Blocks

## Scope

This module filters normal remote room text before Relink and the mod overlay display it. It also owns per-room blocks, persistent PlayFab blocks, sliding-window hit counts, automatic blocking, and the in-game settings pages `04 Chat Filter` and `05 Block Management`.

Primary sources:

- `Configuration/ChatFilterConfiguration.cs`
- `Core/ChatModerationContracts.cs`
- `Core/ChatModerationService.cs`
- `Native/Chat/SteamOfficialTextFilter.cs`
- `Native/Chat/RelinkIncomingChatModerationPolicy.cs`
- `Overlay/ChatModerationSettingsPresentation.cs`
- `Overlay/ChatOverlayPeer.cs`

## Receive pipeline

```text
verified sender slot and local slot
  -> resolve name, PlayFab EntityId, sender key, and UI player number
  -> reject local player from moderation
  -> enforce persistent or current-room player block
  -> decode normal Relink raw text
  -> skip every official communication cue
  -> apply enabled custom terms with Unicode FormKC and invariant case folding
  -> optionally pass the custom-masked text through Steamworks ISteamUtils FilterText
  -> choose allow, mask, or hide-entire-message
  -> update one message hit, each matched-rule hit, and the player's sliding window
  -> optionally queue one automatic-block event
  -> pass the original or rewritten packet to Relink
  -> Relink applies its native WordFilter
  -> publish the final callback text to both the official UI and ChatHistory
```

Custom matches are merged before masking, so overlapping terms never expose an inner substring. A rule counts at most once per message even when the term occurs repeatedly. A message counts once toward automatic blocking even when several rules and the optional Steam supplementary filter match it. Relink's native WordFilter is a later game-owned stage and is not used as an identity signal or a custom-rule hit.

## Relink native WordFilter synchronization

Raw incoming and outgoing text uses Relink's own `WordFilterImpl::sanitizeComment` path. The mod hooks the two narrow completion lambdas at RVAs `0x00905160` and `0x009054B0`, rather than hooking the shared sanitizer or message append functions. The incoming callback first forwards the final string to Relink's official UI, then requires a current-room RPC association before copying that same final string into mod history. The outgoing callback forwards the final string to Relink's actual send path before publishing the local history fallback.

The callback's sender key is an opaque Relink member key. It does not encode Steam, PlayStation, or Nintendo platform type. Cross-platform moderation and blocking use the game-owned member-slot and PlayFab EntityId path; Steam text filtering is never used to infer sender identity.

## Steamworks adapter

`SteamOfficialTextFilter` is an optional PC-only supplementary stage. It dynamically resolves these flat Steam API exports from an already loaded Steam API DLL:

```text
SteamAPI_SteamUtils_v010
SteamAPI_ISteamUtils_InitFilterText
SteamAPI_ISteamUtils_FilterText
```

The delegates use the native cdecl ABI and the one-byte Steam boolean return. Input is strict UTF-8 with an explicit NUL terminator. Output is bounded to three UTF-8 bytes per input byte plus the terminator. The adapter borrows an existing module handle through `GetModuleHandleW`; it does not load or free Steam's DLL. Missing exports, initialization failure, invalid UTF-8, missing termination, or a native exception return the original text and report an unavailable or passthrough status.

The Steam API call uses source SteamID `0` because the Relink packet does not provide a trustworthy Steam identity for every cross-platform sender. For that reason this stage is disabled by default, explicitly labelled supplementary, and fails open. It does not replace or emulate Relink's native WordFilter.

## Identity model

Display names are presentation only. Moderation keys use this priority:

1. PlayFab EntityId: persistent across rooms and the only identity allowed in the saved block list.
2. Relink opaque sender key: current-room fallback when EntityId is unavailable.
3. UI player number 1-4: weakest current-room fallback.

An observed sender-key identity is migrated to a PlayFab EntityId only when the non-zero sender key corroborates that both records describe the same participant. A slot number alone is never continuity evidence. When a new sender key or EntityId occupies the same UI slot, it replaces the current participant row without inheriting the previous occupant's hit window or temporary block. Slot-only ambiguous text is still filtered, but it does not accrue a player hit or automatic block.

When a member leaves, EntityId-backed persistent state remains available to the saved block list. A leave event carrying EntityId or sender-key evidence removes only the matching observed identity; slot fallback is used only when the event has no stronger identity. This prevents a late leave event from erasing a replacement member in the reused slot.

## Automatic blocking

The configured threshold is evaluated over a true sliding time window. Old timestamps are removed before the current hit is appended. Reaching the threshold:

- blocks the participant for the current room immediately;
- persists the block when a PlayFab EntityId is available;
- queues exactly one event for that block transition;
- formats the configured notification with `{player}`, `{count}`, and `{threshold}`.

Notifications can be local system history, Party chat, or disabled. The formatter removes NUL and line breaks, expands placeholders once, and truncates on UTF-8 rune boundaries to Relink's packet limit. A room-exit tick consumes pending automatic-block events before clearing room counters; the first tick in a new active room does not discard native events that arrived before the overlay tick.

## Settings pages

Page `04 Chat Filter` owns:

- master enable for custom moderation;
- read-only Relink native WordFilter synchronization state;
- optional Steam PC supplementary-filter enable and refresh status;
- mask-matched-words or hide-entire-message mode;
- independent enabled custom-term rows;
- side-effect-free live preview;
- automatic-block threshold and time window;
- notification destination and template.

Page `05 Block Management` owns:

- current-room participants and hit counts;
- room-only block or unblock;
- persistent block or unblock when EntityId is available;
- saved blocked identities and removal.

A persistent block disables the room-only toggle because removing only the temporary flag cannot override the persistent decision.

## Failure behavior

- Local messages and all official communication cues bypass filtering and hit counters.
- Unverified sender or local slots bypass moderation instead of guessing a player.
- Steamworks supplementary failures preserve custom-rule behavior and pass that stage through unchanged.
- A confirmed match that cannot be encoded into the fixed Relink packet is hidden, not leaked as original text.
- Configuration updates are deep-copied into the service; UI writes repair externally null rule and block lists, and removing a saved EntityId also removes its room and automatic block state.
- Leaving or changing rooms clears temporary players, counts, events, and room blocks while retaining the saved EntityId list.
