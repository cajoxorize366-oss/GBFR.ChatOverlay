# Third-party notices

This project consumes the following packages without vendoring or modifying their source:

- [Reloaded.Imgui.Hook](https://github.com/Sewer56/Reloaded.Imgui.Hook) and its Direct3D 11 backend. The source repository contains an MIT license; the NuGet package metadata for the core package currently identifies LGPL-3.0-only. Binary distributions must preserve the license files and notices supplied by the packages.
- [DearImguiSharp](https://github.com/Sewer56/DearImguiSharp), which wraps cimgui and Dear ImGui.
- [Dear ImGui](https://github.com/ocornut/imgui), distributed under the MIT license.
- Reloaded-II interfaces and hooks from the [Reloaded Project](https://github.com/Reloaded-Project).
- [NAudio 2.2.1](https://github.com/naudio/NAudio), distributed under the MIT license and used only by the isolated Windows microphone worker. See `licenses/NAudio-LICENSE.txt`.
- [OpenAI Whisper](https://github.com/openai/whisper) `base` multilingual model, distributed under the MIT license. See `licenses/OpenAI-Whisper-LICENSE.txt`.
- [whisper.cpp 1.9.1](https://github.com/ggml-org/whisper.cpp) Windows x64 runtime, distributed under the MIT license. See `licenses/whisper.cpp-LICENSE.txt`.

The Nenkai [Relink Modding Tools / Overlay](https://github.com/Nenkai/gbfr.utility.modtools) was consulted to validate the Relink-specific DirectX 11 and DirectInput integration approach. No source file from that project is vendored here.

The runtime preparation script pins SHA-256 for both the whisper.cpp archive and `ggml-base.bin`. The model is not committed to Git; it is downloaded into `SttRuntime/` and verified before use.
