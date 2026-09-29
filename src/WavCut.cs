using System;
using System.IO;

namespace LoopTap
{
    sealed class WavInfo
    {
        public string Path;
        public byte[] Fmt;
        public int Channels;
        public int Rate;
        public int Bits;
        public int BlockAlign;
        public int ChannelMask;
        public long DataOffset;
        public long DataBytes;
        public long Frames;
    }

    sealed class WavScan
    {
        public float[] Min;
        public float[] Max;
        public long AudibleStart;
        public long AudibleEnd;

        public WavScan(float[] min, float[] max, long audibleStart, long audibleEnd)
        {
            Min = min;
            Max = max;
            AudibleStart = audibleStart;
            AudibleEnd = audibleEnd;
        }
    }

    static class WavCut
    {
        public static WavInfo Open(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length < 44)
                    throw new InvalidOperationException("文件太小，不是完整的 WAV。");
                byte[] riff = new byte[12];
                ReadExact(fs, riff, 12);
                if (!Four(riff, 0, 'R', 'I', 'F', 'F') || !Four(riff, 8, 'W', 'A', 'V', 'E'))
                    throw new InvalidOperationException("不是 WAV 文件。");

                byte[] fmt = null;
                long dataOffset = -1;
                long dataBytes = 0;
                byte[] chunk = new byte[8];
                while (fs.Position + 8 <= fs.Length)
                {
                    ReadExact(fs, chunk, 8);
                    uint size = BitConverter.ToUInt32(chunk, 4);
                    long payload = fs.Position;
                    if (Four(chunk, 0, 'f', 'm', 't', ' '))
                    {
                        if (size < 16 || size > 1024)
                            throw new InvalidOperationException("WAV 的 fmt 块无法识别。");
                        fmt = new byte[size];
                        ReadExact(fs, fmt, (int)size);
                    }
                    else if (Four(chunk, 0, 'd', 'a', 't', 'a'))
                    {
                        dataOffset = payload;
                        dataBytes = size;
                        break;
                    }
                    else
                    {
                        if (payload > fs.Length - size)
                            break;
                        fs.Position = payload + size;
                    }
                    if ((size & 1) == 1 && fs.Position < fs.Length)
                        fs.Position += 1;
                }

                if (fmt == null || dataOffset < 0)
                    throw new InvalidOperationException("WAV 里没有 fmt 或 data。");

                long remain = fs.Length - dataOffset;
                if (dataBytes > remain) dataBytes = remain;

                int channels = BitConverter.ToUInt16(fmt, 2);
                int rate = BitConverter.ToInt32(fmt, 4);
                int block = BitConverter.ToUInt16(fmt, 12);
                int bits = BitConverter.ToUInt16(fmt, 14);
                ushort tag = BitConverter.ToUInt16(fmt, 0);
                bool isFloat = false;
                int mask = 0;
                if (tag == 3)
                    isFloat = true;
                else if (tag == 0xFFFE)
                {
                    if (fmt.Length < 40)
                        throw new InvalidOperationException("WAV 的 fmt 块不完整。");
                    if (BitConverter.ToInt32(fmt, 24) == 3)
                        isFloat = true;
                    mask = BitConverter.ToInt32(fmt, 20);
                }
                if (!isFloat || bits != 32 || channels < 1 || rate < 1 || block != channels * 4)
                    throw new InvalidOperationException("只能裁剪 32 位 float 录音。这个文件不是。");
                if (dataBytes < 0) dataBytes = 0;
                dataBytes -= dataBytes % block;

