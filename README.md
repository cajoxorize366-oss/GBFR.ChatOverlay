# GBFR Chat Overlay

一个面向《碧蓝幻想 Relink》PC 版的 MMO 风格聊天框 Mod。目标是在不打断普通操作的情况下保留队友聊天记录，通过热键快速输入文字，并复用游戏现有 PlayFab Party 会话提供按键说话语音。

## 当前阶段

项目目前提供原生文字聊天桥和实验性的 Stage 3 双端实时语音测试。仓库包含 Reloaded-II Mod 骨架、聊天记录与输入状态机、DirectX 11 ImGui 窗口、Relink 2.0.2 的原生文字聊天收发桥，以及连接现有 PartyNetwork 的 ChatControl。按 `Y` 打开输入框后，Enter 会调用游戏自己的 `ui::hud::Manager::sendMessage` 路径；收到的自由文字消息会由 `rpcMessage` Hook 复制到聊天记录。按住 `I` 可把所选麦克风只回放到本机所选播放设备；双方安装相同测试包后，按住 `U` 会解除 Party 原生所选麦克风的静音，由 Party 自己完成采集、编码、传输与对端播放。

当前验证进度：

1. 已用主机/客机日志确认现有 Party manager、认证、网络和 endpoint 生命周期。
2. 已确认双方 muted ChatControl 的创建、连接、远端发现和退出房间前清理事件。
3. 当前测试包只向同一 PartyNetwork 中检测到的 Mod ChatControl 授予麦克风收发权限。ChatControl 使用 Party 原生音频输入，不配置 audio-manipulation capture stream；输入默认静音，仅在按住 `U` 且 Party 回读确认解除静音后，状态栏才显示“正在语音”。
4. Reloaded-II 配置会动态列出当前 Windows 活跃的录音和播放端点，可分别选择麦克风与耳机/扬声器；配置保存稳定 endpoint ID，失效选择会在启动时记录日志并回退到 Windows 默认通信设备。`I` 本地监听复用这两个选择，默认 35% 音量并硬性限制在 50% 以内。
5. `I` 和 `U` 互斥，`U` 优先；如果 `U` 打断了正在按住的 `I`，必须松开后再按 `I` 才会重新监听。聊天框顶部会显示本地监听、检测到输入信号、等待房间、等待队友、已就绪、正在语音、断开和 fail-closed 状态。
6. 语音调试包会记录原生输入/输出状态、所选设备、静音回读、双方 ChatIndicator、权限回读、接收静音与渲染音量，并在退出时给出按远端成员隔离的判定摘要。真正的联机通过要求讲话端出现 `localIndicator=Talking`、对端出现 `remoteIndicator=Talking`，并实际听到声音。手柄按键与成员音量/静音仍待后续实现。
7. Preview.12 在 ANSI 游戏窗口边界重组并按当前输入法代码页解码 DBCS 提交字符，避免 CP936 的 `我`（`CE D2`）被 ImGui 当作 `ÎÒ`；输入框激活时同时维护 IMM32 组合与候选窗位置，以兼容搜狗等第三方中文输入法。聊天缓冲内部、原生聊天桥和网络收发仍统一使用 UTF-8。
8. Preview.13 在捕获现有 Party manager 后读取 Party 的 `Audio`/`Networking` work mode。`Audio=Automatic` 时完全交给 Party 内部线程；只有 `Audio=Manual` 时才由 Mod 在独立高优先级线程每 40 ms 调用一次 `PartyDoWork(Audio)`，且绝不调用 `PartySetWorkMode`。这修复了 Windows 本地 `I` 自检通过、Party 输入/输出也显示 Initialized，但 `U` 始终停在 `NoAudioInput` 的宿主工作模式断层。

当前版本不会构造或修改游戏网络包，也不会尝试绕过任何联机保护。Stage 3 只复用游戏已经认证的 local user、PartyNetwork 和 local device，使用 Party 自带的 ChatControl 与原生音频设备路径，并严格只设置 `SendMicrophoneAudio | ReceiveMicrophoneAudio`（`0x0005`）。松开 `U` 会恢复 Party 输入静音；输入心跳超时、暂停和退出会话同样 fail-closed。所有原生功能只在 SHA-256 和唯一特征码匹配已验证的 Relink 2.0.2/Party 1.10.12 时启用，否则保持禁用。

第三方组件及许可证说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

Relink 2.0.2 的实机检查见 [docs/SMOKE_TEST.md](docs/SMOKE_TEST.md)，单次双人语音排障步骤与判定矩阵见 [docs/VOICE_TROUBLESHOOTING_MATRIX.md](docs/VOICE_TROUBLESHOOTING_MATRIX.md)，聊天收发逆向边界见 [docs/CHAT_BRIDGE.md](docs/CHAT_BRIDGE.md)，联机与语音传输研究见 [docs/VOICE_TRANSPORT.md](docs/VOICE_TRANSPORT.md)。

## 构建

需要 .NET 8 SDK：

```powershell
dotnet build --configuration Debug
dotnet test tests/GBFR.ChatOverlay.Tests/GBFR.ChatOverlay.Tests.csproj
```

如果设置了 `RELOADEDIIMODS` 环境变量，构建产物会复制到对应的 Reloaded-II Mods 目录；否则产物位于 `bin/Debug`。

## 设计边界

- ImGui 负责聊天窗口、文字输入和交互状态。
- Win32 输入边界负责区分 ANSI/Unicode 窗口、把 `WM_IME_CHAR`/DBCS `WM_CHAR` 规范化为 UTF-8，并仅在输入框激活期间维护输入法上下文与候选窗位置。
- Relink 桥接层负责调用游戏原生聊天发送函数并观察接收消息。
- `GBFR.ChatOverlay.ConfiguratorUI.dll` 只在 Reloaded-II 启动器中提供麦克风/播放设备 ComboBox；游戏侧主 DLL 不引用 HandyControl 或 WPF。
- `I` 使用独立的 NAudio/WASAPI 共享模式本地路径，不申请 Party 权限、不连接网络，也不改变 `U` 的 ChatControl 路由。建议戴耳机测试，避免扬声器到麦克风形成声反馈。
- `U` 不另开 WASAPI 采集，也不创建 audio-manipulation capture stream；它只控制 Party ChatControl 原生所选输入的静音状态。采集、编解码、网络传输和所选输出设备播放均由 Party 负责。
- Party Audio 任务若为 `Automatic`，Mod 不会调用 `PartyDoWork`；若宿主已将它设为 `Manual`，Mod 只补齐官方要求的 40 ms `PartyDoWork(Audio)` 调度。泵会在暂停和 `PartyCleanup` 前同步停止，不会改动进程全局 work mode，也不会驱动 Networking 任务。
- Party 语音接入不得自行消费宿主的全局状态队列，也不得阻塞游戏渲染线程。
- 游戏版本或签名不匹配时，桥接功能应保持禁用，而不是尝试调用未知地址。

## Git 约定

使用小而可验证的提交：项目配置、聊天核心、渲染接入、游戏 Hook 和语音功能分别提交。提交前至少运行一次对应的构建或测试。
