// VideoWallpaper — 把影片設成 Windows 桌布
// 原理：請 Explorer 在「桌面圖示」後面產生一層 WorkerW，再把我們的播放視窗塞進那一層。
//   * Win10 / Win11 23H2 以前：圖示層被搬進一個頂層 WorkerW，影片視窗掛在它後面那個 WorkerW 底下。
//   * Win11 24H2 以後：圖示層 (SHELLDLL_DefView) 與 WorkerW 都是 Progman 的子視窗，
//     影片視窗改掛在 Progman 底下，z-order 夾在兩者之間，且必須是 layered child window。
// 操作介面是點系統匣圖示後彈出的 Windows 11 風格面板（毛玻璃背景、跟隨系統深淺色與強調色）。

using System;
using System.Net;
using System.Threading;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace VideoWallpaper
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }   // Per-Monitor V2（manifest 已宣告，這裡是保險）
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;          // TLS 1.2：天氣、歌詞都是 HTTPS，.NET Framework 預設沒開

            bool isFirst;
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "VideoWallpaper_ShowPanel", out isFirst))
            {
                if (!isFirst)
                {
                    // 已經在執行了：請原本那個打開控制面板，自己直接結束
                    Native.AllowSetForegroundWindow(-1);
                    showSignal.Set();
                    return;
                }

                WinForms.Application.EnableVisualStyles();
                var app = new WallpaperApp(args.Length > 0 ? args[0] : null);
                var dispatcher = Dispatcher.CurrentDispatcher;
                HangWatch.Start();   // 畫面執行緒卡住超過 5 秒時，把卡在哪裡記到 hang.log
                ThreadPool.RegisterWaitForSingleObject(showSignal,
                    delegate { dispatcher.BeginInvoke(new Action(app.ShowPanel)); }, null, -1, false);
                Dispatcher.Run();
            }
            Environment.Exit(0);
        }
    }
}
