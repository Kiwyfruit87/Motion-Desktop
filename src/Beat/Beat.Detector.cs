using System;

namespace VideoWallpaper
{
    // 光暈亮度的演算法：Biquad 濾波器、Detector（跟著大鼓的衝擊感）。擷取聲音和畫面上的亮度在 Beat.cs
    static partial class Beat
    {
        // 二階濾波器（RBJ Audio EQ Cookbook）：kind 0 = 低通、1 = 帶通、2 = 高通
        class Biquad
        {
            readonly double b0, b1, b2, a1, a2;
            double z1, z2;

            public Biquad(int kind, double frequency, double q, int rate)
            {
                double w = 2 * Math.PI * frequency / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
                if (kind == 0) { b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = b0; }
                else if (kind == 1) { b0 = alpha; b1 = 0; b2 = -alpha; }
                else { b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = b0; }
                b0 /= a0; b1 /= a0; b2 /= a0;
                a1 = -2 * cos / a0; a2 = (1 - alpha) / a0;
            }

            public double Run(double x)
            {
                double y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }
        }

        // 跟著大鼓的衝擊感（即時，像音響的音量燈；不猜速度、也不判斷是不是拍子），每 2.5 毫秒算一次。
        // 1. 衝擊感：150 Hz 以下（大鼓、貝斯）快的包絡（2.5 毫秒跟上、60 毫秒退下）比慢的包絡（0.4 秒的平均）多出來的量——
        //    鼓點一進來就有，持續的低音很快就不算了；
        // 2. 跟這首歌最近最重的一下比（大約 20 秒減一半，換歌只減一半）：15% 以下不亮、六成以上全亮。
        //    記得夠久，沒有鼓的段落裡鋼琴、人聲比較低的聲音才不會被當成重拍；
        // 3. 只亮敲出來的低音，下面兩種其中一種成立，接下來 40 毫秒的低音才算數：
        //    a. 有敲擊聲：7k 以上（貝斯、鋼琴的低音碰不到）比前 15 毫秒突然變大 6 dB 以上。鼓打下去的「喀」比低音早 5～20 毫秒，
        //       所以一般的鼓組反應最快；
        //    b. 比當下的低音突出：衝擊感到了平常低音（慢的包絡）的 1.1 倍，而且 7k 以上一直有聲音（鈸、小鼓這類鼓組；
        //       比整體音量小不到 30 dB）。救鈸一直很響、「喀」跳不出來的密集段落：
        //       低音一直很滿的歌（電子舞曲厚厚的貝斯、跟著大鼓一縮一放的貝斯），大鼓疊在上面還是突出（1.3～2 倍），
        //       貝斯自己的起伏大多不到 1 倍，不會在反拍亂亮；只有貝斯、鋼琴這類高頻很少的音樂不走這條；
        //    （只有 a：密集段落四成的大鼓會漏掉；只有 b：大鼓本身就佔滿低音的歌（808、快歌）反而顯得不突出，會慢或漏）
        //    而且低音要真的正在衝上來（前 40 毫秒內衝上來過）：上一下拖很長的尾巴（808）遇到鈸，不會被當成新的一下；
        //    聽到明顯的「喀」（1.5k、3.5k、7k 一起突然變大，比低音早 5～20 毫秒）之後 25 毫秒內，
        //    低音一衝上來就先亮到八成（不用等它長到最大），光才跟得上鼓聲；
        // 4. 像不像鼓：鼓的低音一下就消失，或是音高很快往下滑（大鼓、808 都是），鋼琴、貝斯的音會延續、音高不變。
        //    每次低音衝上來，0.15 秒後看衝擊感掉了多少（掉 12 dB 以上像鼓、4 dB 以下像長音）、
        //    前 50 毫秒的音高比 60～150 毫秒高多少（高三成以上像鼓），記住最近幾次的樣子；
        //    最近都是延續、音高不變的長音（沒有鼓的歌、段落）就不亮（像鼓的程度 0.4 以下不亮、0.7 以上全亮）。
        //    看的是已經過去的聲音，不會讓光變慢；
        // 5. 沒有大鼓的段落（3 秒沒有確定像鼓的大鼓亮起來）改跟拍手、小鼓，最多亮到六成：1.5k、3.5k 都突然變大 8 dB 以上、
        //    三個頻段一共大了 70 dB 以上（很明顯的一聲「啪」；鋼琴和弦剛好碰上鈸大約 55～60），
        //    1.5k → 3.5k → 7k 一路沒有掉超過 6 dB（像噪音；鋼琴、吉他、人聲集中在中低頻，越高越小聲），
        //    1.5k 跟最近最大聲的時候差不多大聲（小 12 dB 以內）；完全安靜之後的 0.5 秒內不算（歌剛開始，每個頻段都是從零跳起來）。
        //    一有大鼓就回到只跟大鼓。鈸不跟：電子舞曲前奏、主歌的高頻每拍有 6～8 下（鈸、沙鈴、音效、齒音疊在一起），
        //    就算只挑最響的，也只有兩三成剛好落在拍子上，看起來是亂閃；鋼琴、吉他的琴槌、撥弦聲也會被當成鈸；
        // 6. 畫面上的亮度：往上幾毫秒內就跟上，往下慢慢退：平常照 ReleaseMs；打得很密（drop 前的連打）時，退的時間跟著
        //    上一下到這一下的間隔變短（間隔的一半，最快 45 毫秒），一下一下才分得出來，不會糊成一直亮著。
        // 聽到多少就亮多少，所以不規律的音樂也跟得上；沒有低音的安靜段落不亮
        class Detector
        {
            readonly Biquad low1, low2;   // 150 Hz 低通兩次（比一次乾淨，人聲、小鼓的鼓身不太會混進來）
            readonly Biquad clickFloor;   // 「喀」：先濾掉 1000 Hz 以下（很大聲的貝斯不會漏進來），再分成
            readonly Biquad[] clicks;     // 1.5k、3.5k 帶通，7k 以上高通
            readonly double[] clickSums = new double[3];
            readonly double[][] clickHistory = { new double[6], new double[6], new double[6] };   // 各頻段前 15 毫秒的音量（dB）
            readonly int blockSize;
            readonly double blockMs;
            readonly double fastUp, fastDown, slowRate, strongestKeep, glowUp, glowKeep, loudestFall;   // 每一段（2.5 毫秒）各自移動多少
            double sum, fast, slow, glow;
            double mixSum, mix = 1e-10;   // 整體音量（全部頻段，大約 0.4 秒的平均）
            readonly double[] punches = new double[6];   // 前 15 毫秒的衝擊感（看低音是不是正在衝上來）
            readonly double[] loudest = { -100, -100, -100 };   // 各頻段最近最大聲的時候（dB；大約 20 秒退 6 dB）
            readonly double[] bandRise = new double[3], bandLevel = new double[3];   // 這一段各頻段突然變大幾 dB、多大聲（dB）
            double loudestClick;          // 3.5k 最近最大聲的「喀」
            double clickLoud;             // 這一段 3.5k 多大聲（dB）
            double highLevel;             // 這一段 7k 以上多大聲（dB）
            int count, blocks;
            int struck;                   // 剛有敲擊聲、或低音剛比平常突出，接下來還有幾段的低音算數（0 = 沒有，不亮）
            int clap;                     // 剛有拍手、小鼓，接下來還有幾段照 clapHeight 亮
            double clapHeight;
            double lastTarget;            // 上一段的目標亮度（看是不是剛亮起來）
            int lastTrigger = -100000;    // 上一次亮起來是第幾段
            readonly int[] triggerTimes = new int[8];   // 最近八次亮起來是第幾段（看最近 0.75 秒亮了幾下）
            int triggers;
            double release;               // 現在往下退每一段剩多少
            public double ReleaseNow = ReleaseMs;   // 現在往下退的時間常數（毫秒；Beat 推算兩包聲音之間的亮度用）
            int lastKick = -100000;       // 上一次大鼓亮起來是第幾段
            int lastSilent;               // 上一次完全安靜（-70 dB 以下）是第幾段
            int attack;                   // 低音剛衝上來，接下來還有幾段算是「新的一下」
            int armed;                    // 剛聽到明顯的「喀」，接下來還有幾段低音一冒出來就亮到八成
            public double Strongest = 0.08;   // 這首歌最近最重的一下（衝擊感；正常音量的大鼓大約 0.1～0.2）
            public double Drumness = 0.8;     // 最近的低音像不像鼓（0～1）
            int peakUntil, measureAt;         // 正在看的那一次低音：哪一段之前找最高點、哪一段量掉了多少（0 = 沒有在看）
            double eventPeak;
            long sample, eventSample;         // 第幾個取樣；正在看的那一次從第幾個取樣開始
            double lastLow;                   // 上一個取樣的低音（找波形往上穿過 0 的時間，量音高）
            readonly double[] crossings = new double[64];   // 最近低音往上穿過 0 的時間（第幾個取樣，有小數）
            int crossingCount;
            readonly int rate;
            public Action<long, double, bool> Show = delegate { };   // 每一段算好的亮度：時間、亮度、是不是正在往下退（Beat 把它交給畫面；測試時拿來記錄）

