# GBFR Chat Overlay

一个面向《碧蓝幻想 Relink》PC 版的 MMO 风格聊天框 Mod。目标是在不打断普通操作的情况下保留队友聊天记录，通过热键快速输入文字，并复用游戏现有 PlayFab Party 会话提供按键说话语音。

## 当前阶段

项目目前提供原生文字聊天桥和实验性的 Stage 3 双端实时语音测试。仓库包含 Reloaded-II Mod 骨架、聊天记录与输入状态机、DirectX 11 ImGui 窗口、Relink 2.0.2 的原生文字聊天收发桥，以及连接现有 PartyNetwork 的 ChatControl。按 `Y` 打开输入框后，Enter 会调用游戏自己的 `ui::hud::Manager::sendMessage` 路径；收到的自由文字消息会由 `rpcMessage` Hook 复制到聊天记录。双方安装相同测试包后，按住 `U` 说话，松开即静音。

当前验证进度：

1. 已用主机/客机日志确认现有 Party manager、认证、网络和 endpoint 生命周期。
2. 已确认双方 muted ChatControl 的创建、连接、远端发现和退出房间前清理事件。
3. 当前测试包只向同一 PartyNetwork 中检测到的 Mod ChatControl 授予麦克风收发权限；输入默认静音，仅在按住 `U` 时开启。
4. Reloaded-II 配置会动态列出当前 Windows 活跃的录音和播放端点，可分别选择麦克风与耳机/扬声器；配置保存稳定 endpoint ID，失效选择会在启动时记录日志并回退到 Windows 默认通信设备。
5. 手柄按键、成员音量/静音和正式 UI 状态仍待后续实现。

当前版本不会构造或修改游戏网络包，也不会尝试绕过任何联机保护。Stage 3 只复用游戏已经认证的 local user、PartyNetwork 和 local device，使用 Party 自带的 ChatControl 语音通道，并严格只设置 `SendMicrophoneAudio | ReceiveMicrophoneAudio`（`0x0005`）。松键、输入心跳超时、暂停和退出会话都会强制静音；所有原生功能只在 SHA-256 和唯一特征码匹配已验证的 Relink 2.0.2/Party 1.10.12 时启用，否则自动 fail-closed。

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
- `GBFR.ChatOverlay.ConfiguratorUI.dll` 只在 Reloaded-II 启动器中提供麦克风/播放设备 ComboBox；游戏侧主 DLL 不引用 HandyControl 或 WPF。
- Party 语音接入不得自行消费宿主的全局状态队列，也不得阻塞游戏渲染线程。
- 游戏版本或签名不匹配时，桥接功能应保持禁用，而不是尝试调用未知地址。

## Git 约定

使用小而可验证的提交：项目配置、聊天核心、渲染接入、游戏 Hook 和语音功能分别提交。提交前至少运行一次对应的构建或测试。
