# Documentation

The documentation follows the same hierarchy as the codebase: architecture first, production modules second, fixed-build and operational references last. It describes the current design rather than the sequence of development fixes that produced it.

## Architecture

- [System overview](architecture/system-overview.md): process boundaries, module ownership, data flow, and fail-closed rules.
- [Runtime lifecycle](architecture/runtime-lifecycle.md): Reloaded startup, OverlayHub election, hook activation, suspend/resume, recovery, and disposal.

## Modules

| Large module | Mid-level module | Primary source paths | Documentation |
| --- | --- | --- | --- |
| Communication | Native text and identity | `Core/`, `Native/Chat/`, `Native/Identity/` | [Native chat and identity](modules/chat/native-chat-and-identity.md) |
| Communication | Filtering and block management | `Configuration/ChatFilterConfiguration.cs`, `Core/ChatModeration*`, `Native/Chat/SteamOfficialTextFilter.cs` | [Chat filtering and blocks](modules/chat/chat-filtering-and-blocks.md) |
| Voice | Party ChatControl transport | `Native/Party/PartyVoiceSession.cs`, `Native/Party/PartyNativeApi.cs` | [Party voice](modules/voice/party-voice.md) |
| Online room | Room and membership lifecycle | `Native/Party/PartyLifecycleProbe.cs`, `Native/Party/PartyRoom*` | [Room and membership](modules/party/room-and-membership.md) |
| Graphics | Shared OverlayHub and Present | `GBFR.OverlayHub.Contracts/`, `OverlayBroker/`, `NativeBridge/dxgi_present_bridge.cpp` | [OverlayHub and Present](modules/graphics/overlay-hub-and-present.md) |
| Input | Keyboard, controller, and IME | `Input/`, `NativeBridge/directinput_broker.cpp`, `Overlay/Win32Ime*` | [Input and IME](modules/input/input-and-ime.md) |
| HUD | Native party anchors and voice icons | `Native/Hud/`, `Overlay/VoiceIndicatorOverlay.cs` | [Voice indicators](modules/hud/voice-indicators.md) |
| Audio | Endpoint selection and local test | `Audio/`, `ConfiguratorUI/` | [Audio devices and self-test](modules/audio/audio-devices-and-self-test.md) |
| Runtime | Reloaded composition and configuration | `Runtime/`, `Mod.cs`, `Configuration/` | [Runtime lifecycle](architecture/runtime-lifecycle.md) |
| Runtime | Opt-in diagnostic file logging | `Runtime/Diagnostics/`, `Runtime/Startup.cs`, `Mod.cs` | [Debug logging](modules/runtime/debug-logging.md) |

## Reference

- [Relink 2.0.4 addresses and layouts](reference/relink-2.0.4-addresses.md)
- [Configuration](reference/configuration.md)
- [Build, validation, and release](reference/build-release.md)

## Design rules

1. Relink and Party objects remain owned by the game. The mod observes or joins existing authenticated objects; it does not create a second gameplay network.
2. Fixed addresses are accepted only after byte-pattern and derived-target validation.
3. Native callbacks copy the minimum state required and defer managed or Party work until the original owner has completed its critical section.
4. One process has one OverlayHub graphics writer. Chat Overlay and Extra Sigil Slots are peers, not competing Present hooks.
5. Ambiguous identity, HUD geometry, room state, audio state, or input ownership hides or disables the affected feature instead of inventing a fallback identity or address.
