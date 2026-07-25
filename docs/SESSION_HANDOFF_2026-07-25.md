# GBFR Chat Overlay 会话交接（2026-07-25）

> 给下一次 Codex 会话：先完整阅读本文，再按“下一会话第一步”继续。不要从头重新逆向，也不要恢复已经撤回的 STT 路线。

## 1. 交接快照

- 仓库：`C:\Users\Kuro\Documents\Codex\2026-07-23\new-chat\outputs\gbfr-chat-overlay`
- GitHub：`https://github.com/cajoxorize366-oss/GBFR.ChatOverlay.git`
- 分支：`main`
- 本文创建前的源码 HEAD：`7c43f74 fix: render IMM32 candidate fallback`
- Mod 版本：`0.3.0-preview.16`
- 已验证游戏：Granblue Fantasy: Relink PC 版 `2.0.2`
- 已验证 Party：游戏自带 `PartyWin.dll`，产品版本 `1.10.12`
- 最新本地完整包（被 `.gitignore` 排除，不会随 Git 推送）：
  `artifacts\GBFR.ChatOverlay-preview16-ime-candidate-fallback-7c43f74-full.zip`
- ZIP SHA-256：`D7EE1398FF38169A3C3A3FCCFE9F120E3E829F3A27DE20C4B0D1B207F45C698F`
- 本文创建前最后一次实测：Release 隔离目录构建成功，`245/245` 测试通过；新会话修改代码后必须重新运行，不能把这个数字当作永久保证。

本文自身会作为 `7c43f74` 之后的独立提交推送。进入新会话后，以 `git log -1 --oneline` 显示的实际 HEAD 为准。

## 2. 用户真正要做的产品

目标是给《碧蓝幻想 Relink》做一个不打断游戏操作的 MMO 风格聊天与语音 Mod：

- 只在真实联机 Party 房间内显示聊天 Overlay；标题、选档、加载、单机城镇不显示。
- 按 `Y` 快速打开自由文字输入，保留队友聊天历史。
- Enter 走游戏原生文字聊天发送函数，接收也从游戏原生 RPC Hook 进入 Overlay。
- 按住 `U` 使用游戏现有 PlayFab Party 会话进行队友语音；松开立即静音。
- 按住 `I` 只在本机监听所选麦克风，用于无需第二位测试者的设备自检。
- 麦克风和播放设备必须在 Reloaded-II 配置中以动态下拉列表选择，且都有显式 `Default (Windows system default)`。
- 中文输入必须保持 UTF-8，不能再把 `我` 变成 `ÎÒ`；搜狗等输入法还必须能看到候选字。
- 一个问题一个提交，验证后再进入下一个问题。

## 3. 已完成的路线与关键决定

### 3.1 文字聊天底座

最初完成了 Reloaded-II Mod 骨架、聊天历史/输入状态机、DX11 ImGui Overlay 和 DirectInput 键盘劫持。之后为 Relink 2.0.2 建立了版本/签名校验：

- `Y` 打开输入框。
- Enter 调用游戏自己的 `ui::hud::Manager::sendMessage` 路径。
- `rpcMessage` Hook 复制收到的自由文字到聊天历史。
- 不构造、不修改游戏网络包。
- 未知 EXE 版本或签名不唯一时原生桥 fail-closed。

### 3.2 STT 路线已经完整撤回

曾经实现过 Whisper base、本地麦克风、语言选择、诊断包和键盘/手柄 PTT，但离线识别质量与资源占用不符合目标。`92b13f1` 已删除整个 STT 实现。

下一会话不要恢复 `Stt/`、`SttWorker/` 或 Whisper 模型。当前语音方向是实时 Party Voice，不是语音转文字。

### 3.3 Steam Voice/WebRTC 被研究后放弃

逆向结果表明 Relink 的实际在线栈是：Steam 登录身份 → PlayFab Authentication/Lobby → PlayFab PartyNetwork。游戏没有可直接复用的 Steam Lobby Voice 通道。WebRTC 会额外引入信令、NAT/Relay 和第二套身份边界。

