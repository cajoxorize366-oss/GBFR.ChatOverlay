# GBFR Chat Overlay

一个面向《碧蓝幻想 Relink》PC 版的 MMO 风格聊天框 Mod。目标是在不打断普通操作的情况下保留队友聊天记录，并通过一个可配置热键快速输入文字；后续阶段会加入手柄按住说话的语音转文字。

## 当前阶段

项目处于早期原型阶段。目前仓库包含 Reloaded-II Mod 骨架、可单元测试的聊天记录与输入状态机，以及 DirectX 11 ImGui 本地预览窗口。按 `Y` 可以打开输入框，但消息目前只显示在本机，不会发给队友。下一步依次实现：

1. 在 Relink 2.0 中实机验证 Overlay、WndProc/DirectInput 键盘捕获和中文输入法。
2. 原版聊天发送、接收桥接。
3. 手柄语音转文字。

当前版本不会修改网络包，也不会尝试绕过任何联机保护。

第三方组件及许可证说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

Relink 2.0.2 的实机检查见 [docs/SMOKE_TEST.md](docs/SMOKE_TEST.md)，下一阶段的聊天收发逆向边界见 [docs/CHAT_BRIDGE.md](docs/CHAT_BRIDGE.md)。

## 构建

需要 .NET 8 SDK：

```powershell
dotnet build --configuration Debug
dotnet test tests/GBFR.ChatOverlay.Tests/GBFR.ChatOverlay.Tests.csproj
```

如果设置了 `RELOADEDIIMODS` 环境变量，构建产物会复制到对应的 Reloaded-II Mods 目录；否则产物位于 `bin/Debug`。

## 设计边界

- ImGui 负责聊天窗口、文字输入和交互状态。
- Relink 桥接层负责调用游戏原生聊天发送函数并观察接收消息。
- 语音识别必须在后台线程运行，不能阻塞游戏渲染线程。
- 游戏版本或签名不匹配时，桥接功能应保持禁用，而不是尝试调用未知地址。

## Git 约定

使用小而可验证的提交：项目配置、聊天核心、渲染接入、游戏 Hook 和语音功能分别提交。提交前至少运行一次对应的构建或测试。