            public Detector(int rate)
            {
                this.rate = rate;
                low1 = new Biquad(0, 150, 0.707, rate);
                low2 = new Biquad(0, 150, 0.707, rate);
                clickFloor = new Biquad(2, 1000, 0.707, rate);
                clicks = new[] { new Biquad(1, 1500, 1, rate), new Biquad(1, 3500, 1, rate), new Biquad(2, 7000, 0.707, rate) };
                blockSize = Math.Max(1, rate / 400);
                blockMs = blockSize * 1000.0 / rate;
                fastUp = 1 - Math.Exp(-blockMs / 2.5);
                fastDown = 1 - Math.Exp(-blockMs / 60);
                slowRate = 1 - Math.Exp(-blockMs / 400);
                strongestKeep = Math.Pow(0.5, blockMs / 20000);
                glowUp = 1 - Math.Exp(-blockMs / 4);
                glowKeep = Math.Exp(-blockMs / ReleaseMs);
                release = glowKeep;
                loudestFall = 6 * blockMs / 20000;
            }

            // 換歌：之前那首的鼓有多重只信一半（新的歌比較小聲的話，比較快就能亮），像不像鼓也不太確定了
            // （新的歌沒有鼓的話，最多第一個音亮四分之一）；正在亮的光照樣慢慢暗回去
            void Clear()
            {
                Strongest = Math.Max(0.02, Strongest * 0.5);
                Drumness = Math.Min(Drumness, 0.5);
            }