最终选择：在游戏已经认证并加入的 PartyNetwork 上创建 Party ChatControl，复用 Party 自带的采集、编码、传输和播放。

### 3.4 Party Stage 1/2/3

- Stage 1：只读观察游戏现有 Party 状态队列，捕获 manager/network/user/device/endpoint 生命周期。
- Stage 2：在现有认证用户和现有 PartyNetwork 上创建一个默认静音的本地 ChatControl；双方日志已经验证本地创建、连接、远端发现和退出前清理。
- Stage 3：远端 Mod ChatControl 加入同一网络后，只授予
  `SendMicrophoneAudio | ReceiveMicrophoneAudio`（`0x0005`），`U` 只切换 Party 原生输入静音。

重要历史教训：

- 不要再使用 audio-manipulation capture sink。旧路线把 200 ms sink 填满后持续返回 `0x10D8`，原因是没有消费者排空。
- 生产路径不另开 WASAPI 给 `U` 采集，不提交自定义 PCM，也不调用 `PartyAudioManipulationSinkStreamSubmitBuffer`。
- 若 Party `Audio=Automatic`，完全由 Party 内部线程负责。
- 若 Party `Audio=Manual`，Mod 用独立高优先级线程每 40 ms 只调用一次 `PartyDoWork(Audio)`。
- 绝不调用 `PartySetWorkMode`，绝不由 Mod 驱动 Networking task。
- 用户和双端实机测试已经确认 `U` 的实时声音曾经成功打通；它仍需在后续改动后按双端日志矩阵回归，不能只凭本地 `I` 判定。

### 3.5 `I` 本地自检

`I` 使用独立 NAudio/WASAPI 共享模式，把所选麦克风只回放到本机所选播放设备。它不连接 Party、不授予权限、不发网络数据。

- 默认回放音量 35%，硬上限 50%。
- 松开时先关闭无锁静音门，再在后台清理端点，避免第二次按 `I` 卡在“正在监听”。
- `I` 与 `U` 互斥，`U` 优先；被 `U` 打断的 `I` 必须先松开才能再次启动。

### 3.6 Overlay 联机房间门控

不要再猜标题 UI、存档 UI、加载画面或城镇状态。`184fa0c` 将显示和 `Y/U/I` 热键直接绑定到现有 Party 房间：

1. 同一个 network/local user 的 `AuthenticateLocalUserCompleted` 成功；
2. 同一会话的本地 gameplay endpoint 创建成功；
3. 满足后立刻放行，主机不需要等待客机加入；
4. LeaveNetwork、endpoint/user/network destroyed 或 PartyCleanup 时立即隐藏并释放输入。

这解决了启动画面过早显示 Overlay 的问题。

### 3.7 中文输入与当前未闭环问题

- Preview.12：修复 ANSI/CP936 窗口的 DBCS 提交，把 `WM_IME_CHAR`/`WM_CHAR` 规范化为 UTF-8；`我` 不再变成 `ÎÒ`。
- Preview.15：绑定 Dear ImGui 平台 IME 回调并保留全部候选 UI 标志。实机日志确认回调存在，而且 Windows 原始 `WM_IME_SETCONTEXT` 已经是 `0xC000000F`；继续调位置或 OR 标志不会解决搜狗 Qt 外部候选窗不可见。
- Preview.16：调用 `ImmGetCandidateListW` 读取 `CANDIDATELIST`，把不可变候选快照跨线程发布，在聊天输入框上方直接画 `候选：1.我   [2.窝] ...`。数字键、空格、翻页和提交仍由输入法拥有，不模拟键鼠。

用户在本会话结束前只测试了 Preview.15，反馈仍无外部候选窗；Preview.16 包刚生成，尚未收到实机结果。因此，**下一会话的第一项任务就是接收并判断 Preview.16 的测试结果**。

## 4. 当前运行架构

