using System;
using System.Windows.Threading;

namespace VideoWallpaper
{
    static class UiTimer
    {
        // ms 毫秒後在 UI 執行緒做一次 action（只做一次）
        public static void After(int ms, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += delegate { timer.Stop(); action(); };
            timer.Start();
        }
    }
}
