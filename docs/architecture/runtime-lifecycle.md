# Runtime and Hook Lifecycle

## Startup

1. Reloaded-II calls `Runtime.Startup.StartEx`.
2. `Runtime.Configuration.Configurator` resolves `User/Mods/gbfr.qol.chatoverlay/Config.json` and loads `Config`.
3. `OverlayBrokerElectionService` enters a process-local mutex and either joins an existing compatible `IGbfrOverlayHub` or publishes a new one.
4. The elected host creates `OverlayBrokerHost`; it refuses to start if another uncoordinated Reloaded ImGui owner already exists.
5. `Mod` attaches native modules. Each fixed-build module performs its preflight before creating a hook.
6. `ChatOverlayPeer` registers as an ordinary OverlayHub graphics client and binds to the host's exact native cimgui module and ImGui context.
7. The host publishes the graphics binding, marks the writer ready, and begins ticking/rendering registered peers from Present.

## Hook activation order

| Phase | Owner | Hooks or native entry points |
| --- | --- | --- |
| Graphics bootstrap | OverlayHub host | DXGI Present chain, custom WndProc, cursor IAT hooks |
| Party gate | `PartyLifecycleProbe` | `PartyInitialize`, `PartyCleanup`, `PartyNetworkLeaveNetwork`, state-batch start/finish |
| Party HUD | `RelinkPartyHudTracker` | lobby, battle, and Full Chain factory/destructor pairs |
| Native chat | `RelinkChatBridge` | send and RPC receive functions; official action delegates; lobby-owner tracker |
| Input carrier | `DirectInputKeyboardHook` | game-local DirectInput/XInput IAT bridge, polled from Present |

The Party gate is initialized before the chat peer is displayed because the overlay is visible only inside an authenticated online room.

## Suspend and resume

Reloaded suspension disables or suspends every owned runtime boundary:

- local audio self-test;
- Party lifecycle and voice session;
- native party-HUD hooks;
- native chat hooks and lobby-owner tracking;
- overlay registration and input capture.

Resume re-enables the same boundaries in overlay/audio/Party/HUD/chat order. If any native hook cannot resume, that module remains disabled and the exception is allowed to reach Reloaded rather than reporting a false ready state.

## Disposal

`Startup.Disposing` enters the idempotent `Startup.Dispose` path, detaches the configuration watcher, clears the live `Mod` and graphics-host references, and then asks `Mod` to:

1. force all voice inputs released and Party microphone muted;
2. dispose the DirectInput carrier and watchdogs;
3. dispose the overlay registration and controller pollers;
4. flush and dispose local audio monitoring;
5. disable Party lifecycle hooks, stop the Party audio work pump, and terminally dispose the Party voice session;
6. suspend native HUD and chat hooks.

Each teardown step contains its own failure and records the error so later privacy, input, configuration, and graphics resources are still released. The OverlayHub host is disposed after the mod runtime. The Reloaded controller is removed only when no compatible peer has already recovered the host lease.

## Host recovery

`IRecoverableGbfrOverlayHub` uses a monotonically increasing host generation. When the current graphics writer fails or exits:

1. the generation is released and graphics readiness becomes false;
2. every peer receives `OnHostUnavailable` and input capture is cleared;
3. an eligible peer may acquire a new generation under the election mutex;
4. the new host publishes a fresh shared graphics binding;
5. the Chat Overlay peer becomes the DirectInput carrier if it acquired the lease.

Generation checks reject callbacks from stale hosts after a transfer.

## Configuration hot reload

`Configurable<T>` watches `Config.json`. A change event retries only transient file conditions: `IOException`, `UnauthorizedAccessException`, and `JsonException` caused by a partially written, replaced, or temporarily missing file. The retry window is bounded to 250 ms. If no complete file becomes readable, the current in-memory configuration is retained; unrelated programming exceptions are not swallowed.

Runtime edits made from the in-game settings window are serialized through `Startup.UpdateConfiguration` under one configuration lock.
