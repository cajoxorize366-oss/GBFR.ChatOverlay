# Debug logging

## Purpose and ownership

Debug logging is an opt-in runtime diagnostic for support and maintenance. `Runtime/Startup.cs` owns the file sink and the fan-out that copies the Mod's existing Reloaded-II diagnostic messages. Feature modules continue to depend on `Action<string>` and do not open files themselves.

The setting is exposed in both places that edit the same `Config.EnableDebugLogging` value:

- Reloaded-II's Mod configuration under `00 General`;
- the in-game `00 General` settings page.

It is disabled by default and does not create a file until enabled.

## File and lifecycle

The fixed output path is:

```text
Reloaded-II/Mods/GBFR.ChatOverlay/GBFR.ChatOverlay.debug.log
```

The authoritative Mod directory comes from `IModLoader.GetDirectoryForModId`, not the user configuration directory. The first enable in a process starts a fresh UTF-8 session file. Disabling closes the writer immediately; enabling it again in the same process appends to that session. A new game process starts a new file.

The writer permits read sharing so the file can be inspected or copied while the game is running. A diagnostic captures its ISO-8601 timestamp with offset and the producing managed thread ID before it enters the queue, so the file preserves the source context rather than the background writer's thread.

## Data flow

```text
feature module
    |
    | Action<string> (capture time + producer thread)
    v
Startup-owned log fan-out
    |------------------------------|
    v                              v
Reloaded-II logger          bounded channel (1,024)
always attempted                    |
                                    v
                              one file consumer
```

Only the background consumer opens, writes, flushes, or closes the file. Native callbacks, Present, audio workers, and other producers use non-blocking queue writes. If the queue is full, new lines are dropped and one bounded summary is written when logging is disabled or disposed. The file sink does not add chat messages or packet payloads beyond what the Mod already emits as diagnostics. Some existing diagnostics can still contain player names, PlayFab identifiers, configured audio-device names, file paths, or native addresses.

One game-process session is capped at 16 MiB. Reaching the cap closes the file sink and reports the condition through Reloaded-II instead of allowing an unbounded log to consume the drive.

## Hot update and failure rules

In-game saves and external `Config.json` reloads both apply the logging state immediately. Enabling waits for the consumer to open the file and write its header. Disabling first stops new queue writes, then drains already accepted lines, writes the final marker, flushes, and releases the handle before returning. Ordinary diagnostic writes remain asynchronous; returning from a producer call does not by itself guarantee that the line is already on disk.

File creation, encoding, write, flush, fault-reporting, and disposal failures are contained. Reloaded-II logging remains available, and a file-sink failure cannot abort startup, a native callback, a graphics frame, or teardown. Failure reporting bypasses the file fan-out so it cannot recurse into the failing sink. An ordinary open or write failure can be retried by switching Debug Log off and on; the per-process size cap remains in effect until the next game process.

## Privacy and support

Review the file before sharing it. Remove player names, platform identifiers, local paths, or device names that are not needed to reproduce the issue. A useful report should include the observed behavior, the approximate local time, the game phase, and the smallest relevant section of the log rather than an unrelated full session.
