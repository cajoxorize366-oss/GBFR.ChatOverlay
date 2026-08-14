# Changelog

## 0.7.0 - 2026-08-14

Stable chat-moderation and native WordFilter synchronization release for Granblue Fantasy: Relink 2.0.4.

### Chat filtering and blocking

- Added pages `04 Chat Filter` and `05 Block Management` with live custom-term editing, mask-word or hide-message behavior, per-rule hit counts, room blocks, and persistent PlayFab EntityId blocks.
- Added threshold-based automatic blocking with a sliding hit window and configurable local, Party-chat, or disabled notification behavior.
- Applied custom moderation to verified Relink room participants before the game accepts incoming raw-text packets, without treating Steam IDs or packet member keys as platform identifiers.
- Kept Relink automatic communication and victory cues out of custom hit counts and automatic blocking while preserving their resolved player attribution.

### Native text path

- Synchronized mod history with Relink's final native WordFilter text through verified send and receive completion callbacks for game version 2.0.4.
- Added bounded, thread-safe send/receive associations and room-generation resets so synchronous, worker-thread, and delayed callbacks do not normally publish stale room text.
- Changed Steamworks text filtering into a disabled-by-default PC supplementary stage; the adapter now borrows an already loaded Steam API module instead of loading or freeing one during mod startup.
- Fixed the startup crash caused by eager Steam text-filter initialization and preserved fail-open behavior when the supplementary API is unavailable.

### Interface and input

- Enlarged and stabilized the chat resize target so the lower-right handle is easier to drag.
- Removed the obsolete Quick Actions panel hotkey and its unused DirectInput backend path; configured quick actions continue to use their direct bindings.
- Canonicalized numpad binding labels so legacy values such as `VK_61` display as `Num1`, remain executable, and conflict correctly with newly captured bindings.
- Clarified page `04` status text so Relink's native WordFilter synchronization and the optional Steam PC supplementary filter are shown as separate stages.

### Release

- Release ZIP naming is `GBFR.ChatOverlay-0.7.0-Relink-2.0.4.zip`.

## 0.6.0 - 2026-08-13

First stable release for Granblue Fantasy: Relink 2.0.4.

### Runtime

- Declared native room text chat, official communication actions, Party voice, room/member notices, and native-HUD voice indicators ready for the stable channel.
- Preserved authoritative sender-slot, player-name, local-player, and lobby-owner resolution for ordinary chat, automatic communication lines, and victory lines.
- Preserved graceful room-exit detection from the successful `PartyNetworkLeaveNetwork` path so normal post-quest exits are not reported as network interruptions.
- Synchronized the neutral OverlayHub/ImGuiHub contract and single Present/WndProc ownership model with Extra Sigil Slots main.

### Repository

- Reorganized configuration, native build/chat/HUD/identity/interop/Party, and Reloaded runtime sources by module.
- Renamed the Party voice implementation and tests around their production responsibilities.
- Removed the unused local-preview transport, unused online WASAPI capture bridge, obsolete trimming targets, and obsolete publish scripts.
- Removed template-only base classes, empty configuration mixins, unused runtime context fields, and blanket configuration-read exception swallowing.
- Added bounded configuration hot-reload coverage for valid, partially written, and temporarily missing files.

### Documentation and release

- Replaced development handoff and chronological smoke-test documents with architecture, module, hook-flow, address, configuration, and release references.
- Replaced the root README with the stable release page.
- Release ZIP naming is `GBFR.ChatOverlay-0.6.0-Relink-2.0.4.zip`.
