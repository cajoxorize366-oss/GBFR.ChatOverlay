# GBFR Chat Overlay

一个面向《碧蓝幻想 Relink》PC 版的 MMO 风格聊天框 Mod。目标是在不打断普通操作的情况下保留队友聊天记录，通过热键快速输入文字，并复用游戏现有 PlayFab Party 会话提供按键说话语音。

## 当前阶段

项目目前提供原生文字聊天桥和实验性的 Stage 3 双端实时语音测试。仓库包含 Reloaded-II Mod 骨架、聊天记录与输入状态机、DirectX 11 ImGui 窗口、Relink 2.0.4 的原生文字聊天收发桥，以及连接现有 PartyNetwork 的 ChatControl。按 `Y` 打开输入框后，Enter 会调用游戏自己的 `ui::hud::Manager::sendMessage` 路径；收到的自由文字消息会由 `rpcMessage` Hook 复制到聊天记录。按 `F10` 可打开语音与聊天框设置菜单，选择麦克风/扬声器并运行带实时输入电平的本地自检；双方安装相同测试包后，按住 `U` 会解除 Party 原生所选麦克风的静音，由 Party 自己完成采集、编码、传输与对端播放。

当前验证进度：

1. 已用主机/客机日志确认现有 Party manager、认证、网络和 endpoint 生命周期。
2. 已确认双方 muted ChatControl 的创建、连接、远端发现和退出房间前清理事件。
3. 当前测试包只向同一 PartyNetwork 中检测到并完成权限链的远端 ChatControl 授予麦克风收发权限。ChatControl 使用 Party 原生音频输入，不配置 audio-manipulation capture stream；输入默认静音，仅在按住 `U` 且 Party 回读确认解除静音后，状态栏才显示“正在语音”。远端 ChatControl 与 Relink 成员 EntityId 的精确匹配不等同于已认证的 Mod 版本协商。
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
17. `0.5.0-preview.7` 将原生聊天、玩家身份和队伍 HUD 的固定构建配置迁移到 Relink 2.0.3，并对 19 个代码锚点执行同步原字节预检。HUD 构造/析构还会重新计算并核对实际 call 与 vtable 目标，避免结构相同的 UI 控制器误通过。Overlay Broker 同步到 Extra Sigil Slots 0.8.0：失焦、激活和捕获生命周期消息继续交给游戏，鼠标释放不再每帧重复改写 `ClipCursor`，以避免焦点切换闪烁。
18. `0.5.0-preview.8` 修复本地消息被硬编码成 `[房主] You`、Overlay Broker 客户端收不到 `U` 按键说话、按住或输入源抖动导致快捷动作重复发送的问题。发送成功后会等待游戏权威 RPC 回声提供真实玩家名和联机槽位；快捷动作只在物理按下边沿触发。新增 Flydigi Vader 5 Pro HID 支持，可绑定 `C/Z`、`LM/RM`、`M1-M4` 和 `Circle`，且不改变与 Extra Sigil Slots 同步的 Broker ABI。
19. `0.5.0-preview.9` 删除模组自设的快捷动作 2 秒冷却，快速重按直接交给游戏原生发送流程和冷却判定；物理边沿仍保证按住只触发一次。`DPadDown` 作为游戏官方快捷短语保留键，不再允许绑定到模组功能。飞智 HID 只有在第三方接管状态通过并收到 Acquire 成功回应后才接收扩展键，避免 Steam Input/空间站映射与裸 HID 双路触发；必须保留 Steam Input 时，可在飞智空间站把扩展键映射到未占用的 `F13-F21`，再作为键盘热键绑定。
20. `0.5.0-preview.10` 取消单个快捷动作的手柄绑定：快捷动作编辑页只保留键盘键，旧配置里的动作级 `ControllerBinding` 会被忽略且不再序列化。设置菜单、打开聊天、按住说话、快捷动作面板、全局禁言和玩家禁言仍保留截图页中的手柄绑定。
21. `0.5.0-preview.11` 修复设置菜单中“快捷动作 / 自定义文”无法输入中文且 Backspace 无法删除：普通编辑键不再被设置页热键边界提前吞掉，设置页也会复用聊天框现有的 Unicode/CP936 IME 提交、候选与 UTF-8 输入路径；已配置的模组热键在菜单打开时仍只会被拦截，不会误触发。Overlay Broker/Present 同步到 Extra Sigil Slots 0.8.2，并把原生 DirectInput 的松键排水状态反馈给 Host，使 WndProc、DirectInput 与光标捕获在同一中性边界完成释放。
22. `0.5.0-preview.12` 修复 Broker guest 模式下自定义文热键记录为成功但游戏没有实际发出的线程边界错误：WndProc/DirectInput 热键现在只把动作 ID 放入线程安全队列，下一次 Broker Render/Present 回调再统一调用游戏原生聊天或官方动作函数。快捷动作面板按钮仍直接在 Render 线程执行；物理按下边沿和由游戏处理发送冷却的规则保持不变。
23. `0.5.0-preview.13` 修复游戏原生聊天气泡已有消息、模组聊天历史却为空：非联机帧不再提前抽干原生 RPC 接收队列，消息会等到联机房间 gate 恢复后再进入历史；短暂的 endpoint/生命周期重置也不再把已有聊天记录无条件清空。语音栏的“等待队友”仍只表示 Mod 语音握手状态，不参与聊天历史清理。
24. `0.5.0-preview.14` 修复发送者本人只有游戏官方气泡、模组历史没有本地消息：游戏官方聊天输入和模组自定义文在原生发送函数成功返回后立即写入模组接收队列，不再要求 Relink 必须向发送者回送 RPC。同步或迟到的权威回声仍会更新真实姓名与槽位，但会被状态化 echo lifecycle 去重，不会产生第二行。
25. `0.5.0-preview.15` 同步最新主线的 DirectInput 输入状态修复，并把原生聊天、玩家身份、官方快捷动作、队伍 HUD 与大厅房主识别的固定构建配置迁移到 Relink 2.0.4。启动时仍会对全部必要指令和派生目标做同步预检；完整 EXE 哈希只在后台记录为诊断信息。
26. `0.5.0-preview.16` 将中性 OverlayHub/ImGuiHub 与 Extra Sigil Slots 0.8.3 主线重新对齐：关闭期间不再向 ImGui 排队 Win32 输入，重新唤醒首帧会清空陈旧键鼠状态并恢复真实光标位置，快速关闭/重开时的鼠标 reset 请求统一交给 Present 线程，前台 `WM_INPUT` 即使被拦截也会经过系统清理路径。两仓新增自动构建、测试、共享源校验与 ZIP/Release 产出的 GitHub Actions。
27. `0.5.0-preview.17` 新增可选精简模式：联机时平常完全隐藏聊天窗口，按 `Y`（或自定义聊天键）后只显示输入框、IME 候选和必要的发送状态；发送成功或按 `Esc` 后立即收起。`F10` 设置中的完整聊天框预览仍可用于调整位置和尺寸，队伍语音图标也不受精简模式影响。
28. `0.5.0-preview.18` 修复精简模式的设置预览与实际形态不同、输入框上方语音状态缺失的问题；`vo_CMM_chance`、`vo_CMM_win_*`、`vo_CMM_thanks` 不再冒充用户名，而会保留真实玩家身份并标注连携攻击、胜利或感谢。新增房间进入与退出系统提示，退出原因区分主动离开、房主掉线、被踢和网络中断，并报告成功建立的 Party 语音通道人数。
29. `0.5.0-preview.19` 首次阻止 `vo_CMM_*` 机器通信键直接写成本地玩家名，并在接收、入队和历史显示增加 fail-closed 规范化；但它仍把其他普通短字段视作潜在身份，因此没有彻底切断展示字段污染姓名缓存的路径。
30. `0.5.0-preview.20` 尝试修复 Party 身份错认并加入权威本地表与大厅房主绑定，但错误地把聊天包 `+0x18` 直接解释成 `0..3` 槽位、绕开了游戏的 `0x6CD520` 成员键解析器。这条错误假设会让姓名、颜色、禁言、本地回声和房主关系在非零本地索引时交叉错认，已由 Preview.23 的完整反编译推翻。
31. `0.5.0-preview.21` 修复更新后 Party 语音可能一直停在“等待队友”以及 Overlay Broker guest 的 `U` 通路缺少完整心跳保护的问题。本地 Mod ChatControl 加入后会用官方 `PartyNetworkGetChatControls` 对账当前网络，把在本地连接前就已加入的远端 ChatControl 恢复到权限链；查询失败仍保留原生 join 事件回退，不会拆除会话。Broker guest 的键盘按住说话现在按物理状态持续心跳，失焦、主机隔离、暂停、音频设备离开 `Initialized` 或 350 ms 心跳中断都会立即恢复静音，并要求松开后重新按下。
32. `0.5.0-preview.22` 接通此前只用于 debug 定位的队伍语音图标正式回路。每次状态刷新只读取一份一致的 Relink 四人 `EntityId` 快照，把已建立 Party ChatControl、当前发言成员和实际占用槽位映射到原生大厅/战斗 HUD 行；已接通成员低亮显示，讲话成员高亮显示，CPU、没有匹配 ChatControl 的成员、未知身份和不一致布局继续 fail-closed。聊天框总开关关闭或精简模式收起时，语音图标仍可独立渲染；菜单、加载、结算与 Full Chain 的原生 HUD 白名单/黑名单保持不变。
33. `0.5.0-preview.23` 重新反编译普通聊天、自动药水/自动短语、胜利语句和 RPC 回声的完整身份链。`Chat+0x18` 现在先经游戏原生 `0x6CD520` 解析不透明成员键，再查 lobby 成员名；`Chat+0x180` 与 `sendMessage` 第四参数只保留展示/通信提示语义，哪怕内容像 `Djeeta` 或 `trick` 也绝不再成为用户名。本地即时回显固定为 UI 玩家 1，只有成员键证明 RPC 属于本机时才去重，因此队友恰好发送相同文字也不会被吞。每房间前 32 条新增不含聊天正文的归属诊断，方便双端实机核对。
34. `0.5.0-preview.24` 修复双方客户端都把自己标成 `[房主]` 的回归。房主判断不再接受“第一个命中的 `PFLobbyGetOwner` 候选就是本机”这一猜测，而是先跟踪官方 Party 生命周期：创建者正常经历 `CreateNewNetwork` 后再 `ConnectToNetwork`，角色保持 `Created` 并映射为本机 UI 玩家 1；加入者只有 `ConnectToNetwork`，会排除自己的 EntityId，仅在唯一远端 owner 候选与成员表一致时标记房主。角色未知、本机候选、多个候选或成员快照不一致时一律不显示房主标记，并记录不含 EntityId 的角色/房主槽变化诊断。
35. `0.5.0-preview.25` 在不消费游戏 Party 状态队列的前提下观察远端 gameplay endpoint：房间激活后的新 EntityId 会写入聊天历史为成员加入；endpoint 销毁只先缓存 PlayFab Party 的官方 reason，必须等游戏自己的 `PartyFinishProcessingStateChanges` 成功返回，且一致的四人身份快照确认该 EntityId 已消失，才报告主动离开、连接中断、被踢、认证失效或端点创建失败。相同成员的多 endpoint、销毁后重建、快照暂时不可用和旧房间残留都不会提前误报。同时，聊天框顶部语音状态会列出当前正在使用语音的玩家姓名：本机以 Party 接受解除静音且原生静音回读成功为准，远端以 Party `ChatIndicator.Talking` 为准；无人使用时仍显示原有的等待/已就绪状态，队伍 HUD 麦克风图标回路保持独立。
36. `0.5.0-preview.26` 修复结算后按游戏正常流程退出房间却被报告为“网络波动”的回归。反编译确认 Relink 2.0.4 的正常 teardown 会先成功调用 `PartyNetworkLeaveNetwork`，随后再调用 `PartyCleanup`；因此成功排队的官方 LeaveNetwork 现在作为本机主动退出的权威证据。身份快照未知只会让房间名回退为“当前房间”，不会把退出原因升级为网络故障；只有 Party 原生 destroyed/removed 事件明确给出断线原因，或未观察到官方 LeaveNetwork 的异常 teardown，才报告网络中断。

