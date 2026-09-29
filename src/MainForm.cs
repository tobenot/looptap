using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LoopTap
{
    sealed class MainForm : Form
    {
        enum RunState
        {
            Idle,
            Starting,
            Recording,
            Stopping
        }

        readonly ListView _list;
        readonly TextBox _filter;
        readonly TextBox _logBox;
        readonly Label _duration;
        readonly Label _status;
        readonly Button _refresh;
        readonly Button _start;
        readonly Button _stop;
        readonly Button _browse;
        readonly Button _openDir;
        readonly Button _trimBtn;
        readonly Button _openWav;
        readonly TextBox _dirBox;
        readonly CheckBox _hideBg;
        readonly Timer _timer;
        readonly object _logLock = new object();
        readonly Queue<string> _logs = new Queue<string>();
        List<ProcItem> _cache = new List<ProcItem>();
        LoopbackRecorder _rec;
        RunState _state;
        bool _shownError;
        string _lastFile;
        int _uiTick;
        TrimForm _trim;
        string _refreshError;

        public MainForm()
        {
            Text = "LoopTap 进程内录";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 920;
            Height = 680;
            MinimumSize = new Size(780, 540);
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;
            try { Font = new Font("Microsoft YaHei UI", 9f); }
            catch { }

            Label hint = new Label();
            hint.AutoSize = false;
            hint.Left = 8;
            hint.Top = 8;
            hint.Height = 32;
            hint.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            hint.Text = "点标黄的那一行再录。同一进程里的窗口会一起录进去。";

            _filter = new TextBox();
            _filter.Left = 8;
            _filter.Top = 40;
            _filter.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _filter.TextChanged += delegate { FillList(false); };

            _hideBg = new CheckBox();
            _hideBg.Text = "隐藏没窗口的后台进程";
            _hideBg.Checked = true;
            _hideBg.AutoSize = true;
            _hideBg.Left = 8;
            _hideBg.Top = 66;
            _hideBg.CheckedChanged += delegate { FillList(false); };

            Panel top = new Panel();
            top.Height = 92;
            top.Controls.Add(hint);
            top.Controls.Add(_filter);
            top.Controls.Add(_hideBg);
            top.Resize += delegate
            {
                hint.Width = top.ClientSize.Width - 16;
                _filter.Width = top.ClientSize.Width - 16;
            };

            _list = new ListView();
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.HideSelection = false;
            _list.MultiSelect = false;
            _list.Columns.Add("备注", 100);
            _list.Columns.Add("窗口标题", 420);
            _list.Columns.Add("进程名", 120);
            _list.Columns.Add("PID", 72);
            _list.Resize += delegate { SizeColumns(); };
            _list.DoubleClick += delegate { StartRecord(); };

            _refresh = new Button();
            _refresh.Text = "刷新进程";
            _refresh.Left = 8;
            _refresh.Top = 8;
            _refresh.Width = 100;
            _refresh.Height = 28;
            _refresh.Click += delegate { Reload(); };

            _start = new Button();
            _start.Text = "开始录制";
            _start.Left = 116;
            _start.Top = 8;
            _start.Width = 100;
            _start.Height = 28;
            _start.Click += delegate { StartRecord(); };

            _stop = new Button();
            _stop.Text = "停止";
            _stop.Left = 224;
            _stop.Top = 8;
            _stop.Width = 100;
            _stop.Height = 28;
            _stop.Enabled = false;
            _stop.Click += delegate { StopRecord(); };

            _duration = new Label();
            _duration.AutoSize = false;
            _duration.Left = 340;
            _duration.Top = 12;
            _duration.Width = 220;
            _duration.Height = 22;
            _duration.Text = "时长 00:00:00.0";

            _openDir = new Button();
            _openDir.Text = "打开文件夹";
            _openDir.Top = 8;
            _openDir.Width = 110;
            _openDir.Height = 28;
            _openDir.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _openDir.Click += delegate { OpenFolder(); };

            Label dirLabel = new Label();
            dirLabel.AutoSize = false;
            dirLabel.Text = "保存到";
            dirLabel.Left = 8;
            dirLabel.Top = 46;
            dirLabel.Width = 52;
            dirLabel.Height = 22;

            _dirBox = new TextBox();
            _dirBox.Left = 64;
            _dirBox.Top = 44;
            _dirBox.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _dirBox.Text = OutputDir.LoadOrDefault();
            _dirBox.Leave += delegate { CommitDir(false); };

            _browse = new Button();
            _browse.Text = "浏览…";
            _browse.Top = 42;
            _browse.Width = 72;
            _browse.Height = 28;
            _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browse.Click += delegate { BrowseDir(); };

            _trimBtn = new Button();
            _trimBtn.Text = "裁剪这段";
            _trimBtn.Top = 42;
            _trimBtn.Width = 88;
            _trimBtn.Height = 28;
            _trimBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _trimBtn.Click += delegate { OpenTrim(_lastFile); };

            _openWav = new Button();
            _openWav.Text = "打开录音";
            _openWav.Top = 42;
            _openWav.Width = 88;
            _openWav.Height = 28;
            _openWav.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _openWav.Click += delegate { OpenWav(); };

            _status = new Label();
            _status.AutoSize = false;
            _status.Left = 8;
            _status.Top = 76;
            _status.Height = 48;
            _status.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _status.Text = "就绪。选择进程后点「开始录制」。";

            _logBox = new TextBox();
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.Left = 8;
            _logBox.Top = 128;
            _logBox.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            _logBox.Font = Font;

            Panel bottom = new Panel();
            bottom.Height = 250;
            bottom.Controls.Add(_refresh);
            bottom.Controls.Add(_start);
            bottom.Controls.Add(_stop);
            bottom.Controls.Add(_duration);
            bottom.Controls.Add(_openDir);
            bottom.Controls.Add(dirLabel);
            bottom.Controls.Add(_dirBox);
            bottom.Controls.Add(_trimBtn);
            bottom.Controls.Add(_openWav);
            bottom.Controls.Add(_browse);
            bottom.Controls.Add(_status);
            bottom.Controls.Add(_logBox);
            bottom.Resize += delegate
            {
                _openDir.Left = bottom.ClientSize.Width - _openDir.Width - 8;
                _browse.Left = bottom.ClientSize.Width - _browse.Width - 8;
                _trimBtn.Left = _browse.Left - _trimBtn.Width - 8;
                _trimBtn.Top = _browse.Top;
                _openWav.Left = _trimBtn.Left - _openWav.Width - 8;
                _openWav.Top = _browse.Top;
                int dirW = _openWav.Left - 8 - _dirBox.Left;
                if (dirW < 80) dirW = 80;
                _dirBox.Width = dirW;
                _status.Width = bottom.ClientSize.Width - 16;
                _logBox.Width = bottom.ClientSize.Width - 16;
                _logBox.Height = bottom.ClientSize.Height - _logBox.Top - 8;
            };

            Controls.Add(bottom);
            Controls.Add(top);
            Controls.Add(_list);
            Resize += delegate
            {
                int w = ClientSize.Width;
                int h = ClientSize.Height;
                int topH = top.Height;
                int botH = 250;
                int listH = h - topH - botH;
                if (listH < 80)
                {
                    listH = 80;
                    botH = h - topH - listH;
                    if (botH < 140) botH = 140;
                    listH = h - topH - botH;
                    if (listH < 40) listH = 40;
                }
                top.SetBounds(0, 0, w, topH);
                _list.SetBounds(0, topH, w, listH);
                bottom.SetBounds(0, topH + listH, w, h - topH - listH);
                bottom.BringToFront();
            };
            OnResize(EventArgs.Empty);
            UiTheme.Apply(this);
            dirLabel.ForeColor = UiTheme.Muted;
            _duration.ForeColor = UiTheme.Wave;
            try { _duration.Font = new Font(Font, FontStyle.Bold); }
            catch { }
            UiTheme.MarkAccent(_start);
            ScreenPlace.CenterOnCursor(this);

            _timer = new Timer();
            _timer.Interval = 200;
            _timer.Tick += OnTick;
            _timer.Start();

            FormClosing += OnClosing;
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            PerformLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { SendMessage(_filter.Handle, 0x1501, new IntPtr(1), "搜索进程名、窗口标题或 PID"); }
            catch { }
            Log("LoopTap 已启动。不改系统设置，不需要管理员。");
            Log("录音保存到：" + _dirBox.Text);
            BeginInvoke(new Action(Reload));
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            _timer.Stop();
            if (_rec != null)
            {
                _rec.RequestStop();
                _rec.Join(5000);
                _rec.Dispose();
                _rec = null;
            }
            DrainLogs();
        }

        void Reload()
        {
            try
            {
                UseWaitCursor = true;
                _cache = ProcCatalog.List();
                if (!string.IsNullOrEmpty(WhoPlays.LastError))
                    Log("没能判断谁在出声：" + WhoPlays.LastError);
                FillList(true);
            }
            catch (Exception ex)
            {
                Log("刷新进程失败：" + ex.Message);
                MessageBox.Show(this, ex.Message, "LoopTap");
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        void FillList(bool writeLog)
        {
            RowRef keep = null;
            if (_list.SelectedItems.Count > 0)
                keep = _list.SelectedItems[0].Tag as RowRef;
            int scroll = 0;
            try
            {
                if (_list.TopItem != null) scroll = _list.TopItem.Index;
            }
            catch { }

            string filter = _filter.Text.Trim();
            int hidden = 0;
            int playingShown = 0;
            string onePlaying = "";
            bool pidReused = false;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                for (int i = 0; i < _cache.Count; i++)
                {
                    ProcItem p = _cache[i];
                    bool hasTitle = p.Title != null && p.Title.Length > 0;
                    if (_hideBg.Checked && !p.Playing && !hasTitle)
                    {
                        hidden++;
                        continue;
                    }
                    if (filter.Length > 0)
                    {
                        bool hit = p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                            || (p.Title != null && p.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                            || p.Pid.ToString().IndexOf(filter, StringComparison.Ordinal) >= 0;
                        if (!hit) continue;
                    }
                    string note = "";
                    if (p.Playing) note = p.WindowCount > 1 ? "出声·整进程" : "正在出声";
                    else if (p.WindowCount > 1) note = "整进程";
                    else if (p.Browser) note = "浏览器";
                    string title = p.Title ?? "";
                    if (p.Playing && title.Length == 0)
                        title = "(没有窗口，声音在这个进程里)";
                    ListViewItem item = new ListViewItem(note);
                    item.SubItems.Add(title);
                    item.SubItems.Add(p.Name);
                    item.SubItems.Add(p.Pid.ToString());
                    RowRef row = new RowRef();
                    row.Pid = p.Pid;
                    row.StartTicks = p.StartTicks;
                    row.Name = p.Name;
                    row.Title = title;
                    row.WindowCount = p.WindowCount;
                    item.Tag = row;
                    item.BackColor = UiTheme.Field;
                    item.ForeColor = UiTheme.Text;
                    item.UseItemStyleForSubItems = true;
                    if (keep != null && keep.Pid == p.Pid)
                    {
                        bool ticksOk = keep.StartTicks == 0 || p.StartTicks == 0 || keep.StartTicks == p.StartTicks;
                        bool titleOk = string.Equals(keep.Title, title, StringComparison.Ordinal);
                        if (ticksOk && titleOk)
                            item.Selected = true;
                        else if (!ticksOk)
                            pidReused = true;
                    }
                    if (p.Playing)
                    {
                        item.BackColor = UiTheme.Playing;
                        item.ForeColor = UiTheme.PlayingText;
                    }
                    _list.Items.Add(item);
                    if (p.Playing)
                    {
                        playingShown++;
                        if (onePlaying.Length == 0)
                            onePlaying = title.Length > 0 ? title : p.Name;
                    }
                }
            }
            finally
            {
                _list.EndUpdate();
            }
            if (!pidReused && _list.SelectedItems.Count == 0 && playingShown == 1 && _list.Items.Count > 0)
            {
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    if (_list.Items[i].Text.IndexOf("出声", StringComparison.Ordinal) >= 0)
                    {
                        _list.Items[i].Selected = true;
                        break;
                    }
                }
            }
            if (_list.SelectedItems.Count > 0)
                _list.SelectedItems[0].EnsureVisible();
            else if (scroll > 0 && scroll < _list.Items.Count)
            {
                try { _list.TopItem = _list.Items[scroll]; }
                catch { }
            }

            if (_state == RunState.Idle)
            {
                string summary;
                if (pidReused)
                    summary = "有一个 PID 已经换成了别的进程，请重新选择。";
                else
                if (playingShown == 1)
                    summary = "正在出声：" + onePlaying + "。点这一行再开始录制。";
                else if (playingShown > 1)
                    summary = "有 " + playingShown + " 个正在出声，已标黄并排在最上面。";
                else
                    summary = "现在没有程序在出声。点带窗口标题的那一行（浏览器看网页标题）。";
                if (_hideBg.Checked)
                    summary += " 显示 " + _list.Items.Count + " 行，已隐藏 " + hidden + " 个没窗口的后台进程。";
                else
                    summary += " 正在显示全部 " + _list.Items.Count + " 个进程。";
                RowRef sel = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as RowRef : null;
                if (sel != null && sel.WindowCount > 1)
                    summary += " 选中的这一行会录下整个进程（" + sel.WindowCount + " 个窗口）。";
                _status.Text = summary;
                if (writeLog)
                    Log(summary);
            }
        }

        void StartRecord()
        {
            if (_state != RunState.Idle) return;
            if (_list.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "请先在列表里选一个进程。", "LoopTap");
                return;
            }
            RowRef row = _list.SelectedItems[0].Tag as RowRef;
            if (row == null) return;
            int pid = row.Pid;
            string name = row.Name;
            try
            {
                using (System.Diagnostics.Process live = System.Diagnostics.Process.GetProcessById(pid))
                {
                    long ticks = ProcessIdentity.ReadStartTicks(live);
                    if (!ProcessIdentity.Same(row.Name, row.StartTicks, live.ProcessName, ticks))
                    {
                        MessageBox.Show(this, "这个 PID 已经是另一个进程。列表会刷新，请重新选。", "LoopTap");
                        RefreshQuiet();
                        return;
                    }
                }
            }
            catch
            {
                MessageBox.Show(this, "进程已经退出，请刷新列表。", "LoopTap");
                return;
            }

            string dir;
            try { dir = CommitDir(true); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存位置不可用：" + ex.Message, "LoopTap");
                return;
            }
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法创建录音目录：" + ex.Message, "LoopTap");
                return;
            }

            string file = string.Format("{0:yyyyMMdd_HHmmss}_{1}_{2}.wav", DateTime.Now, Safe(name), pid);
            string path = Path.Combine(dir, file);
            _lastFile = path;
            if (_rec != null)
            {
                _rec.Dispose();
                _rec = null;
            }
            _shownError = false;
            _rec = new LoopbackRecorder(pid, path, Log);
            _state = RunState.Starting;
            _start.Enabled = false;
            _refresh.Enabled = false;
            _list.Enabled = false;
            _dirBox.Enabled = false;
            _browse.Enabled = false;
            _trimBtn.Enabled = false;
            _openWav.Enabled = false;
            _stop.Enabled = true;
            _status.Text = "正在连接进程回环……";
            Text = "LoopTap 进程内录 — 正在启动";
            _rec.StartAsync();
        }

        void StopRecord()
        {
            if (_rec == null) return;
            if (_state != RunState.Starting && _state != RunState.Recording) return;
            _state = RunState.Stopping;
            _stop.Enabled = false;
            _status.Text = "正在停止并写好文件……";
            _rec.RequestStop();
        }

        void OnTick(object sender, EventArgs e)
        {
            DrainLogs();
            _uiTick++;
            if (_state == RunState.Idle && (_uiTick % 10) == 0)
                RefreshQuiet();
            if (_rec == null) return;

            if (_state == RunState.Starting && _rec.WaitStarted(0))
            {
                if (_rec.StartSucceeded && _rec.Running)
                {
                    _state = RunState.Recording;
                    _status.Text = "正在录制，文件：" + _rec.OutputPath;
                    Text = "LoopTap 进程内录 — 正在录制";
                }
                else if (_rec.StartSucceeded)
                {
                    _state = RunState.Recording;
                }
                else if (_rec.Cancelled)
                {
                    FinishIdle(_rec.Summary ?? "已取消。");
                    OfferTrim();
                }
                else
                {
                    string err = _rec.Error ?? "启动录制失败。";
                    FinishIdle(err);
                    if (!_shownError)
                    {
                        _shownError = true;
                        MessageBox.Show(this, err, "LoopTap");
                    }
                    OfferTrim();
                }
            }

            if (_state == RunState.Recording || _state == RunState.Stopping)
            {
                long frames = System.Threading.Interlocked.Read(ref _rec.Frames);
                int rate = _rec.SampleRate;
                double sec = rate > 0 ? frames / (double)rate : 0;
                _duration.Text = "时长 " + FormatDuration(sec);
                float peak = _rec.ReadPeak();
                string warn = "";
                if (sec >= 2 && peak < 0.001f)
                    warn = "\r\n几乎没有声音。可能选错了进程，或对方用了独占模式，那种声音录不进来。";
                string disc = _rec.DiscCount > 0 ? ("  间断 " + _rec.DiscCount + " 次，不补静音。") : "";
                _status.Text = "正在录制  " + Meter(peak) + "  峰值 " + peak.ToString("0.000") + disc + warn;
                if (!_rec.Running && _rec.WaitStarted(0))
                {
                    string done = _rec.Summary;
                    if (string.IsNullOrEmpty(done))
                        done = _rec.Failed ? (_rec.Error ?? "录制失败。") : "已停止。";
                    if (_rec.Failed && !string.IsNullOrEmpty(_rec.Error))
                        done = _rec.Error;
                    FinishIdle(done);
                    if (_rec.Failed && !_shownError)
                    {
                        _shownError = true;
                        MessageBox.Show(this, _rec.Error ?? done, "LoopTap");
                    }
                    OfferTrim();
                }
            }
        }

        void FinishIdle(string status)
        {
            _state = RunState.Idle;
            _start.Enabled = true;
            _refresh.Enabled = true;
            _list.Enabled = true;
            _dirBox.Enabled = true;
            _browse.Enabled = true;
            _trimBtn.Enabled = true;
            _openWav.Enabled = true;
            _stop.Enabled = false;
            _status.Text = status;
            Text = "LoopTap 进程内录";
        }

        void OfferTrim()
        {
            if (_rec == null || _rec.Frames <= 0) return;
            if (string.IsNullOrEmpty(_lastFile) || !File.Exists(_lastFile)) return;
            string path = _lastFile;
            BeginInvoke(new Action(delegate
            {
                if (!IsDisposed) OpenTrim(path);
            }));
        }

        void RefreshQuiet()
        {
            try
            {
                _cache = ProcCatalog.List();
                _refreshError = null;
                FillList(false);
            }
            catch (Exception ex)
            {
                if (_refreshError == ex.Message) return;
                _refreshError = ex.Message;
                Log("刷新进程失败：" + ex.Message);
            }
        }

        void OpenWav()
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "WAV (*.wav)|*.wav";
            dlg.Title = "打开要裁剪的录音";
            try { dlg.InitialDirectory = NormalizeDir(_dirBox.Text); }
            catch { }
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _lastFile = dlg.FileName;
            OpenTrim(dlg.FileName);
        }

        static string Meter(float peak)
        {
            if (peak < 0) peak = 0;
            if (peak > 1) peak = 1;
            int n = (int)(peak * 12);
            if (peak > 0 && n == 0) n = 1;
            return "[" + new string('#', n) + new string('-', 12 - n) + "]";
        }

        void OpenTrim(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                MessageBox.Show(this, "没有可以裁剪的录音。先录一段。", "LoopTap");
                return;
            }
            try
            {
                if (_trim != null && !_trim.IsDisposed)
                {
                    _trim.LoadFile(path);
                    if (_trim.WindowState == FormWindowState.Minimized)
                        _trim.WindowState = FormWindowState.Normal;
                    _trim.Activate();
                    return;
                }
                _trim = new TrimForm(path);
                _trim.Show(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "LoopTap");
            }
        }

        static string FormatDuration(double sec)
        {
            if (sec < 0) sec = 0;
            int total = (int)sec;
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            int tenth = (int)((sec - total) * 10);
            if (tenth > 9) tenth = 9;
            return string.Format("{0:00}:{1:00}:{2:00}.{3}", h, m, s, tenth);
        }

        void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            lock (_logLock)
            {
                _logs.Enqueue(line);
                while (_logs.Count > 500)
                    _logs.Dequeue();
            }
        }

        void DrainLogs()
        {
            StringBuilder sb = null;
            lock (_logLock)
            {
                if (_logs.Count == 0) return;
                sb = new StringBuilder();
                while (_logs.Count > 0)
                {
                    sb.AppendLine(_logs.Dequeue());
                }
            }
            _logBox.AppendText(sb.ToString());
            if (_logBox.TextLength > 80000)
                _logBox.Text = _logBox.Text.Substring(_logBox.TextLength - 40000);
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        string CommitDir(bool create)
        {
            string dir = NormalizeDir(_dirBox.Text);
            _dirBox.Text = dir;
            if (create)
                Directory.CreateDirectory(dir);
            try { OutputDir.Save(dir); }
            catch (Exception ex) { Log("没能记住保存位置：" + ex.Message); }
            return dir;
        }

        static string NormalizeDir(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0)
                t = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recordings");
            return Path.GetFullPath(t);
        }

        void BrowseDir()
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "录音文件保存到这个文件夹";
            dlg.ShowNewFolderButton = true;
            try { dlg.SelectedPath = NormalizeDir(_dirBox.Text); }
            catch { }
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _dirBox.Text = dlg.SelectedPath;
            try { CommitDir(false); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存位置不可用：" + ex.Message, "LoopTap");
            }
        }

        void OpenFolder()
        {
            string dir;
            try { dir = CommitDir(true); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开保存位置：" + ex.Message, "LoopTap");
                return;
            }
            try
            {
                if (!string.IsNullOrEmpty(_lastFile) && File.Exists(_lastFile))
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = "explorer.exe";
                    psi.Arguments = "/select,\"" + _lastFile + "\"";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                else
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = dir;
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开文件夹：" + ex.Message, "LoopTap");
            }
        }

        void SizeColumns()
        {
            if (_list.Columns.Count < 4) return;
            int used = _list.Columns[0].Width + _list.Columns[2].Width + _list.Columns[3].Width + 24;
            int w = _list.ClientSize.Width - used;
            if (w < 180) w = 180;
            _list.Columns[1].Width = w;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        sealed class RowRef
        {
            public int Pid;
            public long StartTicks;
            public string Name;
            public string Title;
            public int WindowCount;
        }

        static string Safe(string name)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool invalid = false;
                for (int j = 0; j < bad.Length; j++)
                {
                    if (bad[j] == c) { invalid = true; break; }
                }
                sb.Append(invalid ? '_' : c);
            }
            return sb.Length == 0 ? "proc" : sb.ToString();
        }
    }
}
