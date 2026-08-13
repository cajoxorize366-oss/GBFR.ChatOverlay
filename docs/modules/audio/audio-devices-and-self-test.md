# Audio Devices and Self-Test

## Scope

The audio module has two consumers with intentionally different lifetimes:

- Party voice resolves Windows endpoint IDs once during mod startup and passes them to PlayFab Party;
- the local settings-page microphone self-test can rebuild its WASAPI endpoints immediately.

Primary sources:

- `Audio/AudioEndpointCatalog.cs`
- `Audio/InGameAudioSettingsController.cs`
- `Audio/LocalMicrophoneMonitor.cs`
- `Audio/WasapiLocalAudioMonitorBackend.cs`
- `Audio/AudioSamplePeakMeter.cs`
- `ConfiguratorUI/`

## Endpoint persistence

Configuration stores stable Windows endpoint IDs, not display names. The value `default` means the Windows default communications device. If a configured device is inactive at startup, Party voice falls back to the default communications device and records that fallback in diagnostics.

The Reloaded configurator and in-game settings both enumerate active capture/render endpoints. A disconnected saved endpoint remains visible long enough for the user to recognize and replace it.

Party endpoint changes take effect after mod restart because the owned ChatControl is configured during session creation. The local self-test applies device changes immediately by replacing its monitor instance.

## Local self-test flow

```text
settings test button held
  -> LocalMicrophoneMonitor requests a generation
  -> background reconciliation creates WASAPI backend
  -> shared-mode capture feeds a bounded gated buffer
  -> selected shared-mode output plays the buffer locally
  -> AudioSamplePeakMeter publishes a normalized peak
release / suspend / dispose
  -> playback gate closes synchronously
  -> endpoint stop and disposal continue on a background thread
```

The self-test supports PCM 8/16/24/32-bit and IEEE float 32/64-bit formats, including WAVEFORMATEXTENSIBLE subformats. A peak above `0.01` changes the UI to signal detected.

## Why the local test is separate

NAudio never sends online voice. Keeping the self-test separate means selecting or testing a device cannot inject a second capture stream into Party voice. It also lets a user verify microphone and speaker routing without requiring another player.

## Release behavior

Release builds expose the microphone, speaker, input gain, monitor volume, and test controls in the in-game settings UI. Input gain is clamped to `0.0-2.0`; monitor playback is clamped to `0.0-0.5` to avoid unexpectedly loud loopback.

The moment the test button is released, the gated buffer returns silence even if a Windows audio endpoint is slow to stop. NAudio stop/dispose operations never run on DirectInput, WndProc, or Present threads. A cleanup watchdog logs slow teardown without keeping audio audible or blocking the next test.