```text
Mod.cs
├─ RelinkBuildLocator / RelinkChatBridge
│  ├─ 原生 sendMessage
│  └─ rpcMessage 接收 Hook
├─ PartyLifecycleProbe
│  ├─ 旁路观察游戏的 StateChanges
│  ├─ PartyRoomSessionTracker（Overlay 门控）
│  ├─ PartyChatControlCanary（Stage 2/3）
│  └─ PartyAudioWorkPump（仅 Audio=Manual）
├─ LocalMicrophoneMonitor（I，本地 WASAPI）
├─ VoiceInputModeCoordinator（U/I 互斥）
├─ DirectInputKeyboardHook（Y/U/I）
└─ ChatOverlayHost
   ├─ 历史与输入框
   ├─ 在线房间显示门控
   ├─ ANSI/Unicode/DBCS IME 桥
   └─ IMM32 候选列表 fallback
```

关键文件：

| 文件 | 责任 |
| --- | --- |
| `Mod.cs` | 组合根、配置、Hook/音频/Overlay 生命周期 |
| `Core/ChatSession.cs` | 历史、草稿、发送状态机 |
| `Native/RelinkChatBridge.cs` | 游戏原生文字发送与接收 |
| `Native/PartyLifecycleProbe.cs` | 不消费宿主事件的 Party 观察层 |
| `Native/PartyRoomSessionTracker.cs` | 真实在线房间门控 |
| `Native/PartyChatControlCanary.cs` | Stage 2/3 ChatControl、权限、静音与诊断 |
| `Native/PartyAudioWorkPump.cs` | Manual Audio 模式 40 ms DoWork 泵 |
| `Audio/LocalMicrophoneMonitor.cs` | `I` 本地监听 |
| `Audio/VoiceInputModeCoordinator.cs` | `U/I` 互斥和抢占 |
| `ConfiguratorUI/AudioEndpointPropertyEditors.cs` | Reloaded-II 麦克风/播放设备下拉列表 |
| `Overlay/ChatOverlayHost.cs` | ImGui、输入捕获、IME WndProc、候选绘制 |
| `Overlay/Win32ImeCompatibility.cs` | ANSI/Unicode、CP936/DBCS 和默认窗口过程 |
| `Overlay/Win32ImeCandidateReader.cs` | IMM32 候选列表读取 |
| `Overlay/ImeCandidateSnapshot.cs` | 候选缓冲解析、不可变快照与显示文本 |

## 5. 必须保持的安全/并发边界

1. 不初始化第二个 Party manager。
2. 不替换或分发游戏的 `PartyWin.dll`。
3. 不创建第二个 gameplay endpoint。
4. 不调用 `PartyEndpointSendMessage` 承载自定义语音包。
5. 不独立调用 `PartyStartProcessingStateChanges` 消费全局队列。
6. 观察 Hook 必须把原批次原样交还 Relink；原生动作推迟到原始 `PartyFinishProcessingStateChanges` 返回以后。
7. 离房前先静音并销毁本地 ChatControl；Cleanup/暂停/心跳超时都 fail-closed。
8. 原生调用只允许已验证 EXE/Party DLL hash 和唯一签名。
9. 不让阻塞式音频清理或网络工作运行在渲染线程。
10. WndProc/渲染回调中的异常不能越过 native 边界。

## 6. 默认配置和热键

当前默认值：

- `EnableOverlay = true`
- `EnableImeCandidateFallback = true`
- `EnableNativeChatBridge = true`
- `EnablePartyLifecycleProbe = true`
- `EnableMutedPartyChatControlCanary = true`
- `EnableVoiceInput = true`
- 麦克风：`Default (Windows system default)`
- 播放设备：`Default (Windows system default)`
- `MicrophoneSelfMonitorVolume = 0.35`
- Overlay：`560 × 260`，背景透明度 `0.55`

热键：

- `Y`：联机房间内打开文字输入。
- Enter：发送草稿。
- Escape：取消输入并释放捕获。
- `U`：远端 Mod ChatControl 就绪后，按住 Party PTT。
- `I`：按住本地麦克风监听。

## 7. 下一会话第一步：Preview.16 判定树

