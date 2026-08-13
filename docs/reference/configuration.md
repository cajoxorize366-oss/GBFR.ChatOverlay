# Configuration

## Location and persistence

Reloaded-II stores the user configuration at:

```text
Reloaded-II/User/Mods/gbfr.qol.chatoverlay/Config.json
```

An explicit configurator directory takes precedence. If Reloaded does not supply one, the runtime derives the portable `User/Mods/gbfr.qol.chatoverlay` path from `ModConfig.json`.

Configuration uses indented JSON and string enum names. Unknown JSON properties are ignored by `System.Text.Json`; this keeps older files readable after a property is retired. The removed per-action `ControllerBinding` field is therefore accepted as legacy input but is not represented or written by 0.6.0.

## Hot reload

`FileSystemWatcher` reloads `Config.json` after an external save. A partially written, replaced, or temporarily missing file may raise `IOException`, `UnauthorizedAccessException`, or `JsonException`; these transient cases are retried for at most 250 ms with a 2 ms delay. If no complete file becomes readable, the active in-memory configuration is retained. Unrelated programming exceptions are not swallowed.

In-game edits pass through one runtime configuration lock and save the same file. Audio level sliders use a short deferred save to avoid rewriting JSON on every frame.

## General defaults

| Setting | Default | Notes |
| --- | --- | --- |
| interface language | Simplified Chinese | English is selectable in game |
| chat overlay | enabled | visible only in an authenticated online room |
| compact mode | disabled | hides history until chat is opened |
| IME candidate fallback | enabled | IMM32 candidate list beside active field |
| background opacity | `0.55` | range `0.0-1.0` |
| chat font size | `18` | range `12-30` |
| timestamps | disabled | local `HH:mm` when enabled |
| history capacity | `200` | range `10-5000` |
| player-name size | `18` | range `12-30` |
| player-name weight | `2` | range `1-3` |
| overlay size | `560 x 260` | persisted by hidden fields |
| overlay position ratios | `-1, -1` | negative means automatic initial placement |

Default player colors are `#5ED9FF`, `#FFAD5E`, `#71DF8A`, and `#C69CFF`.

## Hotkey defaults and syntax

| Action | Keyboard | Controller |
| --- | --- | --- |
| open chat | `Y` | unbound |
| push-to-talk | `U` | unbound |
| settings | `F10` | unbound |
| quick-action menu | unbound | unbound |
| global chat mute | unbound | unbound |
| remote players 1-3 chat mute | unbound | unbound |

Keyboard bindings may use Ctrl, Shift, or Alt plus one primary key. Controller bindings use one or two standard/extended buttons. `DPadDown` is invalid because it belongs to Relink's official communication shortcut.

Individual quick actions support only `KeyboardBinding`. Their kinds are:

- `CustomText`: sends `Text` through native room chat;
- `Stamp`: invokes a verified official stamp ID;
- `FixedPhrase`: invokes and locally plays a verified phrase ID;
- `Emotion`: invokes and locally plays a verified emotion ID.

Each action has a stable GUID-like `Id`, `Enabled`, user-facing `Name`, `Kind`, `OfficialId`, `Text`, and keyboard binding. An official action is configured only if its ID exists in `CommunicationCatalog`.

## Voice defaults

| Setting | Default | Runtime behavior |
| --- | --- | --- |
| Party voice | enabled | joins only the current authenticated Party session |
| voice indicators | enabled | requires coherent Party, identity, and HUD snapshots |
| microphone | `default` | Windows default communications capture device |
| playback | `default` | Windows default communications render device |
| self-test input gain | `1.0` | range `0.0-2.0` |
| self-test playback volume | `0.35` | range `0.0-0.5` |

Changing microphone or playback rebuilds the local test immediately. Party voice applies a new endpoint selection after mod restart.

## Release-only diagnostics

`EnableNativeChatBridge`, Party lifecycle logging, and show-all voice anchors exist for development and tests. In Release builds the diagnostic controls are hidden; lifecycle logging and all-anchor presentation are forced off by the effective properties even if stale JSON sets them to true. The production native chat bridge remains enabled by default.

## Reset and migration

There is no alternate legacy configuration directory in 0.6.0. To reset all settings, close the game and delete `Config.json`; Reloaded creates a new file from the defaults on the next load. Deleting or replacing the mod package does not automatically delete the user configuration.
