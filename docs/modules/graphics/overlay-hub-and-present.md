# OverlayHub and Present

## Scope

OverlayHub is the process-local graphics and input coordination layer shared with Extra Sigil Slots. Chat Overlay and Extra Sigil Slots are ordinary peers; neither installs a second independent ImGui writer when a compatible host already exists.

Primary sources:

- `GBFR.OverlayHub.Contracts/`
- `OverlayBroker/OverlayBrokerElection.cs`
- `OverlayBroker/OverlayBrokerHost.cs`
- `OverlayBroker/SharedImguiGraphicsBinding.cs`
- `Overlay/RtssSafeImguiHookDx11.cs`
- `Overlay/CjkConfiguredDx11Hook.cs`
- `NativeBridge/dxgi_present_bridge.cpp`

Protocol versions are OverlayHub API `2` and graphics binding `1`.

## Election and ownership

`OverlayBrokerElectionService` serializes startup with `Local\\GBFR.OverlayBroker.Election.<process id>`. Under that mutex it:

1. joins an already published compatible `IGbfrOverlayHub`;
2. acquires a new host generation if a recoverable hub has lost its writer;
3. otherwise publishes a new neutral broker and becomes the bootstrap host.

The host refuses initialization if Reloaded.Imgui.Hook is already claimed by an uncoordinated owner. This prevents two Present/WndProc writers from mutating the same ImGui globals.

## Shared graphics binding

Reloaded can load identical managed assemblies into separate AssemblyLoadContexts. The host therefore publishes the exact native cimgui module handle and ImGui context pointer. Each `IGbfrOverlayGraphicsClient` binds its managed wrapper to that pair before rendering. A missing module, context, or binding-version match disables the peer.

## Present chain

The DXGI bridge resolves an existing Present entry chain before invoking the original path. It follows supported entry jumps, detects cycles, verifies executable memory, and stops after at most 32 jumps. This lets the broker coexist with RTSS and other already-installed Present layers without assuming the first function address is the chain tail.

The original Present call is wrapped by native structured exception handling so a corrupt third-party chain cannot unwind through managed code. Permanent backend failure releases input and marks the host generation unavailable for coordinated recovery.

There is no independent `ResizeBuffers` hook. Device and frame ownership remain with Reloaded.Imgui.Hook's Direct3D 11 backend.

## WndProc and input capture

One custom WndProc offers each message to enabled peers, then calls the saved original procedure exactly once. Peer exceptions are isolated and fault that peer without abandoning the game's window procedure.

Input requests are merged as `Keyboard`, `Mouse`, and `Text` flags. The host combines requested state with the native DirectInput drain state so a close/reopen transition cannot leak held input into the game. Mouse capture release stores and later restores the clip rectangle and capture window.

The native DXGI bridge patches the game module's imports for `GetCursorPos`, `SetCursorPos`, and `ClipCursor` only while cursor release is required. It freezes the game-facing cursor position while the user operates the overlay, then restores normal game behavior when capture ends.

## Recovery

A recoverable broker owns a monotonically increasing host generation. On writer loss it clears graphics readiness, notifies every peer, and releases the generation. A compatible peer may then acquire the next generation under the election mutex and publish a new binding. Stale-generation callbacks cannot restore the old writer.

## Cross-repository parity

`VerifyOverlayBrokerSync.ps1` compares the shared contract and broker source files byte-for-byte after normalizing line endings. The GitHub quality gate clones `cajoxorize366-oss/GBFR-Extra-Sigil-Slots` main and rejects a Chat Overlay build when these files diverge.
