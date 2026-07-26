# GBFR Chat Overlay

一个面向《碧蓝幻想 Relink》PC 版的 MMO 风格聊天框 Mod。目标是在不打断普通操作的情况下保留队友聊天记录，通过热键快速输入文字，并复用游戏现有 PlayFab Party 会话提供按键说话语音。

## 当前阶段

项目目前提供原生文字聊天桥和实验性的 Stage 3 双端实时语音测试。仓库包含 Reloaded-II Mod 骨架、聊天记录与输入状态机、DirectX 11 ImGui 窗口、Relink 2.0.2 的原生文字聊天收发桥，以及连接现有 PartyNetwork 的 ChatControl。按 `Y` 打开输入框后，Enter 会调用游戏自己的 `ui::hud::Manager::sendMessage` 路径；收到的自由文字消息会由 `rpcMessage` Hook 复制到聊天记录。按 `F10` 可打开语音与聊天框设置菜单，选择麦克风/扬声器并运行带实时输入电平的本地自检；双方安装相同测试包后，按住 `U` 会解除 Party 原生所选麦克风的静音，由 Party 自己完成采集、编码、传输与对端播放。

当前验证进度：

1. 已用主机/客机日志确认现有 Party manager、认证、网络和 endpoint 生命周期。
2. 已确认双方 muted ChatControl 的创建、连接、远端发现和退出房间前清理事件。
3. 当前测试包只向同一 PartyNetwork 中检测到的 Mod ChatControl 授予麦克风收发权限。ChatControl 使用 Party 原生音频输入，不配置 audio-manipulation capture stream；输入默认静音，仅在按住 `U` 且 Party 回读确认解除静音后，状态栏才显示“正在语音”。
4. Reloaded-II 配置会动态列出当前 Windows 活跃的录音和播放端点，可分别选择麦克风与耳机/扬声器；配置保存稳定 endpoint ID，失效选择会在启动时记录日志并回退到 Windows 默认通信设备。`I` 本地监听复用这两个选择，默认 35% 音量并硬性限制在 50% 以内。
5. `I` 和 `U` 互斥，`U` 优先；如果 `U` 打断了正在按住的 `I`，必须松开后再按 `I` 才会重新监听。聊天框顶部会显示本地监听、检测到输入信号、等待房间、等待队友、已就绪、正在语音、断开和 fail-closed 状态。
6. 语音调试包会记录原生输入/输出状态、所选设备、静音回读、双方 ChatIndicator、权限回读、接收静音与渲染音量，并在退出时给出按远端成员隔离的判定摘要。真正的联机通过要求讲话端出现 `localIndicator=Talking`、对端出现 `remoteIndicator=Talking`，并实际听到声音。手柄按键与成员音量/静音仍待后续实现。
7. Preview.12 在 ANSI 游戏窗口边界重组并按当前输入法代码页解码 DBCS 提交字符，避免 CP936 的 `我`（`CE D2`）被 ImGui 当作 `ÎÒ`。聊天缓冲内部、原生聊天桥和网络收发仍统一使用 UTF-8。
8. Preview.13 在捕获现有 Party manager 后读取 Party 的 `Audio`/`Networking` work mode。`Audio=Automatic` 时完全交给 Party 内部线程；只有 `Audio=Manual` 时才由 Mod 在独立高优先级线程每 40 ms 调用一次 `PartyDoWork(Audio)`，且绝不调用 `PartySetWorkMode`。这修复了 Windows 本地 `I` 自检通过、Party 输入/输出也显示 Initialized，但 `U` 始终停在 `NoAudioInput` 的宿主工作模式断层。
9. Preview.14 不再猜测 Relink 的标题、选档、加载或城镇 UI 状态。Overlay 生命周期直接绑定游戏现有的 PlayFab Party 联机房间：同一 Network/LocalUser 完成认证并成功创建本地 gameplay endpoint 后显示并开放 `Y/U/I`，不要求远端玩家已经加入；调用 LeaveNetwork、端点/用户/Network 被销毁或 PartyCleanup 后立即隐藏并释放输入。标题、单机城镇和加载流程自然保持关闭，原生聊天 manager 只继续作为发送函数参数。
10. Preview.15 把游戏 HWND 显式绑定到 Dear ImGui 1.88 的标准平台 IME 回调，由它在输入框激活/失活时管理 IMM32 上下文，并用真实文字光标位置驱动组合窗和 `CFS_CANDIDATEPOS` 候选窗；同时为活动 `WM_IME_SETCONTEXT` 保留原标志并开启全部候选窗 UI。实机日志确认平台回调可用且 Windows 原始标志已经是 `0xC000000F`，但搜狗的 Qt 外部候选窗仍可能不可见。
11. Preview.16 不再依赖第三方外部候选窗：在 `IMN_OPEN/CHANGE/SETCANDIDATEPOS` 和 `WM_IME_COMPOSITION` 时读取 `ImmGetCandidateListW`，把不可变候选快照发布给渲染线程，并在聊天输入框上方绘制当前页、选中项和数字键编号。数字键、空格、翻页和最终提交仍完全由输入法处理；可在 Reloaded-II 中关闭 `Overlay IME Candidate Fallback`。候选读取失败会明确区分“没有 IMM32 列表”和“缓冲区损坏”，方便判断输入法是否只暴露 TSF/Qt UI。
12. Preview.17 修复联机自由文字消息把队友显示为 `Player 00000000`～`Player 00000003`：当 RPC 的短 sender label 为空时，先把 sender ID 映射到当前四人联机槽位，再读取游戏 UI 同一份 `member_name`。原生非空标签仍优先；任何签名、槽位、成员状态或 UTF-8 校验失败都会保留稳定的 `Player XXXXXXXX` 回退，不访问或修改网络包。
13. Preview.18 修复候选 fallback 出现后聊天框底部突然多出一行空白：历史区不再为候选硬编码预留 46px，而是用 Dear ImGui 对当前候选文本的实际换行高度精确让位。同一帧的高度计算与绘制共享一份候选快照，短候选不再浪费空间，长候选换行也不会挤压输入框。
14. `0.4.0` 已完成并作为队伍 HUD 语音图标的 release 基线。位置不使用截图坐标或参考分辨率缩放：Mod 通过唯一特征码跟踪游戏实际创建的 `ControllerPlParameterTown` / `ControllerPlParameter01`，读取角色信息区或 HP 槽的活动子节点、尺寸与最终 4×4 UI 变换，再投影到当前 Direct3D viewport。大厅/战斗自动随原生控制器切换，分辨率、超宽、安全区和 HUD 缩放由游戏矩阵决定。Preview.5 以实机只读内存快照纠正战斗节点：`0x250/0x270` 是正常/红血状态的完整 HP 行几何，本地宽 1504、队友宽 816，其右端投影与原生长/短 HP 条终点吻合；`0x370/0x390` 实为未激活的斜线节点，而 `0x3B0/0x3D0/0x3F0` 是局部动画/遮罩纹理，均不再作为位置锚点。Preview.7 按实机反馈把 2560×1440 图标直径调整为约 48px，并在 Preview.6 的位置基础上向血条右侧移动约 12px；正式 0.4.0 再向右移动 8px，最终中心位于血条右端外约 32px。其他分辨率仍随游戏原生 HUD 变换缩放，最终安全限制为 18–64px。Preview.11 保持严格的队伍 HUD 白名单，并把控制器条件收紧为仅接受 `state=2` 的稳定显示阶段；`state=1` 的进入动画不再提前出图标，`state=3` 的退出动画一开始便立即隐藏。对应 HP 行节点仍须处于活动状态，菜单、加载、结算及其他没有完整队伍 HP HUD 的画面默认都不绘制。Preview.12 保留这条白名单，并针对游戏仍在 Full Chain 插画下渲染 HP 条的特例，单独跟踪 `ControllerChainburst`；该控制器处于打开、显示或关闭状态时列入明确黑名单并压掉全部麦克风图标，演出结束后自动恢复。平台图标所在的名字/徽章区不作为锚点。图标空闲仍为 70% Alpha，并额外使用低亮度配色让待机/讲话区别清晰；讲话为 100%。`Voice Indicator Debug: Show All Slots` 默认在 CPU 队伍也显示所有活动 HUD 行；关闭后会 fail-closed 隐藏全部图标，直到远端 ChatControl 与游戏队伍槽的可靠身份映射完成，绝不把 CPU 或原版玩家误标为 Mod 语音成员。
15. `0.5.0-preview.1` 直接移植因子槽（GBFR Extra Sigil Slots）已经实机证明的 RTSS 兼容边界：DX11 后端只安装 `Present` Hook，先解析 RTSS/其他 Overlay 留在入口处的跳板链并挂到链尾；不再安装 `ResizeBuffers` Hook。每帧仅在需要绘制时创建并释放 RTV/BackBuffer，调用下一个原始 `Present` 时进入单独的 x64 native SEH 边界。若该调用发生 `0xC0000005`，当前帧返回失败并在图形回调线程外停用本 Overlay Hook；后续帧回到游戏/既有 Hook 的 Present 路径，同时聊天、图标和输入捕获 fail-closed。这个实现只参考因子槽仓库，不使用 Luma/ReShade 路线。
16. `0.5.0-preview.2` 新增 `F10` Discord 风格设置菜单。菜单打开时按因子槽的成熟边界拦截 Win32、Raw Input 与 DirectInput 键盘/鼠标，关闭后等待物理键和鼠标按钮松开再归还输入。菜单内可即时选择本地自检设备、调节输入增益/回放音量、查看实时输入电平；原 `I` 键不再被 Mod 占用。设置模式还会显示聊天框预览，可拖动顶部移动，并拖动右下角三角标记缩放；尺寸和按可用画面归一化的位置会写回 `Config.json`。
17. `0.5.0-preview.3` 修复设置功能把聊天 Overlay、F10 与可选音频/DirectInput 初始化绑成同一失败域的问题。图形/WndProc Overlay 现在先启动；音频枚举、DirectInput 构造、设备态与 buffered mouse Hook 任一失败都只降级对应功能，不再阻止聊天框或 Win32 F10。语音状态、语音图标和设置窗口的逐帧异常也分别隔离，不能再拖垮基础聊天渲染。

