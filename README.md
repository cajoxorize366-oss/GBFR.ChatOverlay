# GBFR Chat Overlay

一个面向《碧蓝幻想 Relink》PC 版的 MMO 风格聊天框 Mod。目标是在不打断普通操作的情况下保留队友聊天记录，通过热键快速输入文字，并复用游戏现有 PlayFab Party 会话提供按键说话语音。

## 当前阶段

项目目前提供原生文字聊天桥和实验性的 Stage 3 双端实时语音测试。仓库包含 Reloaded-II Mod 骨架、聊天记录与输入状态机、DirectX 11 ImGui 窗口、Relink 2.0.2 的原生文字聊天收发桥，以及连接现有 PartyNetwork 的 ChatControl。按 `Y` 打开输入框后，Enter 会调用游戏自己的 `ui::hud::Manager::sendMessage` 路径；收到的自由文字消息会由 `rpcMessage` Hook 复制到聊天记录。按住 `I` 可把所选麦克风只回放到本机所选播放设备；双方安装相同测试包后，按住 `U` 会直接采集所选 Windows 麦克风，把它转换为 Party 要求的 24 kHz 单声道浮点音频，并送入官方 capture sink 供队友接收。

当前验证进度：

1. 已用主机/客机日志确认现有 Party manager、认证、网络和 endpoint 生命周期。
2. 已确认双方 muted ChatControl 的创建、连接、远端发现和退出房间前清理事件。
3. 当前测试包只向同一 PartyNetwork 中检测到的 Mod ChatControl 授予麦克风收发权限；创建 ChatControl 时先配置并验证官方 audio-manipulation capture sink。输入默认静音，仅在按住 `U`、Windows 采集成功且 Party 回读确认解除静音后，状态栏才显示“正在语音”；随后由单调时钟严格限制为每 40 ms 最多提交一帧，采集积压也不会追赶式突发提交。
4. Reloaded-II 配置会动态列出当前 Windows 活跃的录音和播放端点，可分别选择麦克风与耳机/扬声器；配置保存稳定 endpoint ID，失效选择会在启动时记录日志并回退到 Windows 默认通信设备。`I` 本地监听复用这两个选择，默认 35% 音量并硬性限制在 50% 以内。
5. `I` 和 `U` 互斥，`U` 优先；如果 `U` 打断了正在按住的 `I`，必须松开后再按 `I` 才会重新监听。聊天框顶部会显示本地监听、检测到输入信号、等待房间、等待队友、已就绪、正在语音、断开和 fail-closed 状态。
6. 语音调试包会记录 capture sink 配置与格式、本地已提交帧数/时长/峰值、可恢复队列背压丢帧、输出状态、双方 ChatIndicator、权限回读、接收静音与渲染音量，并在退出时给出按远端成员隔离的判定摘要。Party `0x10D8` 表示暂时没有队列空间；该帧会被丢弃并在下一个 40 ms 节拍恢复，不再错误销毁 ChatControl。本地提交成功只证明音频进入 Party 发送路径；真正的联机通过仍要求对端出现 `remoteIndicator=Talking` 并实际听到声音。手柄按键与成员音量/静音仍待后续实现。

当前版本不会构造或修改游戏网络包，也不会尝试绕过任何联机保护。Stage 3 只复用游戏已经认证的 local user、PartyNetwork 和 local device，使用 Party 自带的 ChatControl 与官方 `PartyAudioManipulationSinkStreamSubmitBuffer` 语音路径，并严格只设置 `SendMicrophoneAudio | ReceiveMicrophoneAudio`（`0x0005`）。松键会先同步关闭提交门，再异步释放麦克风并恢复 Party 静音；输入心跳超时、暂停和退出会话同样 fail-closed。所有原生功能只在 SHA-256 和唯一特征码匹配已验证的 Relink 2.0.2/Party 1.10.12 时启用，否则保持禁用。

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
- Relink 桥接层负责调用游戏原生聊天发送函数并观察接收消息。
- `GBFR.ChatOverlay.ConfiguratorUI.dll` 只在 Reloaded-II 启动器中提供麦克风/播放设备 ComboBox；游戏侧主 DLL 不引用 HandyControl 或 WPF。
- `I` 使用独立的 NAudio/WASAPI 共享模式本地路径，不申请 Party 权限、不连接网络，也不改变 `U` 的 ChatControl 路由。建议戴耳机测试，避免扬声器到麦克风形成声反馈。
- `U` 另开一次只在按住期间存活的 WASAPI 共享模式采集，将常见 PCM/IEEE-float 麦克风格式下混并重采样为 Party Windows capture sink 要求的 24 kHz、单声道、float32、40 ms 帧。发送线程使用单调节拍且迟到后重新起算，绝不把积压帧瞬间灌入 Party 的 200 ms 队列；播放仍由 Party 和所选 Windows 输出设备负责。
- Party 语音接入不得自行消费宿主的全局状态队列，也不得阻塞游戏渲染线程。
- 游戏版本或签名不匹配时，桥接功能应保持禁用，而不是尝试调用未知地址。

## Git 约定

使用小而可验证的提交：项目配置、聊天核心、渲染接入、游戏 Hook 和语音功能分别提交。提交前至少运行一次对应的构建或测试。
