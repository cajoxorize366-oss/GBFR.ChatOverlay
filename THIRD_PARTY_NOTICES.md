# Third-party notices

This project consumes the following packages without vendoring or modifying their source:

- [Reloaded.Imgui.Hook](https://github.com/Sewer56/Reloaded.Imgui.Hook) and its Direct3D 11 backend. The source repository contains an MIT license; the NuGet package metadata for the core package currently identifies LGPL-3.0-only. Binary distributions must preserve the license files and notices supplied by the packages.
- [DearImguiSharp](https://github.com/Sewer56/DearImguiSharp), which wraps cimgui and Dear ImGui.
- [Dear ImGui](https://github.com/ocornut/imgui), distributed under the MIT license.
- Reloaded-II interfaces and hooks from the [Reloaded Project](https://github.com/Reloaded-Project).

The Nenkai [Relink Modding Tools / Overlay](https://github.com/Nenkai/gbfr.utility.modtools) was consulted to validate the Relink-specific DirectX 11 and DirectInput integration approach. No source file from that project is vendored here.
