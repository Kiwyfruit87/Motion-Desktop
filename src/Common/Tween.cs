namespace VideoWallpaper
{
    // 一個隨時間變化的數值：從 From 慢慢變到 To（Start 開始、花 Ms 毫秒；Ms = 0 就是停在 To）。
    // 播放引擎的疊圖動畫（淡入淡出、捲動、滑動、展開）都用它，時間是 AnimationClock 的毫秒
    struct Tween
    {
        public double From, To;
        public long Start;
        public int Ms;

        public Tween(double value) { From = To = value; Start = 0; Ms = 0; }

        public Tween(double from, double to, long start, int ms) { From = from; To = to; Start = start; Ms = ms; }

        public bool Running(long now) { return Ms > 0 && now - Start < Ms; }

        // 現在的值（ChromeOS 的標準曲線，開頭和結尾都柔和）
        public double Value(long now)
        {
            if (Ms <= 0 || now - Start >= Ms) return To;
            return From + (To - From) * Easing.StandardCurve((now - Start) / (double)Ms);
        }

        // 從現在的值接著變到 to，花 ms 毫秒
        public void Retarget(double to, int ms, long now)
        {
            From = Value(now);
            To = to;
            Start = now;
            Ms = ms;
        }

        // 停在現在的值
        public void Freeze(long now)
        {
            From = To = Value(now);
            Ms = 0;
        }
    }
}