让用户完整替换 Preview.16 ZIP，不要混用旧 DLL。打开联机房间，按 `Y`，用搜狗输入 `wo`。

### A. Overlay 出现 `候选：1.…`

说明 IMM32 fallback 成功。继续验证：

- 当前选中项有方括号；
- 1–9、0、Space 和翻页仍由搜狗正常处理；
- Escape 后重新打开不会残留旧候选；
- 提交的 `我是` 仍然是 UTF-8 且不重复。

确认后不要再改 IME，转入用户提出的下一个独立功能问题并单独提交。

### B. 日志有 `candidate fallback captured list ...`，但界面没有候选行

候选读取已成功，问题在 Overlay 状态/布局。优先核验：

- `ChatOverlayHost.DrawHistory` 是否为候选行保留高度；
- `DrawImeCandidateFallback` 是否在 `DrawComposer` 内被调用；
- `_imeCandidateSnapshot` 是否被错误地提前清空；
- 配置中的 `Overlay IME Candidate Fallback` 是否为 true。

不要去改 IMM32 P/Invoke 或候选窗口位置。

### C. 日志出现以下任一行

```text
Win32 IME candidate notification did not expose a readable IMM32 list: ...
Win32 IME composition ended without an IMM32 candidate list. ... TSF/Qt UI.
```

说明该搜狗版本可能只通过 TSF/Qt 暴露候选，IMM32 没有可读列表。下一步应先研究并设计 TSF UI Element 路线（`ITfUIElementMgr` / `ITfCandidateListUIElement`），再做一个隔离诊断实现。不要继续重复调整 `WM_IME_SETCONTEXT`、`CFS_CANDIDATEPOS` 或 HWND 位置；Preview.15 已经排除了这些因素。

### D. 崩溃或卡死

收集完整 Reloaded-II 日志和 Windows WER/Application Error，先确定最后一条日志位于 DX11/WndProc、候选解析还是游戏原始窗口过程。所有新的候选读取错误必须 fail-closed，不能跨 unmanaged WndProc。

## 8. 构建、测试与打包

游戏正在运行时会锁住真实 Reloaded-II Mods 目录。不要直接使用用户当前配置的输出目录做 CI 式验证；使用隔离路径：

```powershell
$isolatedMods = Join-Path $env:TEMP "gbfr-chat-overlay-next-session"
dotnet test .\GBFR.ChatOverlay.sln `
  --configuration Release `
  --no-restore `
  -p:RELOADEDIIMODS="$isolatedMods"
```

本文创建前的隔离实测结果为 `245/245`；下一会话应以重新执行该命令的结果为准。

本次改动文件的格式检查可用：

```powershell
dotnet format .\GBFR.ChatOverlay.sln `
  --verify-no-changes `
  --no-restore `
  --include <本次修改的 .cs 文件列表>
git diff --check
```

不要为了顺手通过全仓 `dotnet format` 而改无关文件。当前全仓验证会命中历史格式问题：

- `Overlay/CjkConfiguredDx11Hook.cs` 约第 100–108 行；
- `Template/Startup.cs` 约第 62 行。

打包时使用唯一临时目录，保留完整依赖与 `runtimes`，并把下列文档平铺到 ZIP 根目录：

- `README.md`
- `docs/CHAT_BRIDGE.md`
- `docs/SMOKE_TEST.md`
- `docs/VOICE_TRANSPORT.md`
- `docs/VOICE_TROUBLESHOOTING_MATRIX.md`

打包后至少检查 ZIP 内：主 DLL、ConfiguratorUI DLL、ModConfig 版本、win-x64 cimgui、上述文档、条目数和 SHA-256。

## 9. 当前已知限制与后续功能

- Preview.16 的搜狗候选 fallback 尚待用户第一次实机确认。
- 手柄 PTT 尚未实现；早期 STT 阶段的 XInput 代码已经随 STT 撤回，不能直接假定可复用。
- 尚无按成员的语音音量、静音和 UI 控件。
- 快捷聊天/印章的哈希文本尚未解析，接收桥目前只保留自由文字。
- 每位要参加 Party 语音的玩家都必须安装相同 Mod；原版客户端没有 ChatControl 能力协商。
- Party/游戏版本升级后必须重新验证 hash、ABI 和签名，不能放宽 fail-closed。
- `artifacts/` 约有大量历史测试包并被 Git 忽略；不要把它们整目录提交到 GitHub。

