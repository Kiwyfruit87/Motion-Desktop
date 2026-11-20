using System.Diagnostics;

namespace VideoWallpaper
{
    // 動畫用的時間（毫秒，從程式啟動開始算）：播放引擎的疊圖動畫和抓拍子用同一個時鐘，排好的拍子才對得上畫面
    static class AnimationClock
    {
        static readonly Stopwatch clock = Stopwatch.StartNew();

        public static long Now { get { return clock.ElapsedMilliseconds; } }

        // 同一個時鐘，帶小數的毫秒（144Hz 每格 6.9 毫秒，用整數毫秒算位置每格會差一點點）
        public static double Precise { get { return clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency; } }
    }
}
