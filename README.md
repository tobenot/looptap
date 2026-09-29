# LoopTap

在 Windows 10 2004 及更新版本上，只录某一个进程（通常是浏览器）正在播放的声音，写成 32 位 float WAV。

不安装虚拟声卡，不加载内核驱动，不需要管理员，不改默认设备、注册表或系统音频设置。采集只走系统自带的进程回环：

- `ActivateAudioInterfaceAsync`
- 设备 `VAD\Process_Loopback`
- 模式 `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`

程序是纯 .NET Framework 4.x，一个 exe，没有 NuGet，也不需要联网。

## 编译

系统自带编译器即可（64 位 Windows）：

```bat
build.bat
```

等价命令：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /platform:x64 /optimize+ /codepage:65001 /utf8output /win32manifest:src\app.manifest /out:bin\LoopTap.exe src\*.cs
```

产物是 `bin\LoopTap.exe`。旁边不需要任何 DLL。

## 使用

1. 运行 `bin\LoopTap.exe`。
2. 列表默认只留有窗口的进程，正在出声的会标黄并排到最上面（只有一个时会自动选中）。浏览器点带网页标题的那一行，没窗口的后台进程不用管。
3. 「保存到」可以改，点「浏览…」。下次打开还是这个文件夹。
4. 点「开始录制」，看时长，再点「停止」。点「打开文件夹」会在资源管理器里定位到刚才的录音；还没录过就打开保存文件夹。
5. 停下来之后会打开波形。开头那段等待（先点了录制、后放的声音）已经留在选区外面，拖两边可以改，试听后再点「保存裁剪」。留下的采样点是原样拷贝。也可以之后再点「裁剪这段」。

命令行（脚本或验收用）：

```bat
LoopTap.exe --list
LoopTap.exe --record <pid> <秒> <wav路径>
LoopTap.exe --trim <wav路径>
```

内部版本低于 19041 时会直接退出，不会去装别的采集方案。

## 保真度边界

采集端把引擎混音格式原样交给 `IAudioClient.Initialize`。不另外构造一套采样率或位深，也不设置 `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM` 或 `AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY`。格式不匹配时初始化会失败，而不是悄悄重采样。写进 WAV 的是这些 32 位 float 样本的原样拷贝（文件格式标签为 IEEE float，`ffprobe` 里是 `pcm_f32le`）。

优先调用进程回环客户端自己的 `GetMixFormat()`。在当前 Windows 11 上，这个调用会返回「未实现」。这时改为读取**默认播放设备**的 `GetMixFormat()`，把拿到的格式指针原样传给进程回环的 `Initialize`。这一步只读引擎格式，不改默认设备，也不自造 `WAVEFORMATEX`。

**样本级 1:1 的天花板**就是这些样本。文件里的每一个样本，等于进程回环从引擎拿到的那个样本。这是用户态进程回环能对齐的上限。它仍然不是「播放器读到的压缩文件里的原始 PCM」：浏览器在把声音送进引擎之前，可能已经解码、重采样、混音或调过音量。那些步骤发生在本工具之前，回环看不到，也补不回来。

裁剪只删掉选区以外的采样点。留下的采样点按原字节拷进文件，不重采样、不改位深。试听用文件里的 fmt 原样播放；设备不接受这个格式时试听会说明失败，保存仍然按原样拷贝。

**文件级 1:1 做不到。** 要和原始媒体逐字节一致，只能去下载或提取原文件。内录解决的是「这个进程现在放出了什么」，不是「源文件是什么」。

在混音格式为 48 kHz、立体声、32-bit float 的机器上，WAV 就是这个格式。如果某台机器的引擎格式不同，文件跟着 `GetMixFormat()` 走，本工具不会强行改成 48000。若引擎交出来的不是 32 位 float，程序会停下来并写明原因，而不是悄悄做位深转换。

录制过程中如果播放设备被拔掉，采集会停。已经写入的部分仍然是合法 WAV。单文件大约 4GB 上限（WAV 块大小是 32 位）。

## 家用 Linux

家用机如果是 Linux，用 PipeWire 一行即可，不必做这套 Windows 进程回环：

```bash
pw-record --target <目标节点> out.wav
```

节点名可以用 `wpctl status` 看。