            // start：這段聲音第一個取樣播出去的時間
            public void Add(float[] samples, int frames, int channels, double start)
            {
                for (int f = 0; f < frames; f++)
                {
                    double x = 0;
                    for (int c = 0; c < channels; c++) x += samples[f * channels + c];
                    x /= channels;
                    mixSum += x * x;
                    double y = low2.Run(low1.Run(x));
                    sum += y * y;
                    if (lastLow < 0 && y >= 0) crossings[crossingCount++ % crossings.Length] = sample - 1 + lastLow / (lastLow - y);
                    lastLow = y;
                    sample++;
                    double h = clickFloor.Run(x);
                    for (int b = 0; b < 3; b++) { double z = clicks[b].Run(h); clickSums[b] += z * z; }
                    if (++count >= blockSize)
                    {
                        Block((long)(start + (f + 1) * (blockMs / blockSize)));   // 這一段最後一個取樣的時間
                        sum = 0; mixSum = 0; count = 0;
                        Array.Clear(clickSums, 0, 3);
                    }
                }
            }

            void Block(long time)
            {
                if (resetWanted) { resetWanted = false; Clear(); }
                double level = Math.Sqrt(sum / count);
                mix += slowRate * (mixSum / count - mix);
                if (mixSum / count < 1e-7) lastSilent = blocks;
                fast += (level > fast ? fastUp : fastDown) * (level - fast);
                slow += slowRate * (level - slow);
                double punch = Math.Max(0, fast - slow);
                Strongest = Math.Max(punch, Strongest * strongestKeep);
                // 最重的一下至少當作 0.004：歌曲淡出、幾乎沒聲音時，雜音才不會被放大成閃光
                double ratio = punch / Math.Max(Strongest, 0.004);
                // 低音正在衝上來多少：現在的衝擊感比前 15 毫秒最小的時候多出來的量（持續的貝斯不算）
                double lowest = punch;
                foreach (double before in punches) lowest = Math.Min(lowest, before);
                punches[blocks % 6] = punch;
                double rising = (punch - lowest) / Math.Max(Strongest, 0.004);
                double high;
                if (Click(out high) && clickLoud >= loudestClick - 6) armed = 10;   // 25 毫秒
                bool busyHigh = highLevel >= 10 * Math.Log10(mix + 1e-10) - 30;       // 7k 以上一直有聲音（有鼓組）
                // 60 毫秒：有些大鼓的「喀」比低音早 30～40 毫秒到（鼓身的低音要一點時間才衝起來），
                // 太短的話低音到的時候門已經關了，只微微一閃，要等下一個鈸再打開才亮，晚了快 0.1 秒
                if (high >= 6 || (busyHigh && punch >= 1.1 * slow)) struck = 24;
                if (rising >= 0.15) attack = 16;                                     // 40 毫秒
                double target = 0;
                if (struck > 0 && attack > 0)
                {
                    target = Smooth(0.15, 0.6, ratio);
                    if (armed > 0) target = Math.Max(target, 0.8 * Smooth(0.06, 0.3, rising));   // 先亮到八成，夠重的鼓再照上面亮到全亮
                }
                Judge(punch, rising, target);
                target *= Smooth(0.4, 0.7, Drumness);   // 一兩次誤判不會讓光亮起來
                if (target >= 0.3 && Drumness >= 0.6) lastKick = blocks;
                // 沒有大鼓的段落：拍手、小鼓
                if (blocks - lastKick > 1200 && blocks - lastSilent > 200 && bandRise[0] >= 8 && bandRise[1] >= 8 && bandRise[0] + bandRise[1] + bandRise[2] >= 70
                    && bandLevel[1] >= bandLevel[0] - 6 && bandLevel[2] >= bandLevel[1] - 6 && bandLevel[0] >= loudest[0] - 12)
                {
                    clap = 16;   // 40 毫秒
                    clapHeight = 0.6 * Smooth(-12, -3, bandLevel[0] - loudest[0]);
                }
                if (clap > 0) { target = Math.Max(target, clapHeight); clap--; }
                if (struck > 0) struck--;
                if (attack > 0) attack--;
                if (armed > 0) armed--;
                // 只算夠亮的（七成以上）：反拍貝斯那類微微一亮的不算。最近 0.75 秒亮了 4 下以上才算連打，退的時間是每下平均間隔的一半；
                // 一般一拍一下（180 拍也才 3 下）、偶爾多亮一下照舊，大鼓的光不會退太快、讓反拍變成一下一下的閃
                // 同一下鼓的光有時會先掉下去再回來（門開開關關），隔不到 90 毫秒的不算新的一下
                if (target >= 0.7 && lastTarget < 0.7 && (blocks - lastTrigger) * blockMs >= 90)
                {
                    triggerTimes[triggers++ % 8] = blocks;
                    int recent = 0;
                    for (int k = 0; k < Math.Min(triggers, 8); k++) if ((blocks - triggerTimes[k]) * blockMs < 750) recent++;
                    ReleaseNow = recent >= 4 ? Math.Max(45, Math.Min(ReleaseMs, 375.0 / recent)) : ReleaseMs;
                    release = Math.Exp(-blockMs / ReleaseNow);
                    lastTrigger = blocks;
                }
                lastTarget = target;
                bool falling = target <= glow;
                glow = falling ? Math.Max(target, glow * release) : glow + glowUp * (target - glow);
                Show(time, glow, falling);
            }

