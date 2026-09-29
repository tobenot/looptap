using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace LoopTap
{
    static class WhoPlays
    {
        public static string LastError;

        public static void Apply(List<ProcItem> list)
        {
            Dictionary<int, List<string>> titles = WindowTitles();
            for (int i = 0; i < list.Count; i++)
            {
                List<string> wins;
                if (titles.TryGetValue(list[i].Pid, out wins) && wins.Count > 0)
                {
                    list[i].Windows = wins;
                    list[i].Title = wins[0];
                    list[i].WindowCount = wins.Count;
                }
                else
                {
                    list[i].Windows = new List<string>();
                    list[i].Title = "";
                    list[i].WindowCount = 0;
                }
            }
            UpdatePlaying(list);
        }

        public static bool UpdatePlaying(List<ProcItem> list)
        {
            Dictionary<int, int> parentOf;
            Dictionary<int, string> nameOf;
            Parents(out parentOf, out nameOf);
            HashSet<int> audio = new HashSet<int>();
            try
            {
                audio = ActiveAudioPids();
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            HashSet<int> windows = new HashSet<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Title != null && list[i].Title.Length > 0)
                    windows.Add(list[i].Pid);
                if (!nameOf.ContainsKey(list[i].Pid))
                    nameOf[list[i].Pid] = list[i].Name;
            }

            HashSet<int> mark = Targets(audio, parentOf, nameOf, windows);
            bool changed = false;
            for (int i = 0; i < list.Count; i++)
            {
                bool play = mark.Contains(list[i].Pid);
                if (play != list[i].Playing)
                {
                    list[i].Playing = play;
                    changed = true;
                }
            }
            return changed;
        }

        // ponytail: 只沿同名进程往上找窗口（chrome 子进程 → 带窗口的 chrome），最多 8 层。
        // 父进程换了名字（例如 explorer）就停，避免把声音算到别人头上。要跨进程名归因时再换进程树。
        public static HashSet<int> Targets(
            HashSet<int> audio,
            Dictionary<int, int> parentOf,
            Dictionary<int, string> nameOf,
            HashSet<int> hasWindow)
        {
            HashSet<int> mark = new HashSet<int>();
            foreach (int pid in audio)
            {
                string name = "";
                nameOf.TryGetValue(pid, out name);
                int cur = pid;
                int chosen = 0;
                HashSet<int> seen = new HashSet<int>();
                for (int hop = 0; hop < 8 && cur > 4; hop++)
                {
                    if (!seen.Add(cur)) break;
                    if (hasWindow.Contains(cur))
                    {
                        chosen = cur;
                        break;
                    }
                    int parent;
                    if (!parentOf.TryGetValue(cur, out parent) || parent <= 4 || parent == cur)
                        break;
                    string parentName = "";
                    nameOf.TryGetValue(parent, out parentName);
                    if (name.Length == 0 || !string.Equals(name, parentName, StringComparison.OrdinalIgnoreCase))
                        break;
                    cur = parent;
                }
                mark.Add(chosen != 0 ? chosen : pid);
            }
            return mark;
        }

        public static List<string> CleanTitles(List<string> raw)
        {
            List<string> list = new List<string>();
            if (raw == null) return list;
            for (int i = 0; i < raw.Count; i++)
            {
                if (list.Count >= 12) break;
                string title = raw[i] == null ? "" : raw[i].Trim();
                if (title.Length == 0) continue;
                if (title.Length > 160) title = title.Substring(0, 160);
                bool dup = false;
                for (int j = 0; j < list.Count; j++)
                {
                    if (string.Equals(list[j], title, StringComparison.Ordinal))
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup) list.Add(title);
            }
            return list;
        }

        public static void SelfCheck()
        {
            List<string> raw = new List<string>();
            raw.Add("  页A ");
            raw.Add("");
            raw.Add("页A");
            raw.Add("页B");
            List<string> clean = CleanTitles(raw);
            if (clean.Count != 2 || clean[0] != "页A" || clean[1] != "页B")
                throw new InvalidOperationException("同一进程的窗口标题没有分开保留。");
            raw.Clear();
            for (int i = 0; i < 20; i++) raw.Add("窗" + i);
            if (CleanTitles(raw).Count != 12)
                throw new InvalidOperationException("窗口标题没有在 12 条处停住。");

            Dictionary<int, int> parent = new Dictionary<int, int>();
            parent[30] = 20;
            parent[20] = 10;
            parent[10] = 1;
            Dictionary<int, string> name = new Dictionary<int, string>();
            name[30] = "chrome";
            name[20] = "chrome";
            name[10] = "chrome";
            name[1] = "explorer";
            HashSet<int> win = new HashSet<int>();
            win.Add(10);
            HashSet<int> audio = new HashSet<int>();
            audio.Add(30);
            HashSet<int> mark = Targets(audio, parent, name, win);
            if (mark.Count != 1 || !mark.Contains(10))
                throw new InvalidOperationException("同名父进程的窗口没有被标成出声。");

            win.Clear();
            win.Add(30);
            mark = Targets(audio, parent, name, win);
            if (!mark.Contains(30) || mark.Contains(10))
                throw new InvalidOperationException("出声进程自己有窗口时不应再往上找。");

            win.Clear();
            name[20] = "explorer";
            mark = Targets(audio, parent, name, win);
            if (!mark.Contains(30) || mark.Contains(20))
                throw new InvalidOperationException("不同名的父进程不应继承出声标记。");

            parent[7] = 8;
            parent[8] = 7;
            name[7] = "loop";
            name[8] = "loop";
            audio.Clear();
            audio.Add(7);
            mark = Targets(audio, parent, name, win);
            if (mark.Count != 1 || !mark.Contains(7))
                throw new InvalidOperationException("父子成环时没有停住。");
        }

        static Dictionary<int, List<string>> WindowTitles()
        {
            Dictionary<int, List<string>> map = new Dictionary<int, List<string>>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr lparam)
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (GetWindow(hwnd, 4) != IntPtr.Zero) return true;
                int style = GetWindowLong(hwnd, -20);
                if ((style & 0x80) != 0) return true;
                int cloaked;
                if (DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0)
                    return true;
                int len = GetWindowTextLength(hwnd);
                if (len <= 0) return true;
                StringBuilder sb = new StringBuilder(len + 1);
                if (GetWindowText(hwnd, sb, sb.Capacity) <= 0) return true;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid <= 4) return true;
                int key = unchecked((int)pid);
                List<string> bucket;
                if (!map.TryGetValue(key, out bucket))
                {
                    bucket = new List<string>();
                    map[key] = bucket;
                }
                bucket.Add(sb.ToString());
                return true;
            }, IntPtr.Zero);
            List<int> keys = new List<int>(map.Keys);
            for (int i = 0; i < keys.Count; i++)
                map[keys[i]] = CleanTitles(map[keys[i]]);
            return map;
        }

        static void Parents(out Dictionary<int, int> parentOf, out Dictionary<int, string> nameOf)
        {
            parentOf = new Dictionary<int, int>();
            nameOf = new Dictionary<int, string>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return;
            try
            {
                PROCESSENTRY32 pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Process32First(snap, ref pe)) return;
                do
                {
                    int pid = unchecked((int)pe.th32ProcessID);
                    parentOf[pid] = unchecked((int)pe.th32ParentProcessID);
                    nameOf[pid] = BaseName(pe.szExeFile);
                }
                while (Process32Next(snap, ref pe));
            }
            finally
            {
                CloseHandle(snap);
            }
        }

        static string BaseName(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return "";
            if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return exe.Substring(0, exe.Length - 4);
            return exe;
        }

        static HashSet<int> ActiveAudioPids()
        {
            HashSet<int> set = new HashSet<int>();
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                bool enumerated = false;
                IMMDeviceCollection col = null;
                try
                {
                    int hr = en.EnumAudioEndpoints(0, 1, out col);
                    uint count;
                    if (hr == 0 && col != null && col.GetCount(out count) == 0 && count > 0)
                    {
                        enumerated = true;
                        if (count > 32) count = 32;
                        for (uint i = 0; i < count; i++)
                        {
                            IMMDevice dev = null;
                            try
                            {
                                if (col.Item(i, out dev) == 0 && dev != null)
                                    CollectDevice(dev, set);
                            }
                            finally
                            {
                                Release(dev);
                            }
                        }
                    }
                }
                finally
                {
                    Release(col);
                }
                if (!enumerated)
                {
                    Collect(en, 0, set);
                    Collect(en, 1, set);
                    Collect(en, 2, set);
                }
            }
            finally
            {
                Release(en);
            }
            return set;
        }

        static void Collect(IMMDeviceEnumerator en, int role, HashSet<int> set)
        {
            IMMDevice device = null;
            try
            {
                int hr = en.GetDefaultAudioEndpoint(0, role, out device);
                if (hr != 0 || device == null) return;
                CollectDevice(device, set);
            }
            finally
            {
                Release(device);
            }
        }

        static void CollectDevice(IMMDevice device, HashSet<int> set)
        {
            object mgrObj = null;
            IAudioSessionEnumerator sessions = null;
            try
            {
                Guid iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                int hr = device.Activate(ref iid, 23, IntPtr.Zero, out mgrObj);
                if (hr != 0 || mgrObj == null) return;
                hr = ((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sessions);
                if (hr != 0 || sessions == null) return;
                int count;
                hr = sessions.GetCount(out count);
                if (hr != 0 || count <= 0) return;
                if (count > 256) count = 256;
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 ctl = null;
                    try
                    {
                        hr = sessions.GetSession(i, out ctl);
                        if (hr != 0 || ctl == null) continue;
                        int state;
                        if (ctl.GetState(out state) != 0 || state != 1) continue;
                        if (ctl.IsSystemSoundsSession() == 0) continue;
                        if (!LoudEnough(ctl)) continue;
                        uint pid;
                        if (ctl.GetProcessId(out pid) != 0 || pid <= 4) continue;
                        set.Add(unchecked((int)pid));
                    }
                    finally
                    {
                        Release(ctl);
                    }
                }
            }
            finally
            {
                Release(sessions);
                Release(mgrObj);
            }
        }

        static bool LoudEnough(IAudioSessionControl2 session)
        {
            IntPtr unk = IntPtr.Zero;
            IntPtr meterPtr = IntPtr.Zero;
            object meterObj = null;
            try
            {
                unk = Marshal.GetIUnknownForObject(session);
                Guid iid = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
                if (Marshal.QueryInterface(unk, ref iid, out meterPtr) != 0 || meterPtr == IntPtr.Zero)
                    return true;
                meterObj = Marshal.GetObjectForIUnknown(meterPtr);
                float peak;
                if (((IAudioMeterInformation)meterObj).GetPeakValue(out peak) != 0)
                    return true;
                return peak >= 0.01f;
            }
            catch
            {
                return true;
            }
            finally
            {
                Release(meterObj);
                if (meterPtr != IntPtr.Zero) Marshal.Release(meterPtr);
                if (unk != IntPtr.Zero) Marshal.Release(unk);
            }
        }

        static void Release(object o)
        {
            if (o == null) return;
            try { Marshal.ReleaseComObject(o); } catch { }
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lparam);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 pe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 pe);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr session);
        [PreserveSig]
        int GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr volume);
        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int count);
        [PreserveSig]
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport]
    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out int state);
        [PreserveSig]
        int GetDisplayName(out IntPtr name);
        [PreserveSig]
        int SetDisplayName(IntPtr value, IntPtr ctx);
        [PreserveSig]
        int GetIconPath(out IntPtr path);
        [PreserveSig]
        int SetIconPath(IntPtr value, IntPtr ctx);
        [PreserveSig]
        int GetGroupingParam(out Guid grouping);
        [PreserveSig]
        int SetGroupingParam(ref Guid grouping, IntPtr ctx);
        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr notify);
        [PreserveSig]
        int GetSessionIdentifier(out IntPtr id);
        [PreserveSig]
        int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig]
        int GetProcessId(out uint pid);
        [PreserveSig]
        int IsSystemSoundsSession();
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);
    }
}
