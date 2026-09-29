using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LoopTap
{
    sealed class TrimForm : Form
    {
        readonly WavePanel _wave;
        readonly Panel _bar;
        readonly Label _hint;
        readonly Label _detail;
        readonly Button _play;
        readonly Button _align;
        readonly Button _save;
        readonly System.Windows.Forms.Timer _playTimer;
        readonly ManualResetEvent _playDone = new ManualResetEvent(true);
        string _path;
        WavInfo _info;
        WavScan _scan;
        long _start;
        long _end;
        int _scanGen;
        int _peakCols;
        volatile bool _playing;
        long _playFrame = -1;
        bool _justSaved;

        public TrimForm(string path)
        {
            _path = path;
            Text = "裁剪录音";
            StartPosition = FormStartPosition.CenterParent;
            Width = 900;
            Height = 520;
            MinimumSize = new Size(720, 380);
            DoubleBuffered = true;
            try { Font = new Font("Microsoft YaHei UI", 9f); }
            catch { }

            _wave = new WavePanel();
            _wave.RangeChanged += delegate { OnRangeChanged(); };

            _play = new Button();
            _play.Text = "试听选区";
            _play.Left = 8;
            _play.Top = 8;
            _play.Width = 110;
            _play.Height = 30;
            _play.Enabled = false;
            _play.Click += delegate { TogglePlay(); };

            _align = new Button();
            _align.Text = "对齐到有声音处";
            _align.Top = 8;
            _align.Width = 150;
            _align.Height = 30;
            _align.Enabled = false;
            _align.Click += delegate { AlignToSound(); };

            _save = new Button();
            _save.Text = "保存裁剪";
            _save.Top = 8;
            _save.Width = 110;
            _save.Height = 30;
            _save.Enabled = false;
            _save.Click += delegate { SaveCut(); };

            _hint = new Label();
            _hint.AutoSize = false;
            _hint.Left = 8;
            _hint.Top = 44;
            _hint.Height = 36;
            _hint.Text = "正在看波形……";

            _detail = new Label();
            _detail.AutoSize = false;
            _detail.Left = 8;
            _detail.Top = 82;
            _detail.Height = 36;
            _detail.Text = "";

            _bar = new Panel();
            _bar.Height = 126;
            _bar.Controls.Add(_play);
            _bar.Controls.Add(_align);
            _bar.Controls.Add(_save);
            _bar.Controls.Add(_hint);
            _bar.Controls.Add(_detail);
            _bar.Resize += delegate { LayoutBar(); };

            Controls.Add(_wave);
            Controls.Add(_bar);
            Resize += delegate { LayoutForm(); };
            OnResize(EventArgs.Empty);

            _playTimer = new System.Windows.Forms.Timer();
            _playTimer.Interval = 50;
            _playTimer.Tick += delegate
            {
                long frame = Interlocked.Read(ref _playFrame);
                if (!_playing && frame < 0) return;
                _wave.Playhead = frame;
                _wave.Invalidate();
            };
            _playTimer.Start();
        }

        public void LoadFile(string path)
        {
            LoadCore(path, false);
        }

        void LoadCore(string path, bool saved)
        {
            WavInfo info = WavCut.Open(path);
            if (info.Frames <= 0)
                throw new InvalidOperationException("这个录音没有采样点。");
            _playing = false;
            _path = path;
            _info = info;
            _scan = null;
            _justSaved = saved;
            Interlocked.Exchange(ref _playFrame, -1);
            Text = "裁剪录音 — " + Path.GetFileName(path);
            _hint.Text = "正在看波形……";
            _detail.Text = "";
            _play.Enabled = false;
            _align.Enabled = false;
            _save.Enabled = false;
            BeginScan(true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_info != null) return;
            try { LoadCore(_path, false); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "LoopTap");
                Close();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _playing = false;
            _scanGen++;
            base.OnFormClosing(e);
        }

        void LayoutForm()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int barH = _bar.Height;
            int waveH = h - barH;
            if (waveH < 40) waveH = 40;
            _wave.SetBounds(0, 0, w, waveH);
            _bar.SetBounds(0, waveH, w, h - waveH);
            _bar.BringToFront();
            if (_info != null && _scan != null)
            {
                int cols = _wave.ClientSize.Width;
                if (cols >= 32 && Math.Abs(cols - _peakCols) >= 24)
                    BeginScan(false);
            }
        }

        void LayoutBar()
        {
            int w = _bar.ClientSize.Width;
            _save.Left = w - _save.Width - 8;
            _play.Left = 8;
            _align.Left = _play.Right + 8;
            if (_align.Right > _save.Left - 8)
                _align.Left = _save.Left - _align.Width - 8;
            _hint.Width = w - 16;
            _detail.Width = w - 16;
        }

        void BeginScan(bool snap)
        {
            if (_info == null) return;
            int cols = _wave.ClientSize.Width;
            if (cols < 32) cols = 800;
            int gen = ++_scanGen;
            bool doSnap = snap;
            WavInfo info = _info;
            Thread t = new Thread(new ThreadStart(delegate
            {
                WavScan scan = null;
                Exception err = null;
                try { scan = WavCut.Scan(info, cols, 0.005f); }
                catch (Exception ex) { err = ex; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (IsDisposed || gen != _scanGen) return;
                        if (err != null)
                        {
                            _hint.Text = "看波形失败：" + err.Message;
                            return;
                        }
                        _scan = scan;
                        _peakCols = scan.Min.Length;
                        if (doSnap || _end <= _start)
                        {
                            _start = scan.AudibleStart;
                            _end = scan.AudibleEnd;
                        }
                        if (_end > info.Frames) _end = info.Frames;
                        if (_start < 0) _start = 0;
                        if (_end <= _start)
                        {
                            _start = 0;
                            _end = info.Frames;
                        }
                        _wave.ShowScan(scan.Min, scan.Max, info.Frames, _start, _end);
                        _play.Enabled = true;
                        _align.Enabled = true;
                        _save.Enabled = true;
                        UpdateText();
                    }));
                }
                catch (InvalidOperationException) { }
            }));
            t.IsBackground = true;
            t.Start();
        }

        void OnRangeChanged()
        {
            _justSaved = false;
            _start = _wave.StartFrame;
            _end = _wave.EndFrame;
            UpdateText();
        }

        void AlignToSound()
        {
            if (_scan == null || _info == null) return;
            _justSaved = false;
            _start = _scan.AudibleStart;
            _end = _scan.AudibleEnd;
            if (_end <= _start)
            {
                _start = 0;
                _end = _info.Frames;
            }
            _wave.SetRange(_start, _end);
            UpdateText();
        }

        void UpdateText()
        {
            if (_info == null || _info.Rate <= 0) return;
            bool head = _scan != null && _scan.AudibleStart > 0;
            bool tail = _scan != null && _scan.AudibleEnd < _info.Frames;
            bool snapped = _scan != null && _start == _scan.AudibleStart && _end == _scan.AudibleEnd;
            if (snapped && head && tail)
                _hint.Text = "开头和结尾的空白已经留在选区外面（先点了录制、后放的声音）。拖两边可改。";
            else if (snapped && head)
                _hint.Text = "已去掉开头的空白（先点了录制、后放的声音）。拖两边可改。";
            else if (snapped && tail)
                _hint.Text = "已去掉结尾的空白。拖两边可改。";
            else
                _hint.Text = "拖两边选择要留下的部分。点选区外会移动最近的一边。";

            double lead = _start / (double)_info.Rate;
            double tailSec = (_info.Frames - _end) / (double)_info.Rate;
            double keep = (_end - _start) / (double)_info.Rate;
            string prefix = _justSaved ? "已保存。" : "";
            _detail.Text = prefix + string.Format(
                "开头去掉 {0}，结尾去掉 {1}，留下 {2}（{3} 个采样点，原样拷贝）。",
                Clock(lead), Clock(tailSec), Clock(keep), _end - _start);
        }

        void TogglePlay()
        {
            if (!_playDone.WaitOne(0))
            {
                _playing = false;
                return;
            }
            if (_info == null || _end <= _start) return;
            _playDone.Reset();
            _playing = true;
            Interlocked.Exchange(ref _playFrame, _start);
            _play.Text = "停止试听";
            WavInfo info = _info;
            long a = _start;
            long b = _end;
            Thread t = new Thread(new ThreadStart(delegate { PlayBody(info, a, b); }));
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        void PlayBody(WavInfo info, long a, long b)
        {
            string err = null;
            try
            {
                WavPreview.Run(info, a, b, delegate { return _playing; }, delegate(long frame) { Interlocked.Exchange(ref _playFrame, frame); });
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }
            Interlocked.Exchange(ref _playFrame, -1);
            _playDone.Set();
            string msg = err;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed) return;
                    _playing = false;
                    _play.Text = "试听选区";
                    _wave.Playhead = -1;
                    _wave.Invalidate();
                    if (msg != null)
                        _detail.Text = "试听失败：" + msg + " 保存裁剪不受影响。";
                }));
            }
            catch (InvalidOperationException) { }
        }

        void SaveCut()
        {
            if (_info == null) return;
            if (_start <= 0 && _end >= _info.Frames)
            {
                MessageBox.Show(this, "选区已经是整段，没有要裁掉的部分。", "LoopTap");
                return;
            }
            if (_end <= _start)
            {
                MessageBox.Show(this, "选区是空的。", "LoopTap");
                return;
            }
            double lead = _start / (double)_info.Rate;
            double tailSec = (_info.Frames - _end) / (double)_info.Rate;
            string msg = string.Format(
                "开头去掉 {0}，结尾去掉 {1}。\r\n留下的采样点原样拷贝，不改采样率和位深。\r\n原文件会被替换。",
                Clock(lead), Clock(tailSec));
            if (MessageBox.Show(this, msg, "LoopTap", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            _playing = false;
            if (!_playDone.WaitOne(3000))
            {
                MessageBox.Show(this, "试听还在停止，请稍后再保存。", "LoopTap");
                return;
            }

            string path = _info.Path;
            string tmp = path + ".part";
            try
            {
                // ponytail: 拷贝在界面线程上做。家用录音一般几秒到几分钟，拷完就返回。
                // 接近 4GB 时窗口会卡住，那时再改成后台拷贝。
                WavCut.SaveRange(_info, _start, _end, tmp);
                File.Replace(tmp, path, null);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                MessageBox.Show(this, "保存失败：" + ex.Message + "\r\n原文件还在。", "LoopTap");
                return;
            }

            try
            {
                LoadCore(path, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "已经保存，但重新打开失败：" + ex.Message, "LoopTap");
            }
        }

        static string Clock(double sec)
        {
            if (sec < 0) sec = 0;
            int total = (int)sec;
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            int ms = (int)((sec - total) * 1000.0);
            if (ms > 999) ms = 999;
            if (h > 0)
                return string.Format("{0}:{1:00}:{2:00}.{3:000}", h, m, s, ms);
            return string.Format("{0}:{1:00}.{2:000}", m, s, ms);
        }

        sealed class WavePanel : Control
        {
            float[] _min;
            float[] _max;
            long _frames;
            long _start;
            long _end;
            long _play = -1;
            int _drag;
            long _bodyOrigin;
            long _bodyStart;
            long _bodyEnd;

            public event EventHandler RangeChanged;

            public long StartFrame { get { return _start; } }
            public long EndFrame { get { return _end; } }
            public long Playhead { get { return _play; } set { _play = value; } }

            public WavePanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Color.FromArgb(28, 28, 28);
            }

            public void ShowScan(float[] min, float[] max, long frames, long start, long end)
            {
                _min = min;
                _max = max;
                _frames = frames;
                _start = start;
                _end = end;
                Invalidate();
            }

            public void SetRange(long start, long end)
            {
                _start = start;
                _end = end;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                int w = ClientSize.Width;
                int h = ClientSize.Height;
                g.Clear(BackColor);
                if (w <= 1 || h <= 1) return;
                int xs = XAt(_start);
                int xe = XAt(_end);
                if (xe < xs) xe = xs;
                using (Brush keep = new SolidBrush(Color.FromArgb(36, 48, 64)))
                    g.FillRectangle(keep, xs, 0, Math.Max(1, xe - xs), h);

                if (_min != null && _max != null && _min.Length > 0 && _frames > 0)
                {
                    int mid = h / 2;
                    float amp = (h / 2f) - 6f;
                    if (amp < 1f) amp = 1f;
                    using (Pen wave = new Pen(Color.FromArgb(120, 190, 255)))
                    {
                        int n = _min.Length;
                        for (int x = 0; x < w; x++)
                        {
                            int col = x * n / w;
                            if (col >= n) col = n - 1;
                            int y1 = mid - (int)(_max[col] * amp);
                            int y2 = mid - (int)(_min[col] * amp);
                            if (y2 < y1)
                            {
                                int t = y1;
                                y1 = y2;
                                y2 = t;
                            }
                            if (y2 == y1) y2 = y1 + 1;
                            g.DrawLine(wave, x, y1, x, y2);
                        }
                    }
                }

                using (Brush dim = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                {
                    if (xs > 0) g.FillRectangle(dim, 0, 0, xs, h);
                    if (xe < w) g.FillRectangle(dim, xe, 0, w - xe, h);
                }
                using (Pen handle = new Pen(Color.FromArgb(255, 196, 0), 2f))
                {
                    int hs = xs;
                    int he = xe;
                    if (hs > w - 1) hs = w - 1;
                    if (he > w - 1) he = w - 1;
                    g.DrawLine(handle, hs, 0, hs, h - 1);
                    g.DrawLine(handle, he, 0, he, h - 1);
                }
                if (_play >= 0 && _frames > 0)
                {
                    int xp = XAt(_play);
                    if (xp > w - 1) xp = w - 1;
                    using (Pen head = new Pen(Color.White, 1f))
                        g.DrawLine(head, xp, 0, xp, h - 1);
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || _frames <= 0) return;
                Focus();
                int xs = XAt(_start);
                int xe = XAt(_end);
                int grab = 10;
                bool nearS = Math.Abs(e.X - xs) <= grab;
                bool nearE = Math.Abs(e.X - xe) <= grab;
                if (nearS && nearE)
                    _drag = e.X >= (xs + xe) / 2 ? 2 : 1;
                else if (nearS)
                    _drag = 1;
                else if (nearE)
                    _drag = 2;
                else if (e.X > xs && e.X < xe)
                {
                    _drag = 3;
                    _bodyOrigin = FrameAt(e.X);
                    _bodyStart = _start;
                    _bodyEnd = _end;
                }
                else
                    _drag = e.X < xs ? 1 : 2;

                if (_drag == 1) MoveStart(FrameAt(e.X));
                else if (_drag == 2) MoveEnd(FrameAt(e.X));
                Capture = true;
                Invalidate();
                FireRange();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_frames <= 0) return;
                if (_drag == 1)
                {
                    MoveStart(FrameAt(e.X));
                    Invalidate();
                    FireRange();
                    return;
                }
                if (_drag == 2)
                {
                    MoveEnd(FrameAt(e.X));
                    Invalidate();
                    FireRange();
                    return;
                }
                if (_drag == 3)
                {
                    long delta = FrameAt(e.X) - _bodyOrigin;
                    long len = _bodyEnd - _bodyStart;
                    long ns = _bodyStart + delta;
                    if (ns < 0) ns = 0;
                    if (ns + len > _frames) ns = _frames - len;
                    if (ns < 0) ns = 0;
                    _start = ns;
                    _end = ns + len;
                    Invalidate();
                    FireRange();
                    return;
                }
                int xs = XAt(_start);
                int xe = XAt(_end);
                if (Math.Abs(e.X - xs) <= 10 || Math.Abs(e.X - xe) <= 10)
                    Cursor = Cursors.SizeWE;
                else if (e.X > xs && e.X < xe)
                    Cursor = Cursors.SizeAll;
                else
                    Cursor = Cursors.Default;
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _drag = 0;
                Capture = false;
            }

            void MoveStart(long frame)
            {
                if (frame < 0) frame = 0;
                if (frame >= _end) frame = _end - 1;
                if (frame < 0) frame = 0;
                _start = frame;
            }

            void MoveEnd(long frame)
            {
                if (frame > _frames) frame = _frames;
                if (frame <= _start) frame = _start + 1;
                if (frame > _frames) frame = _frames;
                _end = frame;
            }

            long FrameAt(int x)
            {
                int w = ClientSize.Width;
                if (_frames <= 0 || w <= 1) return 0;
                if (x < 0) x = 0;
                if (x >= w) return _frames;
                return x * _frames / w;
            }

            int XAt(long frame)
            {
                int w = ClientSize.Width;
                if (_frames <= 0 || w <= 0) return 0;
                if (frame <= 0) return 0;
                if (frame >= _frames) return w;
                return (int)(frame * w / _frames);
            }

            void FireRange()
            {
                EventHandler h = RangeChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }
    }

    static class WavPreview
    {
        public static void Run(WavInfo info, long startFrame, long endFrame, Func<bool> keepGoing, Action<long> onFrame)
        {
            if (endFrame <= startFrame) return;
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioRenderClient render = null;
            IntPtr fmt = IntPtr.Zero;
            bool started = false;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                int hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                if (hr < 0 || device == null)
                {
                    if (device != null)
                    {
                        try { Marshal.ReleaseComObject(device); } catch { }
                        device = null;
                    }
                    hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
                }
                if (hr < 0 || device == null)
                    throw new InvalidOperationException("找不到默认播放设备。" + AudioErrors.Explain(hr));

                Guid iid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                object activated;
                hr = device.Activate(ref iid, 23, IntPtr.Zero, out activated);
                if (hr < 0)
                    throw new InvalidOperationException("打开播放设备失败。" + AudioErrors.Explain(hr));
                client = activated as IAudioClient;
                if (client == null)
                    throw new InvalidOperationException("播放设备没有返回 IAudioClient。");

                fmt = Marshal.AllocHGlobal(info.Fmt.Length);
                Marshal.Copy(info.Fmt, 0, fmt, info.Fmt.Length);
                // 共享模式，不用 AUTOCONVERT。缓冲约 100ms。格式字节就是文件里的 fmt。
                hr = client.Initialize(0, 0, 1000000, 0, fmt, IntPtr.Zero);
                if (hr < 0)
                    throw new InvalidOperationException("试听无法使用文件里的格式。" + AudioErrors.Explain(hr));

                Guid renderId = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
                object svc;
                hr = client.GetService(ref renderId, out svc);
                if (hr < 0)
                    throw new InvalidOperationException("拿不到播放缓冲。" + AudioErrors.Explain(hr));
                render = svc as IAudioRenderClient;
                if (render == null)
                    throw new InvalidOperationException("拿不到播放缓冲。");

                uint bufferFrames;
                hr = client.GetBufferSize(out bufferFrames);
                if (hr < 0 || bufferFrames == 0)
                    throw new InvalidOperationException("播放缓冲大小无效。" + AudioErrors.Explain(hr));

                using (FileStream fs = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Position = info.DataOffset + startFrame * info.BlockAlign;
                    long remain = endFrame - startFrame;
                    long cursor = startFrame;
                    int cap = 4096;
                    if (cap > bufferFrames) cap = (int)bufferFrames;
                    byte[] chunk = new byte[cap * info.BlockAlign];
                    bool primed = false;
                    // ponytail: 轮询填充，不挂事件回调。停的时候最多再等一个缓冲周期。
                    while (remain > 0 && keepGoing())
                    {
                        uint pad;
                        hr = client.GetCurrentPadding(out pad);
                        if (hr < 0)
                            throw new InvalidOperationException("读取播放进度失败。" + AudioErrors.Explain(hr));
                        uint avail = bufferFrames - pad;
                        if (avail == 0)
                        {
                            if (!primed)
                                throw new InvalidOperationException("播放缓冲没有空位。");
                            Thread.Sleep(5);
                            continue;
                        }
                        if (avail > (uint)cap) avail = (uint)cap;
                        if (avail > remain) avail = (uint)remain;
                        IntPtr dst;
                        hr = render.GetBuffer(avail, out dst);
                        if (hr < 0 || dst == IntPtr.Zero)
                            throw new InvalidOperationException("取播放缓冲失败。" + AudioErrors.Explain(hr));
                        bool released = false;
                        try
                        {
                            int bytes = (int)avail * info.BlockAlign;
                            int got = 0;
                            while (got < bytes)
                            {
                                int n = fs.Read(chunk, got, bytes - got);
                                if (n <= 0) break;
                                got += n;
                            }
                            for (int i = got; i < bytes; i++) chunk[i] = 0;
                            Marshal.Copy(chunk, 0, dst, bytes);
                            hr = render.ReleaseBuffer(avail, 0);
                            released = true;
                            if (hr < 0)
                                throw new InvalidOperationException("提交播放缓冲失败。" + AudioErrors.Explain(hr));
                        }
                        finally
                        {
                            if (!released)
                            {
                                try { render.ReleaseBuffer(avail, 2); } catch { }
                            }
                        }
                        remain -= avail;
                        cursor += avail;
                        if (onFrame != null) onFrame(cursor);
                        if (!primed)
                        {
                            hr = client.Start();
                            if (hr < 0)
                                throw new InvalidOperationException("开始试听失败。" + AudioErrors.Explain(hr));
                            started = true;
                            primed = true;
                        }
                    }
                    if (started && keepGoing())
                    {
                        while (keepGoing())
                        {
                            uint pad;
                            hr = client.GetCurrentPadding(out pad);
                            if (hr < 0 || pad == 0) break;
                            Thread.Sleep(10);
                        }
                    }
                }
            }
            finally
            {
                if (client != null && started)
                {
                    try { client.Stop(); } catch { }
                }
                if (render != null)
                {
                    try { Marshal.ReleaseComObject(render); } catch { }
                }
                if (client != null)
                {
                    try { Marshal.ReleaseComObject(client); } catch { }
                }
                if (device != null)
                {
                    try { Marshal.ReleaseComObject(device); } catch { }
                }
                if (enumerator != null)
                {
                    try { Marshal.ReleaseComObject(enumerator); } catch { }
                }
                if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
            }
        }
    }

    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig]
        int GetBuffer(uint numFramesRequested, out IntPtr data);
        [PreserveSig]
        int ReleaseBuffer(uint numFramesWritten, uint flags);
    }
}