            // 像不像鼓：低音衝上來（會亮）時開始看，40 毫秒內找最高點，0.15 秒後看衝擊感掉了多少；
            // 還沒量到之前又衝上來一次（下一下太近），這次就不算
            void Judge(double punch, double rising, double target)
            {
                if (measureAt == 0)
                {
                    if (target < 0.3) return;
                    peakUntil = blocks + 16;
                    measureAt = blocks + 60;
                    eventPeak = punch;
                    eventSample = sample;
                    return;
                }
                if (blocks <= peakUntil) { eventPeak = Math.Max(eventPeak, punch); return; }
                if (rising >= 0.3) { measureAt = 0; return; }
                if (blocks < measureAt) return;
                double drop = 20 * Math.Log10(eventPeak / Math.Max(punch, eventPeak * 1e-3));
                double early = Pitch(0, 50), late = Pitch(60, 150);
                double slide = early > 0 && late > 0 ? early / late : 1;
                // 升得快、降得慢：有鼓的歌偶爾夾幾個貝斯音（打完低音還很滿、不像鼓），像鼓的程度不會一下子被拉下來、
                // 接下來的大鼓跟著變暗；整首都沒有鼓的話，一直不像，還是會降下去（只是多幾下）
                double score = Math.Max(Smooth(4, 12, drop), Smooth(1.08, 1.3, slide));
                Drumness += (score > Drumness ? 0.35 : 0.15) * (score - Drumness);
                measureAt = 0;
            }

