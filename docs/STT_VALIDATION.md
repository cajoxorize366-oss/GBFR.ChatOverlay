# Local Whisper base STT validation

This milestone adds an isolated Windows worker for push-to-talk. Microphone capture, resampling and Whisper inference run outside the injected game process. The worker uses the default Windows capture endpoint through WASAPI, converts each utterance to 16 kHz mono PCM16, and launches the pinned CPU-only whisper.cpp CLI with the multilingual OpenAI Whisper `base` model.

## Controls and expected flow

- Keyboard: hold `U`, speak, then release `U`.
- XInput-compatible controller: hold `LB + R3`, speak, then release the chord.
- Recognition language defaults to Chinese (`zh`). Reloaded-II exposes Chinese, Japanese, English, Korean and automatic detection as a configuration list; restart after changing it.
- The result opens as an editable draft. Press Enter to send it through the existing native Relink chat bridge, or Escape to discard it.
- Recording stops automatically after the configured maximum, 15 seconds by default.

The validation build intentionally does not auto-send a recognition result. The controller chord is observed rather than removed from the game's own XInput state, so Relink may also react to LB or R3 during this first bottom-layer test. PlayStation controllers need to be exposed as XInput by Steam Input or a compatible layer for this milestone.

## Build a validation package

Run from the repository root:

```powershell
.\scripts\Build-SttValidation.ps1
```

The first run downloads the pinned whisper.cpp 1.9.1 Windows x64 runtime and multilingual `ggml-base.bin`. Both files are SHA-256 verified. The model and generated runtime stay under ignored `SttRuntime/` and `artifacts/` directories rather than Git.

The script runs unit tests, verifies that all runtime pieces and the model are in the output, then writes:

```text
artifacts\validation\Mods\GBFR.ChatOverlay
artifacts\validation\GBFR.ChatOverlay-<version>-stt-base.zip
```

## In-game smoke test

1. Confirm Windows allows desktop applications to access the microphone.
2. Enable `Voice Input` in the Reloaded-II configuration and restart the mod if an older configuration kept it disabled.
3. Leave `Voice Language` at `中文 (zh)` for the first test and confirm the log contains `STT worker ready; Whisper base model hash verified.`
4. In a lobby or offline UI test, hold `U` for two or three seconds while speaking. Confirm the overlay changes from `Recording` to `Transcribing` after release.
5. Confirm recognized Chinese, Japanese, English or Korean appears in the editable draft. Correct it if necessary and press Enter.
6. Repeat with `LB + R3` on an XInput-visible controller.
7. Start another recording and press Escape; confirm the microphone worker cancels and the draft does not appear later.
8. Unplug the active controller while recording; confirm release/disconnect still ends the recording.

## Failure evidence

- `STT runtime is incomplete`: preserve the list of missing filenames and rerun `scripts\Prepare-SttRuntime.ps1`.
- `The Whisper model hash is invalid`: delete `SttRuntime/` and `artifacts/stt-cache/`, then prepare the runtime again.
- Microphone capture error: record the Windows default input device, its sample format, and the privacy setting state.
- Worker exit or timeout: preserve Reloaded-II lines prefixed with `STT worker:` and the current CPU model.
- Correct draft but failed network send: this is the existing Relink chat bridge path; record the current lobby/quest state separately from STT.
