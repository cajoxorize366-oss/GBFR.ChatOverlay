# Third-party notices

This project consumes the following packages:

- [Reloaded.Imgui.Hook](https://github.com/Sewer56/Reloaded.Imgui.Hook) and its official Direct3D 11 backend. `Overlay/CjkConfiguredDx11Hook.cs` wraps that backend to configure and build a pinned CJK font atlas before native DX11 initialization, following the frontend path already validated by this project's Extra Sigil companion Mod. The source repository contains an MIT license, reproduced at `licenses/Reloaded.Imgui.Hook-LICENSE.md`; the NuGet package metadata for the core package currently identifies LGPL-3.0-only.
- [DearImguiSharp](https://github.com/Sewer56/DearImguiSharp), which wraps cimgui and Dear ImGui.
- [Dear ImGui](https://github.com/ocornut/imgui), distributed under the MIT license.
- Reloaded-II interfaces and hooks from the [Reloaded Project](https://github.com/Reloaded-Project).
- [HandyControl](https://github.com/HandyOrg/HandyControl) 3.3.0, under the MIT license, is used only as the compile-time API for Reloaded-II's launcher-side audio-device ComboBox editors. The Mod package does not redistribute `HandyControl.dll`; it uses the matching copy already supplied by Reloaded-II, and the injected game assembly has no HandyControl or WPF reference.
- [NAudio](https://github.com/naudio/NAudio) (`NAudio.Wasapi` and `NAudio.Core` 2.3.0), under the MIT license, supplies the shared-mode Windows audio capture and playback path used only while the local `I` microphone self-monitor is held. The license is reproduced at `licenses/NAudio-LICENSE.txt`.
- [HidSharp](https://github.com/IntergatedCircuits/HidSharp) 2.6.4, under the Apache License 2.0, supplies the Windows HID transport used to read the Flydigi Vader 5 Pro's C/Z, LM/RM, M1-M4, and Circle buttons. The license is reproduced at `licenses/HidSharp-LICENSE.txt`.
- [SafetyHook](https://github.com/cursey/safetyhook), under the Boost Software License 1.0, supplies the vendored C++23 inline-hook implementation used only after the game-local `DirectInput8Create` import has produced keyboard or mouse COM devices. Its amalgamated source is stored under `NativeBridge/third_party`; the license is reproduced at `licenses/SafetyHook-LICENSE.txt`.
- [Zydis](https://github.com/zyantific/zydis), under the MIT license, is the disassembler bundled with SafetyHook's amalgamated build. Its amalgamated source is stored under `NativeBridge/third_party`; the license is reproduced at `licenses/Zydis-LICENSE.txt`.

The Nenkai [Relink Modding Tools / Overlay](https://github.com/Nenkai/gbfr.utility.modtools) was consulted to validate the Relink-specific DirectX 11 and DirectInput integration approach. No source file from that project is vendored here.
