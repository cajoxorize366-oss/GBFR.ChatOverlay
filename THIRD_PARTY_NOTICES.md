# Third-party notices

This project consumes the following packages:

- [Reloaded.Imgui.Hook](https://github.com/Sewer56/Reloaded.Imgui.Hook) and its Direct3D 11 backend. `Overlay/SafeImguiHookDx11.cs` is adapted from the upstream Direct3D 11 backend at commit `c3a42c84536c0a8480bc5667cb891afae274f5a7`; it adds guarded native callbacks, HRESULT-aware resize handling, and fail-open game rendering. The source repository contains an MIT license, reproduced at `licenses/Reloaded.Imgui.Hook-LICENSE.md`; the NuGet package metadata for the core package currently identifies LGPL-3.0-only.
- [DearImguiSharp](https://github.com/Sewer56/DearImguiSharp), which wraps cimgui and Dear ImGui.
- [Dear ImGui](https://github.com/ocornut/imgui), distributed under the MIT license.
- Reloaded-II interfaces and hooks from the [Reloaded Project](https://github.com/Reloaded-Project).
- [HandyControl](https://github.com/HandyOrg/HandyControl) 3.3.0, under the MIT license, is used only as the compile-time API for Reloaded-II's launcher-side audio-device ComboBox editors. The Mod package does not redistribute `HandyControl.dll`; it uses the matching copy already supplied by Reloaded-II, and the injected game assembly has no HandyControl or WPF reference.
- [NAudio](https://github.com/naudio/NAudio) (`NAudio.Wasapi` and `NAudio.Core` 2.3.0), under the MIT license, supplies the shared-mode Windows audio capture and playback path used only while the local `I` microphone self-monitor is held. The license is reproduced at `licenses/NAudio-LICENSE.txt`.

The Nenkai [Relink Modding Tools / Overlay](https://github.com/Nenkai/gbfr.utility.modtools) was consulted to validate the Relink-specific DirectX 11 and DirectInput integration approach. No source file from that project is vendored here.
