# Changelog

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
