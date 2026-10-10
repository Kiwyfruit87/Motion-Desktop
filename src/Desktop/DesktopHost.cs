using System;
using System.Threading;
using WinForms = System.Windows.Forms;

namespace VideoWallpaper
{
    // 找出影片視窗要掛的位置
    class DesktopHost
    {
        public IntPtr Parent;
        public bool Raised;   // true = Win11 24H2+ 的新桌面結構

        public static DesktopHost Find()
        {
            IntPtr progman = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
            if (progman == IntPtr.Zero) return null;

            IntPtr result;
            // 0x052C：要求 Progman 在桌面圖示後面產生 WorkerW（原本是給切換桌布的淡入動畫用）
            Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), Native.SMTO_NORMAL, 1000, out result);

            for (int attempt = 0; attempt < 10; attempt++)
            {
                IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                IntPtr innerWorker = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                if (defView != IntPtr.Zero && innerWorker != IntPtr.Zero)
                    return new DesktopHost { Parent = progman, Raised = true };

                IntPtr legacy = IntPtr.Zero;
                Native.EnumWindows(delegate(IntPtr top, IntPtr lParam)
                {
                    if (top != progman && Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                        legacy = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                    return legacy == IntPtr.Zero;
                }, IntPtr.Zero);
                if (legacy != IntPtr.Zero)
                    return new DesktopHost { Parent = legacy, Raised = false };

                if (attempt == 4)
                    Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, Native.SMTO_NORMAL, 1000, out result);
                Thread.Sleep(100);
            }
            return null;
        }
    }

    // 24H2 的 Progman 沒有 redirection surface，掛在底下的子視窗必須是 layered 才畫得出來，
    // 所以先建一個 layered 容器視窗，影片畫面再畫在它裡面的 VideoWindow。
    class ContainerWindow : WinForms.NativeWindow
    {
        public ContainerWindow(IntPtr parent, int x, int y, int width, int height, bool layered)
        {
            var cp = new WinForms.CreateParams
            {
                Caption = "VideoWallpaper",
                Parent = parent,
                X = x, Y = y, Width = width, Height = height,
                Style = Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_DISABLED | Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN,
                ExStyle = layered ? Native.WS_EX_LAYERED : 0,
            };
            CreateHandle(cp);
            if (layered) Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
        }
    }

    // 影片實際畫出來的視窗（Direct3D 的輸出目標），放在 layered 容器裡面
    class VideoWindow : WinForms.NativeWindow
    {
        public Action Exposed;   // 系統要求重畫時通知播放引擎

        public VideoWindow(IntPtr parent, int width, int height)
        {
            CreateHandle(new WinForms.CreateParams
            {
                Parent = parent, Width = width, Height = height,
                Style = Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_DISABLED,
            });
        }

        protected override void WndProc(ref WinForms.Message m)
        {
            if (m.Msg == 0x0014) { m.Result = new IntPtr(1); return; }   // WM_ERASEBKGND：不用 GDI 塗底色，避免閃爍
            if (m.Msg == 0x000F)                                          // WM_PAINT：交給 Direct3D 重畫
            {
                Native.ValidateRect(m.HWnd, IntPtr.Zero);
                if (Exposed != null) Exposed();
                return;
            }
            base.WndProc(ref m);
        }
    }

    // 每個螢幕一個播放視窗（多個螢幕共用同一個播放引擎）
    class ScreenPlayer
    {
        public WinForms.Screen Screen;
        public IntPtr Monitor;
        public ContainerWindow Container;
        public VideoWindow Video;
        public int Width, Height;
        public IntPtr Hwnd;   // 容器視窗，用來調整上下順序、檢查視窗還在不在
        public bool Covered;
    }
}
