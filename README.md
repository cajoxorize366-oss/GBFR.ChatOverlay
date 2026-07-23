# GBFR Chat Overlay

一个面向《碧蓝幻想 Relink》PC 版的 MMO 风格聊天框 Mod。目标是在不打断普通操作的情况下保留队友聊天记录，并通过一个可配置热键快速输入文字；后续阶段将研究复用游戏现有联机层的按键说话语音。

## 当前阶段

项目处于原生聊天桥接完成、联机语音底层研究阶段。目前仓库包含 Reloaded-II Mod 骨架、聊天记录与输入状态机、DirectX 11 ImGui 窗口，以及 Relink 2.0.2 的原生文字聊天收发桥。按 `Y` 打开输入框后，Enter 会调用游戏自己的 `ui::hud::Manager::sendMessage` 路径；收到的自由文字消息会由 `rpcMessage` Hook 复制到聊天记录。

下一步依次完成：

1. 在私密双客户端会话中启用一次 Party 生命周期探针并保存日志。
2. 验证只连接、不授权音频的 muted ChatControl canary。
3. canary 通过后，基于 Party ChatControl 实现按键说话与设备选择。

当前版本不会构造或修改网络包，也不会尝试绕过任何联机保护。原生桥只在 SHA-256 和唯一特征码都匹配已验证的 Relink 2.0.2 可执行文件时启用；否则自动退回本地预览。

第三方组件及许可证说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

Relink 2.0.2 的实机检查见 [docs/SMOKE_TEST.md](docs/SMOKE_TEST.md)，聊天收发逆向边界见 [docs/CHAT_BRIDGE.md](docs/CHAT_BRIDGE.md)，联机与语音传输研究见 [docs/VOICE_TRANSPORT.md](docs/VOICE_TRANSPORT.md)。

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
- Party 语音接入不得自行消费宿主的全局状态队列，也不得阻塞游戏渲染线程。
- 游戏版本或签名不匹配时，桥接功能应保持禁用，而不是尝试调用未知地址。

## Git 约定

使用小而可验证的提交：项目配置、聊天核心、渲染接入、游戏 Hook 和语音功能分别提交。提交前至少运行一次对应的构建或测试。
