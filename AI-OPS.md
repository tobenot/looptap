# LoopTap — AI 操作手册

> 给任何接手本仓库的 AI 看的操作手册（我 = 上一任 AI 写给你的，人类用户是仓库所有者 tobenot）。
> 照这份手册干，不要凭通用 Windows 开发经验猜——这里每条都是实测结论或踩坑后定下的规矩。

## 一句话

LoopTap 是 Windows 内录小工具：只录指定进程（通常是浏览器/网易云）的声音，写成样本级 1:1 的 32-bit float WAV，带可视化无损裁剪、循环检测、峰值归一化。**单文件零依赖 exe**，不需要安装任何东西。

## 铁律（每次动手前读一遍）

1. **纯 .NET Framework 4.x**。不是 .NET Core/5+。用系统自带编译器：
   `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
   只准用系统程序集。禁 NuGet、禁联网下载、禁第三方库。
2. **零依赖单 exe**：`bin\LoopTap.exe` 旁边不许出现任何 DLL。所有 WinForms 控件手写代码创建，没有 .designer、没有 .resx。
3. **C# 5**（这个老编译器只支持到 C# 5）：无局部函数、无字符串插值 `$""`、无 null 条件 `?.`、无 `nameof`。写代码时刻记住。
4. **样本级无损语义是立身之本**：采集走引擎 GetMixFormat 原格式（本机 48kHz/立体声/32bit float），裁剪是字节拷贝，归一化是 float 域乘一个常数。**任何情况下不重采样、不改位深、不做实时 AGC、不发明静音样本**。
5. **不写注册表、不改系统设置、不碰默认设备、不弹 UAC**。程序是 `asInvoker`。
6. **中文界面、中文文案、中文注释**。项目内所有 UI 文案中文。
7. 每个改动单元完成**立即编译**（build.bat），不要攒到最后。编译要求零警告（`/warn:4`）。
8. 改文件用原子 Edit：old_string 从行首第一个非空白字符开始，不带前导缩进；找不到就停手报告。

## 项目结构

```
src/
  Program.cs        入口：CLI 参数解析、进程枚举(ProcCatalog)、单实例互斥、
                    录音文件命名(RecordingName: 歌名清洗+时长)、错误文案
  MainForm.cs       主窗口：进程列表(自动刷新2s、出声标黄)、录制控制、
                    实时电平表、输出目录、打开录音/裁剪入口
  TrimForm.cs       裁剪窗：波形面板(WavePanel 自绘)、选区拖拽、试听、
                    循环检测 UI、归一化 UI —— 约1600行，最重
  WavCut.cs         WAV 读写/裁剪/扫描/峰值/归一化/循环检测算法 —— 纯逻辑
  AudioNative.cs    WASAPI COM 接口定义 + 进程回环激活(Process Loopback)
  LoopbackCapture.cs 采集线程 + WavWriter
  WhoPlays.cs       「正在出声」检测(IAudioSessionManager2 + 音量表)
  UiTheme.cs        暗色主题(配色+控件自绘+DWM 暗色标题栏)
  app.manifest      PerMonitorV2 DPI
