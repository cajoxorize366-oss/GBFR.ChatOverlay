# STT debug package

`0.2.2-debug.1` 是用于定位语音识别异常的本地验证包。它不会改变 Whisper 的识别策略，但会记录实际使用的麦克风、输入音频指标和 whisper.cpp 原始输出，让“选错设备 / 没录到声音 / 音量异常 / 模型识别错误”可以分开判断。

## 隐私提醒

`Voice Diagnostics` 在这个 debug 包中默认开启。开启后会把原始麦克风录音、转码录音、完整转写结果、设备名称与 endpoint ID、Whisper stdout/stderr 和本机绝对路径保存在磁盘上。目录不会自动清理，并会随测试次数持续增长。

发送调试目录给别人以前，请先试听录音并检查日志。测试完成后可关闭 `Voice Diagnostics`、重启 Mod，并在 worker 停止后手动删除旧 session。

## 选择麦克风

1. 保持 `Voice Microphone = default` 和 `Voice Diagnostics = true`，启动一次 Mod。
2. 打开配置目录旁的 `STT-Debug\microphones.json`。通常位置为：

   ```text
   <Reloaded-II>\User\Mods\gbfr.qol.chatoverlay\STT-Debug\microphones.json
   ```

3. 找到想使用的设备，把它的完整 `name` 或 `id` 复制到 Reloaded-II 配置里的 `Voice Microphone`。
4. 保存配置并重启 Mod。voice worker 的麦克风、语言、线程和诊断设置都只在重启后重新载入。
5. 录一段语音，然后查看最新 session 的 `request-*-audio.json`，确认 `device.name` / `device.id` 是目标设备，且 `usedFallback` 为 `false`。

名称和 ID 都不区分大小写；唯一的部分名称也能匹配。名称重复、部分名称匹配多个设备或设备已失效时，worker 会回退到 Windows 默认输入，并在 `debug.log` 记录 warning。名称重复时应使用 endpoint ID。

这个版本在 Reloaded-II 配置页使用文本字段，而不是动态下拉框。设备列表来自 Windows 运行时枚举，后续可在 ImGui 或自定义 Configurator 中升级为可刷新下拉列表。

## 调试目录内容

每次 worker 启动都会建立一个 `session-日期-时间-PID-GUID` 目录：

- `microphones.json`：当前所有活动录音端点及 Windows 默认端点；位于 `STT-Debug` 根目录，每次启动更新。
- `session.json`：本次语言、麦克风选择器、线程数、模型哈希和运行路径。
- `debug.log`：设备选择、录音格式、耗时、音量指标、fallback warning 和异常。
- `capture-*.raw.wav`：设备原始 WASAPI 格式录音。
- `capture-*.wav`：送入 Whisper 的 16 kHz、单声道、PCM16 录音。
- `request-*-audio.json`：实际设备、格式、时长、peak、RMS、静音率、削波率，以及 `likelySilent` / `likelyClipping`。
- `request-*.whisper-command.txt`：本次 whisper.cpp 参数。
- `request-*.whisper-stdout.log` / `request-*.whisper-stderr.log`：完整 CLI 输出。
- `request-*.whisper.json` / `.txt`：whisper.cpp 的 JSON 与纯文本结果。
- `request-*-whisper-process.json`：退出码及输出规模。
- `request-*-result.json`：最终交给聊天草稿的文字和耗时。

如果首选目录不可写，worker 会尝试 `%LOCALAPPDATA%\GBFR.ChatOverlay\STT-Debug`，再由日志给出实际路径。所有候选目录都不可写时，voice worker 会启动失败。

## 快速判断

- `likelySilent = true`、`peak` 接近 `0`：优先检查是否选中了虚拟麦克风、静音设备或过低的 Windows 输入音量。静音上出现正常句子通常是 Whisper 幻听，不代表采集成功。
- `likelyClipping = true`：输入电平过高，降低 Windows 麦克风音量再测。
- WAV 中人声清楚、设备正确，但文本仍错：保留这一整个 session；它才是分析模型、语言设置或前处理问题的有效样本。
- `usedFallback = true`：配置值未唯一命中当前设备，按 `warning` 修正名称或改用 endpoint ID。

关闭 `Voice Diagnostics` 并重启后，worker 仍会使用临时 scratch 文件完成识别，但会在请求结束时清理音频与 Whisper 输出；设备枚举仍会出现在 Reloaded-II 日志中。