                WavInfo info = new WavInfo();
                info.Path = path;
                info.Fmt = fmt;
                info.Channels = channels;
                info.Rate = rate;
                info.Bits = bits;
                info.BlockAlign = block;
                info.ChannelMask = mask;
                info.DataOffset = dataOffset;
                info.DataBytes = dataBytes;
                info.Frames = dataBytes / block;
                return info;
            }
        }

        // ponytail: 一趟对数直方图，不存每帧。低于 1e-8 进静音桶，其余按 log10 每十倍 8 桶，百分位用桶下沿。
        // pHigh >= pLow*8 时阈值 = pLow*4，否则 1e-5；再夹到 [1e-5, 0.05]。
        // 和底噪贴在一起的轻声仍可能被切掉，波形把手可以拖回去。升级：让用户框一段噪声再估。
        public static float NoiseThreshold(WavInfo info)
        {
            const float floor = 1e-5f;
            const float cap = 0.05f;
            const int per = 8;
            const int steps = 8 * per;
            int[] hist = new int[steps + 1];
            long count = 0;
            if (info.Frames <= 0 || info.DataBytes <= 0 || info.BlockAlign <= 0)
                return floor;

            int block = info.BlockAlign;
            byte[] buf = new byte[65536 - (65536 % block)];
            using (FileStream fs = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Position = info.DataOffset;
                long left = info.DataBytes;
                while (left >= block)
                {
                    int want = buf.Length;
                    if (want > left) want = (int)left;
                    int got = 0;
                    while (got < want)
                    {
                        int n = fs.Read(buf, got, want - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    got -= got % block;
                    if (got <= 0) break;
                    int framesHere = got / block;
                    for (int i = 0; i < framesHere; i++)
                    {
                        int off = i * block;
                        float loud = 0;
                        for (int c = 0; c < info.Channels; c++)
                        {
                            float s = BitConverter.ToSingle(buf, off + c * 4);
                            if (float.IsNaN(s)) s = 0;
                            float a = s < 0 ? -s : s;
                            if (a > loud) loud = a;
                        }
                        int bin = 0;
                        if (loud >= 1e-8f)
                        {
                            int idx = (int)Math.Floor((Math.Log10(loud) + 8.0) * per);
                            if (idx < 0) idx = 0;
                            if (idx >= steps) idx = steps - 1;
                            bin = idx + 1;
                        }
                        hist[bin]++;
                        count++;
                    }
                    left -= got;
                }
            }
            if (count <= 0) return floor;

            long markLow = (count * 5 + 99) / 100;
            long markHigh = (count * 95 + 99) / 100;
            if (markLow < 1) markLow = 1;
            if (markHigh < 1) markHigh = 1;
            if (markHigh > count) markHigh = count;

            int lowBin = 0;
            int highBin = 0;
            bool haveLow = false;
            long acc = 0;
            for (int i = 0; i < hist.Length; i++)
            {
                if (hist[i] == 0) continue;
                acc += hist[i];
                if (!haveLow && acc >= markLow)
                {
                    lowBin = i;
                    haveLow = true;
                }
                if (acc >= markHigh)
                {
                    highBin = i;
                    break;
                }
            }

            float pLow = BinAmp(lowBin);
            float pHigh = BinAmp(highBin);
            float th = (pHigh >= pLow * 8f) ? pLow * 4f : floor;
            if (th < floor) th = floor;
            if (th > cap) th = cap;
            return th;
        }

        static float BinAmp(int bin)
        {
            if (bin <= 0) return 0;
            return (float)Math.Pow(10.0, -8.0 + (bin - 1) / 8.0);
        }

        public static WavScan Scan(WavInfo info, int columns, float threshold)
        {
            if (columns < 1) columns = 1;
            if (columns > 4000) columns = 4000;
            float[] min = new float[columns];
            float[] max = new float[columns];
            bool[] seen = new bool[columns];
            long audibleStart = 0;
            long audibleEnd = info.Frames;
            bool heard = false;
            long frame = 0;
            if (info.Frames <= 0 || info.DataBytes <= 0)
                return new WavScan(min, max, 0, 0);

            int block = info.BlockAlign;
            byte[] buf = new byte[65536 - (65536 % block)];
            using (FileStream fs = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Position = info.DataOffset;
                long left = info.DataBytes;
                while (left >= block)
                {
                    int want = buf.Length;
                    if (want > left) want = (int)left;
                    int got = 0;
                    while (got < want)
                    {
                        int n = fs.Read(buf, got, want - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    got -= got % block;
                    if (got <= 0) break;
                    int framesHere = got / block;
                    for (int i = 0; i < framesHere; i++)
                    {
                        int off = i * block;
                        float lo = 0;
                        float hi = 0;
                        float loud = 0;
                        for (int c = 0; c < info.Channels; c++)
                        {
                            float s = BitConverter.ToSingle(buf, off + c * 4);
                            if (float.IsNaN(s)) s = 0;
                            if (c == 0) { lo = s; hi = s; }
                            else
                            {
                                if (s < lo) lo = s;
                                if (s > hi) hi = s;
                            }
                            float a = s < 0 ? -s : s;
                            if (a > loud) loud = a;
                        }
                        int col = (int)((frame * (long)columns) / info.Frames);
                        if (col < 0) col = 0;
                        if (col >= columns) col = columns - 1;
                        if (!seen[col])
                        {
                            min[col] = lo;
                            max[col] = hi;
                            seen[col] = true;
                        }
                        else
                        {
                            if (lo < min[col]) min[col] = lo;
                            if (hi > max[col]) max[col] = hi;
                        }
                        if (loud >= threshold)
                        {
                            if (!heard)
                            {
                                audibleStart = frame;
                                heard = true;
                            }
                            audibleEnd = frame + 1;
                        }
                        frame++;
                    }
                    left -= got;
                }
            }
            if (!heard)
            {
                audibleStart = 0;
                audibleEnd = frame;
            }
            return new WavScan(min, max, audibleStart, audibleEnd);
        }

        public static string ChooseBackup(string wavPath, Predicate<string> taken)
        {
            string first = wavPath + ".bak.wav";
            if (!taken(first)) return first;
            for (int i = 2; i < 1000; i++)
            {
                string numbered = wavPath + ".bak" + i + ".wav";
                if (!taken(numbered)) return numbered;
            }
            throw new InvalidOperationException("备份文件太多。");
        }

        public static void SaveRange(WavInfo info, long start, long end, string dest)
        {
            if (start < 0 || end > info.Frames || end <= start)
                throw new InvalidOperationException("裁剪范围无效。");
            long frames = end - start;
            long bytes = frames * info.BlockAlign;
            byte[] header = WavWriter.Header(info.Channels, info.Rate, info.Bits, info.BlockAlign, info.ChannelMask, bytes);
            using (FileStream input = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (FileStream output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(header, 0, header.Length);
                input.Position = info.DataOffset + start * info.BlockAlign;
                long left = bytes;
                byte[] buf = new byte[1024 * 1024];
                while (left > 0)
                {
                    int want = buf.Length;
                    if (want > left) want = (int)left;
                    int got = input.Read(buf, 0, want);
                    if (got <= 0)
                        throw new EndOfStreamException("录音文件比预期短，没有改原文件。");
                    output.Write(buf, 0, got);
                    left -= got;
                }
            }
        }

        public static void SelfCheck()
        {
            string path = Path.Combine(Path.GetTempPath(), "looptap-wavcut-selfcheck.wav");
            string cut = path + ".cut";
            string quiet = path + ".quiet";
            try
            {
                int rate = 48000;
                int channels = 2;
                int block = 8;
                int silent = 1000;
                int tone = 100;
                int frames = silent + tone + silent;
                byte[] pcm = new byte[frames * block];
                byte[] half = BitConverter.GetBytes(0.5f);
                for (int i = silent; i < silent + tone; i++)
                {
                    Buffer.BlockCopy(half, 0, pcm, i * block, 4);
                    Buffer.BlockCopy(half, 0, pcm, i * block + 4, 4);
                }
                using (WavWriter w = new WavWriter(path, channels, rate, 32, block, 3))
                {
                    w.Write(pcm, pcm.Length);
                    w.Finish();
                }

                WavInfo info = Open(path);
                Check(info.Frames == frames, "帧数不对");
                Check(info.Rate == rate && info.Channels == channels && info.Bits == 32, "格式被改了");
                Check(info.DataOffset == 68, "data 不在录制时的位置");
                WavScan scan = Scan(info, 32, 0.005f);
                Check(scan.AudibleStart == silent && scan.AudibleEnd == silent + tone,
                    "有声音的范围不对：" + scan.AudibleStart + ".." + scan.AudibleEnd);

                SaveRange(info, scan.AudibleStart, scan.AudibleEnd, cut);
                byte[] expectHead = WavWriter.Header(channels, rate, 32, block, 3, tone * block);
                byte[] gotHead = new byte[68];
                byte[] got = new byte[tone * block];
                using (FileStream fs = new FileStream(cut, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    ReadExact(fs, gotHead, 68);
                    ReadExact(fs, got, got.Length);
                }
                Check(gotHead.Length == expectHead.Length, "文件头长度不对");
                for (int i = 0; i < expectHead.Length; i++)
                    Check(gotHead[i] == expectHead[i], "裁剪后的文件头和录制用的不是同一套");
                for (int i = 0; i < got.Length; i++)
                    Check(got[i] == pcm[(silent * block) + i], "留下的采样点和原文件不一致");

                WavInfo cutInfo = Open(cut);
                Check(cutInfo.Frames == tone && cutInfo.DataBytes == tone * block, "裁剪后的长度不对");

                string replaced = path + ".keep.wav";
                string part = replaced + ".part";
                File.Copy(path, replaced, true);
                SaveRange(Open(replaced), silent, silent + tone, part);
                File.Replace(part, replaced, null);
                Check(!File.Exists(part), "临时文件还在");
                WavInfo again = Open(replaced);
                Check(again.Frames == tone, "替换原文件后长度不对");
                byte[] back = new byte[tone * block];
                using (FileStream fs = new FileStream(replaced, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Position = again.DataOffset;
                    ReadExact(fs, back, back.Length);
                }
                for (int i = 0; i < back.Length; i++)
                    Check(back[i] == pcm[(silent * block) + i], "替换后的采样点和原文件不一致");

                byte[] zeros = new byte[200 * block];
                using (WavWriter w = new WavWriter(quiet, channels, rate, 32, block, 3))
                {
                    w.Write(zeros, zeros.Length);
                    w.Finish();
                }
                WavScan quietScan = Scan(Open(quiet), 8, 0.005f);
                Check(quietScan.AudibleStart == 0 && quietScan.AudibleEnd == 200, "全静音应保留整段");

                string soft = path + ".soft";
                int lead = 2000;
                int quietTone = 400;
                int hot = 400;
                int tail = 2000;
                int softFrames = lead + quietTone + hot + tail;
                byte[] softPcm = new byte[softFrames * block];
                byte[] amp002 = BitConverter.GetBytes(0.002f);
                for (int i = lead; i < lead + quietTone; i++)
                {
                    Buffer.BlockCopy(amp002, 0, softPcm, i * block, 4);
                    Buffer.BlockCopy(amp002, 0, softPcm, i * block + 4, 4);
                }
                for (int i = lead + quietTone; i < lead + quietTone + hot; i++)
                {
                    Buffer.BlockCopy(half, 0, softPcm, i * block, 4);
                    Buffer.BlockCopy(half, 0, softPcm, i * block + 4, 4);
                }
                using (WavWriter w = new WavWriter(soft, channels, rate, 32, block, 3))
                {
                    w.Write(softPcm, softPcm.Length);
                    w.Finish();
                }
                WavInfo softInfo = Open(soft);
                float softTh = NoiseThreshold(softInfo);
                WavScan softScan = Scan(softInfo, 32, softTh);
                Check(softScan.AudibleStart == lead && softScan.AudibleEnd == lead + quietTone + hot,
                    "轻开头被切掉了：" + softScan.AudibleStart + ".." + softScan.AudibleEnd + " th=" + softTh);

                string floorWav = path + ".floor";
                int noiseN = 2000;
                int toneN = 400;
                int floorFrames = noiseN + toneN + noiseN;
                byte[] floorPcm = new byte[floorFrames * block];
                byte[] amp01 = BitConverter.GetBytes(0.01f);
                for (int i = 0; i < floorFrames; i++)
                {
                    Buffer.BlockCopy(amp01, 0, floorPcm, i * block, 4);
                    Buffer.BlockCopy(amp01, 0, floorPcm, i * block + 4, 4);
                }
                for (int i = noiseN; i < noiseN + toneN; i++)
                {
                    Buffer.BlockCopy(half, 0, floorPcm, i * block, 4);
                    Buffer.BlockCopy(half, 0, floorPcm, i * block + 4, 4);
                }
                using (WavWriter w = new WavWriter(floorWav, channels, rate, 32, block, 3))
                {
                    w.Write(floorPcm, floorPcm.Length);
                    w.Finish();
                }
                WavInfo floorInfo = Open(floorWav);
                float floorTh = NoiseThreshold(floorInfo);
                WavScan floorScan = Scan(floorInfo, 32, floorTh);
                Check(floorScan.AudibleStart == noiseN && floorScan.AudibleEnd == noiseN + toneN,
                    "底噪被算进选区了：" + floorScan.AudibleStart + ".." + floorScan.AudibleEnd + " th=" + floorTh);

                string bak = ChooseBackup(path, delegate(string candidate) { return candidate.EndsWith(".bak.wav", StringComparison.Ordinal); });
                Check(bak.EndsWith(".bak2.wav", StringComparison.Ordinal), "备份文件名没有避开已有文件");
            }
            finally
            {
                try { File.Delete(path); } catch { }
                try { File.Delete(cut); } catch { }
                try { File.Delete(quiet); } catch { }
                try { File.Delete(path + ".keep.wav"); } catch { }
                try { File.Delete(path + ".keep.wav.part"); } catch { }
                try { File.Delete(path + ".soft"); } catch { }
                try { File.Delete(path + ".floor"); } catch { }
            }
        }

        static void Check(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException(msg);
        }

        static bool Four(byte[] b, int o, char a, char c, char d, char e)
        {
            return b[o] == (byte)a && b[o + 1] == (byte)c && b[o + 2] == (byte)d && b[o + 3] == (byte)e;
        }

        static void ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new EndOfStreamException("WAV 文件不完整。");
                off += n;
            }
        }
    }
}
