using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Threading;

namespace VideoWallpaper
{
    // 卡住監視器：程式偶爾會整個沒有回應、被 Windows 關掉，原因還沒找到。
    // 背景執行緒每秒請畫面執行緒（UI）回應一次，超過 5 秒沒回應，就把畫面執行緒當下在做什麼（呼叫堆疊）
    // 記到程式旁邊的 hang.log；同一次卡住只記一次，恢復之後再卡住會再記
    static class HangWatch
    {
        const int Limit = 5000;
        static Dispatcher ui;
        static Thread uiThread;
        static volatile int answeredAt;

        public static void Start()
        {
            ui = Dispatcher.CurrentDispatcher;
            uiThread = Thread.CurrentThread;
            answeredAt = Environment.TickCount;
            new Thread(Watch) { IsBackground = true, Name = "HangWatch", Priority = ThreadPriority.AboveNormal }.Start();
        }

        static void Watch()
        {
            bool logged = false;
            while (true)
            {
                Thread.Sleep(1000);
                ui.BeginInvoke(new Action(() => answeredAt = Environment.TickCount), DispatcherPriority.Send);
                int silent = Environment.TickCount - answeredAt;
                if (silent < Limit) { logged = false; continue; }
                if (logged) continue;
                logged = true;
                Log(silent);
            }
        }

        static void Log(int silent)
        {
            string stack;
            try
            {
#pragma warning disable 618   // 暫停別的執行緒拿堆疊是過時的做法，但這是唯一能看到卡在哪裡的辦法，只在卡住時用一次
                uiThread.Suspend();
                // 保險：拿堆疊時如果剛好要等畫面執行緒手上的鎖，兩邊會互相等、永遠卡住；2 秒後一定讓畫面執行緒繼續
                // （拿堆疊那邊就會失敗，記一行拿不到）。不讀原始碼檔案的位置，少碰一點東西
                using (new Timer(delegate { try { uiThread.Resume(); } catch { } }, null, 2000, Timeout.Infinite))
                {
                    try { stack = new StackTrace(uiThread, false).ToString(); }
                    finally { try { uiThread.Resume(); } catch { } }
                }
#pragma warning restore 618
            }
            catch (Exception ex) { stack = "（拿不到堆疊：" + ex.Message + "）"; }
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(HangWatch).Assembly.Location), "hang.log");
                File.AppendAllText(path, string.Format("{0:yyyy-MM-dd HH:mm:ss}  畫面執行緒 {1:0.0} 秒沒有回應：{2}{3}{2}",
                    DateTime.Now, silent / 1000.0, Environment.NewLine, stack));
            }
            catch { }
        }
    }
}
