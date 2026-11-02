using System;

namespace VideoWallpaper
{
    // 一個隨時間變化的數值：從 From 慢慢變到 To（Start 開始、花 Ms 毫秒；Ms = 0 就是停在 To）。
    // 播放引擎的疊圖動畫（淡入淡出、捲動、滑動、展開）都用它，時間是 AnimationClock 的毫秒
    struct Tween
    {
        public double From, To;
        public long Start;
        public int Ms;
        public double V0;   // 開始時接著的速度（每毫秒；見 Continue），途中慢慢消失

        public Tween(double value) { From = To = value; Start = 0; Ms = 0; V0 = 0; }

        public Tween(double from, double to, long start, int ms) { From = from; To = to; Start = start; Ms = ms; V0 = 0; }

        public bool Running(long now) { return Ms > 0 && now - Start < Ms; }

        // 現在的值（ChromeOS 的標準曲線，開頭和結尾都柔和）
        public double Value(long now)
        {
            if (Ms <= 0 || now - Start >= Ms) return To;
            double u = Math.Max(0, (now - Start) / (double)Ms);
            // 接著的速度：u (1 − u)² 開頭的斜率是 1、結尾是 0，所以一開始照原本的速度走，到終點時剛好消失
            return From + (To - From) * Easing.StandardCurve(u) + V0 * Ms * u * (1 - u) * (1 - u);
        }

        // 現在的速度（每毫秒）
        public double Velocity(long now)
        {
            if (!Running(now) || now < Start) return 0;
            long before = Math.Max(now - 1, Start);   // 剛開始的那一毫秒往前沒有東西：只往後算（不然只算到一半的速度）
            return (Value(now + 1) - Value(before)) / (now + 1 - before);
        }

        // 從現在的值接著變到 to，花 ms 毫秒
        public void Retarget(double to, int ms, long now)
        {
            From = Value(now);
            To = to;
            Start = now;
            Ms = ms;
            V0 = 0;
        }

        // 同上，但接著現在的速度（捲動、滑動途中又換目標時不會先停一下再重新加速）。
        // 接著的速度有上限：最多「距離 / 時間」（這樣不會衝過目標再退回來）；新目標在反方向時只接三成（先順順地慢下來再回頭）。
        // 不限的話，拖進度條時歌詞捲得很快，剛好換到下一行（距離只有一行）會帶著整串的速度衝過頭好幾行再彈回來
        public void Continue(double to, int ms, long now)
        {
            double v = Velocity(now);
            Retarget(to, ms, now);
            double d = to - From, max = ms > 0 ? Math.Abs(d) / ms : 0;
            if (v * d < 0) max *= 0.3;
            V0 = Math.Max(-max, Math.Min(max, v));
        }

        // 停在現在的值
        public void Freeze(long now)
        {
            From = To = Value(now);
            Ms = 0;
            V0 = 0;
        }
    }
}
