using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
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
        readonly Button _saveAs;
        readonly Button _findLoop;
        readonly Button _oneLoop;
        readonly TextBox _periodBox;
        readonly Label _secLabel;
        readonly CheckBox _gridOn;
        readonly CheckBox _normOn;
        readonly TextBox _targetBox;
        readonly Label _dbUnit;
        readonly Label _normInfo;
        readonly Label _loopInfo;
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
        bool _saving;
        string _savedExtra;
        long _periodFrames;
        long _phaseFrames;
        int _loopGen;
        bool _loopBusy;
        bool _suppress;
        bool _normSuppress;
        bool _usedLoop;
        float _selPeak = -1f;
        int _peakGen;
        int _peakWait;
        double _targetDb = -1.0;

        public TrimForm(string path)
        {
            _path = path;
            Text = "裁剪录音";
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            Width = 900;
            Height = 560;
            MinimumSize = new Size(720, 420);
            DoubleBuffered = true;
            try { Font = new Font("Microsoft YaHei UI", 9f); }
            catch { }

            _wave = new WavePanel();
            _wave.RangeChanged += delegate { OnRangeChanged(); };
            _wave.LoopChanged += delegate { OnLoopDragged(); };

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
            _save.Click += delegate { SaveCut(true); };

            _saveAs = new Button();
            _saveAs.Text = "另存为";
            _saveAs.Top = 8;
            _saveAs.Width = 90;
            _saveAs.Height = 30;
            _saveAs.Enabled = false;
            _saveAs.Click += delegate { SaveCut(false); };

            _findLoop = new Button();
            _findLoop.Text = "找循环";
            _findLoop.Left = 8;
            _findLoop.Top = 44;
            _findLoop.Width = 90;
            _findLoop.Height = 30;
            _findLoop.Enabled = false;
            _findLoop.Click += delegate { BeginFind(); };

            _periodBox = new TextBox();
            _periodBox.Left = 106;
            _periodBox.Top = 48;
            _periodBox.Width = 72;
            _periodBox.Height = 22;
            _periodBox.KeyDown += delegate(object s, KeyEventArgs ev)
            {
                if (ev.KeyCode != Keys.Enter) return;
                ev.SuppressKeyPress = true;
                ApplyPeriodText();
            };
            _periodBox.Leave += delegate { ApplyPeriodText(); };

            _secLabel = new Label();
            _secLabel.AutoSize = true;
            _secLabel.Text = "秒";
            _secLabel.Left = 182;
            _secLabel.Top = 52;

            _oneLoop = new Button();
            _oneLoop.Text = "选区=一个周期";
            _oneLoop.Top = 44;
            _oneLoop.Width = 138;
            _oneLoop.Height = 30;
            _oneLoop.Enabled = false;
            _oneLoop.Click += delegate { SelectOneLoop(); };

            _gridOn = new CheckBox();
            _gridOn.Text = "周期网格";
            _gridOn.AutoSize = true;
            _gridOn.Top = 50;
            _gridOn.CheckedChanged += delegate
            {
                if (_suppress) return;
                PushLoop();
            };

            _normOn = new CheckBox();
            _normOn.Text = "如果不满意音量，试试归一化";
            _normOn.AutoSize = true;
            _normOn.Left = 8;
            _normOn.Top = 82;
            _normOn.CheckedChanged += delegate
            {
                if (_normSuppress) return;
                UpdateNormText();
                UpdateText();
            };

            _targetBox = new TextBox();
            _targetBox.Left = 250;
            _targetBox.Top = 80;
            _targetBox.Width = 64;
            _targetBox.Height = 22;
            _targetBox.Text = "-1.00";
            _targetBox.KeyDown += delegate(object s, KeyEventArgs ev)
            {
                if (ev.KeyCode != Keys.Enter) return;
                ev.SuppressKeyPress = true;
                ApplyTargetText();
            };
            _targetBox.Leave += delegate { ApplyTargetText(); };

            _dbUnit = new Label();
            _dbUnit.AutoSize = true;
            _dbUnit.Text = "dBFS";
            _dbUnit.Left = 318;
            _dbUnit.Top = 84;

            _normInfo = new Label();
            _normInfo.AutoSize = false;
            _normInfo.AutoEllipsis = true;
            _normInfo.Left = 360;
            _normInfo.Top = 82;
            _normInfo.Height = 22;
            _normInfo.Text = "";

            _loopInfo = new Label();
            _loopInfo.AutoSize = false;
            _loopInfo.AutoEllipsis = true;
            _loopInfo.Top = 50;
            _loopInfo.Height = 22;
            _loopInfo.Text = "";

            _hint = new Label();
            _hint.AutoSize = false;
            _hint.Left = 8;
            _hint.Top = 116;
            _hint.Height = 36;
            _hint.Text = "正在看波形……";

            _detail = new Label();
            _detail.AutoSize = false;
            _detail.Left = 8;
            _detail.Top = 154;
            _detail.Height = 64;
            _detail.Text = "";

            _bar = new Panel();
            _bar.Height = 230;
            _bar.Controls.Add(_play);
            _bar.Controls.Add(_align);
            _bar.Controls.Add(_save);
            _bar.Controls.Add(_saveAs);
            _bar.Controls.Add(_findLoop);
            _bar.Controls.Add(_periodBox);
            _bar.Controls.Add(_secLabel);
            _bar.Controls.Add(_oneLoop);
            _bar.Controls.Add(_gridOn);
            _bar.Controls.Add(_normOn);
            _bar.Controls.Add(_targetBox);
            _bar.Controls.Add(_dbUnit);
            _bar.Controls.Add(_normInfo);
            _bar.Controls.Add(_loopInfo);
            _bar.Controls.Add(_hint);
            _bar.Controls.Add(_detail);
            _bar.Resize += delegate { LayoutBar(); };

            Controls.Add(_wave);
            Controls.Add(_bar);
            Resize += delegate { LayoutForm(); };
            OnResize(EventArgs.Empty);
            UiTheme.Apply(this);
            UiTheme.MarkAccent(_save);

            _playTimer = new System.Windows.Forms.Timer();
            _playTimer.Interval = 50;
            _playTimer.Tick += delegate
            {
                if (_peakWait > 0 && --_peakWait == 0)
                    StartPeak();
                long frame = Interlocked.Read(ref _playFrame);
                if (!_playing && frame < 0) return;
                _wave.Playhead = frame;
                _wave.Invalidate();
            };
            _playTimer.Start();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ScreenPlace.CenterOnOwner(this);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            PerformLayout();
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
            if (!saved) _savedExtra = null;
            Interlocked.Exchange(ref _playFrame, -1);
            Text = "裁剪录音 — " + Path.GetFileName(path);
            _hint.Text = "正在看波形……";
            _detail.Text = "";
            _play.Enabled = false;
            _align.Enabled = false;
            _save.Enabled = false;
            _saveAs.Enabled = false;
            _loopGen++;
            _loopBusy = false;
            _periodFrames = 0;
            _phaseFrames = 0;
            _suppress = true;
            _periodBox.Text = "";
            _gridOn.Checked = false;
            _suppress = false;
            _loopInfo.Text = "";
            _selPeak = -1f;
            _peakGen++;
            _peakWait = 0;
            _normInfo.Text = "正在看选区峰值……";
            _wave.DrawGain = 1f;
            _findLoop.Enabled = true;
            _oneLoop.Enabled = false;
            _wave.SetLoop(0, 0, false);
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
            _peakGen++;
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
            _saveAs.Left = _save.Left - _saveAs.Width - 8;
            _play.Left = 8;
            _align.Left = _play.Right + 8;
            if (_align.Right > _saveAs.Left - 8)
                _align.Left = _saveAs.Left - _align.Width - 8;
            _findLoop.Left = 8;
            _periodBox.Left = _findLoop.Right + 8;
            _secLabel.Left = _periodBox.Right + 4;
            _oneLoop.Left = _secLabel.Right + 8;
            _gridOn.Left = _oneLoop.Right + 8;
            _loopInfo.Left = _gridOn.Right + 8;
            _normOn.Top = 82;
            _targetBox.Left = _normOn.Right + 8;
            _targetBox.Top = 80;
            _dbUnit.Left = _targetBox.Right + 4;
            _dbUnit.Top = 84;
            _normInfo.Left = _dbUnit.Right + 8;
            _normInfo.Top = 82;
            int normW = w - _normInfo.Left - 8;
            if (normW < 40) normW = 40;
            _normInfo.Width = normW;
            int infoW = w - _loopInfo.Left - 8;
            if (infoW < 40) infoW = 40;
            _loopInfo.Width = infoW;
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
                try { scan = WavCut.Scan(info, cols, WavCut.NoiseThreshold(info)); }
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
                        SchedulePeak();
                        if (!_saving)
                        {
                            _play.Enabled = true;
                            _align.Enabled = true;
                            _save.Enabled = true;
                            _saveAs.Enabled = true;
                        }
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
            SchedulePeak();
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

        void BeginFind()
        {
            if (_info == null || _loopBusy || _saving) return;
            int gen = ++_loopGen;
            _loopBusy = true;
            _findLoop.Enabled = false;
            _loopInfo.Text = "正在找循环……";
            WavInfo info = _info;
            Thread t = new Thread(new ThreadStart(delegate
            {
                LoopHit hit = null;
                Exception err = null;
                try { hit = WavCut.FindLoop(info); }
                catch (Exception ex) { err = ex; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (IsDisposed || gen != _loopGen) return;
                        _loopBusy = false;
                        if (_saving)
                        {
                            _findLoop.Enabled = false;
                            return;
                        }
                        _findLoop.Enabled = true;
                        if (err != null)
                        {
                            _loopInfo.Text = "找循环失败：" + err.Message;
                            return;
                        }
                        if (hit == null || hit.Period <= 1)
                        {
                            _periodFrames = 0;
                            _phaseFrames = 0;
                            PushLoop();
                            float conf = hit == null ? 0 : hit.Confidence;
                            _loopInfo.Text = conf > 0.2f
                                ? "重复不够清楚（置信度 " + conf.ToString("0%") + "）。可以自己填周期（秒）。"
                                : "没听出重复的循环。可以自己填周期（秒）。";
                            return;
                        }
                        _periodFrames = hit.Period;
                        _phaseFrames = hit.Start;
                        if (_phaseFrames < 0) _phaseFrames = 0;
                        if (_info != null && _phaseFrames >= _info.Frames) _phaseFrames = 0;
                        SetPeriodBox(hit.Period);
                        _suppress = true;
                        _gridOn.Checked = true;
                        _suppress = false;
                        PushLoop();
                        double sec = hit.Period / (double)info.Rate;
                        double reps = info.Frames / (double)hit.Period;
                        _loopInfo.Text = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0:0.00} 秒，置信度 {1:0%}，约 {2:0.0} 遍。金线平移，灰线改长短。",
                            sec, hit.Confidence, reps);
                    }));
                }
                catch (InvalidOperationException) { }
            }));
            t.IsBackground = true;
            t.Start();
        }

        void ApplyPeriodText()
        {
            if (_suppress || _info == null || _info.Rate <= 0) return;
            string text = _periodBox.Text.Trim().Replace('，', '.').Replace(',', '.');
            if (text.Length == 0) return;
            double sec;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out sec) || sec <= 0)
                return;
            if (sec < 0.05) sec = 0.05;
            long frames = (long)Math.Round(sec * _info.Rate);
            if (frames < 1) frames = 1;
            if (frames > _info.Frames) frames = _info.Frames;
            if (frames == _periodFrames) return;
            _periodFrames = frames;
            if (_phaseFrames < 0 || _phaseFrames >= frames)
                _phaseFrames = _scan != null ? _scan.AudibleStart : 0;
            _suppress = true;
            _gridOn.Checked = true;
            _suppress = false;
            PushLoop();
            SetPeriodBox(frames);
            _loopInfo.Text = "已按填写的周期画网格。金线平移，灰线改长短。";
        }

        void SelectOneLoop()
        {
            if (_info == null || _periodFrames <= 1) return;
            if (_periodFrames >= _info.Frames)
            {
                _loopInfo.Text = "这个周期不短于整段。";
                return;
            }
            // 相位（金线）在第一遍内的偏移
            long phase = _phaseFrames % _periodFrames;
            if (phase < 0) phase += _periodFrames;
            long reps = _info.Frames / _periodFrames;  // 完整遍数

            // 锁定最干净的一遍：>=3 遍锁中间（首遍常有起播抖动，末遍可能被截断）；
            // 2 遍锁后一遍（避开首遍起播抖动）；1 遍锁唯一那一遍。
            long pick;
            if (reps >= 3) pick = reps / 2;
            else if (reps == 2) pick = 1;
            else pick = 0;

            long s = phase + pick * _periodFrames;
            if (s + _periodFrames > _info.Frames)
                s = _info.Frames - _periodFrames;
            if (s < 0) s = 0;
            long e = s + _periodFrames;
            if (e > _info.Frames) e = _info.Frames;
            if (e <= s) return;
            _justSaved = false;
            _usedLoop = true;
            _start = s;
            _end = e;
            _phaseFrames = s;
            _wave.SetRange(_start, _end);
            PushLoop();
            UpdateText();
            if (reps >= 3)
                _loopInfo.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "选区已锁定第 {0} 遍（共 {1} 遍）。中间的最干净，金线是这一遍的起点。",
                    pick + 1, reps);
            else if (reps == 2)
                _loopInfo.Text = "选区已锁定第 2 遍。首遍可能有起播抖动，第 2 遍最干净。";
            else
                _loopInfo.Text = "选区已锁定这一遍。金线是这一遍的起点。";
        }

        void OnLoopDragged()
        {
            _periodFrames = _wave.LoopPeriod;
            _phaseFrames = _wave.LoopPhase;
            SetPeriodBox(_periodFrames);
            _oneLoop.Enabled = _periodFrames > 1 && !_saving;
            _loopInfo.Text = PeriodText(_periodFrames) + " 秒。金线平移，灰线改长短。";
        }

        void PushLoop()
        {
            _wave.SetLoop(_periodFrames, _phaseFrames, _gridOn.Checked);
            _oneLoop.Enabled = _periodFrames > 1 && !_saving && !_loopBusy;
        }

        void SetPeriodBox(long frames)
        {
            if (_info == null || _info.Rate <= 0) return;
            _suppress = true;
            _periodBox.Text = (frames / (double)_info.Rate).ToString("0.00", CultureInfo.InvariantCulture);
            _suppress = false;
        }

        string PeriodText(long frames)
        {
            if (_info == null || _info.Rate <= 0) return "0.00";
            return (frames / (double)_info.Rate).ToString("0.00", CultureInfo.InvariantCulture);
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
            string prefix = _justSaved ? (_savedExtra ?? "已保存。") : "";
            string size = "";
            try
            {
                size = " 文件 " + RecordingName.FormatSize(new FileInfo(_info.Path).Length) + "。";
            }
            catch { }
            string how = "原样拷贝";
            if (_normOn.Checked && _selPeak > 1e-12f)
            {
                float gain = WavCut.DbToAmp(_targetDb) / _selPeak;
                if (gain != 1f) how = "按峰值乘一个常数";
            }
            _detail.Text = prefix + string.Format(
                "开头去掉 {0}，结尾去掉 {1}，留下 {2}（{3} 个采样点，{4}）。",
                Clock(lead), Clock(tailSec), Clock(keep), _end - _start, how) + size;
        }

        void TogglePlay()
        {
            if (!_playDone.WaitOne(0))
            {
                _playing = false;
                return;
            }
            if (_info == null || _end <= _start) return;
            float peak;
            float target;
            float gain = PreviewGain(out peak, out target);
            _playDone.Reset();
            _playing = true;
            Interlocked.Exchange(ref _playFrame, _start);
            _play.Text = "停止试听";
            WavInfo info = _info;
            long a = _start;
            long b = _end;
            float playGain = gain;
            float playPeak = peak;
            float playTarget = target;
            Thread t = new Thread(new ThreadStart(delegate { PlayBody(info, a, b, playGain, playPeak, playTarget); }));
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        void PlayBody(WavInfo info, long a, long b, float gain, float peak, float target)
        {
            string err = null;
            try
            {
                WavPreview.Run(info, a, b, gain, peak, target, delegate { return _playing; }, delegate(long frame) { Interlocked.Exchange(ref _playFrame, frame); });
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

        void SaveCut(bool replaceOriginal)
        {
            if (_saving || _info == null) return;
            if (_end <= _start)
            {
                MessageBox.Show(this, "选区是空的。", "LoopTap");
                return;
            }
            bool norm = _normOn.Checked;
            float gain = 1f;
            float peak = 0f;
            float targetAmp = 0f;
            bool full = _start <= 0 && _end >= _info.Frames;
            if (norm)
            {
                if (!ApplyTargetText())
                {
                    MessageBox.Show(this, "目标峰值请填 dBFS，例如 -1.00。", "LoopTap");
                    return;
                }
                UseWaitCursor = true;
                try { peak = WavCut.Peak(_info, _start, _end); }
                catch (Exception ex)
                {
                    UseWaitCursor = false;
                    MessageBox.Show(this, "看峰值失败：" + ex.Message, "LoopTap");
                    return;
                }
                UseWaitCursor = false;
                _selPeak = peak;
                UpdateNormText();
                try
                {
                    targetAmp = WavCut.DbToAmp(_targetDb);
                    gain = WavCut.GainToTarget(peak, targetAmp);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "LoopTap");
                    return;
                }
            }
            if (full && gain == 1f)
            {
                MessageBox.Show(this, norm
                    ? "选区已经是整段，峰值也已经在目标电平。"
                    : "选区已经是整段，没有要裁掉的部分。", "LoopTap");
                return;
            }
            double lead = _start / (double)_info.Rate;
            double tailSec = (_info.Frames - _end) / (double)_info.Rate;
            string path = _info.Path;
            string dest = path;
            string backup = null;
            string volLine = "";
            if (norm && gain != 1f)
            {
                volLine = string.Format(
                    CultureInfo.InvariantCulture,
                    "\r\n音量按峰值归一化到 {0:0.00} dBFS（现在 {1:0.0} dBFS，增益 {2:+0.0;-0.0;0} dB）。每个采样点乘同一个常数，不重采样。",
                    _targetDb, WavCut.AmpToDb(peak), _targetDb - WavCut.AmpToDb(peak));
            }
            if (replaceOriginal)
            {
                backup = WavCut.ChooseBackup(path, delegate(string candidate) { return File.Exists(candidate); });
                string msg;
                if (full)
                    msg = "不会裁掉采样点。" + volLine + "\r\n这条录音会换成归一化结果。完整原件留在：\r\n" + backup;
                else if (gain == 1f)
                    msg = string.Format(
                        "开头去掉 {0}，结尾去掉 {1}。\r\n留下的采样点原样拷贝，不改采样率和位深。\r\n这条录音会换成裁剪结果。完整原件留在：\r\n{2}",
                        Clock(lead), Clock(tailSec), backup);
                else
                    msg = string.Format(
                        "开头去掉 {0}，结尾去掉 {1}。{2}\r\n不改采样率和位深。\r\n这条录音会换成裁剪结果。完整原件留在：\r\n{3}",
                        Clock(lead), Clock(tailSec), volLine, backup);
                if (MessageBox.Show(this, msg, "LoopTap", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                    return;
            }
            else
            {
                SaveFileDialog dlg = new SaveFileDialog();
                dlg.Filter = "WAV (*.wav)|*.wav";
                dlg.Title = "另存裁剪结果";
                dlg.OverwritePrompt = true;
                try { dlg.InitialDirectory = Path.GetDirectoryName(path); }
                catch { }
                string baseName = Path.GetFileNameWithoutExtension(path);
                string suffix = _usedLoop ? "_循环一遍" : (gain != 1f && full ? "_归一化" : "_裁剪");
                dlg.FileName = WavCut.ChooseFree(Path.GetDirectoryName(path), baseName, suffix, ".wav");
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                dest = dlg.FileName;
                if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "另存请换一个文件名。要用原文件名，点「保存裁剪」，完整原件会留成备份。", "LoopTap");
                    return;
                }
            }

            _playing = false;
            if (!_playDone.WaitOne(3000))
            {
                MessageBox.Show(this, "试听还在停止，请稍后再保存。", "LoopTap");
                return;
            }

            _saving = true;
            _save.Enabled = false;
            _saveAs.Enabled = false;
            _play.Enabled = false;
            _findLoop.Enabled = false;
            _oneLoop.Enabled = false;
            _normOn.Enabled = false;
            _targetBox.Enabled = false;
            WavInfo info = _info;
            long a = _start;
            long b = _end;
            string finalDest = dest;
            string bak = backup;
            bool replace = replaceOriginal;
            float saveGain = gain;
            float savePeak = peak;
            float saveTarget = targetAmp;
            Thread t = new Thread(new ThreadStart(delegate
            {
                string err = null;
                string reopen = finalDest;
                try
                {
                    if (replace)
                    {
                        string tmp = path + ".part";
                        try
                        {
                            WavCut.SaveRange(info, a, b, tmp, saveGain, savePeak, saveTarget);
                            File.Replace(tmp, path, bak);
                        }
                        catch
                        {
                            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                            throw;
                        }
                        reopen = path;
                    }
                    else
                    {
                        WavCut.SaveRange(info, a, b, finalDest, saveGain, savePeak, saveTarget);
                    }
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
                string error = err;
                string openPath = reopen;
                string note = replace
                    ? "已保存。完整原件留在 " + Path.GetFileName(bak) + "。"
                    : "已另存，原来的录音没动。";
                if (saveGain != 1f)
                    note = "音量已归一化。" + note;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _saving = false;
                        if (IsDisposed) return;
                        if (error != null)
                        {
                            _play.Enabled = true;
                            _save.Enabled = true;
                            _saveAs.Enabled = true;
                            _findLoop.Enabled = true;
                            _oneLoop.Enabled = _periodFrames > 1;
                            _normOn.Enabled = true;
                            _targetBox.Enabled = true;
                            MessageBox.Show(this, "保存失败：" + error + "\r\n原文件还在。", "LoopTap");
                            return;
                        }
                        _savedExtra = note;
                        _normOn.Enabled = true;
                        _targetBox.Enabled = true;
                        try { LoadCore(openPath, true); }
                        catch (Exception ex)
                        {
                            _play.Enabled = true;
                            _save.Enabled = true;
                            _saveAs.Enabled = true;
                            _findLoop.Enabled = true;
                            _normOn.Enabled = true;
                            _targetBox.Enabled = true;
                            MessageBox.Show(this, "已经保存，但重新打开失败：" + ex.Message, "LoopTap");
                        }
                    }));
                }
                catch (InvalidOperationException) { }
            }));
            t.IsBackground = false;
            t.Start();
        }

        void SchedulePeak()
        {
            if (_info == null || _end <= _start) return;
            _peakWait = 4;
        }

        void StartPeak()
        {
            if (_info == null || _end <= _start) return;
            int gen = ++_peakGen;
            WavInfo info = _info;
            long a = _start;
            long b = _end;
            Thread t = new Thread(new ThreadStart(delegate
            {
                float peak = 0f;
                Exception err = null;
                try { peak = WavCut.Peak(info, a, b); }
                catch (Exception ex) { err = ex; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (IsDisposed || gen != _peakGen) return;
                        if (err != null)
                        {
                            _selPeak = -1f;
                            _normInfo.Text = "看峰值失败：" + err.Message;
                            _wave.DrawGain = 1f;
                            return;
                        }
                        _selPeak = peak;
                        UpdateNormText();
                        UpdateText();
                    }));
                }
                catch (InvalidOperationException) { }
            }));
            t.IsBackground = true;
            t.Start();
        }

        bool ApplyTargetText()
        {
            if (_normSuppress) return true;
            string text = _targetBox.Text == null ? "" : _targetBox.Text.Trim().Replace('，', '.').Replace(',', '.');
            double db;
            if (text.Length == 0 || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out db)
                || double.IsNaN(db) || double.IsInfinity(db))
            {
                _normInfo.Text = "目标峰值请填 dBFS，例如 -1.00。";
                return false;
            }
            // ponytail: 目标夹在 -60 到 +12 dBFS。再往上试听会在设备里削波。升级：去掉上限。
            if (db < -60) db = -60;
            if (db > 12) db = 12;
            _targetDb = db;
            _normSuppress = true;
            _targetBox.Text = db.ToString("0.00", CultureInfo.InvariantCulture);
            _normSuppress = false;
            UpdateNormText();
            return true;
        }

        void UpdateNormText()
        {
            if (_normInfo == null) return;
            if (_selPeak < 0f)
            {
                _normInfo.Text = "正在看选区峰值……";
                _wave.DrawGain = 1f;
                return;
            }
            if (!(_selPeak > 1e-12f))
            {
                _normInfo.Text = _normOn.Checked ? "这段是静音，归一化不会放大。" : "选区是静音。";
                _wave.DrawGain = 1f;
                _wave.Invalidate();
                return;
            }
            double now = WavCut.AmpToDb(_selPeak);
            if (!_normOn.Checked)
            {
                _normInfo.Text = "选区峰值 " + now.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS。";
                _wave.DrawGain = 1f;
                _wave.Invalidate();
                return;
            }
            float target = WavCut.DbToAmp(_targetDb);
            float draw = target / _selPeak;
            if (!(draw > 0f) || float.IsNaN(draw) || float.IsInfinity(draw)) draw = 1f;
            _wave.DrawGain = draw;
            double gainDb = _targetDb - now;
            _normInfo.Text = string.Format(
                CultureInfo.InvariantCulture,
                "峰值 {0:0.0} dB → {1:0.00} dB，增益 {2:+0.0;-0.0;0} dB。",
                now, _targetDb, gainDb);
            _wave.Invalidate();
        }

        float PreviewGain(out float peak, out float target)
        {
            peak = 0f;
            target = 0f;
            if (!_normOn.Checked || _info == null) return 1f;
            if (_selPeak < 0f)
            {
                try { _selPeak = WavCut.Peak(_info, _start, _end); }
                catch { _selPeak = 0f; }
                UpdateNormText();
            }
            if (!(_selPeak > 1e-12f)) return 1f;
            try
            {
                target = WavCut.DbToAmp(_targetDb);
                float gain = WavCut.GainToTarget(_selPeak, target);
                peak = _selPeak;
                return gain;
            }
            catch
            {
                return 1f;
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
            long _period;
            long _phase;
            bool _grid;
            long _gridK;
            long _grabFrame;
            long _grabPhase;
            public float DrawGain = 1f;

            public event EventHandler RangeChanged;
            public event EventHandler LoopChanged;

            public long StartFrame { get { return _start; } }
            public long EndFrame { get { return _end; } }
            public long Playhead { get { return _play; } set { _play = value; } }
            public long LoopPeriod { get { return _period; } }
            public long LoopPhase { get { return _phase; } }

            public void SetLoop(long period, long phase, bool show)
            {
                _period = period;
                if (period > 1)
                    _phase = Mod(phase, period);
                else
                    _phase = phase;
                _grid = show && period > 1;
                Invalidate();
            }

            public WavePanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = UiTheme.Bg;
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
                using (Brush keep = new SolidBrush(UiTheme.Keep))
                    g.FillRectangle(keep, xs, 0, Math.Max(1, xe - xs), h);

                if (_min != null && _max != null && _min.Length > 0 && _frames > 0)
                {
                    int mid = h / 2;
                    float amp = (h / 2f) - 6f;
                    if (amp < 1f) amp = 1f;
                    using (Pen wave = new Pen(UiTheme.Wave))
                    {
                        int n = _min.Length;
                        float draw = DrawGain;
                        if (!(draw > 0f) || float.IsNaN(draw) || float.IsInfinity(draw)) draw = 1f;
                        for (int x = 0; x < w; x++)
                        {
                            int col = x * n / w;
                            if (col >= n) col = n - 1;
                            float hi = _max[col] * draw;
                            float lo = _min[col] * draw;
                            if (hi > 1f) hi = 1f;
                            if (hi < -1f) hi = -1f;
                            if (lo > 1f) lo = 1f;
                            if (lo < -1f) lo = -1f;
                            int y1 = mid - (int)(hi * amp);
                            int y2 = mid - (int)(lo * amp);
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

                using (Brush dim = new SolidBrush(UiTheme.Dim))
                {
                    if (xs > 0) g.FillRectangle(dim, 0, 0, xs, h);
                    if (xe < w) g.FillRectangle(dim, xe, 0, w - xe, h);
                }
                DrawGrid(g, w, h);
                using (Pen handle = new Pen(UiTheme.Gold, 2f))
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
                    using (Pen head = new Pen(UiTheme.Text, 1f))
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
                else
                {
                    long gk;
                    bool shift = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                    bool onGrid = HitGrid(e.X, out gk);
                    if (_grid && (onGrid || shift))
                    {
                        _drag = (onGrid && gk != 0 && !shift) ? 5 : 4;
                        _gridK = gk;
                        _grabFrame = FrameAt(e.X);
                        _grabPhase = _phase;
                        Capture = true;
                        DragGrid(e.X);
                        return;
                    }
                    if (e.X > xs && e.X < xe)
                    {
                        _drag = 3;
                        _bodyOrigin = FrameAt(e.X);
                        _bodyStart = _start;
                        _bodyEnd = _end;
                    }
                    else
                        _drag = e.X < xs ? 1 : 2;
                }

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
                if (_drag == 4 || _drag == 5)
                {
                    DragGrid(e.X);
                    return;
                }
                int xs = XAt(_start);
                int xe = XAt(_end);
                if (Math.Abs(e.X - xs) <= 10 || Math.Abs(e.X - xe) <= 10)
                    Cursor = Cursors.SizeWE;
                else
                {
                    long ignore;
                    if (HitGrid(e.X, out ignore))
                        Cursor = Cursors.SizeWE;
                    else if (e.X > xs && e.X < xe)
                        Cursor = Cursors.SizeAll;
                    else
                        Cursor = Cursors.Default;
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (_drag == 4 || _drag == 5)
                {
                    if (_period > 1) _phase = Mod(_phase, _period);
                    _drag = 0;
                    Capture = false;
                    Invalidate();
                    FireLoop();
                    return;
                }
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

            void FireLoop()
            {
                EventHandler h = LoopChanged;
                if (h != null) h(this, EventArgs.Empty);
            }

            void DrawGrid(Graphics g, int w, int h)
            {
                if (!_grid || _period <= 1 || _frames <= 0 || w <= 1) return;
                int gap = XAt(_phase + _period) - XAt(_phase);
                if (gap < 0) gap = -gap;
                if (gap < 4) return;
                using (Pen muted = new Pen(UiTheme.Muted))
                using (Pen gold = new Pen(UiTheme.Gold))
                {
                    muted.DashStyle = DashStyle.Dash;
                    gold.DashStyle = DashStyle.Dash;
                    long k = 0;
                    if (_phase > 0) k = -(_phase / _period);
                    for (int n = 0; n < 400; n++, k++)
                    {
                        long fr = _phase + k * _period;
                        if (fr < 0) continue;
                        if (fr > _frames) break;
                        int x = XAt(fr);
                        if (x < 0 || x > w) continue;
                        g.DrawLine(k == 0 ? gold : muted, x, 0, x, h - 1);
                    }
                }
            }

            bool HitGrid(int x, out long k)
            {
                k = 0;
                if (!_grid || _period <= 1 || _frames <= 0) return false;
                int gap = XAt(_phase + _period) - XAt(_phase);
                if (gap < 0) gap = -gap;
                if (gap < 4) return false;
                int best = 7;
                bool hit = false;
                long kk = 0;
                if (_phase > 0) kk = -(_phase / _period);
                for (int n = 0; n < 400; n++, kk++)
                {
                    long fr = _phase + kk * _period;
                    if (fr < 0) continue;
                    if (fr > _frames) break;
                    int d = x - XAt(fr);
                    if (d < 0) d = -d;
                    if (d < best)
                    {
                        best = d;
                        k = kk;
                        hit = true;
                    }
                }
                return hit;
            }

            void DragGrid(int x)
            {
                if (_period <= 1) return;
                long nf = FrameAt(x);
                if (_drag == 4)
                {
                    long phase = _grabPhase + (nf - _grabFrame);
                    if (phase < 0) phase = 0;
                    if (phase >= _period) phase = _period - 1;
                    _phase = phase;
                }
                else if (_drag == 5 && _gridK != 0)
                {
                    long k = _gridK;
                    long numer = nf - _grabPhase;
                    long np = k > 0 ? (numer + k / 2) / k : ((-numer) + (-k) / 2) / (-k);
                    long minP = _frames / 400;
                    if (minP < 1) minP = 1;
                    if (np < minP) np = minP;
                    if (_frames > 1 && np > _frames) np = _frames;
                    _period = np;
                    _phase = _grabPhase;
                    if (_phase < 0) _phase = 0;
                    if (_phase >= _period) _phase = _period - 1;
                }
                Invalidate();
                FireLoop();
            }

            static long Mod(long value, long period)
            {
                if (period <= 0) return 0;
                long r = value % period;
                if (r < 0) r += period;
                return r;
            }
        }
    }

    static class WavPreview
    {
        public static void Run(WavInfo info, long startFrame, long endFrame, float gain, float peak, float target, Func<bool> keepGoing, Action<long> onFrame)
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
                            if (gain != 1f)
                                WavCut.ApplyGain(chunk, bytes, gain, peak, target);
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