            // 正在看的那一次低音，開始後 from～to 毫秒的音高（Hz，用往上穿過 0 的間隔算，至少要一個完整的週期）；量不到回傳 0
            double Pitch(double from, double to)
            {
                double start = eventSample + from * rate / 1000, end = eventSample + to * rate / 1000, first = -1, last = -1;
                int found = 0;
                for (int k = 0; k < Math.Min(crossingCount, crossings.Length); k++)
                {
                    double t = crossings[k];
                    if (t < start || t >= end) continue;
                    if (first < 0 || t < first) first = t;
                    if (t > last) last = t;
                    found++;
                }
                return found >= 2 && last > first ? (found - 1) * rate / (last - first) : 0;
            }

            // 明顯的「喀」：三個頻段這一段比前 15 毫秒最小聲的時候一共大了 30 dB 以上
            // （呼叫的人再看 3.5k 跟最近最大聲的「喀」是不是差不多大聲）；high：7k 以上大了幾 dB。
            // 比各頻段最近最大聲的時候小 40 dB 以上、或比整體音量小 45 dB 以上，都當作一樣安靜：
            // 從幾乎沒聲音到有一點點聲音不算敲擊（例如只有貝斯時，漏進高頻的那一點點）
            bool Click(out double high)
            {
                double all = 0;
                high = 0;
                int slot = blocks++ % 6;
                double silent = 10 * Math.Log10(mix + 1e-10) - 45;
                for (int b = 0; b < 3; b++)
                {
                    double db = 10 * Math.Log10(clickSums[b] / count + 1e-10);
                    double quietest = double.MaxValue;
                    foreach (double before in clickHistory[b]) quietest = Math.Min(quietest, before);
                    loudest[b] = Math.Max(db, loudest[b] - loudestFall);
                    double rise = blocks > 6 ? Math.Max(0, db - Math.Max(quietest, Math.Max(loudest[b] - 40, silent))) : 0;
                    clickHistory[b][slot] = db;
                    bandRise[b] = rise;
                    bandLevel[b] = db;
                    all += rise;
                    if (b == 2) { high = rise; highLevel = db; }
                    if (b == 1) clickLoud = db;
                }
                loudestClick = loudest[1];
                return all >= 30;
            }

            static double Smooth(double from, double to, double x)
            {
                double t = Math.Max(0, Math.Min(1, (x - from) / (to - from)));
                return t * t * (3 - 2 * t);
            }
        }
    }
}