当前版本不会构造或修改游戏网络包，也不会尝试绕过任何联机保护。Stage 3 只复用游戏已经认证的 local user、PartyNetwork 和 local device，使用 Party 自带的 ChatControl 与原生音频设备路径，并严格只设置 `SendMicrophoneAudio | ReceiveMicrophoneAudio`（`0x0005`）。松开 `U` 会恢复 Party 输入静音；输入心跳超时、暂停和退出会话同样 fail-closed。所有原生功能只在 SHA-256 和唯一特征码匹配已验证的 Relink 2.0.2/Party 1.10.12 时启用，否则保持禁用。

第三方组件及许可证说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

Relink 2.0.2 的实机检查见 [docs/SMOKE_TEST.md](docs/SMOKE_TEST.md)，单次双人语音排障步骤与判定矩阵见 [docs/VOICE_TROUBLESHOOTING_MATRIX.md](docs/VOICE_TROUBLESHOOTING_MATRIX.md)，聊天收发逆向边界见 [docs/CHAT_BRIDGE.md](docs/CHAT_BRIDGE.md)，联机与语音传输研究见 [docs/VOICE_TRANSPORT.md](docs/VOICE_TRANSPORT.md)。

## 构建

需要 .NET 8 SDK，以及 Visual Studio 2022 Build Tools 的 MSVC v143/Windows 10 SDK C++ 工作负载（用于构建 x64 Present 边界）：

