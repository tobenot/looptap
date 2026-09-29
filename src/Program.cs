using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LoopTap
{
    sealed class ProcItem
    {
        public string Name;
        public int Pid;
        public string Title;
        public bool Browser;
        public bool Playing;
        public long StartTicks;
        public int WindowCount;
        public List<string> Windows;
    }

    static class ScreenPlace
    {
        public static void CenterOnCursor(Form f)
        {
            Screen s = Screen.FromPoint(Cursor.Position);
            Rectangle wa = s.WorkingArea;
            int x = wa.Left + Math.Max(0, (wa.Width - f.Width) / 2);
            int y = wa.Top + Math.Max(0, (wa.Height - f.Height) / 2);
            f.StartPosition = FormStartPosition.Manual;
            f.SetBounds(x, y, f.Width, f.Height);
        }

        public static void CenterOnOwner(Form f)
        {
            if (f.Owner == null)
            {
                CenterOnCursor(f);
                return;
            }
            Rectangle wa = f.Owner.Bounds;
            int x = wa.Left + Math.Max(0, (wa.Width - f.Width) / 2);
            int y = wa.Top + Math.Max(0, (wa.Height - f.Height) / 2);
            Screen s = Screen.FromControl(f.Owner);
            if (x < s.WorkingArea.Left) x = s.WorkingArea.Left;
            if (y < s.WorkingArea.Top) y = s.WorkingArea.Top;
            f.StartPosition = FormStartPosition.Manual;
            f.SetBounds(x, y, f.Width, f.Height);
        }
    }

    static class ProcessIdentity
    {
        public static long ReadStartTicks(Process p)
        {
            try { return p.StartTime.ToUniversalTime().Ticks; }
            catch { return 0; }
        }

        public static bool Same(string listedName, long listedTicks, string liveName, long liveTicks)
        {
            if (string.IsNullOrEmpty(listedName) || string.IsNullOrEmpty(liveName)) return false;
            if (!string.Equals(listedName, liveName, StringComparison.OrdinalIgnoreCase)) return false;
            if (listedTicks == 0 || liveTicks == 0) return true;
            return listedTicks == liveTicks;
        }

        public static string FailureText(bool processAlive, string exceptionMessage)
        {
            if (!processAlive)
                return "目标进程已退出。已经写入的部分仍是合法 WAV。";
            string msg = exceptionMessage ?? "";
            if (msg.IndexOf("88890004", StringComparison.Ordinal) >= 0)
                return "播放设备失效或默认设备变了。已经写入的部分仍是合法 WAV。";
            if (msg.Length == 0) return "录制中断。";
            return msg;
        }

        public static void SelfCheck()
        {
            if (!Same("chrome", 10, "Chrome", 10))
                throw new InvalidOperationException("同一进程被当成了另一个。");
            if (Same("chrome", 10, "chrome", 11))
                throw new InvalidOperationException("PID 复用没有被认出来。");
            if (Same("chrome", 10, "firefox", 10))
                throw new InvalidOperationException("进程名不同还当成了同一个。");
            if (!Same("chrome", 0, "chrome", 10))
                throw new InvalidOperationException("拿不到启动时间时不该直接判成复用。");
            string dead = FailureText(false, "错误码 0x88890004");
            if (dead.IndexOf("进程已退出", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("进程退出和设备失效说成了同一件事。");
            string device = FailureText(true, "音频设备已失效（0x88890004）。");
            if (device.IndexOf("播放设备", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("设备失效没有单独说明。");
            string disk = FailureText(true, "磁盘已满，录音停在最后一个完整位置。");
            if (disk.IndexOf("磁盘已满", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("磁盘满的说明被盖掉了。");
        }
    }

    static class OutputDir
    {
        public static string LoadOrDefault()
        {
            string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recordings");
            try
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "looptap.ini");
                if (!File.Exists(file)) return fallback;
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("dir=") && line.Length > 4)
                        return line.Substring(4).Trim();
                }
            }
            catch { }
            return fallback;
        }

        public static void Save(string dir)
        {
            string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "looptap.ini");
            File.WriteAllText(file, "dir=" + dir + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    static class ProcCatalog
    {
        static readonly string[] BrowserNames = new string[]
        {
            "chrome", "msedge", "msedgewebview2", "firefox", "brave", "opera",
            "vivaldi", "iexplore", "chromium", "qqbrowser", "360chrome", "360se",
            "360chromex", "sogouexplorer", "maxthon", "lbbrowser", "whale", "yandex"
        };

        public static List<ProcItem> List()
        {
            List<ProcItem> list = new List<ProcItem>();
            int self = Process.GetCurrentProcess().Id;
            Process[] all = Process.GetProcesses();
            for (int i = 0; i < all.Length; i++)
            {
                Process p = all[i];
                try
                {
                    if (p.Id <= 4) continue;
                    if (p.Id == self) continue;
                    string name;
                    try { name = p.ProcessName; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;

                    ProcItem item = new ProcItem();
                    item.Name = name;
                    item.Pid = p.Id;
                    item.Title = "";
                    item.Browser = IsBrowser(name);
                    item.StartTicks = ProcessIdentity.ReadStartTicks(p);
                    item.Windows = new List<string>();
                    list.Add(item);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
            WhoPlays.Apply(list);
            list = ExpandWindows(list);
            list.Sort(Compare);
            return list;
        }

        public static List<ProcItem> ExpandWindows(List<ProcItem> list)
        {
            List<ProcItem> next = new List<ProcItem>();
            for (int i = 0; i < list.Count; i++)
            {
                ProcItem src = list[i];
                int n = src.Windows == null ? 0 : src.Windows.Count;
                if (n <= 1)
                {
                    src.WindowCount = n;
                    if (n == 1) src.Title = src.Windows[0];
                    next.Add(src);
                    continue;
                }
                for (int w = 0; w < n; w++)
                {
                    ProcItem row = new ProcItem();
                    row.Name = src.Name;
                    row.Pid = src.Pid;
                    row.Title = src.Windows[w];
                    row.Browser = src.Browser;
                    row.Playing = src.Playing;
                    row.StartTicks = src.StartTicks;
                    row.WindowCount = n;
                    row.Windows = src.Windows;
                    next.Add(row);
                }
            }
            return next;
        }

        public static void SelfCheck()
        {
            ProcItem src = new ProcItem();
            src.Name = "chrome";
            src.Pid = 5;
            src.StartTicks = 99;
            src.Playing = true;
            src.Browser = true;
            src.Windows = new List<string>();
            src.Windows.Add("页A");
            src.Windows.Add("页B");
            List<ProcItem> rows = ExpandWindows(new List<ProcItem>(new ProcItem[] { src }));
            if (rows.Count != 2 || rows[0].Title != "页A" || rows[1].Title != "页B")
                throw new InvalidOperationException("多窗口没有拆成两行。");
            if (rows[1].Pid != 5 || rows[1].StartTicks != 99 || !rows[1].Playing || rows[0].WindowCount != 2)
                throw new InvalidOperationException("拆开的窗口行丢了进程信息。");
        }

        public static bool IsBrowser(string name)
        {
            string n = name.ToLowerInvariant();
            for (int i = 0; i < BrowserNames.Length; i++)
            {
                if (n == BrowserNames[i]) return true;
            }
            if (n.IndexOf("chrome") >= 0) return true;
            if (n.IndexOf("firefox") >= 0) return true;
            if (n.IndexOf("msedge") >= 0) return true;
            return false;
        }

        static int Compare(ProcItem a, ProcItem b)
        {
            int c = (b.Playing ? 1 : 0) - (a.Playing ? 1 : 0);
            if (c != 0) return c;
            c = (b.Browser ? 1 : 0) - (a.Browser ? 1 : 0);
            if (c != 0) return c;
            int at = a.Title != null && a.Title.Length > 0 ? 1 : 0;
            int bt = b.Title != null && b.Title.Length > 0 ? 1 : 0;
            c = bt - at;
            if (c != 0) return c;
            c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = a.Pid.CompareTo(b.Pid);
            if (c != 0) return c;
            return string.Compare(a.Title, b.Title, StringComparison.Ordinal);
        }
    }

    static class Program
    {
        static Mutex _single;

        [STAThread]
        static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { }

            if (!OsInfo.IsSupported)
            {
                if (args == null || args.Length == 0)
                    MessageBox.Show(OsInfo.RequirementText, "LoopTap");
                else
                    Console.Error.WriteLine(OsInfo.RequirementText);
                return 2;
            }

            if (args != null && args.Length == 1 && args[0] == "--selfcheck")
            {
                WhoPlays.SelfCheck();
                ProcCatalog.SelfCheck();
                ProcessIdentity.SelfCheck();
                WavWriter.SelfCheck();
                WavCut.SelfCheck();
                Console.WriteLine("selfcheck ok");
                return 0;
            }

            if (args != null && args.Length == 2 && args[0] == "--trim")
            {
                if (!File.Exists(args[1]))
                {
                    MessageBox.Show("找不到录音：" + args[1], "LoopTap");
                    return 1;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrimForm(args[1]));
                return 0;
            }

            if (args == null || args.Length == 0)
            {
                bool createdNew;
                _single = new Mutex(true, @"Local\LoopTap", out createdNew);
                if (!createdNew)
                {
                    SignalExisting();
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
                {
                    Crash(e.Exception);
                    MessageBox.Show(e.Exception.Message, "LoopTap");
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Exception ex = e.ExceptionObject as Exception;
                    if (ex != null) Crash(ex);
                };
                Application.Run(new MainForm());
                return 0;
            }

            try
            {
                if (args[0] == "--list")
                    return ListCmd();
                if (args[0] == "--record")
                    return RecordCmd(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Crash(ex);
                return 1;
            }

            Console.WriteLine("用法:");
            Console.WriteLine("  LoopTap.exe                         打开窗口");
            Console.WriteLine("  LoopTap.exe --list                  列出进程");
            Console.WriteLine("  LoopTap.exe --selfcheck             检查出声归因和裁剪");
            Console.WriteLine("  LoopTap.exe --trim <wav路径>        打开裁剪窗口");
            Console.WriteLine("  LoopTap.exe --record <pid> <秒> <wav路径>");
            return 1;
        }

        static int ListCmd()
        {
            List<ProcItem> list = ProcCatalog.List();
            Console.WriteLine("内部版本=" + OsInfo.Build);
            HashSet<int> pids = new HashSet<int>();
            HashSet<int> playing = new HashSet<int>();
            int titled = 0;
            for (int i = 0; i < list.Count; i++)
            {
                pids.Add(list[i].Pid);
                if (list[i].Playing) playing.Add(list[i].Pid);
                if (list[i].Title != null && list[i].Title.Length > 0) titled++;
            }
            Console.WriteLine("共 " + pids.Count + " 个进程，有窗口 " + titled + " 个，正在出声 " + playing.Count + " 个");
            if (!string.IsNullOrEmpty(WhoPlays.LastError))
                Console.WriteLine("出声检测失败：" + WhoPlays.LastError);
            for (int i = 0; i < list.Count; i++)
            {
                ProcItem p = list[i];
                string note = p.Playing ? "正在出声" : (p.Browser ? "浏览器" : "");
                if (p.WindowCount > 1)
                    note = note.Length == 0 ? "整进程" : note + " 整进程";
                Console.WriteLine(p.Pid + "\t" + p.Name + "\t" + note + "\t" + p.Title);
            }
            return 0;
        }

        static int RecordCmd(string[] args)
        {
            if (args.Length != 4)
            {
                Console.Error.WriteLine("用法：LoopTap.exe --record <pid> <秒> <wav路径>");
                return 1;
            }
            int pid;
            int seconds;
            if (!int.TryParse(args[1], out pid) || !int.TryParse(args[2], out seconds) || pid <= 0 || seconds <= 0)
            {
                Console.Error.WriteLine("参数不正确。pid 和秒数必须是正整数。");
                return 1;
            }

            try
            {
                Process proc = Process.GetProcessById(pid);
                Console.WriteLine("目标进程 " + proc.ProcessName + " pid=" + pid);
                proc.Dispose();
            }
            catch
            {
                Console.Error.WriteLine("找不到进程 " + pid);
                return 1;
            }

            string full = Path.GetFullPath(args[3]);
            LoopbackRecorder rec = new LoopbackRecorder(pid, full, delegate(string msg)
            {
                Console.WriteLine(msg);
            });
            rec.StartAsync();
            if (!rec.WaitStarted(15000) || !rec.StartSucceeded)
            {
                string err = rec.Error ?? "启动录制失败。";
                Console.Error.WriteLine(err);
                rec.Join(8000);
                rec.Dispose();
                return 1;
            }

            Console.WriteLine("录制 " + seconds + " 秒……");
            System.Threading.Thread.Sleep(seconds * 1000);
            rec.RequestStop();
            rec.Join(8000);
            int code = 0;
            if (rec.Failed || rec.SampleRate <= 0 || rec.Channels <= 0 || rec.Bits != 32)
            {
                Console.Error.WriteLine(rec.Error ?? "录制失败。");
                code = 1;
            }
            Console.WriteLine("文件=" + rec.OutputPath);
            rec.Dispose();
            return code;
        }

        static void SignalExisting()
        {
            EnumWindows(delegate(IntPtr hwnd, IntPtr lparam)
            {
                if (!IsWindowVisible(hwnd)) return true;
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, sb.Capacity);
                if (sb.ToString().IndexOf("LoopTap", StringComparison.Ordinal) < 0) return true;
                ShowWindow(hwnd, 9);
                SetForegroundWindow(hwnd);
                return false;
            }, IntPtr.Zero);
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lparam);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int cmd);

        public static void Crash(Exception ex)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "looptap-crash.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + ex + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