当前版本不会构造或修改游戏网络包，也不会尝试绕过任何联机保护。Stage 3 只复用游戏已经认证的 local user、PartyNetwork 和 local device，使用 Party 自带的 ChatControl 与原生音频设备路径，并严格只设置 `SendMicrophoneAudio | ReceiveMicrophoneAudio`（`0x0005`）。松开 `U` 会恢复 Party 输入静音；输入心跳超时、暂停和退出会话同样 fail-closed。所有原生代码 Hook 只在固定 RVA 的必要原始字节、RIP 相对目标以及 Party 路径/版本/导出全部通过同步预检时启用；完整 EXE/PartyWin SHA-256 在 Hook 安装后于后台计算，仅作为诊断信息。

第三方组件及许可证说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

Relink 2.0.4 的实机检查见 [docs/SMOKE_TEST.md](docs/SMOKE_TEST.md)，单次双人语音排障步骤与判定矩阵见 [docs/VOICE_TROUBLESHOOTING_MATRIX.md](docs/VOICE_TROUBLESHOOTING_MATRIX.md)，聊天收发逆向边界见 [docs/CHAT_BRIDGE.md](docs/CHAT_BRIDGE.md)，联机与语音传输研究见 [docs/VOICE_TRANSPORT.md](docs/VOICE_TRANSPORT.md)。

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
- Relink 桥接层负责调用游戏原生聊天发送函数、观察接收消息，把 RPC 的不透明成员键经游戏原生解析器映射为四人成员索引，再从已验证的 lobby 成员表读取真实玩家名；短展示字段和自动通信正文不参与身份判断。
- `GBFR.ChatOverlay.ConfiguratorUI.dll` 只在 Reloaded-II 启动器中提供麦克风/播放设备 ComboBox；游戏侧主 DLL 不引用 HandyControl 或 WPF。
- `I` 使用独立的 NAudio/WASAPI 共享模式本地路径，不申请 Party 权限、不连接网络，也不改变 `U` 的 ChatControl 路由。建议戴耳机测试，避免扬声器到麦克风形成声反馈。
- `U` 不另开 WASAPI 采集，也不创建 audio-manipulation capture stream；它只控制 Party ChatControl 原生所选输入的静音状态。采集、编解码、网络传输和所选输出设备播放均由 Party 负责。
- Party Audio 任务若为 `Automatic`，Mod 不会调用 `PartyDoWork`；若宿主已将它设为 `Manual`，Mod 只补齐官方要求的 40 ms `PartyDoWork(Audio)` 调度。泵会在暂停和 `PartyCleanup` 前同步停止，不会改动进程全局 work mode，也不会驱动 Networking 任务。
- Party 语音接入不得自行消费宿主的全局状态队列，也不得阻塞游戏渲染线程。
- 游戏版本或签名不匹配时，桥接功能应保持禁用，而不是尝试调用未知地址。

## Git 约定

使用小而可验证的提交：项目配置、聊天核心、渲染接入、游戏 Hook 和语音功能分别提交。提交前至少运行一次对应的构建或测试。
