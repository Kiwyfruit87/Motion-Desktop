using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace VideoWallpaper
{
    // 工作列透明（搭配 TranslucentTB）
    // 新版 Windows 11 上，TranslucentTB 設成「透明」之後，工作列常常整條變成黑色：
    // 它已經把工作列的背景清掉了，但檔案總管的合成狀態沒跟上，露出底下的黑色。
    // 這時送 WM_DWMCOMPOSITIONCHANGED 請檔案總管重新套用一次，就會變回透明（不會閃，也不改任何設定）。
    // 怎麼知道變黑了：看每條工作列最上面一排像素，幾乎全是純黑（#000000）就是。
    static class TaskbarFix
    {
        const uint WM_DWMCOMPOSITIONCHANGED = 0x031E, SRCCOPY = 0x00CC0020;
        const string TranslucentTBPackage = "28017CharlesMilette.TranslucentTB_v826wp6bftszj";   // 市集版的套件名稱
        static int lastFix = Environment.TickCount - 600000, wait = 3000;
        static int lastProcessCheck = Environment.TickCount - 600000;
        static bool translucentTB;
        static bool launchDone;
        static int launchTries, lastLaunch = Environment.TickCount - 600000;

        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationActivationManager
        {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
        }
        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] class ApplicationActivationManager { }

        // TranslucentTB 自己的開機啟動排在 Windows「啟動應用程式」的佇列裡，常被延後好幾分鐘；
        // 動態桌布是登入當下就啟動，所以由這邊先把它叫起來。
        // 只在動態桌布剛啟動時做：之後使用者自己關掉 TranslucentTB 的話就不再打開它。
        // 它已經在執行時再啟動一次不會有任何反應（不會多開、不會跳視窗），之後 Windows 照常啟動它也沒關係。
        static void LaunchTranslucentTB()
        {
            if (launchDone) return;
            if (translucentTB || launchTries >= 5) { launchDone = true; return; }
            if (Environment.TickCount - lastLaunch < 10000) return;   // 剛登入時系統可能還沒準備好，失敗的話 10 秒後再試
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", TranslucentTBPackage);
            if (!Directory.Exists(data)) { launchDone = true; return; }   // 沒有安裝
            launchTries++;
            lastLaunch = Environment.TickCount;
            uint pid;
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            manager.ActivateApplication(TranslucentTBPackage + "!TranslucentTB", null, 2 /* AO_NOERRORUI */, out pid);
            Marshal.ReleaseComObject(manager);
            lastProcessCheck = Environment.TickCount - 600000;   // 下次馬上重新確認有沒有在執行
        }

        // 有沒有開著 TranslucentTB（列舉所有程式比較花時間，10 秒查一次就好）
        public static bool TranslucentTBRunning
        {
            get
            {
                if (Environment.TickCount - lastProcessCheck > 10000)
                {
                    lastProcessCheck = Environment.TickCount;
                    var found = System.Diagnostics.Process.GetProcessesByName("TranslucentTB");
                    translucentTB = found.Length > 0;
                    foreach (var p in found) p.Dispose();
                }
                return translucentTB;
            }
        }

        public static void Check()
        {
            bool running = TranslucentTBRunning;
            try { LaunchTranslucentTB(); } catch { }
            if (!running) return;
            var bars = Taskbars();
            bool black = false;
            foreach (var bar in bars) if (IsBlack(bar)) black = true;
            if (!black) { wait = 3000; return; }

            // 修了還是黑的：可能工作列後面本來就是黑色（例如影片的黑邊），間隔逐漸拉長，不要一直送
            if (Environment.TickCount - lastFix < wait) return;
            lastFix = Environment.TickCount;
            wait = Math.Min(wait * 2, 60000);
            foreach (var bar in bars) Native.PostMessage(bar, WM_DWMCOMPOSITIONCHANGED, IntPtr.Zero, IntPtr.Zero);
        }

        // 主螢幕的工作列 ＋ 其他螢幕的工作列
        static List<IntPtr> Taskbars()
        {
            var list = new List<IntPtr>();
            IntPtr main = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
            if (main != IntPtr.Zero) list.Add(main);
            for (IntPtr h = IntPtr.Zero; (h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero; )
                list.Add(h);
            return list;
        }

        static bool IsBlack(IntPtr bar)
        {
            if (!Native.IsWindowVisible(bar) || Native.IsCloaked(bar)) return false;
            Native.RECT r;
            if (!Native.GetWindowRect(bar, out r)) return false;
            int width = r.Right - r.Left;
            if (width < 100 || r.Bottom - r.Top < 20) return false;   // 自動隱藏、縮起來的時候不看
            if (CoveredByForeground(r)) return false;                 // 全螢幕程式（遊戲、影片）蓋住工作列時不去讀畫面

            // 最上面第二排（第一排可能是工作列上緣的細線）
            IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen), bits;
            var info = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = 1, biPlanes = 1, biBitCount = 32 };
            IntPtr dib = Native.CreateDIBSection(screen, ref info, 0, out bits, IntPtr.Zero, 0);
            IntPtr old = Native.SelectObject(mem, dib);
            var pixels = new int[width];
            bool ok = Native.BitBlt(mem, 0, 0, width, 1, screen, r.Left, r.Top + 1, SRCCOPY);
            if (ok) Marshal.Copy(bits, pixels, 0, width);
            Native.SelectObject(mem, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
            if (!ok) return false;

            int black = 0;
            foreach (int p in pixels) if ((p & 0xFFFFFF) == 0) black++;
            return black >= width * 97 / 100;
        }

        static bool CoveredByForeground(Native.RECT bar)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            string cls = Native.ClassOf(fg);
            if (cls.StartsWith("Shell_") || cls == "Progman" || cls == "WorkerW") return false;
            Native.RECT r;
            return Native.GetWindowRect(fg, out r) && r.Left <= bar.Left && r.Top <= bar.Top && r.Right >= bar.Right && r.Bottom >= bar.Bottom;
        }
    }
}
