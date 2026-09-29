using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
            list.Sort(Compare);
            return list;
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
            return a.Pid.CompareTo(b.Pid);
        }
    }

    static class Program
    {
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
            int playing = 0;
            int titled = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Playing) playing++;
                if (list[i].Title != null && list[i].Title.Length > 0) titled++;
            }
            Console.WriteLine("共 " + list.Count + " 个进程，有窗口 " + titled + " 个，正在出声 " + playing + " 个");
            if (!string.IsNullOrEmpty(WhoPlays.LastError))
                Console.WriteLine("出声检测失败：" + WhoPlays.LastError);
            for (int i = 0; i < list.Count; i++)
            {
                ProcItem p = list[i];
                string note = p.Playing ? "正在出声" : (p.Browser ? "浏览器" : "");
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
