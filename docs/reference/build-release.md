# Build, Validation, and Release

## Toolchain

- Windows x64
- .NET SDK 8
- Visual Studio 2022 Build Tools with MSBuild and the C++ workload
- PowerShell 7 or Windows PowerShell 5.1
- Git and GitHub CLI for publishing

Set `RELOADEDIIMODS` to a writable directory before a direct build. The project deliberately fails when this variable is missing because normal builds copy Reloaded mod output there.

## Project layout

| Project | Purpose |
| --- | --- |
| `GBFR.ChatOverlay.csproj` | injected managed runtime and package root |
| `NativeBridge/GBFR.ChatOverlay.Native.vcxproj` | x64 Present, cursor, DirectInput, and XInput bridge |
| `ConfiguratorUI/GBFR.ChatOverlay.ConfiguratorUI.csproj` | Reloaded launcher-side audio endpoint editors |
| `GBFR.OverlayHub.Contracts/GBFR.OverlayHub.Contracts.csproj` | shared process-local graphics contract/runtime |
| `tests/GBFR.ChatOverlay.Tests/GBFR.ChatOverlay.Tests.csproj` | unit, integration, ABI, Hook preflight, and packaging contract tests |

`GBFR.ChatOverlay.csproj` builds the native bridge before the managed build and copies the configurator assembly into build and publish output.

## Local quality gate

From the repository root:

```powershell
dotnet restore tests\GBFR.ChatOverlay.Tests\GBFR.ChatOverlay.Tests.csproj

dotnet build tests\GBFR.ChatOverlay.Tests\GBFR.ChatOverlay.Tests.csproj `
  -c Release `
  --no-restore `
  -p:TreatWarningsAsErrors=true

dotnet test tests\GBFR.ChatOverlay.Tests\GBFR.ChatOverlay.Tests.csproj `
  -c Release `
  --no-build `
  --no-restore

.\ci\package-chat.ps1 -Version 0.6.0
```

Before publishing, clone or update Extra Sigil Slots main and run:

```powershell
.\VerifyOverlayBrokerSync.ps1 -OtherRepository <path-to-GBFR-Extra-Sigil-Slots>
```

The sync gate normalizes line endings and requires exact source parity for OverlayHub contracts, election, host, input classifier, graphics binding, and input-reset gate.

## Release package

`ci/package-chat.ps1` creates:

```text
artifacts/GBFR.ChatOverlay-0.6.0-Relink-2.0.4.zip
artifacts/GBFR.ChatOverlay-0.6.0-Relink-2.0.4.zip.sha256
```

The ZIP has one top-level `GBFR.ChatOverlay/` directory. The script:

1. publishes Release managed output and builds the x64 native bridge;
2. removes PDB/XML files and non-Windows/non-x64 cimgui runtimes;
3. copies the release README, changelog, notices, licenses, and complete `docs/` hierarchy;
4. verifies all required DLLs, `ModConfig.json`, `Icon.png`, documentation, and `win-x64` cimgui;
5. verifies stable version parity between the requested version, ModConfig, and assembly metadata;
6. verifies the native bridge PE machine is AMD64 (`0x8664`);
7. rejects development-only filenames;
8. writes a lowercase SHA-256 checksum file.

## GitHub Actions

`.github/workflows/quality-gate.yml` exposes one stable required job name: `quality-gate`. It runs for branch pushes, tags, pull requests, merge queue entries, and manual dispatches.

The job checks out Extra Sigil Slots main, verifies OverlayHub parity, builds the complete test graph with the repository analyzers and warnings as errors, runs the complete test project, packages the ZIP, and uploads both release assets.

A pushed `v*` tag starts the release job after `quality-gate` passes. The release job verifies the downloaded checksum, extracts the matching version section from `CHANGELOG.md`, creates or updates a non-draft GitHub Release, and uploads the ZIP plus checksum.

## Version procedure

For a stable release `X.Y.Z`:

1. set `ModConfig.json` `ModVersion` to `X.Y.Z`;
2. set project `Version`, `AssemblyVersion`, `FileVersion`, and `InformationalVersion` consistently;
3. add `## X.Y.Z - YYYY-MM-DD` to `CHANGELOG.md`;
4. run every local gate and inspect the final ZIP;
5. commit and push the release commit;
6. wait for the branch `quality-gate` to pass;
7. create and push annotated tag `vX.Y.Z`;
8. wait for the tag workflow and verify the GitHub Release assets.

Tags and package versions must be plain stable semantic versions. Preview suffixes are rejected by the package and CI version gates.

## Manual runtime verification

Automated tests cannot prove live two-client behavior inside Relink. A release candidate should still be exercised with two installed clients for:

- creator/joiner sender and host attribution;
- custom text and every official action kind;
- Chinese IME composition, candidate display, Backspace, and send;
- Party voice creation, PTT press/release, remote playback, and focus-loss mute;
- lobby and battle voice icons, including Full Chain masking;
- member join/leave reasons and normal post-quest room exit;
- OverlayHub coexistence with Extra Sigil Slots and RTSS.

Any item not exercised on the final package must be recorded as runtime `UNVERIFIED`, even when its unit/integration coverage passes.
