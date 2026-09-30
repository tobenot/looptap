using System;
using System.IO;
using System.Runtime.InteropServices;

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

    sealed class LoopHit
    {
        public long Period;
        public long Start;
        public float Confidence;

        public LoopHit(long period, long start, float confidence)
        {
            Period = period;
            Start = start;
            Confidence = confidence;
        }
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

        // ponytail: 10ms 峰值包络（最多 8192 档）上做 FFT 自相关，再在粗周期 ± 几档里用更细的 hop 核对。
        // 细档大约是周期的 1/6000。内容若自己又是整段循环，会认成更短的那档。短于 0.2 秒不搜。
        // 升级：用户框一段再搜。
        public static LoopHit FindLoop(WavInfo info)
        {
            long firstHot = 0;
            if (info == null || info.Frames < 32 || info.Rate < 1 || info.BlockAlign < 4)
                return new LoopHit(0, 0, 0);
            long hop = info.Rate / 100;
            if (hop < 1) hop = 1;
            long binsL = (info.Frames + hop - 1) / hop;
            if (binsL > 8192)
            {
                hop = (info.Frames + 8191) / 8192;
                if (hop < 1) hop = 1;
                binsL = (info.Frames + hop - 1) / hop;
            }
            if (binsL > 8192) binsL = 8192;
            if (binsL < 16) return new LoopHit(0, 0, 0);
            int bins = (int)binsL;
            float[] env = new float[bins];
            firstHot = FillEnvelope(info, 0, info.Frames, env, hop);
            if (firstHot < 0) firstHot = 0;

            int nfft = 1;
            while (nfft < bins * 2) nfft <<= 1;
            double[] re = new double[nfft];
            double[] im = new double[nfft];
            double mean = 0;
            for (int i = 0; i < bins; i++) mean += env[i];
            mean /= bins;
            double energy = 0;
            for (int i = 0; i < bins; i++)
            {
                double v = env[i] - mean;
                re[i] = v;
                energy += v * v;
            }
            if (energy < 1e-8)
                return new LoopHit(0, firstHot, 0);

            Fft(re, im, false);
            for (int i = 0; i < nfft; i++)
            {
                re[i] = re[i] * re[i] + im[i] * im[i];
                im[i] = 0;
            }
            Fft(re, im, true);
            double r0 = re[0];
            if (r0 <= 1e-12)
                return new LoopHit(0, firstHot, 0);

            long minFrames = info.Rate / 5;
            if (minFrames < 1) minFrames = 1;
            int minLag = (int)((minFrames + hop - 1) / hop);
            if (minLag < 4) minLag = 4;
            int maxLag = bins / 2;
            if (minLag >= maxLag)
                return new LoopHit(0, firstHot, 0);

            float[] corr = new float[maxLag + 1];
            for (int lag = 1; lag <= maxLag; lag++)
            {
                double c = re[lag] / r0;
                if (c < 0) c = 0;
                if (c > 1.25) c = 1.25;
                corr[lag] = (float)c;
            }

            float peak = 0;
            int peakLag = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                if (corr[lag] + 1e-4f < corr[lag - 1]) continue;
                if (lag < maxLag && corr[lag] + 1e-4f < corr[lag + 1]) continue;
                if (corr[lag] > peak)
                {
                    peak = corr[lag];
                    peakLag = lag;
                }
            }
            if (peakLag == 0 || peak < 0.40f)
                return new LoopHit(0, firstHot, peak);

            int chosen = peakLag;
            for (int k = 2; k <= 12; k++)
            {
                int guess = (int)Math.Round(peakLag / (double)k);
                if (guess < minLag) break;
                int radius = guess / 40;
                if (radius < 2) radius = 2;
                int lo = guess - radius;
                int hi = guess + radius;
                if (lo < minLag) lo = minLag;
                if (hi > maxLag) hi = maxLag;
                int at = lo;
                for (int lag = lo; lag <= hi; lag++)
                {
                    if (corr[lag] > corr[at]) at = lag;
                }
                if (at < chosen && corr[at] >= peak * 0.82f)
                    chosen = at;
            }

            double delta = 0;
            if (chosen > 0 && chosen < maxLag)
            {
                double y0 = corr[chosen - 1];
                double y1 = corr[chosen];
                double y2 = corr[chosen + 1];
                double den = y0 - 2.0 * y1 + y2;
                if (den > 1e-6 || den < -1e-6)
                    delta = 0.5 * (y0 - y2) / den;
                if (delta > 0.5) delta = 0.5;
                if (delta < -0.5) delta = -0.5;
            }
            long coarse = (long)Math.Round((chosen + delta) * (double)hop);
            if (coarse < 1) coarse = 1;
            float pearson;
            long period = RefinePeriod(info, coarse, bins, out pearson);
            if (period < 1) period = coarse;
            if (pearson < 0)
            {
                if (peak < 0.55f) return new LoopHit(0, firstHot, peak);
                return new LoopHit(period, firstHot, peak > 1f ? 1f : peak);
            }
            if (pearson < 0.72f)
                return new LoopHit(0, firstHot, pearson);
            return new LoopHit(period, firstHot, pearson > 1f ? 1f : pearson);
        }

        static long RefinePeriod(WavInfo info, long coarse, int coarseBins, out float confidence)
        {
            confidence = -1;
            if (coarse < 2 || info.Frames < coarse * 2) return coarse;
            long slop = (info.Frames * 4) / coarseBins + 1;
            long cap = coarse / 15;
            if (cap < 4) cap = 4;
            if (slop > cap) slop = cap;
            long hop = coarse / 6000;
            if (hop < 1) hop = 1;
            long span = coarse * 3 + slop;
            if (span > info.Frames) span = info.Frames;
            if (span / hop > 20000)
            {
                hop = span / 20000;
                if (hop < 1) hop = 1;
            }
            int bins = (int)(span / hop);
            if (bins < 64) return coarse;
            float[] env = new float[bins];
            long framesRead = (long)bins * hop;
            if (framesRead > info.Frames) framesRead = info.Frames;
            FillEnvelope(info, 0, framesRead, env, hop);

            int lagLo = (int)((coarse - slop) / hop);
            int lagHi = (int)((coarse + slop + hop - 1) / hop);
            if (lagLo < 1) lagLo = 1;
            if (lagHi > bins / 2) lagHi = bins / 2;
            if (lagHi - lagLo > 400) lagHi = lagLo + 400;
            if (lagHi <= lagLo) return coarse;

            int bestLag = lagLo;
            double best = -2;
            for (int lag = lagLo; lag <= lagHi; lag++)
            {
                double c = Pearson(env, lag);
                if (c > best)
                {
                    best = c;
                    bestLag = lag;
                }
            }
            confidence = (float)best;
            if (best < 0.45) return coarse;
            long refined = (long)bestLag * hop;
            if (refined < 1) return coarse;
            return refined;
        }

        static double Pearson(float[] x, int lag)
        {
            int n = x.Length - lag;
            if (n < 8) return 0;
            double sa = 0, sb = 0;
            for (int i = 0; i < n; i++)
            {
                sa += x[i];
                sb += x[i + lag];
            }
            double ma = sa / n;
            double mb = sb / n;
            double num = 0, da = 0, db = 0;
            for (int i = 0; i < n; i++)
            {
                double a = x[i] - ma;
                double b = x[i + lag] - mb;
                num += a * b;
                da += a * a;
                db += b * b;
            }
            if (da < 1e-12 || db < 1e-12) return 0;
            return num / Math.Sqrt(da * db);
        }

        static void Fft(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double tr = re[i]; re[i] = re[j]; re[j] = tr;
                    double ti = im[i]; im[i] = im[j]; im[j] = ti;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = (2.0 * Math.PI / len) * (inverse ? 1 : -1);
                double wlenRe = Math.Cos(ang);
                double wlenIm = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double wRe = 1, wIm = 0;
                    int half = len >> 1;
                    for (int j = 0; j < half; j++)
                    {
                        int u = i + j;
                        int v = u + half;
                        double vr = re[v] * wRe - im[v] * wIm;
                        double vi = re[v] * wIm + im[v] * wRe;
                        re[v] = re[u] - vr;
                        im[v] = im[u] - vi;
                        re[u] += vr;
                        im[u] += vi;
                        double nextRe = wRe * wlenRe - wIm * wlenIm;
                        wIm = wRe * wlenIm + wIm * wlenRe;
                        wRe = nextRe;
                    }
                }
            }
            if (!inverse) return;
            double inv = 1.0 / n;
            for (int i = 0; i < n; i++)
            {
                re[i] *= inv;
                im[i] *= inv;
            }
        }

        static long FillEnvelope(WavInfo info, long startFrame, long frameCount, float[] env, long hop)
        {
            long firstHot = -1;
            if (frameCount <= 0 || info.BlockAlign <= 0 || env == null || env.Length == 0) return firstHot;
            if (hop < 1) hop = 1;
            int bins = env.Length;
            int block = info.BlockAlign;
            byte[] buf = new byte[65536 - (65536 % block)];
            using (FileStream fs = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Position = info.DataOffset + startFrame * block;
                long left = frameCount * block;
                long remain = fs.Length - fs.Position;
                if (left > remain) left = remain;
                long frame = startFrame;
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
                        int col = (int)((frame - startFrame) / hop);
                        if (col >= bins) col = bins - 1;
                        if (col >= 0 && loud > env[col]) env[col] = loud;
                        if (firstHot < 0 && loud >= 1e-4f) firstHot = frame;
                        frame++;
                    }
                    left -= got;
                }
            }
            return firstHot;
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

        // 另存为的默认名：<原名><后缀>.wav，撞名加序号（原名_裁剪2.wav、原名_裁剪3.wav…）
        public static string ChooseFree(string dir, string baseName, string suffix, string ext)
        {
            Func<string, string> join = delegate(string name)
            {
                try { return Path.Combine(dir ?? "", name + ext); }
                catch { return name + ext; }
            };
            string first = baseName + suffix;
            if (!File.Exists(join(first))) return first;
            for (int i = 2; i < 1000; i++)
            {
                string numbered = baseName + suffix + i;
                if (!File.Exists(join(numbered))) return numbered;
            }
            return baseName + suffix + DateTime.Now.ToString("HHmmss");
        }

        public static float DbToAmp(double db)
        {
            if (double.IsNaN(db) || double.IsInfinity(db))
                throw new InvalidOperationException("目标电平无效。");
            return (float)Math.Pow(10.0, db / 20.0);
        }

        public static double AmpToDb(float amp)
        {
            if (!(amp > 0f) || float.IsNaN(amp) || float.IsInfinity(amp))
                return double.NegativeInfinity;
            return 20.0 * Math.Log10(amp);
        }

        public static float GainToTarget(float peak, float targetAmp)
        {
            if (!(peak > 1e-12f) || float.IsNaN(peak) || float.IsInfinity(peak))
                throw new InvalidOperationException("这段是静音，没有可放大的峰值。");
            if (!(targetAmp > 0f) || float.IsNaN(targetAmp) || float.IsInfinity(targetAmp))
                throw new InvalidOperationException("目标电平无效。");
            float gain = targetAmp / peak;
            if (!(gain > 0f) || float.IsNaN(gain) || float.IsInfinity(gain))
                throw new InvalidOperationException("归一化增益无效。");
            return gain;
        }

        public static float Peak(WavInfo info, long start, long end)
        {
            if (info == null || info.BlockAlign <= 0 || end <= start) return 0f;
            if (start < 0) start = 0;
            if (end > info.Frames) end = info.Frames;
            if (end <= start) return 0f;
            float peak = 0f;
            int block = info.BlockAlign;
            byte[] buf = new byte[65536 - (65536 % block)];
            using (FileStream fs = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Position = info.DataOffset + start * block;
                long left = (end - start) * (long)block;
                long remain = fs.Length - fs.Position;
                if (left > remain) left = remain;
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
                    int samples = got / 4;
                    for (int i = 0; i < samples; i++)
                    {
                        float s = BitConverter.ToSingle(buf, i * 4);
                        if (float.IsNaN(s) || float.IsInfinity(s)) continue;
                        float a = s < 0f ? -s : s;
                        if (a > peak) peak = a;
                    }
                    left -= got;
                }
            }
            return peak;
        }

        // ponytail: 和峰值相等的采样写成正好的目标值，其余乘同一个 float。差在 1 ulp。升级：double 增益后再修一次峰值。
        public static void ApplyGain(byte[] buf, int bytes, float gain, float peak, float target)
        {
            if (bytes <= 0 || gain == 1f) return;
            bool snap = peak > 0f && !float.IsNaN(target) && !float.IsInfinity(target);
            int n = bytes >> 2;
            for (int i = 0; i < n; i++)
            {
                int o = i << 2;
                float s = BitConverter.ToSingle(buf, o);
                float v;
                if (float.IsNaN(s) || float.IsInfinity(s))
                    v = 0f;
                else if (snap && (s == peak || s == -peak))
                    v = s < 0f ? -target : target;
                else
                    v = s * gain;
                PutSingle(buf, o, v);
            }
        }

        public static void SaveRange(WavInfo info, long start, long end, string dest)
        {
            SaveRange(info, start, end, dest, 1f, 0f, 0f);
        }

        public static void SaveRange(WavInfo info, long start, long end, string dest, float gain, float peak, float target)
        {
            if (start < 0 || end > info.Frames || end <= start)
                throw new InvalidOperationException("裁剪范围无效。");
            if (float.IsNaN(gain) || float.IsInfinity(gain) || gain <= 0f)
                throw new InvalidOperationException("归一化增益无效。");
            bool scale = gain != 1f;
            long frames = end - start;
            long bytes = frames * info.BlockAlign;
            byte[] header = WavWriter.Header(info.Channels, info.Rate, info.Bits, info.BlockAlign, info.ChannelMask, bytes);
            using (FileStream input = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (FileStream output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(header, 0, header.Length);
                input.Position = info.DataOffset + start * info.BlockAlign;
                long left = bytes;
                int chunk = 1024 * 1024;
                if (scale)
                    chunk -= chunk % info.BlockAlign;
                byte[] buf = new byte[chunk];
                while (left > 0)
                {
                    int want = buf.Length;
                    if (want > left) want = (int)left;
                    int got = 0;
                    while (got < want)
                    {
                        int n = input.Read(buf, got, want - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got <= 0)
                        throw new EndOfStreamException("录音文件比预期短，没有改原文件。");
                    if (scale)
                    {
                        int aligned = got - (got % info.BlockAlign);
                        if (aligned <= 0)
                            throw new EndOfStreamException("录音文件比预期短，没有改原文件。");
                        ApplyGain(buf, aligned, gain, peak, target);
                        output.Write(buf, 0, aligned);
                        left -= aligned;
                        if (aligned < got)
                            input.Position -= (got - aligned);
                    }
                    else
                    {
                        output.Write(buf, 0, got);
                        left -= got;
                    }
                }
            }
        }

        static void PutSingle(byte[] buf, int o, float v)
        {
            FloatBits bits = new FloatBits();
            bits.F = v;
            int i = bits.I;
            buf[o] = (byte)i;
            buf[o + 1] = (byte)(i >> 8);
            buf[o + 2] = (byte)(i >> 16);
            buf[o + 3] = (byte)(i >> 24);
        }

        [StructLayout(LayoutKind.Explicit)]
        struct FloatBits
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public int I;
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

                string loop3 = path + ".loop3";
                WriteToneLoops(loop3, rate, 0, 12000, 12000, 3, 0);
                LoopHit h3 = FindLoop(Open(loop3));
                Check(Math.Abs(h3.Period - 24000) <= 8, "三段循环周期不对：" + h3.Period + " conf=" + h3.Confidence);
                Check(h3.Start >= 0 && h3.Start <= 8, "三段循环起点不对：" + h3.Start);
                Check(h3.Confidence >= 0.7f, "三段循环置信度太低：" + h3.Confidence);

                string loopLead = path + ".loopLead";
                WriteToneLoops(loopLead, rate, 4800, 12000, 12000, 3, 0);
                LoopHit leadHit = FindLoop(Open(loopLead));
                Check(Math.Abs(leadHit.Period - 24000) <= 8, "带空白的周期不对：" + leadHit.Period + " conf=" + leadHit.Confidence);
                Check(Math.Abs(leadHit.Start - 4800) <= 8, "带空白的起点不对：" + leadHit.Start);

                string loopOnce = path + ".loopOnce";
                WriteToneLoops(loopOnce, rate, 0, 12000, 48000, 1, 0);
                LoopHit onceHit = FindLoop(Open(loopOnce));
                Check(onceHit.Period == 0, "没有重复却找出了周期：" + onceHit.Period + " conf=" + onceHit.Confidence);

                string loop14 = path + ".loop14";
                WriteToneLoops(loop14, rate, 0, 6000, 6000, 14, 3000);
                LoopHit many = FindLoop(Open(loop14));
                Check(Math.Abs(many.Period - 12000) <= 8, "十四遍周期不对：" + many.Period + " conf=" + many.Confidence);
                Check(many.Start >= 0 && many.Start <= 8, "十四遍起点不对：" + many.Start);

                string nLow = path + ".nlow";
                string nHigh = path + ".nhigh";
                string nZero = path + ".nzero";
                string nLowOut = nLow + ".out";
                string nHighOut = nHigh + ".out";
                float[] low = new float[16];
                for (int i = 0; i < 8; i++) low[i] = 0.02f;
                for (int i = 8; i < 16; i++) low[i] = 0.1f;
                low[10] = 0.04f;
                low[11] = 0f;
                WriteFloatWav(nLow, low, rate);
                WavInfo lowInfo = Open(nLow);
                float lowPeak = Peak(lowInfo, 0, lowInfo.Frames);
                float lowHead = Peak(lowInfo, 0, 8);
                Check(lowPeak == 0.1f, "0.1 峰值没读准：" + lowPeak);
                Check(lowHead == 0.02f, "选区峰值串到了后面：" + lowHead);
                float target = DbToAmp(-1.0);
                Check(Math.Abs(target - 0.891250938f) < 1e-4f, "-1 dBFS 不是约 0.891：" + target);
                float gLow = GainToTarget(lowPeak, target);
                Check(gLow > 1f, "0.1 应该放大");
                SaveRange(lowInfo, 0, lowInfo.Frames, nLowOut, gLow, lowPeak, target);
                WavInfo lowOut = Open(nLowOut);
                Check(lowOut.Rate == rate && lowOut.Bits == 32 && lowOut.Channels == 2, "归一化改了格式");
                float outPeak = Peak(lowOut, 0, lowOut.Frames);
                Check(outPeak == target, "放大后峰值不是目标：" + outPeak + " target=" + target);
                float outBig = FrameAmp(nLowOut, 8);
                float outMid = FrameAmp(nLowOut, 10);
                float outZero = FrameAmp(nLowOut, 11);
                Check(outBig == target, "峰值采样不是目标：" + outBig);
                Check(outZero == 0f, "静音采样被放大了");
                float ratioIn = 0.1f / 0.04f;
                float ratioOut = outBig / outMid;
                Check(Math.Abs(ratioOut - ratioIn) <= 1e-4f * ratioIn, "放大后波形比例变了：" + ratioIn + " -> " + ratioOut);

                float[] high = new float[8];
                for (int i = 0; i < high.Length; i++) high[i] = 0.9f;
                high[2] = 0.3f;
                WriteFloatWav(nHigh, high, rate);
                WavInfo highInfo = Open(nHigh);
                float highPeak = Peak(highInfo, 0, highInfo.Frames);
                Check(highPeak == 0.9f, "0.9 峰值没读准：" + highPeak);
                float gHigh = GainToTarget(highPeak, target);
                Check(gHigh < 1f, "高于目标时应该往下收");
                SaveRange(highInfo, 0, highInfo.Frames, nHighOut, gHigh, highPeak, target);
                float highOutPeak = Peak(Open(nHighOut), 0, high.Length);
                Check(highOutPeak == target, "往下收之后峰值不是目标：" + highOutPeak);
                float highBig = FrameAmp(nHighOut, 0);
                float highMid = FrameAmp(nHighOut, 2);
                float ratioInH = 0.9f / 0.3f;
                float ratioOutH = highBig / highMid;
                Check(Math.Abs(ratioOutH - ratioInH) <= 1e-4f * ratioInH, "往下收波形比例变了：" + ratioInH + " -> " + ratioOutH);

                float[] mute = new float[8];
                WriteFloatWav(nZero, mute, rate);
                bool refused = false;
                try { GainToTarget(Peak(Open(nZero), 0, mute.Length), target); }
                catch (InvalidOperationException ex) { refused = ex.Message.IndexOf("静音", StringComparison.Ordinal) >= 0; }
                Check(refused, "全静音没有拒绝放大");
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
                try { File.Delete(path + ".loop3"); } catch { }
                try { File.Delete(path + ".loopLead"); } catch { }
                try { File.Delete(path + ".loopOnce"); } catch { }
                try { File.Delete(path + ".loop14"); } catch { }
                try { File.Delete(path + ".nlow"); } catch { }
                try { File.Delete(path + ".nlow.out"); } catch { }
                try { File.Delete(path + ".nhigh"); } catch { }
                try { File.Delete(path + ".nhigh.out"); } catch { }
                try { File.Delete(path + ".nzero"); } catch { }
            }
        }

        static void WriteFloatWav(string path, float[] mono, int rate)
        {
            int channels = 2;
            int block = 8;
            byte[] pcm = new byte[mono.Length * block];
            for (int i = 0; i < mono.Length; i++)
            {
                byte[] sample = BitConverter.GetBytes(mono[i]);
                Buffer.BlockCopy(sample, 0, pcm, i * block, 4);
                Buffer.BlockCopy(sample, 0, pcm, i * block + 4, 4);
            }
            using (WavWriter w = new WavWriter(path, channels, rate, 32, block, 3))
            {
                w.Write(pcm, pcm.Length);
                w.Finish();
            }
        }

        static float FrameAmp(string path, int frame)
        {
            WavInfo info = Open(path);
            byte[] buf = new byte[4];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Position = info.DataOffset + (long)frame * info.BlockAlign;
                ReadExact(fs, buf, 4);
            }
            return BitConverter.ToSingle(buf, 0);
        }

        static void WriteToneLoops(string path, int rate, int lead, int burst, int gap, int reps, int tail)
        {
            int channels = 2;
            int block = 8;
            int unit = burst + gap;
            int frames = lead + reps * unit + tail;
            byte[] pcm = new byte[frames * block];
            for (int r = 0; r < reps; r++)
            {
                int origin = lead + r * unit;
                for (int j = 0; j < burst; j++)
                {
                    float s = (float)(0.5 * Math.Sin((2.0 * Math.PI * 440.0 * j) / rate));
                    byte[] sample = BitConverter.GetBytes(s);
                    int idx = (origin + j) * block;
                    Buffer.BlockCopy(sample, 0, pcm, idx, 4);
                    Buffer.BlockCopy(sample, 0, pcm, idx + 4, 4);
                }
            }
            using (WavWriter w = new WavWriter(path, channels, rate, 32, block, 3))
            {
                w.Write(pcm, pcm.Length);
                w.Finish();
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
