using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LoopTap
{
    static class OsInfo
    {
        public static readonly int Build = ReadBuild();

        public static bool IsSupported
        {
            get { return Build >= 19041; }
        }

        public static string RequirementText
        {
            get
            {
                return "需要 Windows 10 2004（内部版本 19041）或更高版本才能使用进程回环。当前内部版本 " + Build + "。";
            }
        }

        static int ReadBuild()
        {
            OsVersion v = new OsVersion();
            v.Size = Marshal.SizeOf(typeof(OsVersion));
            if (RtlGetVersion(ref v) == 0 && v.Build > 0)
                return v.Build;
            return Environment.OSVersion.Version.Build;
        }

        [DllImport("ntdll.dll")]
        static extern int RtlGetVersion(ref OsVersion info);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OsVersion
        {
            public int Size;
            public int Major;
            public int Minor;
            public int Build;
            public int Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Csd;
        }
    }

    static class AudioErrors
    {
        public static string Explain(int hr)
        {
            uint u = unchecked((uint)hr);
            if (u == 0x80070057) return "参数无效（0x80070057）。请确认目标进程仍在运行。";
            if (u == 0x80070005) return "拒绝访问（0x80070005）。本工具不会提权。";
            if (u == 0x80070490) return "找不到进程回环设备（0x80070490）。需要 Windows 10 2004 或更高版本。";
            if (u == 0x80040154) return "音频接口未注册（0x80040154）。";
            if (u == 0x80004002) return "接口不存在（0x80004002）。";
            if (u == 0x8000000E) return "调用不被接受（0x8000000E，E_ILLEGAL_METHOD_CALL）。";
            if (u == 0x80004001) return "未实现（0x80004001）。";
            if (u == 0x8001010E) return "线程单元不匹配（0x8001010E）。";
            if (u == 0x88890001) return "音频客户端未初始化（0x88890001）。";
            if (u == 0x88890002) return "音频客户端已初始化（0x88890002）。";
            if (u == 0x88890003) return "端点类型不符合（0x88890003）。";
            if (u == 0x88890004) return "音频设备已失效（0x88890004）。目标进程可能已退出，或播放设备中途变了。";
            if (u == 0x88890008) return "引擎拒绝了该格式（0x88890008）。程序不会改用别的格式。";
            if (u == 0x8889000A) return "音频设备正被独占（0x8889000A）。";
            if (u == 0x88890010) return "Windows Audio 服务没有运行（0x88890010）。";
            if (u == 0x88890019) return "缓冲区长度未对齐（0x88890019）。";
            return "错误码 0x" + hr.ToString("X8");
        }
    }

    sealed class MixFormat
    {
        public static readonly Guid FloatSub = new Guid("00000003-0000-0010-8000-00aa00389b71");

        public int Tag;
        public int Channels;
        public int SampleRate;
        public int AvgBytesPerSec;
        public int Bits;
        public int BlockAlign;
        public int ExtraBytes;
        public int ChannelMask;
        public int ValidBits;
        public bool IsFloat;
        public Guid SubFormat;

        public string Describe()
        {
            string ch;
            if (Channels == 1) ch = "单声道";
            else if (Channels == 2) ch = "立体声";
            else ch = Channels + " 声道";
            string kind = IsFloat ? "float" : "非 float";
            return string.Format(
                "{0} Hz，{1}，{2} 位 {3}，块对齐 {4} 字节，标签 0x{5:X4}，子格式 {6}",
                SampleRate, ch, Bits, kind, BlockAlign, Tag, SubFormat.ToString());
        }

        public static MixFormat Parse(IntPtr p)
        {
            if (p == IntPtr.Zero)
                throw new InvalidOperationException("GetMixFormat 返回了空格式。");

            MixFormat m = new MixFormat();
            m.Tag = U16(p, 0);
            m.Channels = U16(p, 2);
            m.SampleRate = Marshal.ReadInt32(IntPtr.Add(p, 4));
            m.AvgBytesPerSec = Marshal.ReadInt32(IntPtr.Add(p, 8));
            m.BlockAlign = U16(p, 12);
            m.Bits = U16(p, 14);
            m.ExtraBytes = U16(p, 16);

            if (m.Tag == 3 && m.Bits == 32)
                m.IsFloat = true;

            if (m.Tag == 0xFFFE && m.ExtraBytes >= 22)
            {
                m.ValidBits = U16(p, 18);
                m.ChannelMask = Marshal.ReadInt32(IntPtr.Add(p, 20));
                byte[] g = new byte[16];
                Marshal.Copy(IntPtr.Add(p, 24), g, 0, 16);
                m.SubFormat = new Guid(g);
                m.IsFloat = m.Bits == 32 && m.SubFormat == FloatSub;
            }

            if (m.Channels <= 0 || m.SampleRate <= 0 || m.BlockAlign <= 0)
                throw new InvalidOperationException("引擎混音格式无效：" + m.Describe());

            return m;
        }

        static int U16(IntPtr p, int offset)
        {
            return (ushort)Marshal.ReadInt16(IntPtr.Add(p, offset));
        }
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig]
        int GetBufferSize(out uint numBufferFrames);
        [PreserveSig]
        int GetStreamLatency(out long latency);
        [PreserveSig]
        int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig]
        int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig]
        int Start();
        [PreserveSig]
        int Stop();
        [PreserveSig]
        int Reset();
        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);
        [PreserveSig]
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint numFrames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig]
        int ReleaseBuffer(uint numFramesRead);
        [PreserveSig]
        int GetNextPacketSize(out uint numFrames);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    // 空接口。进程回环要求完成回调同时是敏捷对象，否则 ActivateAudioInterfaceAsync 返回 0x8000000E。
    [ComImport]
    [Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject
    {
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivationCompletion : IActivateAudioInterfaceCompletionHandler, IAgileObject, ICustomQueryInterface
    {
        static readonly Guid AgileIid = new Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
        {
            if (iid == AgileIid)
            {
                // IAgileObject 的虚表就是 IUnknown。直接交出自身 IUnknown，避免再进一次 QueryInterface。
                ppv = Marshal.GetIUnknownForObject(this);
                return CustomQueryInterfaceResult.Handled;
            }
            ppv = IntPtr.Zero;
            return CustomQueryInterfaceResult.NotHandled;
        }

        public int MethodHr;
        public int ActivateHr;
        public object Activated;
        public string Error;
        public readonly ManualResetEvent Done = new ManualResetEvent(false);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                if (operation == null)
                {
                    MethodHr = unchecked((int)0x80004003);
                    Error = "激活回调没有返回操作对象。";
                    return;
                }
                object obj;
                int ahr;
                MethodHr = operation.GetActivateResult(out ahr, out obj);
                ActivateHr = ahr;
                Activated = obj;
            }
            catch (Exception ex)
            {
                MethodHr = unchecked((int)0x80004005);
                ActivateHr = MethodHr;
                Error = ex.Message;
            }
            finally
            {
                Done.Set();
            }
        }
    }

    static class ProcessLoopback
    {
        public const uint StreamFlagsLoopback = 0x00020000;
        public const uint StreamFlagsEventCallback = 0x00040000;
        const int ActivationTypeProcessLoopback = 1;
        const int IncludeTargetProcessTree = 0;
        const ushort VtBlob = 65;

        [DllImport("mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid riid,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [StructLayout(LayoutKind.Sequential)]
        struct ActivationParams
        {
            public int ActivationType;
            public uint TargetProcessId;
            public int ProcessLoopbackMode;
        }

        // x64 PROPVARIANT：vt 在 0，BLOB.cbSize 在 8，BLOB.pBlobData 在 16。
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct BlobVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public uint cbSize;
            [FieldOffset(16)] public IntPtr pBlobData;
        }

        public static IAudioClient Activate(int pid)
        {
            if (IntPtr.Size != 8)
                throw new InvalidOperationException("LoopTap 只支持 64 位进程。");
            if (Marshal.SizeOf(typeof(ActivationParams)) != 12)
                throw new InvalidOperationException("进程回环激活参数大小异常。");

            ActivationParams ap = new ActivationParams();
            ap.ActivationType = ActivationTypeProcessLoopback;
            ap.TargetProcessId = unchecked((uint)pid);
            ap.ProcessLoopbackMode = IncludeTargetProcessTree;

            int paramSize = Marshal.SizeOf(typeof(ActivationParams));
            IntPtr pParams = Marshal.AllocHGlobal(paramSize);
            IntPtr pVar = Marshal.AllocHGlobal(24);
            ActivationCompletion handler = new ActivationCompletion();
            IActivateAudioInterfaceAsyncOperation op = null;
            try
            {
                Zero(pParams, paramSize);
                Zero(pVar, 24);
                Marshal.StructureToPtr(ap, pParams, false);

                BlobVariant blob = new BlobVariant();
                blob.vt = VtBlob;
                blob.cbSize = unchecked((uint)paramSize);
                blob.pBlobData = pParams;
                Marshal.StructureToPtr(blob, pVar, false);

                Guid iid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                int hr = ActivateAudioInterfaceAsync(
                    "VAD\\Process_Loopback",
                    ref iid,
                    pVar,
                    handler,
                    out op);
                if (hr < 0)
                    throw Fail("ActivateAudioInterfaceAsync", hr);
                if (op == null)
                    throw new InvalidOperationException("ActivateAudioInterfaceAsync 没有返回异步操作。");
                if (!handler.Done.WaitOne(8000))
                    throw new InvalidOperationException("等待进程回环接口激活超时。");
                if (!string.IsNullOrEmpty(handler.Error) && handler.Activated == null)
                    throw new InvalidOperationException("激活进程回环失败：" + handler.Error);
                if (handler.MethodHr < 0)
                    throw Fail("GetActivateResult", handler.MethodHr);
                if (handler.ActivateHr < 0)
                    throw Fail("激活进程回环", handler.ActivateHr);
                IAudioClient client = handler.Activated as IAudioClient;
                if (client == null)
                    throw new InvalidOperationException("激活结果不是 IAudioClient。");
                return client;
            }
            finally
            {
                if (op != null)
                {
                    try { Marshal.ReleaseComObject(op); } catch { }
                }
                Marshal.FreeHGlobal(pVar);
                Marshal.FreeHGlobal(pParams);
                handler.Done.Close();
                GC.KeepAlive(handler);
            }
        }

        static void Zero(IntPtr p, int n)
        {
            byte[] z = new byte[n];
            Marshal.Copy(z, 0, p, n);
        }

        public static Exception Fail(string action, int hr)
        {
            return new InvalidOperationException(action + "失败：" + AudioErrors.Explain(hr));
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);
        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    static class EngineMix
    {
        public static IntPtr Obtain(IAudioClient loopback, Action<string> log, out MixFormat parsed)
        {
            IntPtr mix;
            int hr = loopback.GetMixFormat(out mix);
            if (hr >= 0 && mix != IntPtr.Zero)
            {
                parsed = MixFormat.Parse(mix);
                log("已从进程回环客户端 GetMixFormat 取得引擎格式：" + parsed.Describe());
                return mix;
            }

            if (unchecked((uint)hr) != 0x80004001)
                throw ProcessLoopback.Fail("GetMixFormat", hr);

            // 当前 Windows 11 上，进程回环的 IAudioClient::GetMixFormat 返回 E_NOTIMPL。
            // 引擎混音格式在默认播放设备上。只读取，不改设备、不造格式。
            log("进程回环客户端的 GetMixFormat 返回未实现。改为读取默认播放设备的 GetMixFormat。这仍是引擎混音格式，没有自造格式，也不打开自动转换。");
            mix = DefaultRenderMix();
            parsed = MixFormat.Parse(mix);
            log("默认播放设备 GetMixFormat：" + parsed.Describe());
            return mix;
        }

        static IntPtr DefaultRenderMix()
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice device = null;
            IAudioClient client = null;
            try
            {
                int hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                if (hr < 0)
                    throw ProcessLoopback.Fail("读取默认播放设备", hr);
                Guid iid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                object activated;
                hr = device.Activate(ref iid, 23, IntPtr.Zero, out activated);
                if (hr < 0)
                    throw ProcessLoopback.Fail("打开默认播放设备", hr);
                client = activated as IAudioClient;
                if (client == null)
                    throw new InvalidOperationException("默认播放设备没有返回 IAudioClient。");
                IntPtr mix;
                hr = client.GetMixFormat(out mix);
                if (hr < 0 || mix == IntPtr.Zero)
                    throw ProcessLoopback.Fail("默认播放设备 GetMixFormat", hr);
                return mix;
            }
            finally
            {
                if (client != null)
                {
                    try { Marshal.ReleaseComObject(client); } catch { }
                }
                if (device != null)
                {
                    try { Marshal.ReleaseComObject(device); } catch { }
                }
                try { Marshal.ReleaseComObject(enumerator); } catch { }
            }
        }
    }
}