build.bat           编译脚本
bin/                编译产物 + looptap.ini(输出目录配置) + recordings/(录音)
recordings/         运行时产物，gitignore
```

## 编译

```bat
build.bat
```

唯一命令。零警告才算过。如果报「另一个程序正在使用此文件」= 有 LoopTap.exe 在运行，先 `taskkill /IM LoopTap.exe /F` 或 PowerShell `Stop-Process -Name LoopTap -Force` 再编译。

## 验收协议（每次改动后必做，这是本项目最重要的部分）

1. **build.bat 零警告**
2. **`LoopTap.exe --selfcheck`** 输出 `selfcheck ok`。这是纯逻辑自检（不打开声卡），覆盖：出声归因、裁剪字节拷贝、备份命名、归一化边界、循环检测（合成文件找周期）、文件名清洗、错误文案。**新功能必须往这里加自检**。
3. **真机采集验证**（改采集/音频路径时必须）：
   - 起一个已知电平音源（`test\tone_player.py` 播 440Hz 振幅 0.5），拿 PID 后 `LoopTap.exe --record <pid> <秒> <wav>`，验证峰值 ≈ 0.500
   - 隔离验证：同时起 `test\tone880_player.py`（880Hz），录完 FFT，880Hz 处能量必须为 0
4. **ffprobe 验证**录出文件：`pcm_f32le`、48000、stereo
5. **GUI 注意**：自动化点击进不了 WinForms ListView（合成事件被忽略）。GUI 交互类改动，逻辑读代码验证 + 用户人工验收，不要声称自动点过。

## CLI 一览

```bat
LoopTap.exe                         打开主窗口
LoopTap.exe --list                  列出进程（含正在出声标记）
LoopTap.exe --record <pid> <秒> <wav>  命令行录制（不改名）
LoopTap.exe --trim <wav>            打开裁剪窗
LoopTap.exe --loop <wav>            打印循环检测结果
LoopTap.exe --selfcheck             纯逻辑自检
```

## 关键实现知识点（实测结论，别重踩）

- **进程回环**：`ActivateAudioInterfaceAsync` + `VAD\Process_Loopback` + `INCLUDE_TARGET_PROCESS_TREE`（Chromium 音频在子进程，必须整树）。本机（Win11 26100）上进程回环的 `GetMixFormat` 返回 **E_NOTIMPL**，降级读默认播放设备的 GetMixFormat（这是引擎混音格式，不是自造格式）。
- **回环采集点在引擎混音之后、端点音量之前**（实测）：任务栏主音量、耳机旋钮**不影响**录音电平；音量混音器里该进程的音量**按比例影响**；网页播放器音量也影响。用户策略「浏览器音量开够 + Windows/外设音量随意调低」正确。
- **首遍起播抖动**：浏览器第一遍播放常有 ~20ms 抖动（实测第 1/2 遍互相关 0.9986，第 2/3 遍 1.0000）。所以「选区=一个周期」默认锁中间遍。
- **WAV 头**：WAVE_FORMAT_EXTENSIBLE + IEEE float 子格式，68 字节头（RIFF/fmt 40+cbsize22/data），无 fact chunk。裁剪重建头必须复用 `WavWriter.Header`，别手写。
- **循环检测**：峰值包络(最多8192档/约10ms档) + FFT 自相关 O(n log n)，短于 0.2s 的周期不搜。真机验证过 124.04s 周期、置信度 92%。
- **归一化**：float 域乘一个常数，无损（无量化损失），峰值超目标向下收，全静音拒绝。默认 -1.00 dBFS，范围 -60~+12。
- **单实例**：Mutex `Local\LoopTap`，第二个实例找到已有窗口恢复置前后**静默退出**（不带弹框）。
- **文件名**：`日期时间_歌名_进程名_时长.wav`。歌名从窗口标题清洗（剥平台后缀短表，见 Program.cs Platforms 数组）。日期必须最前（用户要求）。
- **双开保护**、**PID 复用防护**（开始录制核 StartTime）、**磁盘满收回文件尾**、**设备拔出与进程退出文案区分**——这些已有，别在重构时丢掉。

## 开发流程（和人类用户的协作）

- 用户的工作流：派 AI 干活 → AI 改完 → **git 全绿后直接 commit + push**（用户明确要求，git 项目惯例；P4 项目才绝不 submit）。
- 提交前 `git status` 自查：**绝不把运行时产物交进仓库**（`.cursor/`、`bin/`、`recordings/`、`*.wav`、`looptap.ini`、`looptap-crash.log` 都在 .gitignore）。
- 用户会自己开 exe 人工验收 GUI 手感（裁剪拖拽、命名等），AI 只做可验证的引擎层验收。
- 改完汇报：改了什么 + 验证结果 + 已知限制/没实测的东西，诚实标出「只读过代码没实测」的部分。

## 用户偏好备忘

- 「选区=一个周期」锁中间遍（用户 2026-09-30 确认：中间最干净）
- 日期在文件名最前（用户强调「日期很重要」）
- 归一化开关文案「如果不满意音量，试试归一化」（用户想要的口吻）
- 浏览器音量拉满录、外设音量调低听——用户定的策略，实测成立
- 仓库：https://github.com/tobenot/looptap.git（push 走系统 GCM 凭据，非交互模式可用）

## 已知限制（README 也有一份，这里是要记住的）

- 不能按标签/窗口录，只能按进程树（Process Loopback 是进程级的）
- 独占模式/ASIO 声音录不进来（绕开共享引擎）
- 循环内容若自身是重复的两半，可能认成更短周期
- 单文件 WAV 上限约 4GB
- 未签名 exe，别机首次运行有 SmartScreen 提示
