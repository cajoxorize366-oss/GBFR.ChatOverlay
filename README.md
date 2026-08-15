# GBFR Chat Overlay 0.7.0

GBFR Chat Overlay is a Reloaded-II mod for **Granblue Fantasy: Relink 2.0.4**. It adds native online-room text chat, configurable quick actions, PlayFab Party push-to-talk voice, room/member notices, and voice indicators aligned to Relink's own party HUD.

Version 0.7.0 adds cross-platform room-chat moderation and keeps the mod history synchronized with Relink's final native WordFilter text. Development handoff notes and preview-only scaffolding are not shipped.

## Features

- Native Relink text send/receive with authoritative sender, slot, local-player, and lobby-owner resolution.
- Custom term filtering, whole-message blocking, hit-threshold automatic blocks, and persistent PlayFab EntityId block management.
- Custom text, official stamps, fixed phrases, and emotions through Relink's native communication functions.
- PlayFab Party voice that joins the game's authenticated Party network and remains muted unless push-to-talk is physically held.
- Voice status in the chat header plus microphone icons on the native lobby and battle party HUD.
- Room entry/exit and member join/leave notices with normal leave, host loss, kick, and network interruption reasons.
- Chinese IME input, ANSI/DBCS compatibility, Backspace editing, keyboard history, compact mode, blacklist, and per-player mute controls.
- Keyboard, XInput controller, and Flydigi Vader 5 Pro extended-button input.
- A shared OverlayHub/ImGuiHub with Extra Sigil Slots so only one Present/WndProc graphics writer exists in the process.
- Opt-in debug logging to `GBFR.ChatOverlay.debug.log` in the Mod folder for support and maintenance.

## Compatibility

| Component | Supported target |
| --- | --- |
| Game | Granblue Fantasy: Relink 2.0.4, Windows x64 |
| Game executable SHA-256 | `f827f3c13caa90b290fab2fe7e28165a80448fde0a3f7a96d79dac6b8343ff2a` |
| PlayFab Party | PartyWin 1.10.12, file version `1.10.2509.24002` |
| Loader | Reloaded-II 1.30.2 or a compatible build |
| Required dependency | `reloaded.sharedlib.hooks` |
| Optional companion | `GBFR.ExtraSigilSlots.Reloaded` |

Every fixed native address is checked against its expected machine-code pattern before the corresponding hook is enabled. Unsupported or ambiguous builds fail closed: the affected chat, HUD, input, or voice feature stays unavailable instead of guessing an address.

## Installation

1. Close the game and Reloaded-II.
2. Delete any existing `Reloaded-II/Mods/GBFR.ChatOverlay` folder. This removes stale preview binaries and documents.
3. Extract `GBFR.ChatOverlay-0.7.0-Relink-2.0.4.zip` into `Reloaded-II/Mods`.
4. Confirm the resulting path is `Reloaded-II/Mods/GBFR.ChatOverlay/ModConfig.json`.
5. Enable `GBFR Chat Overlay` and its required hooks dependency in Reloaded-II.

All players who want Party voice must install a compatible build of the mod. Text chat continues to use Relink's native room channel.

## Default Controls

| Action | Default |
| --- | --- |
| Open chat | `Y` |
| Push to talk | hold `U` |
| Open settings | `F10` |

Controller bindings are unbound by default. Quick actions use keyboard bindings only; `DPadDown` is reserved for Relink's own communication shortcut and is rejected as a mod binding.

The settings window contains microphone/speaker selection and a local microphone test. Device changes affect the local test immediately; Party voice applies the selected devices after the mod restarts.

## Documentation

- [Documentation index](docs/index.md)
- [System architecture](docs/architecture/system-overview.md)
- [Runtime and hook lifecycle](docs/architecture/runtime-lifecycle.md)
- [Relink 2.0.4 addresses and layouts](docs/reference/relink-2.0.4-addresses.md)
- [Configuration reference](docs/reference/configuration.md)
- [Debug logging](docs/modules/runtime/debug-logging.md)
- [Build, validation, and release](docs/reference/build-release.md)
- [Changelog](CHANGELOG.md)

## Build

The repository requires .NET 8 and Visual Studio 2022 Build Tools with the C++ workload. Set `RELOADEDIIMODS` to a writable Reloaded-II Mods directory, then run:

```powershell
dotnet test tests\GBFR.ChatOverlay.Tests\GBFR.ChatOverlay.Tests.csproj -c Release
./ci/package-chat.ps1 -Version 0.7.0
```

The packaging script builds the managed projects and x64 native bridge, validates required files and version metadata, and creates the release ZIP under `artifacts/`.

## License Notices

Third-party components and licenses are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
