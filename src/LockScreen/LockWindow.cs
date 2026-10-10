using System;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace VideoWallpaper
{
    // 鎖定畫面的全螢幕視窗（每個螢幕一個），影片直接由播放引擎畫進來
    class LockWindow : WinForms.NativeWindow
    {
        public Action Dismiss, Exposed;
        public Action<int> Leave;            // 被切到別的程式（參數：切過去的那個程式的執行緒；Alt+F4 時是 0）
        public Func<int, int, bool> Click;   // 回傳 true = 點到「正在播放」的按鈕，不要滑走
        public Action<int, int> Hover;       // 滑鼠移動（停在喇叭上展開音量條用）
        public Action<int, int> Drag;        // 按著左鍵拖曳音量滑桿（StartDrag 之後才會收到）
        public Action Drop;                  // 放開左鍵
        public Func<int, int, int, bool> Wheel;   // 滑鼠滾輪（位置、轉了多少）；回傳 true = 用掉了
        public System.Drawing.Rectangle Bounds { get; private set; }
        bool dragging;

        // 開始拖曳：滑鼠移到別的螢幕也繼續收到移動，直到放開左鍵。
        // 拖曳中前景被搶走（Spotify 設定音量時會跳到前景）可能會失去這個「抓住」，
        // 不過鎖定畫面蓋滿整個螢幕，移動一樣會送到這裡，所以拖曳照常繼續
        public void StartDrag()
        {
            dragging = true;
            Native.SetCapture(Handle);
        }

        public void EndDrag()
        {
            if (!dragging) return;
            dragging = false;
            Native.ReleaseCapture();
        }

        // 滑鼠左鍵現在是不是按著（實際的按鍵狀態；左右鍵對調的話看右鍵）
        public static bool LeftButtonDown()
        {
            int key = Native.GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 ? 0x02 : 0x01;
            return (Native.GetAsyncKeyState(key) & 0x8000) != 0;
        }
        public bool PaintBlack = true;   // 影片畫上來之後就不要再用 GDI 塗黑（會蓋掉影片、閃一下黑）

        // 滑鼠游標：移動滑鼠時才出現（才點得到播放按鈕），停一下就由 LockScreen 藏起來
        public static bool CursorShown;
        public static DateTime LastMove;
        static Native.POINT lastPos;
        static readonly IntPtr Arrow = Native.LoadCursor(IntPtr.Zero, 32512);

        public static void ResetCursor()
        {
            CursorShown = false;
            Native.GetCursorPos(out lastPos);
        }

        public LockWindow(System.Drawing.Rectangle bounds, int y)
        {
            Bounds = bounds;
            CreateHandle(new WinForms.CreateParams
            {
                Caption = Lang.T("動態桌布鎖定畫面", "Motion Desktop lock screen"),
                X = bounds.X, Y = y, Width = bounds.Width, Height = bounds.Height,
                Style = unchecked((int)0x80000000),   // WS_POPUP（先不顯示，滑動時才顯示）
                ExStyle = 0x8 | 0x80,                 // WS_EX_TOPMOST | WS_EX_TOOLWINDOW
            });
        }

        protected override void WndProc(ref WinForms.Message m)
        {
            switch (m.Msg)
            {
                case 0x0014:   // WM_ERASEBKGND：影片畫上來之前先塗黑，不會閃白
                    if (PaintBlack)
                    {
                        Native.RECT r;
                        Native.GetClientRect(m.HWnd, out r);
                        Native.FillRect(m.WParam, ref r, Native.GetStockObject(4));
                    }
                    m.Result = new IntPtr(1);
                    return;
                case 0x000F:   // WM_PAINT：交給 Direct3D 重畫
                    Native.ValidateRect(m.HWnd, IntPtr.Zero);
                    if (Exposed != null) Exposed();
                    return;
                case 0x0020:   // WM_SETCURSOR：平常不顯示滑鼠游標
                    Native.SetCursor(CursorShown ? Arrow : IntPtr.Zero);
                    m.Result = new IntPtr(1);
                    return;
                case 0x0200:   // WM_MOUSEMOVE：用螢幕座標判斷是不是真的有移動（視窗滑動時也會收到這個訊息）
                    Native.POINT p;
                    Native.GetCursorPos(out p);
                    if (Math.Abs(p.X - lastPos.X) + Math.Abs(p.Y - lastPos.Y) > 3)
                    {
                        lastPos = p;
                        LastMove = DateTime.Now;
                        if (!CursorShown) { CursorShown = true; Native.SetCursor(Arrow); }
                    }
                    long mp = m.LParam.ToInt64();
                    int mx = (short)(mp & 0xFFFF), my = (short)((mp >> 16) & 0xFFFF);
                    if (Hover != null) Hover(mx, my);
                    if (dragging && Drag != null) Drag(mx, my);
                    break;
                case 0x0202:   // WM_LBUTTONUP：拖曳結束
                    if (dragging)
                    {
                        EndDrag();
                        if (Drop != null) Drop();
                    }
                    return;
                case 0x020A:   // WM_MOUSEWHEEL：位置是螢幕座標，換成視窗裡的（鎖定畫面蓋滿整個螢幕）
                    long wp = m.LParam.ToInt64();
                    int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                    if (Wheel != null && Wheel((short)(wp & 0xFFFF) - Bounds.X, (short)((wp >> 16) & 0xFFFF) - Bounds.Y, delta)) return;
                    break;
                case 0x0100:   // WM_KEYDOWN：空白鍵 / Enter / Esc
                    int key = m.WParam.ToInt32();
                    if ((key == 0x20 || key == 0x0D || key == 0x1B) && Dismiss != null) Dismiss();
                    return;
                case 0x0201:   // 滑鼠左鍵：點到播放按鈕就控制 Spotify，其他地方就滑走
                    long lp = m.LParam.ToInt64();
                    if (Click != null && Click((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF))) return;
                    if (Dismiss != null) Dismiss();
                    return;
                case 0x0204: case 0x0207:   // 滑鼠右鍵 / 中鍵
                    if (Dismiss != null) Dismiss();
                    return;
                case 0x0312:   // WM_HOTKEY：鎖定畫面顯示時登記的空白鍵 / Enter / Esc（不管前景在哪個視窗都收得到）
                    if (Dismiss != null) Dismiss();
                    return;
                case 0x001C:   // WM_ACTIVATEAPP：被切到別的程式（Alt+Tab、Win 鍵、Ctrl+Alt+Del…）；lParam 是切過去的那個程式的執行緒
                    if (m.WParam == IntPtr.Zero && Leave != null) Leave((int)m.LParam.ToInt64());
                    break;
                case 0x0010:   // WM_CLOSE（Alt+F4）：不直接關視窗，照樣滑走
                    if (Leave != null) Leave(0);
                    return;
            }
            base.WndProc(ref m);
        }
    }

    // 等待螢幕的下一次垂直同步（120Hz 螢幕就是每 8.3ms 一次），讓動畫每一格都跟螢幕更新對齊
    class VBlank : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct OpenFromHdc { public IntPtr hDc; public uint hAdapter; public uint luidLow; public int luidHigh; public uint vidPnSourceId; }
        [StructLayout(LayoutKind.Sequential)] struct WaitEvent { public uint hAdapter; public uint hDevice; public uint vidPnSourceId; }
        [StructLayout(LayoutKind.Sequential)] struct CloseAdapter { public uint hAdapter; }
        [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromHdc(ref OpenFromHdc data);
        [DllImport("gdi32.dll")] static extern int D3DKMTWaitForVerticalBlankEvent(ref WaitEvent data);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter data);

        WaitEvent wait;
        readonly bool ok;

        public VBlank()
        {
            var open = new OpenFromHdc { hDc = Native.GetDC(IntPtr.Zero) };   // 主螢幕
            ok = D3DKMTOpenAdapterFromHdc(ref open) == 0;
            Native.ReleaseDC(IntPtr.Zero, open.hDc);
            wait = new WaitEvent { hAdapter = open.hAdapter, vidPnSourceId = open.vidPnSourceId };
        }

        public void Wait()
        {
            if (!ok || D3DKMTWaitForVerticalBlankEvent(ref wait) != 0) Native.DwmFlush();   // 拿不到就退回等 DWM
        }

        public void Dispose()
        {
            if (!ok) return;
            var close = new CloseAdapter { hAdapter = wait.hAdapter };
            D3DKMTCloseAdapter(ref close);
        }
    }
}