## 10. 验证文档入口

新会话不要靠聊天记录猜流程，按以下顺序阅读：

1. 本文；
2. `README.md`；
3. `docs/SMOKE_TEST.md`；
4. 涉及语音时读 `docs/VOICE_TROUBLESHOOTING_MATRIX.md`；
5. 需要理解联机/Party 边界时读 `docs/VOICE_TRANSPORT.md`；
6. 修改文字收发时读 `docs/CHAT_BRIDGE.md`。

## 11. 完整提交历史（按阶段）

### 基础、Overlay 与原生聊天

```text
57f0268 chore: scaffold Reloaded-II mod
bd8eb2b chore: configure Relink target and local build
d23d236 feat: add tested chat session core
02030c0 feat: add DX11 ImGui chat preview
4690e9e feat: capture DirectInput keyboard while chatting
635fdde docs: add runtime test and chat bridge plan
d4060eb feat: add versioned Relink chat discovery
91cde53 feat: hook Relink native chat send and receive
ab17c50 docs: add native chat bridge validation guide
```

### 已撤回的 STT 试验

```text
61d107e feat: add isolated STT protocol and coordinator
322cd06 feat: add isolated Whisper base STT worker
0f7212a feat: integrate keyboard and controller push-to-talk
6d5983a build: package Whisper base validation release
7b1ff81 feat: add voice language selector with Chinese default
ecb47bb build: release Chinese-default STT validation update
df08944 fix: keep versioned validation archives
49b32cf feat: add selectable microphones and STT diagnostics
2b50f84 docs: prepare STT debug validation release
92b13f1 revert: remove the STT implementation
```

### Party 联机研究、Stage 1/2/3 与实时语音

```text
162d56a docs: map Relink voice transport
e479877 feat: add read-only Party lifecycle probe
f9ac641 chore: enable Party probe for validation
9bd2218 fix: recover missing Reloaded config path
fbfd2b6 fix: guard DX11 resize callbacks
5eff0d4 docs: explain DX11 guard diagnostics
41b0009 feat: add muted Party ChatControl canary
6e44ca4 docs: add stage 2 validation workflow
a1481e3 fix: teardown Party ChatControl before network leave
6deb9f9 feat: add experimental Party voice push to talk
1775a06 feat: add selectable Party audio devices
c98ef28 fix: make system audio default explicit
caf6518 feat: show live Party voice status
459c7ee feat: add one-run Party voice diagnostics
f415e84 feat: add hold-I local microphone monitor
c230a2c fix: make local monitor cleanup non-blocking
58e4b07 feat: bridge Windows microphone into Party voice
b494435 docs: label the Party capture bridge preview
ba5d05a fix: pace Party voice capture submissions
f6fa4c1 fix: reuse Extra Sigil ImGui compatibility path
e72bf83 fix: use Party native microphone for push to talk
9d125fc fix: drive manual Party audio work
```

### 联机门控与 IME

```text
e29d1cf fix: support ANSI game-window IME input
184fa0c fix: gate overlay on online Party rooms
36ee04e fix: restore third-party IME candidate windows
7c43f74 fix: render IMM32 candidate fallback
```

## 12. 新会话建议开场指令

可以把下面这段直接交给下一会话：

```text
请先完整阅读 docs/SESSION_HANDOFF_2026-07-25.md、README.md 和
docs/SMOKE_TEST.md。保持一个问题一个 commit，不恢复 STT，不放宽
Relink/Party 的 fail-closed 边界。当前第一任务是判断用户对 Preview.16
搜狗候选 fallback 的实机结果；按交接文档 A/B/C/D 分支继续。构建和
测试必须把 RELOADEDIIMODS 指向隔离临时目录，避免运行中的游戏锁文件。
```
