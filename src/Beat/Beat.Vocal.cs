using System;
using System.Collections.Generic;

namespace VideoWallpaper
{
    // 聽人聲（放大歌詞的逐字亮用）：跟邊框光暈聽同一份聲音（Beat.cs 的 loopback，只在記憶體裡算數字，不錄音、不存檔、不傳出去）。
    // 主唱通常在左右聲道的正中間、300～3500 Hz。每 10 毫秒記一筆：
    //   音量：中間聲道（左右平均）在這段頻率的音量，扣掉兩側（左右相減：立體聲寬的樂器、殘響）佔的比例（dB）
    //   開口強度：四個頻段（450、900、1800、3500 Hz 附近）的音量比前一筆變大多少（只算變大的，加起來；新的字開口時子音過後母音一出來會一起跳）
    // 留最近 10 秒給歌詞那邊用（見 LockLyrics 的 Vocal*）
    static partial class Beat
    {
        const int VocalCells = 1000;   // 10 毫秒一筆，留 10 秒
        static readonly long[] vocalTimes = new long[VocalCells];     // 那一筆是什麼時候播出去的（播放引擎的時間，毫秒）
        static readonly float[] vocalLevels = new float[VocalCells];  // 人聲大概多大聲（dB）
        static readonly float[] vocalFlux = new float[VocalCells];    // 開口強度（dB，0 以上）
        static int vocalCount;                                        // 一共記了幾筆（下一筆寫在 vocalCount % VocalCells）
        static readonly object vocalGate = new object();

        // 歌詞那邊要不要聽人聲（放大歌詞開著時）：跟邊框光暈其中一個要就開著聽
        static volatile bool glowWanted, vocalWanted;

        public static void Vocal(bool on)
        {
            vocalWanted = on;
            if (on) StartListening();
            else running = glowWanted || vocalWanted;
            if (!on) lock (vocalGate) vocalCount = 0;   // 下次打開從頭記（舊的時間已經對不上了）
        }

        // 鎖定畫面收起來：光暈、人聲都不聽了
        public static void StopAll()
        {
            glowWanted = vocalWanted = false;
            running = false;
            lock (vocalGate) vocalCount = 0;
        }

        // 最近 from（播放引擎的時間）之後的每一筆：時間、多大聲，照時間先後；回傳幾筆（陣列至少要 VocalCells 長）
        public static int VocalSince(long from, long[] times, float[] levels)
        {
            return VocalSince(from, times, levels, null);
        }

        // 同上，連開口強度一起（不要的傳 null）
        public static int VocalSince(long from, long[] times, float[] levels, float[] flux)
        {
            lock (vocalGate)
            {
                int n = Math.Min(vocalCount, VocalCells), got = 0;
                for (int i = vocalCount - n; i < vocalCount; i++)
                {
                    int at = i % VocalCells;
                    if (vocalTimes[at] < from) continue;
                    times[got] = vocalTimes[at]; levels[got] = vocalLevels[at];
                    if (flux != null) flux[got] = vocalFlux[at];
                    got++;
                }
                return got;
            }
        }

        static void PushVocal(long time, float level, float flux)
        {
            lock (vocalGate)
            {
                int at = vocalCount % VocalCells;
                vocalTimes[at] = time; vocalLevels[at] = level; vocalFlux[at] = flux;
                vocalCount++;
            }
        }

        class VocalDetector
        {
            static readonly double[] BandCenters = { 450, 900, 1800, 3500 };
            readonly Biquad[] mid, side;
            readonly Biquad[][] bands;
            readonly int blockSize, rate;
            readonly double[] bandSum, lastBand;
            double sumMid, sumSide, blockStart;
            int n;
            public VocalDetector(int rate)
            {
                this.rate = rate;
                mid = new[] { new Biquad(2, 300, 0.707, rate), new Biquad(2, 300, 0.707, rate), new Biquad(0, 3500, 0.707, rate), new Biquad(0, 3500, 0.707, rate) };
                side = new[] { new Biquad(2, 300, 0.707, rate), new Biquad(2, 300, 0.707, rate), new Biquad(0, 3500, 0.707, rate), new Biquad(0, 3500, 0.707, rate) };
                bands = new Biquad[BandCenters.Length][];
                for (int b = 0; b < bands.Length; b++) bands[b] = new[] { new Biquad(1, BandCenters[b], 1.4, rate), new Biquad(1, BandCenters[b], 1.4, rate) };
                blockSize = Math.Max(1, rate / 100);
                bandSum = new double[bands.Length];
                lastBand = new double[bands.Length];
            }

            // start：這段聲音第一個取樣播出去的時間（播放引擎的時間，毫秒）
            public void Add(float[] samples, int frames, int channels, double start)
            {
                for (int f = 0; f < frames; f++)
                {
                    double l = samples[f * channels], r = channels > 1 ? samples[f * channels + 1] : l;
                    double raw = (l + r) / 2, m = raw, s = (l - r) / 2;
                    foreach (var b in mid) m = b.Run(m);
                    foreach (var b in side) s = b.Run(s);
                    for (int b = 0; b < bands.Length; b++)
                    {
                        double x = bands[b][1].Run(bands[b][0].Run(raw));
                        bandSum[b] += x * x;
                    }
                    if (n == 0) blockStart = start + f * 1000.0 / rate;
                    sumMid += m * m; sumSide += s * s;
                    if (++n < blockSize) continue;
                    double em = sumMid / n, es = sumSide / n;
                    // 兩側佔的比例越大越不像主唱（寬的合成器、吉他、殘響）：最多扣 6 dB
                    // 最小算到 -80 dB（完全無聲時是 -120，混進來會讓「平常多大聲」變得很低，什麼都像在唱）
                    double level = Math.Max(-80, 10 * Math.Log10(em + 1e-12)) - 6 * Math.Min(1, es / (em + 1e-12));
                    double flux = 0;
                    for (int b = 0; b < bands.Length; b++)
                    {
                        double db = Math.Max(-80, 10 * Math.Log10(bandSum[b] / n + 1e-12));
                        flux += Math.Max(0, db - lastBand[b]);
                        lastBand[b] = db; bandSum[b] = 0;
                    }
                    PushVocal((long)Math.Round(blockStart + 5), (float)level, (float)(flux / bands.Length));
                    sumMid = sumSide = 0; n = 0;
                }
            }
        }
    }
}
