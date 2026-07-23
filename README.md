# GBFR Chat Overlay

一个面向《碧蓝幻想 Relink》PC 版的 MMO 风格聊天框 Mod。目标是在不打断普通操作的情况下保留队友聊天记录，并通过一个可配置热键快速输入文字；后续阶段会加入手柄按住说话的语音转文字。

## 当前阶段

项目处于原生聊天桥与本地语音输入的实机验证阶段。目前仓库包含 Reloaded-II Mod 骨架、聊天记录与输入状态机、DirectX 11 ImGui 窗口、Relink 2.0.2 的原生文字聊天收发桥，以及隔离进程运行的 Whisper `base` 多语言语音识别。按 `Y` 打开输入框后，Enter 会调用游戏自己的 `ui::hud::Manager::sendMessage` 路径；按住 `U` 或手柄 `LB + R3` 可以录音，松开后识别结果会进入可编辑草稿。语音语言默认固定为中文，也可在配置列表中选择日语、英语、韩语或自动检测。

下一步依次完成：

1. 实机验证键盘 `U` 与 XInput 手柄 `LB + R3` 的录音、取消和中英日识别。
2. 根据实测延迟决定是否把每次启动 CLI 升级为常驻 whisper.cpp 推理服务。
3. 解析原版哈希快捷消息，并用玩家表补全稳定的发送者名称。

当前版本不会构造或修改网络包，也不会尝试绕过任何联机保护。原生桥只在 SHA-256 和唯一特征码都匹配已验证的 Relink 2.0.2 可执行文件时启用；否则自动退回本地预览。

第三方组件及许可证说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

Relink 2.0.2 的实机检查见 [docs/SMOKE_TEST.md](docs/SMOKE_TEST.md)，语音验证见 [docs/STT_VALIDATION.md](docs/STT_VALIDATION.md)，聊天收发逆向边界见 [docs/CHAT_BRIDGE.md](docs/CHAT_BRIDGE.md)。

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
- 麦克风采集与 Whisper 推理运行在独立 worker 进程；默认强制 CPU 推理，不与 Relink 争抢显存。
- 游戏版本或签名不匹配时，桥接功能应保持禁用，而不是尝试调用未知地址。

## Git 约定

使用小而可验证的提交：项目配置、聊天核心、渲染接入、游戏 Hook 和语音功能分别提交。提交前至少运行一次对应的构建或测试。
