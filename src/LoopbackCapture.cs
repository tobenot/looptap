using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LoopTap
{
    sealed class WavWriter : IDisposable
    {
        readonly FileStream _fs;
        readonly int _channels;
        readonly int _rate;
        readonly int _bits;
        readonly int _blockAlign;
        readonly int _channelMask;
        long _dataBytes;
        long _patchedAt;
        bool _closed;

        public long DataBytes { get { return _dataBytes; } }

        public WavWriter(string path, int channels, int rate, int bits, int blockAlign, int channelMask)
        {
            _channels = channels;
            _rate = rate;
            _bits = bits;
            _blockAlign = blockAlign;
            _channelMask = channelMask;
            _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            byte[] header = BuildHeader(0);
            _fs.Write(header, 0, header.Length);
        }

        public void Write(byte[] data, int count)
        {
            if (count <= 0) return;
            // ponytail: WAV 块大小是 32 位，单文件约 4GB。更长的录音要换 RF64。
            if (_dataBytes + count > 0xFFFFFFF0L)
                throw new InvalidOperationException("录音超过 WAV 单文件上限（约 4GB），已停止。");
            try
            {
                _fs.Write(data, 0, count);
            }
            catch (IOException)
            {
                try { FitStream(_fs, Header(_channels, _rate, _bits, _blockAlign, _channelMask, _dataBytes)); }
                catch { }
                throw new InvalidOperationException("磁盘已满，录音停在最后一个完整位置。已经写入的部分仍是合法 WAV。");
            }
            _dataBytes += count;
            if (_dataBytes - _patchedAt >= (long)_rate * _blockAlign)
                Patch();
        }

        public void Finish()
        {
            if (_closed) return;
            Patch();
            _fs.Flush();
        }

        void Patch()
        {
            long pos = _fs.Position;
            byte[] header = BuildHeader(_dataBytes);
            _fs.Position = 0;
            _fs.Write(header, 0, header.Length);
            _fs.Position = pos;
            _patchedAt = _dataBytes;
        }

        byte[] BuildHeader(long dataBytes)
        {
            return Header(_channels, _rate, _bits, _blockAlign, _channelMask, dataBytes);
        }

        public static void FitStream(FileStream fs, byte[] header)
        {
            uint data = BitConverter.ToUInt32(header, 64);
            fs.SetLength(header.Length + data);
            fs.Position = 0;
            fs.Write(header, 0, header.Length);
            fs.Flush();
        }

        public static void SelfCheck()
        {
            string path = Path.Combine(Path.GetTempPath(), "looptap-wavfit-selfcheck.wav");
            try
            {
                byte[] pcm = new byte[160];
                using (WavWriter w = new WavWriter(path, 2, 48000, 32, 8, 3))
                {
                    w.Write(pcm, pcm.Length);
                    w.Finish();
                }
                using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    byte[] junk = new byte[64];
                    for (int i = 0; i < junk.Length; i++) junk[i] = 0xFF;
                    fs.Write(junk, 0, junk.Length);
                }
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    FitStream(fs, Header(2, 48000, 32, 8, 3, pcm.Length));
                WavInfo info = WavCut.Open(path);
                if (info.Frames != 20 || info.DataBytes != pcm.Length)
                    throw new InvalidOperationException("磁盘满时收回的 WAV 长度不对。");
                if (new FileInfo(path).Length != 68 + pcm.Length)
                    throw new InvalidOperationException("多余的尾部没有裁掉。");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        public static byte[] Header(int channels, int rate, int bits, int blockAlign, int channelMask, long dataBytes)
        {
            if (dataBytes < 0 || dataBytes > uint.MaxValue)
                throw new InvalidOperationException("WAV 数据长度超出 32 位块大小。");
            uint data = (uint)dataBytes;
            uint byteRate = (uint)rate * (uint)blockAlign;
            int mask = channelMask;
            if (mask == 0 && channels == 2) mask = 3;
            if (mask == 0 && channels == 1) mask = 4;
            // WAVEFORMATEXTENSIBLE + IEEE float。样本本身不改，只把引擎的声道掩码写进文件，ffprobe 才能标成 stereo。
            byte[] b = new byte[68];
            Put(b, 0, "RIFF");
            PutU32(b, 4, 60u + data);
            Put(b, 8, "WAVE");
            Put(b, 12, "fmt ");
            PutU32(b, 16, 40);
            PutU16(b, 20, 0xFFFE);
            PutU16(b, 22, (ushort)channels);
            PutU32(b, 24, (uint)rate);
            PutU32(b, 28, byteRate);
            PutU16(b, 32, (ushort)blockAlign);
            PutU16(b, 34, (ushort)bits);
            PutU16(b, 36, 22);
            PutU16(b, 38, (ushort)bits);
            PutU32(b, 40, (uint)mask);
            PutU32(b, 44, 3);
            PutU16(b, 48, 0);
            PutU16(b, 50, 0x0010);
            b[52] = 0x80;
            b[53] = 0x00;
            b[54] = 0x00;
            b[55] = 0xaa;
            b[56] = 0x00;
            b[57] = 0x38;
            b[58] = 0x9b;
            b[59] = 0x71;
            Put(b, 60, "data");
            PutU32(b, 64, data);
            return b;
        }

        static void Put(byte[] b, int o, string s)
        {
            for (int i = 0; i < s.Length; i++)
                b[o + i] = (byte)s[i];
        }

        static void PutU16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _fs.Dispose();
        }
    }

    sealed class LoopbackRecorder : IDisposable
    {
        readonly int _pid;
        readonly string _path;
        readonly Action<string> _log;
        readonly ManualResetEvent _started = new ManualResetEvent(false);
        volatile bool _stop;
        Thread _thread;
        bool _disposed;

        public volatile bool StartSucceeded;
        public volatile bool Running;
        public volatile bool Failed;
        public volatile bool Cancelled;
        public string Error;
        public string Summary;
        public int SampleRate;
        public int Channels;
        public int Bits;
        public long Frames;
        public long AudibleFrames;
        public float Peak;
        public int DiscCount;
        public string OutputPath;
        readonly object _peakLock = new object();

        public float ReadPeak()
        {
            lock (_peakLock) return Peak;
        }

        public LoopbackRecorder(int pid, string path, Action<string> log)
        {
            _pid = pid;
            _path = path;
            _log = log;
            OutputPath = path;
        }

        public void StartAsync()
        {
            _thread = new Thread(ThreadMain);
            _thread.IsBackground = true;
            _thread.Name = "LoopTapCapture";
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public bool WaitStarted(int milliseconds)
        {
            return _started.WaitOne(milliseconds);
        }

        public void RequestStop()
        {
            _stop = true;
        }

        public void Join(int milliseconds)
        {
            if (_thread != null)
                _thread.Join(milliseconds);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RequestStop();
            Join(2000);
            _started.Close();
        }

        void ThreadMain()
        {
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            AutoResetEvent audioEvent = null;
            WavWriter wav = null;
            IntPtr mix = IntPtr.Zero;
            bool clientStarted = false;
            byte[] buf = null;
            try
            {
                _log("采集线程 " + Thread.CurrentThread.GetApartmentState() + "。目标 PID " + _pid + "，模式 INCLUDE_TARGET_PROCESS_TREE。");
                _log("正在激活进程回环 VAD\\Process_Loopback ……");

                MixFormat fmt = null;
                uint flags = 0;
                bool useEvent = false;
                client = OpenAndInitialize(out fmt, out flags, out useEvent, out mix);

                if (!fmt.IsFloat || fmt.Bits != 32 || fmt.BlockAlign != fmt.Channels * 4)
                {
                    throw new InvalidOperationException(
                        "引擎混音格式不是 32 位 float（" + fmt.Describe() + "）。为避免转换失真，已停止，没有改写成别的格式。");
                }

                SampleRate = fmt.SampleRate;
                Channels = fmt.Channels;
                Bits = fmt.Bits;
                _log("已采用引擎混音格式（GetMixFormat），未指定自定义格式，不做重采样：" + fmt.Describe());

                if (mix != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(mix);
                    mix = IntPtr.Zero;
                }

                uint bufferFrames;
                Check(client.GetBufferSize(out bufferFrames), "GetBufferSize");
                _log("引擎缓冲 " + bufferFrames + " 帧。");

                Guid capId = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
                object svc;
                Check(client.GetService(ref capId, out svc), "GetService(IAudioCaptureClient)");
                capture = svc as IAudioCaptureClient;
                if (capture == null)
                    throw new InvalidOperationException("没有拿到 IAudioCaptureClient。");

                if ((flags & ProcessLoopback.StreamFlagsEventCallback) != 0)
                {
                    audioEvent = new AutoResetEvent(false);
                    Check(client.SetEventHandle(audioEvent.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
                    useEvent = true;
                }

                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                wav = new WavWriter(_path, fmt.Channels, fmt.SampleRate, fmt.Bits, fmt.BlockAlign, fmt.ChannelMask);

                if (_stop)
                {
                    Cancelled = true;
                    _log("已在开始前取消。");
                    return;
                }

                Check(client.Start(), "Start");
                clientStarted = true;
                StartSucceeded = true;
                Running = true;
                _started.Set();
                _log("开始写入 " + _path);

                int block = fmt.BlockAlign;
                long lastLog = Environment.TickCount;
                while (!_stop)
                {
                    if (useEvent)
                        audioEvent.WaitOne(50);
                    else
                        Thread.Sleep(10);
                    long now = Environment.TickCount;
                    if (unchecked(now - lastLog) >= 1000)
                    {
                        lastLog = now;
                        if (!ProcessAlive(_pid))
                            throw new InvalidOperationException("目标进程已退出。");
                        long frames = Interlocked.Read(ref Frames);
                        _log(string.Format("已录 {0:0.0} 秒", frames / (double)fmt.SampleRate));
                    }
                    Drain(capture, wav, block, ref buf);
                }
                Drain(capture, wav, block, ref buf);
            }
            catch (Exception ex)
            {
                Failed = true;
                Error = ProcessIdentity.FailureText(ProcessAlive(_pid), ex.Message);
                _log(Error);
            }
            finally
            {
                if (clientStarted && client != null)
                {
                    try { client.Stop(); } catch { }
                    if (capture != null)
                    {
                        try
                        {
                            int block = Channels > 0 && Bits > 0 ? Channels * (Bits / 8) : 0;
                            if (block > 0 && wav != null)
                                Drain(capture, wav, block, ref buf);
                        }
                        catch (Exception ex)
                        {
                            _log("停止时排空缓冲失败：" + ex.Message);
                        }
                    }
                }
                if (wav != null)
                {
                    try { wav.Finish(); } catch (Exception ex) { _log("写入 WAV 头失败：" + ex.Message); }
                    wav.Dispose();
                    if (Interlocked.Read(ref Frames) == 0)
                    {
                        try { File.Delete(_path); } catch { }
                        if (!Failed)
                            _log("没有采到样本，已删除空文件。");
                    }
                }
                if (mix != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(mix);
                if (capture != null)
                {
                    try { Marshal.ReleaseComObject(capture); } catch { }
                }
                if (client != null)
                {
                    try { Marshal.ReleaseComObject(client); } catch { }
                }
                if (audioEvent != null)
                    audioEvent.Close();

                long framesDone = Interlocked.Read(ref Frames);
                if (Failed)
                {
                    Summary = Error;
                }
                else if (StartSucceeded && framesDone > 0)
                {
                    double sec = SampleRate > 0 ? framesDone / (double)SampleRate : 0;
                    string disc = DiscCount > 0 ? ("，间断 " + DiscCount + " 次（没有补静音）") : "";
                    Summary = string.Format(
                        "录制完成。时长 {0:0.00} 秒，采样率 {1}，声道 {2}，位深 {3}，格式 float，非静音帧 {4}，峰值 {5:0.000}{6}，文件 {7}",
                        sec, SampleRate, Channels, Bits, Interlocked.Read(ref AudibleFrames), ReadPeak(), disc, OutputPath);
                    _log(Summary);
                    _log(string.Format(
                        "验收 采样率={0} 声道={1} 位深={2} 格式=float 编码=pcm_f32le 帧数={3}",
                        SampleRate, Channels, Bits, framesDone));
                }
                else if (Cancelled)
                {
                    Summary = "已取消。";
                }

                Running = false;
                _started.Set();
            }
        }

        IAudioClient OpenAndInitialize(out MixFormat fmt, out uint flags, out bool useEvent, out IntPtr mix)
        {
            fmt = null;
            flags = 0;
            useEvent = false;
            mix = IntPtr.Zero;

            IAudioClient client = null;
            try
            {
                client = ProcessLoopback.Activate(_pid);
                mix = EngineMix.Obtain(client, _log, out fmt);
            }
            catch
            {
                if (client != null)
                {
                    try { Marshal.ReleaseComObject(client); } catch { }
                }
                throw;
            }

            uint[] flagTry = new uint[]
            {
                ProcessLoopback.StreamFlagsLoopback | ProcessLoopback.StreamFlagsEventCallback,
                ProcessLoopback.StreamFlagsEventCallback
            };
            string[] flagName = new string[] { "LOOPBACK|EVENTCALLBACK", "EVENTCALLBACK" };
            long[] buffers = new long[] { 200000L, 0L };
            string errors = null;

            for (int fi = 0; fi < flagTry.Length; fi++)
            {
                for (int bi = 0; bi < buffers.Length; bi++)
                {
                    if (client == null)
                        client = ProcessLoopback.Activate(_pid);

                    // 格式指针原样传入。不设置 AUTOCONVERTPCM / SRC_DEFAULT_QUALITY，避免引擎重采样。
                    int hr = client.Initialize(0, flagTry[fi], buffers[bi], 0, mix, IntPtr.Zero);
                    if (hr >= 0)
                    {
                        flags = flagTry[fi];
                        useEvent = (flags & ProcessLoopback.StreamFlagsEventCallback) != 0;
                        _log("IAudioClient.Initialize 成功，标志 " + flagName[fi]
                            + "，缓冲 " + buffers[bi] + "（100 纳秒）。未启用自动转换。");
                        return client;
                    }

                    string one = "Initialize " + flagName[fi] + " / " + buffers[bi] + " 失败：" + AudioErrors.Explain(hr);
                    _log(one + "。仍使用同一混音格式重试。");
                    errors = one;
                    try { Marshal.ReleaseComObject(client); } catch { }
                    client = null;
                }
            }

            throw new InvalidOperationException(errors ?? "Initialize 失败。");
        }

        void Drain(IAudioCaptureClient capture, WavWriter wav, int blockAlign, ref byte[] buf)
        {
            while (true)
            {
                uint packet;
                int hr = capture.GetNextPacketSize(out packet);
                if (hr < 0)
                    throw ProcessLoopback.Fail("GetNextPacketSize", hr);
                if (packet == 0)
                    return;

                IntPtr data;
                uint frames;
                uint dwFlags;
                ulong devPos;
                ulong qpc;
                hr = capture.GetBuffer(out data, out frames, out dwFlags, out devPos, out qpc);
                if (hr < 0)
                    throw ProcessLoopback.Fail("GetBuffer", hr);
                try
                {
                    int bytes = checked((int)frames * blockAlign);
                    if (buf == null || buf.Length < bytes)
                        buf = new byte[bytes];
                    bool silent = (dwFlags & 2) != 0 || data == IntPtr.Zero;
                    if (silent)
                    {
                        Array.Clear(buf, 0, bytes);
                    }
                    else
                    {
                        Marshal.Copy(data, buf, 0, bytes);
                        UpdatePeak(buf, bytes);
                        Interlocked.Add(ref AudibleFrames, frames);
                    }
                    if ((dwFlags & 1) != 0)
                        NoteDiscontinuity();
                    wav.Write(buf, bytes);
                    Interlocked.Add(ref Frames, frames);
                }
                finally
                {
                    int rel = capture.ReleaseBuffer(frames);
                    if (rel < 0)
                        throw ProcessLoopback.Fail("ReleaseBuffer", rel);
                }
            }
        }

        bool _loggedDisc;

        void NoteDiscontinuity()
        {
            DiscCount++;
            if (_loggedDisc) return;
            _loggedDisc = true;
            _log("音频不连续。文件按拿到的样本拼接，不补静音。");
        }

        void UpdatePeak(byte[] data, int bytes)
        {
            float peak = ReadPeak();
            for (int i = 0; i + 4 <= bytes; i += 4)
            {
                float s = BitConverter.ToSingle(data, i);
                if (float.IsNaN(s)) continue;
                if (s < 0) s = -s;
                if (s > peak) peak = s;
            }
            lock (_peakLock) Peak = peak;
        }

        static bool ProcessAlive(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch
            {
                return true;
            }
        }

        static void Check(int hr, string action)
        {
            if (hr < 0)
                throw ProcessLoopback.Fail(action, hr);
        }
    }
}