```powershell
dotnet build --configuration Debug
dotnet test tests/GBFR.ChatOverlay.Tests/GBFR.ChatOverlay.Tests.csproj
```

如果设置了 `RELOADEDIIMODS` 环境变量，构建产物会复制到对应的 Reloaded-II Mods 目录；否则产物位于 `bin/Debug`。

## 设计边界

- ImGui 负责聊天窗口、文字输入和交互状态。
- Win32 输入边界负责区分 ANSI/Unicode 窗口、把 `WM_IME_CHAR`/DBCS `WM_CHAR` 规范化为 UTF-8，并仅在输入框激活期间维护输入法上下文、系统候选窗显示标志与候选窗位置；第三方候选窗不可见时，Overlay 会读取并显示 IMM32 当前候选页。
- Relink 桥接层负责调用游戏原生聊天发送函数、观察接收消息，并从游戏已验证的四人联机成员表解析空 sender label 对应的真实玩家名。
- `GBFR.ChatOverlay.ConfiguratorUI.dll` 只在 Reloaded-II 启动器中提供麦克风/播放设备 ComboBox；游戏侧主 DLL 不引用 HandyControl 或 WPF。
- `I` 使用独立的 NAudio/WASAPI 共享模式本地路径，不申请 Party 权限、不连接网络，也不改变 `U` 的 ChatControl 路由。建议戴耳机测试，避免扬声器到麦克风形成声反馈。
- `U` 不另开 WASAPI 采集，也不创建 audio-manipulation capture stream；它只控制 Party ChatControl 原生所选输入的静音状态。采集、编解码、网络传输和所选输出设备播放均由 Party 负责。
- Party Audio 任务若为 `Automatic`，Mod 不会调用 `PartyDoWork`；若宿主已将它设为 `Manual`，Mod 只补齐官方要求的 40 ms `PartyDoWork(Audio)` 调度。泵会在暂停和 `PartyCleanup` 前同步停止，不会改动进程全局 work mode，也不会驱动 Networking 任务。
- Party 语音接入不得自行消费宿主的全局状态队列，也不得阻塞游戏渲染线程。
- 游戏版本或签名不匹配时，桥接功能应保持禁用，而不是尝试调用未知地址。

## Git 约定

使用小而可验证的提交：项目配置、聊天核心、渲染接入、游戏 Hook 和语音功能分别提交。提交前至少运行一次对应的构建或测试。
