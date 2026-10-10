using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UIA = System.Windows.Automation;

namespace VideoWallpaper
{
    // Spotify 自己的音量（Spotify 程式右下角那條音量條，不是 Windows 的音量混音器）：
    // 透過 Windows 的協助工具介面（UI Automation，螢幕閱讀器用的那套）找到那條滑桿來讀取和設定。
    // 從外面設定時 Spotify 的音量條一格是 10%，所以只能調到 0%、10%、…、100%。
    // 跨程式呼叫一次要幾十毫秒，全部在背景執行緒做，鎖定畫面不會卡
    static class SpotifyVolume
    {
        public const double Step = 0.1;

        static readonly object gate = new object();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static Thread worker;
        static bool running;
        static bool wanted = true;    // 卡片上顯示的是 Spotify（不是的話不用一直讀，Spotify 也不用一直維持協助工具模式）
        static double pending = -1;   // 等著要設定的音量（-1 = 沒有）
        static bool pendingMute;      // 等著要按 Spotify 的靜音鈕
        static double level = -1;     // 最近讀到的音量（-1 = 找不到 Spotify 的音量條）
        static UIA.AutomationElement slider, muteButton;
        static int searchTick;

        public static bool Found { get { lock (gate) return level >= 0; } }
        public static double Level { get { lock (gate) return Math.Max(0, level); } }

        // 鎖定畫面打開時開始每半秒讀一次（上次讀到的音量先留著，卡片一出現就有東西顯示）
        public static void Start()
        {
            lock (gate)
            {
                running = true;
                if (worker == null)
                {
                    worker = new Thread(Run) { IsBackground = true, Name = "SpotifyVolume" };
                    worker.Start();
                }
            }
            wake.Set();
        }

        public static void Stop() { lock (gate) { running = false; pending = -1; pendingMute = false; } }

        // 卡片上是不是 Spotify：不是的話暫停讀取，換回 Spotify 時馬上讀一次
        public static void SetWanted(bool value)
        {
            lock (gate)
            {
                if (wanted == value) return;
                wanted = value;
            }
            if (value) wake.Set();
        }

        // 設定音量（0～1，會對齊到 10% 一格）；拖曳時連續呼叫的話，背景只會設最後一次
        public static void Set(double volume)
        {
            lock (gate) { pending = Math.Max(0, Math.Min(1, volume)); pendingMute = false; }
            wake.Set();
        }

        // 按 Spotify 自己的靜音鈕：靜音，或回到靜音前的音量（Spotify 自己記得確切的音量，不會被 10% 一格進位）。
        // expected：按完應該變成多少（找不到靜音鈕時就直接設成這個音量）
        public static void ToggleMute(double expected)
        {
            lock (gate) { pending = Math.Max(0, Math.Min(1, expected)); pendingMute = true; }
            wake.Set();
        }

        static void Run()
        {
            while (true)
            {
                bool on;
                lock (gate) on = running && wanted;
                wake.WaitOne(on ? 500 : Timeout.Infinite);
                double target;
                bool mute;
                lock (gate)
                {
                    if (!running) continue;
                    target = pending;
                    mute = pendingMute;
                    pending = -1;
                    pendingMute = false;
                    if (!wanted && target < 0 && !mute) continue;   // 卡片上不是 Spotify、也沒有要設定什麼：不讀
                }
                double result = Access(target, mute);
                lock (gate) level = result;
            }
        }

        // target ≥ 0：設定成 target（mute = true 時改按靜音鈕）；否則讀現在的音量。回傳音量，找不到音量條回傳 -1
        static double Access(double target, bool mute)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (slider == null)
                    {
                        // 找不到的話每 2 秒找一次（第一次問的時候 Spotify 才開始整理介面，要過一下才找得到）
                        if (attempt == 0 && Environment.TickCount - searchTick < 2000) return -1;
                        searchTick = Environment.TickCount;
                        slider = FindSlider();
                        if (slider == null) return -1;
                        muteButton = FindMuteButton(slider);
                    }
                    if (mute && muteButton != null)
                    {
                        mute = false;   // 只按一次（就算後面出錯重試，也改成直接設定音量，不會按兩次又切回去）
                        ((UIA.InvokePattern)muteButton.GetCurrentPattern(UIA.InvokePattern.Pattern)).Invoke();
                        return target;   // Spotify 要一下子才會更新，先當作按好了
                    }
                    var range = (UIA.RangeValuePattern)slider.GetCurrentPattern(UIA.RangeValuePattern.Pattern);
                    if (target < 0) return range.Current.Value;
                    double snapped = Math.Round(Math.Round(target / Step) * Step, 2);
                    // 注意：設定時 Spotify 會把自己的視窗叫到前景（鎖定畫面那邊會處理，不會因此收起來）
                    range.SetValue(snapped);
                    return snapped;   // Spotify 要一下子才會更新，先當作設好了，下次再讀實際的
                }
                catch { slider = muteButton = null; }   // Spotify 重開、換了介面：重新找
            }
            return -1;
        }

        // 靜音鈕：音量條旁邊的按鈕（從音量條往外一層一層找，第一個有按鈕的那層；不看名稱，Spotify 是什麼語言都一樣）
        static UIA.AutomationElement FindMuteButton(UIA.AutomationElement slider)
        {
            var walker = UIA.TreeWalker.RawViewWalker;
            var e = slider;
            for (int level = 0; level < 4; level++)
            {
                e = walker.GetParent(e);
                if (e == null) return null;
                for (var child = walker.GetFirstChild(e); child != null; child = walker.GetNextSibling(child))
                {
                    object pattern;
                    if (child.Current.ControlType == UIA.ControlType.Button && child.TryGetCurrentPattern(UIA.InvokePattern.Pattern, out pattern)) return child;
                }
            }
            return null;
        }

        // Spotify 視窗裡範圍是 0～1 的滑桿就是音量條（播放進度的單位是毫秒、側欄寬度是像素）
        static UIA.AutomationElement FindSlider()
        {
            var sliders = new UIA.PropertyCondition(UIA.AutomationElement.ControlTypeProperty, UIA.ControlType.Slider);
            foreach (var window in SpotifyWindows())
            {
                foreach (UIA.AutomationElement e in UIA.AutomationElement.FromHandle(window).FindAll(UIA.TreeScope.Descendants, sliders))
                {
                    object pattern;
                    if (!e.TryGetCurrentPattern(UIA.RangeValuePattern.Pattern, out pattern)) continue;
                    var range = ((UIA.RangeValuePattern)pattern).Current;
                    if (range.Minimum == 0 && range.Maximum == 1) return e;
                }
            }
            return null;
        }

        // Spotify 的主視窗（縮到系統匣時視窗是藏起來的，一樣找得到）
        static List<IntPtr> SpotifyWindows()
        {
            var pids = new HashSet<int>();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("Spotify")) { pids.Add(p.Id); p.Dispose(); }
            var windows = new List<IntPtr>();
            if (pids.Count == 0) return windows;
            var name = new StringBuilder(64);
            Native.EnumWindows(delegate(IntPtr h, IntPtr lParam)
            {
                int pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid) || Native.GetWindowTextLength(h) == 0) return true;
                name.Length = 0;
                Native.GetClassName(h, name, name.Capacity);
                if (name.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) windows.Add(h);
                return true;
            }, IntPtr.Zero);
            return windows;
        }
    }
}
