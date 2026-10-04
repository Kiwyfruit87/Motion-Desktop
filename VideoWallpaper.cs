// VideoWallpaper — 把影片設成 Windows 桌布
// 原理：請 Explorer 在「桌面圖示」後面產生一層 WorkerW，再把我們的播放視窗塞進那一層。
//   * Win10 / Win11 23H2 以前：圖示層被搬進一個頂層 WorkerW，影片視窗掛在它後面那個 WorkerW 底下。
//   * Win11 24H2 以後：圖示層 (SHELLDLL_DefView) 與 WorkerW 都是 Progman 的子視窗，
//     影片視窗改掛在 Progman 底下，z-order 夾在兩者之間，且必須是 layered child window。
// 操作介面是點系統匣圖示後彈出的 Windows 11 風格面板（毛玻璃背景、跟隨系統深淺色與強調色）。
using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using UIA = System.Windows.Automation;

namespace VideoWallpaper
{
    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_DISABLED = 0x08000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
        public const int WS_EX_LAYERED = 0x80000;
        public const uint LWA_ALPHA = 0x2;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_BOTTOM = new IntPtr(1);
        public const uint GW_HWNDNEXT = 2, GW_CHILD = 5;
        public const uint SMTO_NORMAL = 0;
        public const uint SPI_SETDESKWALLPAPER = 0x14, SPI_GETDESKWALLPAPER = 0x73;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3, DWMWA_CLOAKED = 14, DWMWA_USE_IMMERSIVE_DARK_MODE = 20,
            DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(int thread, int attachTo, bool attach);
        [DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();
        [DllImport("kernel32.dll")] public static extern IntPtr OpenThread(uint access, bool inherit, int threadId);
        [DllImport("kernel32.dll")] public static extern int GetProcessIdOfThread(IntPtr thread);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern int MapWindowPoints(IntPtr from, IntPtr to, [In, Out] POINT[] points, int count);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder pvParam, uint winIni);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool ValidateRect(IntPtr hWnd, IntPtr rect);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, int flags);

        // 鎖定畫面用
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr instance, int id);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
        [DllImport("user32.dll")] public static extern IntPtr BeginDeferWindowPos(int count);
        [DllImport("user32.dll")] public static extern IntPtr DeferWindowPos(IntPtr info, IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool EndDeferWindowPos(IntPtr info);
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int index);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("msimg32.dll")] public static extern bool AlphaBlend(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, BLENDFUNCTION blend);

        [DllImport("gdi32.dll")] public static extern int SaveDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool RestoreDC(IntPtr hdc, int saved);
        [DllImport("gdi32.dll")] public static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

        // 工作列透明用
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2, GA_ROOT = 2;
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

        public static string ClassOf(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool IsCloaked(IntPtr hWnd)
        {
            int cloaked;
            return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0;
        }

        public static void SetDwm(IntPtr hWnd, int attr, int value)
        {
            DwmSetWindowAttribute(hWnd, attr, ref value, 4);
        }

        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }
        [StructLayout(LayoutKind.Sequential)] struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hWnd, ref WindowCompositionAttributeData data);

        // Acrylic 模糊背景，tint 是 AABBGGRR 格式的疊色（alpha 越小越透）
        public static bool SetAcrylic(IntPtr hWnd, uint tint)
        {
            var accent = new AccentPolicy { AccentState = 4 /* ACCENT_ENABLE_ACRYLICBLURBEHIND */, AccentFlags = 2, GradientColor = tint };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, SizeOfData = size };
                return SetWindowCompositionAttribute(hWnd, ref data) != 0;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
    }

    // 介面文字：跟著 Windows 的顯示語言，中文 Windows 用中文，其他語言用英文
    static class Lang
    {
        public static readonly bool Chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        public static string T(string chinese, string english) { return Chinese ? chinese : english; }
        public static string AppName { get { return T("動態桌布", "Motion Desktop"); } }
    }

    class Settings
    {
        public string VideoPath = "";
        public bool Muted = true;
        public bool AutoPause = true;
        public bool TaskbarFix = true;   // 工作列透明（搭配 TranslucentTB）
        public Stretch Stretch = Stretch.UniformToFill;
        public string WeatherLocation = "";   // 選填：「緯度,經度」，不填就用 IP 自動判斷位置

        // 設定檔放在 exe 旁邊（可攜式），不寫進 C 槽的 AppData
        static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    if (key == "video") s.VideoPath = value;
                    else if (key == "muted") s.Muted = value == "1";
                    else if (key == "autopause") s.AutoPause = value == "1";
                    else if (key == "taskbarfix") s.TaskbarFix = value == "1";
                    else if (key == "stretch") { Stretch st; if (Enum.TryParse(value, out st)) s.Stretch = st; }
                    else if (key == "weatherlocation") s.WeatherLocation = value;
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new[] {
                    "video=" + VideoPath,
                    "muted=" + (Muted ? "1" : "0"),
                    "autopause=" + (AutoPause ? "1" : "0"),
                    "taskbarfix=" + (TaskbarFix ? "1" : "0"),
                    "stretch=" + Stretch,
                    "weatherlocation=" + WeatherLocation,
                }, Encoding.UTF8);
            }
            catch { }
        }
    }

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

    // ================= 播放引擎：Media Foundation Media Engine 解碼 + Direct3D 11 輸出 =================
    // 以下是 Windows COM 介面的宣告。方法的順序必須跟 Windows SDK 標頭檔完全一致，用不到的方法只是佔位置。

    [StructLayout(LayoutKind.Sequential)] public struct MFVideoNormalizedRect { public float Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MFARGB { public byte Blue, Green, Red, Alpha; }
    [StructLayout(LayoutKind.Sequential)] public struct D3DRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct DXGI_SWAP_CHAIN_DESC1
    {
        public uint Width, Height;
        public int Format, Stereo;
        public uint SampleCount, SampleQuality, BufferUsage, BufferCount;
        public int Scaling, SwapEffect, AlphaMode;
        public uint Flags;
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
        void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob();
        [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    }

    [ComImport, Guid("fee7c112-e776-42b5-9bbf-0048524e2bd5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEngineNotify
    {
        [PreserveSig] int EventNotify(uint meEvent, UIntPtr param1, uint param2);
    }

    [ComImport, Guid("fc0e10d2-ab2a-4501-a951-06bb1075184c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaError
    {
        [PreserveSig] ushort GetErrorCode();
        [PreserveSig] int GetExtendedErrorCode();
    }

    [ComImport, Guid("98a1b0bb-03eb-4935-ae7c-93c1fa0e1c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEngine
    {
        [PreserveSig] int GetError(out IMFMediaError error);
        [PreserveSig] int SetErrorCode(int error);
        [PreserveSig] int SetSourceElements(IntPtr elements);
        [PreserveSig] int SetSource([MarshalAs(UnmanagedType.BStr)] string url);
        [PreserveSig] int GetCurrentSource(out IntPtr url);
        [PreserveSig] ushort GetNetworkState();
        [PreserveSig] int GetPreload();
        [PreserveSig] int SetPreload(int preload);
        [PreserveSig] int GetBuffered(out IntPtr buffered);
        [PreserveSig] int Load();
        [PreserveSig] int CanPlayType([MarshalAs(UnmanagedType.BStr)] string type, out int answer);
        [PreserveSig] ushort GetReadyState();
        [PreserveSig] int IsSeeking();
        [PreserveSig] double GetCurrentTime();
        [PreserveSig] int SetCurrentTime(double seconds);
        [PreserveSig] double GetStartTime();
        [PreserveSig] double GetDuration();
        [PreserveSig] int IsPaused();
        [PreserveSig] double GetDefaultPlaybackRate();
        [PreserveSig] int SetDefaultPlaybackRate(double rate);
        [PreserveSig] double GetPlaybackRate();
        [PreserveSig] int SetPlaybackRate(double rate);
        [PreserveSig] int GetPlayed(out IntPtr played);
        [PreserveSig] int GetSeekable(out IntPtr seekable);
        [PreserveSig] int IsEnded();
        [PreserveSig] int GetAutoPlay();
        [PreserveSig] int SetAutoPlay(int autoPlay);
        [PreserveSig] int GetLoop();
        [PreserveSig] int SetLoop(int loop);
        [PreserveSig] int Play();
        [PreserveSig] int Pause();
        [PreserveSig] int GetMuted();
        [PreserveSig] int SetMuted(int muted);
        [PreserveSig] double GetVolume();
        [PreserveSig] int SetVolume(double volume);
        [PreserveSig] int HasVideo();
        [PreserveSig] int HasAudio();
        [PreserveSig] int GetNativeVideoSize(out uint width, out uint height);
        [PreserveSig] int GetVideoAspectRatio(out uint x, out uint y);
        [PreserveSig] int Shutdown();
        [PreserveSig] int TransferVideoFrame(IntPtr dstSurface, [In] ref MFVideoNormalizedRect src, [In] ref D3DRect dst, [In] ref MFARGB borderColor);
        [PreserveSig] int OnVideoStreamTick(out long pts);
    }

    [ComImport, Guid("4D645ACE-26AA-4688-9BE1-DF3516990B93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEngineClassFactory
    {
        [PreserveSig] int CreateInstance(uint flags, IMFAttributes attributes, out IMFMediaEngine engine);
    }

    [ComImport, Guid("B44392DA-499B-446b-A4CB-005FEAD0E6D5")]
    public class MFMediaEngineClassFactory { }

    [ComImport, Guid("eb533d5d-2db6-40f8-97a9-494692014f07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFDXGIDeviceManager
    {
        void CloseDeviceHandle(); void GetVideoService(); void LockDevice(); void OpenDeviceHandle();
        [PreserveSig] int ResetDevice(IntPtr device, uint resetToken);
    }

    [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D11Device
    {
        void CreateBuffer(); void CreateTexture1D(); void CreateTexture2D(); void CreateTexture3D(); void CreateShaderResourceView();
        void CreateUnorderedAccessView(); void CreateRenderTargetView(); void CreateDepthStencilView(); void CreateInputLayout(); void CreateVertexShader();
        void CreateGeometryShader(); void CreateGeometryShaderWithStreamOutput(); void CreatePixelShader(); void CreateHullShader(); void CreateDomainShader();
        void CreateComputeShader(); void CreateClassLinkage(); void CreateBlendState(); void CreateDepthStencilState(); void CreateRasterizerState();
        void CreateSamplerState(); void CreateQuery(); void CreatePredicate(); void CreateCounter(); void CreateDeferredContext();
        void OpenSharedResource(); void CheckFormatSupport(); void CheckMultisampleQualityLevels(); void CheckCounterInfo(); void CheckCounter();
        void CheckFeatureSupport(); void GetPrivateData(); void SetPrivateData(); void SetPrivateDataInterface();
        [PreserveSig] int GetFeatureLevel();
        [PreserveSig] uint GetCreationFlags();
        [PreserveSig] int GetDeviceRemovedReason();
    }

    [ComImport, Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D10Multithread
    {
        void Enter(); void Leave();
        [PreserveSig] int SetMultithreadProtected(int protect);
    }

    [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIDevice
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int GetAdapter(out IDXGIAdapter adapter);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIAdapter
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData();
        [PreserveSig] int GetParent([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object parent);
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIOutput
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void GetDesc(); void GetDisplayModeList(); void FindClosestMatchingMode();
        [PreserveSig] int WaitForVBlank();
    }

    [ComImport, Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIFactory2
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters();
        [PreserveSig] int MakeWindowAssociation(IntPtr hwnd, uint flags);
        void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        void EnumAdapters1(); void IsCurrent();
        void IsWindowedStereoEnabled();
        [PreserveSig] int CreateSwapChainForHwnd(IntPtr device, IntPtr hwnd, [In] ref DXGI_SWAP_CHAIN_DESC1 desc,
            IntPtr fullscreenDesc, IntPtr restrictToOutput, out IDXGISwapChain1 swapChain);
    }

    [ComImport, Guid("4AE63092-6327-4c1b-80AE-BFE12EA32B86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGISurface1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void GetDevice();
        void GetDesc(); void Map(); void Unmap();
        [PreserveSig] int GetDC(int discard, out IntPtr hdc);
        [PreserveSig] int ReleaseDC(IntPtr dirtyRect);
    }

    [ComImport, Guid("790a45f7-0d42-4876-983a-0a55cfe6f4aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGISwapChain1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void GetDevice();
        [PreserveSig] int Present(uint syncInterval, uint flags);
        [PreserveSig] int GetBuffer(uint buffer, [In] ref Guid riid, out IntPtr surface);
    }

    // Media Engine 的事件通知（在 Media Foundation 自己的執行緒上呼叫）
    public class EngineNotify : IMFMediaEngineNotify
    {
        readonly VideoEngine owner;
        internal EngineNotify(VideoEngine owner) { this.owner = owner; }

        public int EventNotify(uint meEvent, UIntPtr param1, uint param2)
        {
            try { owner.OnEngineEvent(meEvent, param2); } catch { }
            return 0;
        }
    }

    // 播放引擎：自己一條執行緒，負責解碼並把畫面輸出到每個螢幕的 VideoWindow。
    // 外面（UI 執行緒）只透過下面幾個方法下指令，實際動作都排進佇列、在引擎執行緒上執行。
    // Direct2D：把疊圖（時鐘、正在播放、歌詞）直接用顯示卡畫在影片畫面上。
    // 以前用 GDI 的 GetDC 貼圖，GetDC 偶爾會卡住 100 毫秒以上，影片跟著卡一下、那一格的疊圖還會變亮。
    // 直接呼叫 COM 介面的函式表（d2d1.h 的順序），不需要額外的套件。
    class D2DTarget
    {
        [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid riid, IntPtr options, out IntPtr factory);
        static readonly Guid IID_ID2D1Factory = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
        static readonly Guid IID_IDXGISurface = new Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");

        [StructLayout(LayoutKind.Sequential)] struct RectF { public float Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct SizeU { public uint Width, Height; }
        [StructLayout(LayoutKind.Sequential)] struct SizeF { public float Width, Height; }
        [StructLayout(LayoutKind.Sequential)] struct PointU { public uint X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RectU { public uint Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct PixelFormat { public int Format, AlphaMode; }
        [StructLayout(LayoutKind.Sequential)] struct RoundedRect { public RectF Rect; public float RadiusX, RadiusY; }
        [StructLayout(LayoutKind.Sequential)] struct Matrix { public float M11, M12, M21, M22, Dx, Dy; }
        [StructLayout(LayoutKind.Sequential)] struct BitmapBrushProperties { public int ExtendModeX, ExtendModeY, Interpolation; }
        [StructLayout(LayoutKind.Sequential)] struct BrushProperties { public float Opacity; public Matrix Transform; }
        [StructLayout(LayoutKind.Sequential)] struct BitmapProperties { public int Format, AlphaMode; public float DpiX, DpiY; }
        [StructLayout(LayoutKind.Sequential)] struct RenderTargetProperties { public int Type, Format, AlphaMode; public float DpiX, DpiY; public int Usage, MinLevel; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateDxgiSurfaceRenderTargetFn(IntPtr self, IntPtr surface, ref RenderTargetProperties props, out IntPtr target);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBitmapFn(IntPtr self, SizeU size, IntPtr data, uint pitch, ref BitmapProperties props, out IntPtr bitmap);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DrawBitmapFn(IntPtr self, IntPtr bitmap, ref RectF dest, float opacity, int interpolation, ref RectF source);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void BeginDrawFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EndDrawFn(IntPtr self, out ulong tag1, out ulong tag2);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateCompatibleRenderTargetFn(IntPtr self, ref SizeF size, ref SizeU pixelSize, ref PixelFormat format, int options, out IntPtr target);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBitmapFn(IntPtr self, out IntPtr bitmap);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CopyFromRenderTargetFn(IntPtr self, ref PointU dest, IntPtr renderTarget, ref RectU source);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBitmapBrushFn(IntPtr self, IntPtr bitmap, ref BitmapBrushProperties props, ref BrushProperties brushProps, out IntPtr brush);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetTransformFn(IntPtr self, ref Matrix transform);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetOpacityFn(IntPtr self, float opacity);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FillRoundedRectangleFn(IntPtr self, ref RoundedRect rect, IntPtr brush);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void PushAxisAlignedClipFn(IntPtr self, ref RectF rect, int antialias);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void PopAxisAlignedClipFn(IntPtr self);
        PushAxisAlignedClipFn pushClip;
        PopAxisAlignedClipFn popClip;

        // 接下來畫的東西只在 (x, y, w, h) 範圍內看得到，畫完要 PopClip
        public void PushClip(double x, double y, double w, double h)
        {
            if (pushClip == null) { pushClip = Method<PushAxisAlignedClipFn>(target, 45); popClip = Method<PopAxisAlignedClipFn>(target, 46); }   // ID2D1RenderTarget::PushAxisAlignedClip / PopAxisAlignedClip
            var rect = new RectF { Left = (float)x, Top = (float)y, Right = (float)(x + w), Bottom = (float)(y + h) };
            pushClip(target, ref rect, 0 /* 邊緣反鋸齒 */);
        }

        public void PopClip() { popClip(target); }

        readonly IntPtr target;
        readonly CreateBitmapFn createBitmap;
        readonly DrawBitmapFn drawBitmap;
        readonly BeginDrawFn beginDraw;
        readonly EndDrawFn endDraw;

        D2DTarget(IntPtr target)
        {
            this.target = target;
            createBitmap = Method<CreateBitmapFn>(target, 4);    // ID2D1RenderTarget::CreateBitmap
            drawBitmap = Method<DrawBitmapFn>(target, 26);      // ::DrawBitmap
            beginDraw = Method<BeginDrawFn>(target, 48);        // ::BeginDraw
            endDraw = Method<EndDrawFn>(target, 49);            // ::EndDraw
        }

        // ---------- 毛玻璃 ----------
        // 把畫面上一塊複製下來，連續縮小一半 3 次（每次把 2×2 個點平均成 1 個點），再一次放大 2 倍慢慢放大回去：
        // 等於把影片糊掉，但還看得出後面燈光、建築的模糊輪廓（縮太多會變成一片均勻的灰）。
        // 每一格都重做，所以後面的影片在動，毛玻璃也會跟著變。
        const int BlurLevels = 2;   // 縮到 1/4：後面的燈光、建築還看得出模糊的輪廓

        class Level
        {
            public IntPtr Target, Bitmap;
            public int W, H;
            public BeginDrawFn Begin;
            public EndDrawFn End;
            public DrawBitmapFn Draw;
        }

        IntPtr copy, brush;
        int copyW, copyH;
        readonly List<Level> levels = new List<Level>();
        CopyFromRenderTargetFn copyFrom;
        SetTransformFn setTransform;
        SetOpacityFn setOpacity;

        // 準備好 (x, y, w, h) 這塊的模糊圖；要在 Begin() 之前呼叫（這時畫面上只有影片）
        public bool PrepareBlur(int x, int y, int w, int h)
        {
            if (w < 32 || h < 32) return false;
            if ((w != copyW || h != copyH) && !CreateBlurResources(w, h)) return false;
            var dest = new PointU();
            var source = new RectU { Left = (uint)x, Top = (uint)y, Right = (uint)(x + w), Bottom = (uint)(y + h) };
            if (copyFrom(copy, ref dest, target, ref source) < 0) return false;

            IntPtr from = copy;
            int fromW = w, fromH = h;
            foreach (var level in levels)
            {
                DrawLevel(level, from, fromW, fromH);
                from = level.Bitmap; fromW = level.W; fromH = level.H;
            }
            // 再一次放大 2 倍、慢慢放大回 1/2（最後一次放大在 FillBlur），比一次放大 8 倍平滑，不會有格子感
            for (int i = levels.Count - 2; i >= 0; i--)
                DrawLevel(levels[i], levels[i + 1].Bitmap, levels[i + 1].W, levels[i + 1].H);
            return true;
        }

        static void DrawLevel(Level level, IntPtr bitmap, int w, int h)
        {
            var dest = new RectF { Right = level.W, Bottom = level.H };
            var source = new RectF { Right = w, Bottom = h };
            ulong tag1, tag2;
            level.Begin(level.Target);
            level.Draw(level.Target, bitmap, ref dest, 1, 1 /* 線性 */, ref source);
            level.End(level.Target, out tag1, out tag2);
        }

        // 把模糊圖填進圓角長方形 (x, y, w, h)；(blurX, blurY, blurW, blurH) 是 PrepareBlur 時的那塊範圍
        public void FillBlur(double x, double y, double w, double h, double radius, int blurX, int blurY, int blurW, int blurH, double opacity)
        {
            var up = levels[0];
            var m = new Matrix { M11 = blurW / (float)up.W, M22 = blurH / (float)up.H, Dx = blurX, Dy = blurY };
            setTransform(brush, ref m);
            setOpacity(brush, (float)opacity);
            var rect = new RoundedRect { Rect = new RectF { Left = (float)x, Top = (float)y, Right = (float)(x + w), Bottom = (float)(y + h) }, RadiusX = (float)radius, RadiusY = (float)radius };
            if (fillRounded == null) fillRounded = Method<FillRoundedRectangleFn>(target, 19);   // ID2D1RenderTarget::FillRoundedRectangle（每一格都會用，記起來）
            fillRounded(target, ref rect, brush);
        }

        FillRoundedRectangleFn fillRounded;

        bool CreateBlurResources(int w, int h)
        {
            ReleaseBlurResources();
            try
            {
                // 複製畫面用的圖（格式要跟畫面一樣：BGRA、不管 alpha）
                var props = new BitmapProperties { Format = 87, AlphaMode = 3, DpiX = 96, DpiY = 96 };
                if (createBitmap(target, new SizeU { Width = (uint)w, Height = (uint)h }, IntPtr.Zero, 0, ref props, out copy) < 0) { copy = IntPtr.Zero; return false; }
                copyFrom = Method<CopyFromRenderTargetFn>(copy, 9);   // ID2D1Bitmap::CopyFromRenderTarget

                var createCompatible = Method<CreateCompatibleRenderTargetFn>(target, 12);   // ID2D1RenderTarget::CreateCompatibleRenderTarget
                var format = new PixelFormat { Format = 87, AlphaMode = 3 };
                int lw = w, lh = h;
                for (int i = 0; i < BlurLevels; i++)
                {
                    lw = Math.Max(1, (lw + 1) / 2); lh = Math.Max(1, (lh + 1) / 2);
                    var size = new SizeF { Width = lw, Height = lh };
                    var pixels = new SizeU { Width = (uint)lw, Height = (uint)lh };
                    IntPtr rt, bitmap;
                    if (createCompatible(target, ref size, ref pixels, ref format, 0, out rt) < 0) return false;
                    var level = new Level { Target = rt, W = lw, H = lh };
                    levels.Add(level);
                    if (Method<GetBitmapFn>(rt, 57)(rt, out bitmap) < 0) return false;   // ID2D1BitmapRenderTarget::GetBitmap
                    level.Bitmap = bitmap;
                    level.Begin = Method<BeginDrawFn>(rt, 48);
                    level.End = Method<EndDrawFn>(rt, 49);
                    level.Draw = Method<DrawBitmapFn>(rt, 26);
                }

                var brushProps = new BitmapBrushProperties { ExtendModeX = 0, ExtendModeY = 0, Interpolation = 1 };   // 邊緣延伸、線性內插
                var common = new BrushProperties { Opacity = 1, Transform = new Matrix { M11 = 1, M22 = 1 } };
                if (Method<CreateBitmapBrushFn>(target, 7)(target, levels[0].Bitmap, ref brushProps, ref common, out brush) < 0) { brush = IntPtr.Zero; return false; }
                setTransform = Method<SetTransformFn>(brush, 5);   // ID2D1Brush::SetTransform
                setOpacity = Method<SetOpacityFn>(brush, 4);       // ID2D1Brush::SetOpacity
                copyW = w; copyH = h;
                return true;
            }
            catch { return false; }
        }

        void ReleaseBlurResources()
        {
            if (brush != IntPtr.Zero) { Marshal.Release(brush); brush = IntPtr.Zero; }
            foreach (var level in levels)
            {
                if (level.Bitmap != IntPtr.Zero) Marshal.Release(level.Bitmap);
                if (level.Target != IntPtr.Zero) Marshal.Release(level.Target);
            }
            levels.Clear();
            if (copy != IntPtr.Zero) { Marshal.Release(copy); copy = IntPtr.Zero; }
            copyW = copyH = 0;
        }

        static T Method<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size), typeof(T)) as T;
        }

        public static IntPtr CreateFactory()
        {
            try
            {
                var iid = IID_ID2D1Factory;
                IntPtr factory;
                return D2D1CreateFactory(0 /* 單執行緒 */, ref iid, IntPtr.Zero, out factory) >= 0 ? factory : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        // 在 Direct3D 的輸出畫面（texture）上建立 Direct2D 的繪圖目標；不支援就回傳 null（改用 GDI）
        public static D2DTarget Create(IntPtr factory, IntPtr texture)
        {
            if (factory == IntPtr.Zero) return null;
            IntPtr surface;
            var iid = IID_IDXGISurface;
            if (Marshal.QueryInterface(texture, ref iid, out surface) < 0) return null;
            try
            {
                // 87 = BGRA，3 = 忽略輸出畫面的 alpha；96 DPI：座標就是像素
                var props = new RenderTargetProperties { Format = 87, AlphaMode = 3, DpiX = 96, DpiY = 96 };
                IntPtr target;
                int hr = Method<CreateDxgiSurfaceRenderTargetFn>(factory, 15)(factory, surface, ref props, out target);   // ID2D1Factory::CreateDxgiSurfaceRenderTarget
                return hr < 0 ? null : new D2DTarget(target);
            }
            catch { return null; }
            finally { Marshal.Release(surface); }
        }

        // pixels：預先乘好 alpha 的 BGRA（WPF 的 Pbgra32）
        public IntPtr CreateBitmap(int[] pixels, int width, int height)
        {
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var props = new BitmapProperties { Format = 87, AlphaMode = 1 /* 預乘 */, DpiX = 96, DpiY = 96 };
                IntPtr bitmap;
                int hr = createBitmap(target, new SizeU { Width = (uint)width, Height = (uint)height }, pin.AddrOfPinnedObject(), (uint)(width * 4), ref props, out bitmap);
                return hr < 0 ? IntPtr.Zero : bitmap;
            }
            finally { pin.Free(); }
        }

        public void Begin() { beginDraw(target); }

        // 把 bitmap 從 (sourceY 開始、高 height 的一段) 貼到 (x, y)；位置可以有小數（線性內插，捲動更滑）
        public void Draw(IntPtr bitmap, double x, double y, double width, double height, double sourceY, double opacity)
        {
            var dest = new RectF { Left = (float)x, Top = (float)y, Right = (float)(x + width), Bottom = (float)(y + height) };
            var source = new RectF { Left = 0, Top = (float)sourceY, Right = (float)width, Bottom = (float)(sourceY + height) };
            drawBitmap(target, bitmap, ref dest, (float)opacity, 1 /* 線性 */, ref source);
        }

        // 把 bitmap 上 (sx, sy, sw, sh) 這塊貼到 (dx, dy, dw, dh)；大小不一樣就拉伸
        public void DrawPart(IntPtr bitmap, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh, double opacity)
        {
            var dest = new RectF { Left = (float)dx, Top = (float)dy, Right = (float)(dx + dw), Bottom = (float)(dy + dh) };
            var source = new RectF { Left = (float)sx, Top = (float)sy, Right = (float)(sx + sw), Bottom = (float)(sy + sh) };
            drawBitmap(target, bitmap, ref dest, (float)opacity, 1 /* 線性 */, ref source);
        }

        // 關掉邊緣反鋸齒（拼接好幾塊時，接縫的地方才不會兩邊都只畫一半、變成一條淡淡的縫）
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetAntialiasModeFn(IntPtr self, int mode);
        SetAntialiasModeFn setAntialias;
        public void SetAliased(bool aliased)
        {
            if (setAntialias == null) setAntialias = Method<SetAntialiasModeFn>(target, 32);   // ID2D1RenderTarget::SetAntialiasMode
            setAntialias(target, aliased ? 1 /* 不反鋸齒 */ : 0 /* 預設 */);
        }

        public int End() { ulong tag1, tag2; return endDraw(target, out tag1, out tag2); }

        public void Release()
        {
            ReleaseBlurResources();
            Marshal.Release(target);
        }
    }

    class VideoEngine : IDisposable
    {
        [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            [In] int[] featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);
        [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] static extern int MFShutdown();
        [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
        [DllImport("mfplat.dll")] static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager manager);

        static readonly Guid MF_MEDIA_ENGINE_CALLBACK = new Guid("c60381b8-83a4-41f8-a3d0-de05076849a9");
        static readonly Guid MF_MEDIA_ENGINE_DXGI_MANAGER = new Guid("065702da-1094-486d-8617-ee7cc4ee4648");
        static readonly Guid MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT = new Guid("5066893c-8cf9-42bc-8b8a-472212e52726");
        static readonly Guid IID_IDXGIFactory2 = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
        const int DXGI_STATUS_OCCLUDED = 0x087A0001;   // 視窗目前看不到（例如鎖定中），不算錯誤

        class Target
        {
            public IntPtr Hwnd;
            public int Width, Height;
            public IDXGISwapChain1 SwapChain;
            public IntPtr BackBuffer;
            // 有疊圖的輸出畫面（鎖定畫面）：影片先畫在這張乾淨的畫布，每一格整張蓋到輸出畫面上再貼疊圖。
            // 就算某一格影片沒更新到，蓋上去的也是乾淨的影片，疊圖不會被貼兩次（那一格會突然變亮、閃一下）
            public IntPtr VideoTexture;
            public bool Visible = true;
            // 已經畫上至少一格影片：剛建好的視窗是空的，就算被視窗蓋住（不用畫）也要先畫一格，
            // 不然工作列後面、視窗縮小的瞬間看到的會是原本的桌布
            public bool Presented;
            public int FirstFrameTries;
            // 疊在影片上的圖（鎖定畫面的時鐘、正在播放、歌詞）：每一格影片畫好後貼上去。
            // 優先用 Direct2D（D2D）；這台電腦不支援才退回 GDI（Surface）
            public D2DTarget D2D;
            public IDXGISurface1 Surface;
            public bool CanOverlay { get { return D2D != null || Surface != null; } }
            public readonly Dictionary<string, Overlay> Overlays = new Dictionary<string, Overlay>();
        }

        class Overlay
        {
            public IntPtr D2DBitmap;              // Direct2D 的圖
            public IntPtr DC, Bitmap, OldBitmap;  // GDI 的圖（退回 GDI 時）
            public int X, Y, W, H;
            // 捲動的疊圖（歌詞）：整張長圖只露出 ViewH 高的一段，Mask 是露出那段每一列的亮度（0～1）
            public int ViewH;
            public float[] Mask;
            public double ScrollFrom, ScrollTo;
            public long ScrollStart;
            public int ScrollMs;
            // 整張圖的淡入淡出
            public double FadeFrom = 1, FadeTo = 1;
            public long FadeStart;
            public int FadeMs;
            public bool RemoveWhenFaded;
            public int Z;   // 疊的順序：數字小的先畫（卡片的毛玻璃 0、卡片的顏色 1、封面文字和按鈕 2、歌詞 3）
            // 左右滑動（換歌時封面和文字的翻頁動畫），只在 Clip 範圍內看得到
            public double MoveFrom, MoveTo;
            public long MoveStart;
            public int MoveMs;
            public bool Clip;
            public int ClipX, ClipY, ClipW, ClipH;
            // 展開 / 收起（音量條）：圖上 RevealX 開始、RevealW 寬的這段，展開到一半時從左邊長出來——
            // 切成「左邊滑桿｜把手（RevealPivot 左右 RevealPivotW）｜右邊滑桿」三塊，兩邊照展開程度拉長、把手保持原樣。RevealW = 0 就是不用
            public int RevealX, RevealW;
            public double RevealPivot, RevealPivotW;
            public double RevealFrom = 1, RevealTo = 1;
            public long RevealStart;
            public int RevealMs;
            // 毛玻璃：圖底下這塊圓角長方形 (GlassX, GlassY, GlassW, GlassH) 的影片先糊掉再貼圖
            public bool Glass;
            public int GlassX, GlassY, GlassW, GlassH;
            public double GlassRadius;
            public bool GlassReady;
            public int BlurX, BlurY, BlurW, BlurH;
        }

        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();   // 疊圖動畫用的時間

        readonly string path;
        readonly bool initialMuted;
        readonly Dispatcher ui;
        readonly Action<string> onError;
        readonly Action onLost;
        readonly Thread thread;
        readonly ConcurrentQueue<Action> commands = new ConcurrentQueue<Action>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool redraw;
        volatile bool frameReady;   // 影片已經解碼出第一格（在這之前複製出來的是黑畫面，不能當作「畫好了」）

        // 健康狀態，給 UI 執行緒的監控用
        volatile int lastHealthyTick = Environment.TickCount;   // 上次輸出畫面成功（或正常地被遮住）的時間
        volatile int presentedFrames;                          // 成功輸出的新畫面數量
        public int MillisecondsSinceHealthy { get { return Environment.TickCount - lastHealthyTick; } }
        public int PresentedFrames { get { return presentedFrames; } }

        volatile int positionMs;   // 目前播放到哪裡（控制面板的預覽會對齊這個位置）
        public TimeSpan Position { get { return TimeSpan.FromMilliseconds(positionMs); } }

        // 以下只在引擎執行緒上使用
        readonly List<Target> targets = new List<Target>();
        IntPtr device, context;
        object deviceObject;
        int transferFailures, lastDeviceCheck;
        IMFDXGIDeviceManager manager;
        IDXGIFactory2 factory;
        IntPtr d2dFactory;   // Direct2D（畫疊圖用），建立失敗就是 0，改用 GDI
        IDXGIOutput output;
        IMFMediaEngine engine;
        EngineNotify notify;
        Stretch stretch;
        bool playing, stopping, mfStarted;

        // UI 執行緒這邊記住上次送出的狀態，沒變就不重複下指令
        bool uiPlaying;
        readonly Dictionary<int, bool> uiVisible = new Dictionary<int, bool>();

        public VideoEngine(string path, bool muted, Stretch stretch, Action<string> onError, Action onLost)
        {
            this.path = path;
            this.initialMuted = muted;
            this.stretch = stretch;
            this.onError = onError;
            this.onLost = onLost;
            ui = Dispatcher.CurrentDispatcher;
            thread = new Thread(Run) { IsBackground = true, Name = "VideoEngine" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        // ---------- 給 UI 執行緒用的指令 ----------

        public void AddTarget(IntPtr hwnd, int width, int height) { AddTarget(hwnd, width, height, false); }

        // withOverlay：之後要疊時鐘圖上去（輸出畫面要能用 GDI 畫）
        public void AddTarget(IntPtr hwnd, int width, int height, bool withOverlay)
        {
            Post(delegate { CreateTarget(hwnd, width, height, withOverlay); });
        }

        public void RemoveTarget(IntPtr hwnd)
        {
            Post(delegate
            {
                var t = targets.Find(x => x.Hwnd == hwnd);
                if (t == null) return;
                ReleaseTarget(t);
                targets.Remove(t);
            });
        }

        // 設定一張疊圖（name 不同就是不同張，例如 "clock"、"music"）；pixels 為 null 代表拿掉這張。
        // pixels：預先乘好 alpha 的 BGRA（WPF 的 Pbgra32），貼在畫面的 (x, y)
        public void SetOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y)
        {
            Post(delegate
            {
                var t = targets.Find(o => o.Hwnd == hwnd);
                if (t == null || !t.CanOverlay) return;
                Overlay old;
                if (t.Overlays.TryGetValue(name, out old)) { ReleaseOverlay(old); t.Overlays.Remove(name); }
                redraw = true;
                if (pixels == null) return;
                t.Overlays[name] = CreateOverlay(t, pixels, width, height, x, y);
            });
        }

        // 淡入換圖（音樂卡片的顏色）：新的從看不到慢慢變清楚，舊的同時淡出、淡完拿掉；fadeMs = 0 直接換。pixels 是 null 就拿掉
        int fadeSerial;
        public void SetFadeOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y, int z, int fadeMs)
        {
            Post(delegate
            {
                var t = targets.Find(o => o.Hwnd == hwnd);
                if (t == null || !t.CanOverlay) return;
                long now = clock.ElapsedMilliseconds;
                Overlay old;
                if (t.Overlays.TryGetValue(name, out old))
                {
                    t.Overlays.Remove(name);
                    if (fadeMs > 0)
                    {
                        old.FadeFrom = Animated(old.FadeFrom, old.FadeTo, old.FadeStart, old.FadeMs, now);
                        old.FadeTo = 0; old.FadeStart = now; old.FadeMs = fadeMs;
                        old.RemoveWhenFaded = true;
                        t.Overlays[name + "#" + (++fadeSerial)] = old;
                    }
                    else ReleaseOverlay(old);
                }
                redraw = true;
                if (pixels == null) return;
                var o2 = CreateOverlay(t, pixels, width, height, x, y);
                o2.Z = z;
                if (fadeMs > 0) { o2.FadeFrom = 0; o2.FadeTo = 1; o2.FadeStart = now; o2.FadeMs = fadeMs; }
                t.Overlays[name] = o2;
            });
        }

        // 底下帶毛玻璃的疊圖（音樂卡片）：(glassX, glassY, glassW, glassH) 這塊圓角長方形裡的影片會先糊掉
        public void SetGlassOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y,
            int glassX, int glassY, int glassW, int glassH, double radius)
        {
            Post(delegate
            {
                var t = targets.Find(o => o.Hwnd == hwnd);
                if (t == null || !t.CanOverlay) return;
                Overlay old;
                if (t.Overlays.TryGetValue(name, out old)) { ReleaseOverlay(old); t.Overlays.Remove(name); }
                redraw = true;
                if (pixels == null) return;
                var o2 = CreateOverlay(t, pixels, width, height, x, y);
                o2.Z = 0;
                o2.Glass = true;
                o2.GlassX = glassX; o2.GlassY = glassY; o2.GlassW = glassW; o2.GlassH = glassH; o2.GlassRadius = radius;
                t.Overlays[name] = o2;
            });
        }

        // 捲動的疊圖（歌詞）：pixels 是整張長圖，畫面上從 (x, y) 開始只露出 viewH 高的一段，
        // 露出的是長圖從第 scroll 列開始的部分；mask 是露出那段每一列的亮度（0～1）。出現時用 fadeMs 淡入
        public void SetScrollOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y, int viewH, float[] mask, double scroll, int fadeMs)
        {
            Post(delegate
            {
                var t = targets.Find(o => o.Hwnd == hwnd);
                if (t == null || !t.CanOverlay) return;
                var o2 = CreateOverlay(t, pixels, width, height, x, y);
                o2.Z = 3;
                o2.ViewH = viewH;
                o2.Mask = mask;
                o2.ScrollFrom = o2.ScrollTo = scroll;
                o2.FadeFrom = 0; o2.FadeTo = 1; o2.FadeStart = clock.ElapsedMilliseconds; o2.FadeMs = fadeMs;
                Overlay old;
                if (t.Overlays.TryGetValue(name, out old)) ReleaseOverlay(old);
                t.Overlays[name] = o2;
                redraw = true;
            });
        }

        // 可以左右滑動的疊圖（音樂卡片的封面和文字），只在 (clipX, clipY, clipW, clipH) 範圍內看得到。
        // shift ≠ 0：換歌的翻頁動畫——舊的往左滑出 shift、新的從右邊 shift 處滑進來（shift 是負的就反過來），
        //            兩張一起移動、間距不變，舊的完全離開卡片才拿掉。
        // shift = 0：只是換圖（例如封面晚一點才讀到），如果正在滑動就接著滑，不會跳。
        // opacity：這張圖的亮度（0～1）。只是換圖（shift = 0）時，從原本的亮度花 fadeMs 慢慢變到新的亮度
        // revealW > 0：可以展開 / 收起的音量條（範圍 revealX 開始 revealW 寬，把手在 pivot、左右各 pivotW；
        // reveal 是展開程度，之後用 RevealOverlay 慢慢展開 / 收起）；換圖時接著原本的展開程度
        public void SetSlideOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y, int z,
            int clipX, int clipY, int clipW, int clipH, int shift, int ms, double opacity, int fadeMs,
            int revealX = 0, int revealW = 0, double reveal = 1, double pivot = 0, double pivotW = 0)
        {
            Post(delegate
            {
                var t = targets.Find(o => o.Hwnd == hwnd);
                if (t == null || !t.CanOverlay) return;
                long now = clock.ElapsedMilliseconds;
                Overlay old;
                t.Overlays.TryGetValue(name, out old);
                if (old != null) t.Overlays.Remove(name);
                redraw = true;
                if (pixels == null) { if (old != null) ReleaseOverlay(old); return; }

                var o2 = CreateOverlay(t, pixels, width, height, x, y);
                o2.Z = z;
                o2.Clip = true; o2.ClipX = clipX; o2.ClipY = clipY; o2.ClipW = clipW; o2.ClipH = clipH;
                o2.FadeFrom = o2.FadeTo = opacity;
                o2.RevealX = revealX; o2.RevealW = revealW; o2.RevealPivot = pivot; o2.RevealPivotW = pivotW;
                if (revealW > 0 && old != null && old.RevealW > 0)
                {
                    o2.RevealFrom = old.RevealFrom; o2.RevealTo = old.RevealTo; o2.RevealStart = old.RevealStart; o2.RevealMs = old.RevealMs;
                }
                else o2.RevealFrom = o2.RevealTo = reveal;
                if (shift != 0)
                {
                    if (old != null)
                    {
                        double from = Animated(old.MoveFrom, old.MoveTo, old.MoveStart, old.MoveMs, now);
                        old.MoveFrom = from; old.MoveTo = from - shift; old.MoveStart = now; old.MoveMs = ms;
                        old.FadeFrom = old.FadeTo = Animated(old.FadeFrom, old.FadeTo, old.FadeStart, old.FadeMs, now);   // 亮度停在現在的樣子
                        old.FadeMs = 0;
                        old.RemoveWhenFaded = true;   // 滑完就拿掉
                        t.Overlays[name + "#" + now] = old;
                    }
                    o2.MoveFrom = shift; o2.MoveTo = 0; o2.MoveStart = now; o2.MoveMs = ms;
                }
                else if (old != null)
                {
                    o2.MoveFrom = old.MoveFrom; o2.MoveTo = old.MoveTo; o2.MoveStart = old.MoveStart; o2.MoveMs = old.MoveMs;
                    double current = Animated(old.FadeFrom, old.FadeTo, old.FadeStart, old.FadeMs, now);
                    if (Math.Abs(current - opacity) > 0.001) { o2.FadeFrom = current; o2.FadeStart = now; o2.FadeMs = fadeMs; }
                    ReleaseOverlay(old);
                }
                t.Overlays[name] = o2;
            });
        }

        // 捲動到長圖的第 to 列（從目前畫面上的位置接著捲，花 ms 毫秒）
        public void ScrollOverlay(IntPtr hwnd, string name, double to, int ms)
        {
            Post(delegate
            {
                var o = FindOverlay(hwnd, name);
                if (o == null) return;
                long now = clock.ElapsedMilliseconds;
                o.ScrollFrom = Animated(o.ScrollFrom, o.ScrollTo, o.ScrollStart, o.ScrollMs, now);
                o.ScrollTo = to; o.ScrollStart = now; o.ScrollMs = ms;
                redraw = true;
            });
        }

        // 淡入 / 淡出到 to（0～1）；remove = true 時淡出完就拿掉
        public void FadeOverlay(IntPtr hwnd, string name, double to, int ms, bool remove)
        {
            Post(delegate
            {
                var o = FindOverlay(hwnd, name);
                if (o == null) return;
                long now = clock.ElapsedMilliseconds;
                o.FadeFrom = Animated(o.FadeFrom, o.FadeTo, o.FadeStart, o.FadeMs, now);
                o.FadeTo = to; o.FadeStart = now; o.FadeMs = ms;
                o.RemoveWhenFaded = remove;
                redraw = true;
            });
        }

        // 展開 / 收起（to = 1 全部露出、0 收起來），從目前的樣子接著變，花 ms 毫秒
        public void RevealOverlay(IntPtr hwnd, string name, double to, int ms)
        {
            Post(delegate
            {
                var o = FindOverlay(hwnd, name);
                if (o == null || o.RevealW <= 0) return;
                long now = clock.ElapsedMilliseconds;
                o.RevealFrom = RevealProgress(o, now);
                o.RevealTo = to; o.RevealStart = now; o.RevealMs = ms;
                redraw = true;
            });
        }

        // 展開的程度：展開用減速曲線（一開始就動、最後慢慢停下），收起用加速曲線（慢慢起步、滑回去）
        static double RevealProgress(Overlay o, long now)
        {
            if (o.RevealMs <= 0 || now - o.RevealStart >= o.RevealMs) return o.RevealTo;
            double t = (now - o.RevealStart) / (double)o.RevealMs;
            double eased = o.RevealTo >= o.RevealFrom ? 1 - Math.Pow(1 - t, 3) : t * t;
            return o.RevealFrom + (o.RevealTo - o.RevealFrom) * eased;
        }

        Overlay FindOverlay(IntPtr hwnd, string name)
        {
            var t = targets.Find(x => x.Hwnd == hwnd);
            Overlay o;
            return t != null && t.Overlays.TryGetValue(name, out o) ? o : null;
        }

        static Overlay CreateOverlay(Target t, int[] pixels, int width, int height, int x, int y)
        {
            var o = new Overlay { X = x, Y = y, W = width, H = height };
            if (t.D2D != null)
            {
                o.D2DBitmap = t.D2D.CreateBitmap(pixels, width, height);   // 太大（超過顯示卡上限）會是 0，就不畫
                return o;
            }
            var header = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            o.Bitmap = Native.CreateDIBSection(IntPtr.Zero, ref header, 0, out bits, IntPtr.Zero, 0);
            Marshal.Copy(pixels, 0, bits, width * height);
            o.DC = Native.CreateCompatibleDC(IntPtr.Zero);
            o.OldBitmap = Native.SelectObject(o.DC, o.Bitmap);
            return o;
        }

        // 動畫中某個時間點的值（ChromeOS 的標準曲線，開頭和結尾都柔和）
        static double Animated(double from, double to, long start, int ms, long now)
        {
            if (ms <= 0 || now - start >= ms) return to;
            return from + (to - from) * LockScreen.StandardCurve((now - start) / (double)ms);
        }

        static bool IsAnimating(Overlay o, long now)
        {
            return (o.ScrollMs > 0 && now - o.ScrollStart < o.ScrollMs) || (o.FadeMs > 0 && now - o.FadeStart < o.FadeMs)
                || (o.MoveMs > 0 && now - o.MoveStart < o.MoveMs) || (o.RevealMs > 0 && now - o.RevealStart < o.RevealMs);
        }

        public void SetPlaying(bool play)
        {
            if (play == uiPlaying) return;
            uiPlaying = play;
            if (play) lastHealthyTick = Environment.TickCount;   // 監控從現在開始算
            Post(delegate
            {
                playing = play;
                if (play) engine.Play(); else engine.Pause();
            });
        }

        public void SetMuted(bool muted) { Post(delegate { engine.SetMuted(muted ? 1 : 0); }); }

        public void SetStretch(Stretch value) { Post(delegate { stretch = value; redraw = true; }); }

        public void SetTargetVisible(int index, bool visible)
        {
            bool old;
            if (uiVisible.TryGetValue(index, out old) && old == visible) return;
            uiVisible[index] = visible;
            if (visible) lastHealthyTick = Environment.TickCount;
            Post(delegate
            {
                if (index >= targets.Count) return;
                targets[index].Visible = visible;
                if (visible) redraw = true;
            });
        }

        public void RequestRedraw() { redraw = true; wake.Set(); }

        // 前面排的指令都做完之後，在 UI 執行緒執行 done
        public void WhenIdle(Action done) { Post(delegate { ui.BeginInvoke(done); }); }

        public void Dispose()
        {
            Post(delegate { stopping = true; });
            thread.Join(3000);
        }

        void Post(Action command) { commands.Enqueue(command); wake.Set(); }

        // ---------- 引擎執行緒 ----------

        void Run()
        {
            try
            {
                Init();
                while (true)
                {
                    Action command;
                    while (commands.TryDequeue(out command))
                    {
                        try { command(); }
                        catch (Exception ex) { ReportError(Lang.T("播放引擎發生錯誤：", "Playback engine error: ") + ex.Message); }
                    }
                    if (stopping) break;

                    long pts;
                    bool newFrame = engine.OnVideoStreamTick(out pts) == 0;   // S_OK = 有新的一格
                    if (newFrame) frameReady = true;
                    positionMs = (int)(engine.GetCurrentTime() * 1000);
                    if (newFrame || redraw) Render(newFrame);
                    if (stopping) break;

                    // 每秒確認一次顯示卡裝置還在（驅動更新、顯示卡重設時會失效）
                    if (Environment.TickCount - lastDeviceCheck > 1000)
                    {
                        lastDeviceCheck = Environment.TickCount;
                        int reason = ((ID3D11Device)deviceObject).GetDeviceRemovedReason();
                        if (reason != 0) { Lost(Lang.T("顯示卡裝置失效 0x", "Graphics device lost 0x") + reason.ToString("X8")); break; }
                    }

                    if (playing)
                    {
                        // 跟著螢幕更新頻率走；拿不到螢幕（例如鎖定中）就退回用睡的
                        if (output == null || output.WaitForVBlank() < 0) Thread.Sleep(8);
                    }
                    else wake.WaitOne(redraw ? 15 : 250);
                }
            }
            catch (Exception ex)
            {
                ReportError(Lang.T("無法啟動播放引擎：", "Couldn't start the playback engine: ") + ex.Message);
            }
            finally
            {
                Cleanup();
            }
        }

        void Init()
        {
            Check(MFStartup(0x20070, 0), "MFStartup");
            mfStarted = true;

            int[] levels = { 0xb100, 0xb000, 0xa100, 0xa000, 0x9300 };
            int level;
            const uint BGRA = 0x20, VIDEO = 0x800;
            int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, BGRA | VIDEO, levels, (uint)levels.Length, 7, out device, out level, out context);
            if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, BGRA, levels, (uint)levels.Length, 7, out device, out level, out context);
            Check(hr, "D3D11CreateDevice");
            deviceObject = Marshal.GetObjectForIUnknown(device);
            ((ID3D10Multithread)deviceObject).SetMultithreadProtected(1);   // Media Engine 會從別的執行緒使用這個裝置
            d2dFactory = D2DTarget.CreateFactory();

            uint resetToken;
            Check(MFCreateDXGIDeviceManager(out resetToken, out manager), "MFCreateDXGIDeviceManager");
            Check(manager.ResetDevice(device, resetToken), "ResetDevice");

            IDXGIAdapter adapter;
            Check(((IDXGIDevice)deviceObject).GetAdapter(out adapter), "GetAdapter");
            object parent;
            var iidFactory = IID_IDXGIFactory2;
            Check(adapter.GetParent(ref iidFactory, out parent), "GetParent");
            factory = (IDXGIFactory2)parent;
            IDXGIOutput firstOutput;
            if (adapter.EnumOutputs(0, out firstOutput) == 0) output = firstOutput;
            Marshal.ReleaseComObject(adapter);

            IMFAttributes attributes;
            Check(MFCreateAttributes(out attributes, 3), "MFCreateAttributes");
            notify = new EngineNotify(this);
            var key = MF_MEDIA_ENGINE_DXGI_MANAGER; Check(attributes.SetUnknown(ref key, manager), "SetUnknown");
            key = MF_MEDIA_ENGINE_CALLBACK; Check(attributes.SetUnknown(ref key, notify), "SetUnknown");
            key = MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT; Check(attributes.SetUINT32(ref key, DXGI_FORMAT_B8G8R8A8_UNORM), "SetUINT32");
            var classFactory = (IMFMediaEngineClassFactory)new MFMediaEngineClassFactory();
            Check(classFactory.CreateInstance(0, attributes, out engine), "CreateInstance");
            Marshal.ReleaseComObject(classFactory);
            Marshal.ReleaseComObject(attributes);

            engine.SetLoop(1);       // 播完直接從頭接著播
            engine.SetAutoPlay(0);
            engine.SetPreload(4);    // MF_MEDIA_ENGINE_PRELOAD_AUTOMATIC：還沒播放也先解碼好第一格（桌面被蓋住、暫停中也有畫面可以先畫上去）
            engine.SetMuted(initialMuted ? 1 : 0);
            Check(engine.SetSource(path), "SetSource");
        }

        void CreateTarget(IntPtr hwnd, int width, int height, bool withOverlay)
        {
            // 要疊圖時先試 Direct2D；這台電腦不支援的話，改建一個 GDI 能畫的輸出畫面
            var target = CreateSwapChain(hwnd, width, height, false);
            if (withOverlay)
            {
                target.D2D = D2DTarget.Create(d2dFactory, target.BackBuffer);
                if (target.D2D == null)
                {
                    ReleaseTarget(target);
                    target = CreateSwapChain(hwnd, width, height, true);
                    target.Surface = (IDXGISurface1)Marshal.GetObjectForIUnknown(target.BackBuffer);
                }
                target.VideoTexture = CreateVideoTexture(width, height);
            }
            targets.Add(target);
            redraw = true;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Texture2DDesc { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality; public int Usage; public uint BindFlags, CpuAccessFlags, MiscFlags; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture2DFn(IntPtr self, ref Texture2DDesc desc, IntPtr initialData, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyResourceFn(IntPtr self, IntPtr destination, IntPtr source);
        CopyResourceFn copyResource;

        // 跟輸出畫面一樣大小、格式的畫布（影片解碼後畫在這裡）；建立失敗就是 0，影片直接畫在輸出畫面上
        IntPtr CreateVideoTexture(int width, int height)
        {
            try
            {
                var desc = new Texture2DDesc
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                    Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
                    BindFlags = 0x20 | 0x8,   // 可以當繪圖目標（影片畫進來）、也可以被讀取
                };
                IntPtr vtable = Marshal.ReadIntPtr(device);
                var create = (CreateTexture2DFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size), typeof(CreateTexture2DFn));   // ID3D11Device::CreateTexture2D
                IntPtr texture;
                if (create(device, ref desc, IntPtr.Zero, out texture) < 0) return IntPtr.Zero;
                if (copyResource == null)
                {
                    IntPtr contextTable = Marshal.ReadIntPtr(context);
                    copyResource = (CopyResourceFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(contextTable, 47 * IntPtr.Size), typeof(CopyResourceFn));   // ID3D11DeviceContext::CopyResource
                }
                return texture;
            }
            catch { return IntPtr.Zero; }
        }

        Target CreateSwapChain(IntPtr hwnd, int width, int height, bool gdi)
        {
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)width, Height = (uint)height,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1,
                BufferUsage = 0x20,   // DXGI_USAGE_RENDER_TARGET_OUTPUT
                BufferCount = 1,
                SwapEffect = 0,       // DXGI_SWAP_EFFECT_DISCARD：畫進視窗本身，外層 layered 容器才看得到
                Flags = gdi ? 4u : 0u,   // DXGI_SWAP_CHAIN_FLAG_GDI_COMPATIBLE：要用 GDI 貼疊圖
            };
            IDXGISwapChain1 swapChain;
            Check(factory.CreateSwapChainForHwnd(device, hwnd, ref desc, IntPtr.Zero, IntPtr.Zero, out swapChain), "CreateSwapChainForHwnd");
            factory.MakeWindowAssociation(hwnd, 3);   // 不要讓 DXGI 處理 Alt+Enter 全螢幕切換
            var target = new Target { Hwnd = hwnd, Width = width, Height = height, SwapChain = swapChain };
            var iidTexture = IID_ID3D11Texture2D;
            Check(swapChain.GetBuffer(0, ref iidTexture, out target.BackBuffer), "GetBuffer");
            return target;
        }

        static void ReleaseOverlay(Overlay o)
        {
            if (o.D2DBitmap != IntPtr.Zero) { Marshal.Release(o.D2DBitmap); o.D2DBitmap = IntPtr.Zero; }
            if (o.DC != IntPtr.Zero)
            {
                Native.SelectObject(o.DC, o.OldBitmap);
                Native.DeleteObject(o.Bitmap);
                Native.DeleteDC(o.DC);
                o.DC = IntPtr.Zero;
            }
        }

        static void ReleaseTarget(Target t)
        {
            foreach (var o in t.Overlays.Values) ReleaseOverlay(o);
            t.Overlays.Clear();
            if (t.D2D != null) { t.D2D.Release(); t.D2D = null; }
            if (t.Surface != null) Marshal.ReleaseComObject(t.Surface);
            if (t.VideoTexture != IntPtr.Zero) { Marshal.Release(t.VideoTexture); t.VideoTexture = IntPtr.Zero; }
            if (t.BackBuffer != IntPtr.Zero) Marshal.Release(t.BackBuffer);
            if (t.SwapChain != null) Marshal.ReleaseComObject(t.SwapChain);
            t.Surface = null; t.BackBuffer = IntPtr.Zero; t.SwapChain = null;
        }

        // 把疊圖用 alpha 混合貼到剛畫好的影片畫面上。
        // 回傳 true 代表還有疊圖在動畫中（捲動、淡入淡出），下一次螢幕更新要再畫一次
        static bool DrawOverlay(Target t)
        {
            if (t.Overlays.Count == 0 || !t.CanOverlay) return false;
            long now = clock.ElapsedMilliseconds;

            // 淡出完、要拿掉的先拿掉
            List<string> finished = null;
            foreach (var pair in t.Overlays)
                if (pair.Value.RemoveWhenFaded && !IsAnimating(pair.Value, now))
                {
                    if (finished == null) finished = new List<string>();
                    finished.Add(pair.Key);
                }
            if (finished != null)
                foreach (var name in finished) { ReleaseOverlay(t.Overlays[name]); t.Overlays.Remove(name); }
            if (t.Overlays.Count == 0) return false;
            return t.D2D != null ? DrawOverlayD2D(t, now) : DrawOverlayGdi(t, now);
        }

        // Direct2D：直接在顯示卡上畫，不用把畫面借給 GDI；捲動位置可以有小數，更滑
        static bool DrawOverlayD2D(Target t, long now)
        {
            // 毛玻璃要讀取底下的影片，所以先在開始畫疊圖之前做好（範圍往外多抓一點，邊緣才糊得自然）
            foreach (var o in t.Overlays.Values)
            {
                o.GlassReady = false;
                if (!o.Glass || o.D2DBitmap == IntPtr.Zero) continue;
                int margin = o.GlassH / 3;
                o.BlurX = Math.Max(0, o.GlassX - margin); o.BlurY = Math.Max(0, o.GlassY - margin);
                o.BlurW = Math.Min(t.Width, o.GlassX + o.GlassW + margin) - o.BlurX;
                o.BlurH = Math.Min(t.Height, o.GlassY + o.GlassH + margin) - o.BlurY;
                o.GlassReady = t.D2D.PrepareBlur(o.BlurX, o.BlurY, o.BlurW, o.BlurH);
            }

            bool animating = false;
            t.D2D.Begin();
            foreach (var o in t.Overlays.Values.OrderBy(v => v.Z))
            {
                if (IsAnimating(o, now)) animating = true;
                if (o.D2DBitmap == IntPtr.Zero) continue;
                double fade = Animated(o.FadeFrom, o.FadeTo, o.FadeStart, o.FadeMs, now);
                if (fade <= 0.004) continue;
                if (o.ViewH == 0)
                {
                    double reveal = o.RevealW > 0 ? RevealProgress(o, now) : 1;
                    if (reveal <= 0.001) continue;
                    if (o.GlassReady)
                        t.D2D.FillBlur(o.GlassX, o.GlassY, o.GlassW, o.GlassH, o.GlassRadius, o.BlurX, o.BlurY, o.BlurW, o.BlurH, Math.Min(1, fade));
                    double offset = Animated(o.MoveFrom, o.MoveTo, o.MoveStart, o.MoveMs, now);
                    if (o.Clip) t.D2D.PushClip(o.ClipX, o.ClipY, o.ClipW, o.ClipH);
                    if (reveal < 0.999) DrawGrowing(t.D2D, o, o.X + offset, reveal, Math.Min(1, fade));
                    else t.D2D.Draw(o.D2DBitmap, o.X + offset, o.Y, o.W, o.H, 0, Math.Min(1, fade));
                    if (o.Clip) t.D2D.PopClip();
                    continue;
                }
                // 捲動的疊圖：每 2 列一條，各自用遮罩的亮度貼上去（中間亮、上下淡）
                double top = Animated(o.ScrollFrom, o.ScrollTo, o.ScrollStart, o.ScrollMs, now);
                for (int row = 0; row < o.ViewH; row += 2)
                {
                    int h = Math.Min(2, o.ViewH - row);
                    double source = top + row;
                    if (source < 0 || source + h > o.H) continue;
                    double alpha = fade * o.Mask[row];
                    if (alpha <= 0.004) continue;
                    t.D2D.Draw(o.D2DBitmap, o.X, o.Y + row, o.W, h, source, Math.Min(1, alpha));
                }
            }
            t.D2D.End();
            return animating;
        }

        // 展開到一半的音量條：切成「左邊滑桿｜把手｜右邊滑桿」三塊，兩邊照展開程度拉長、把手保持原樣（不會被壓扁），
        // 整條從喇叭旁邊長出來、同時淡入；完全展開時三塊剛好接回原本的圖。x：這張圖現在在畫面上的左邊
        static void GrowingParts(Overlay o, double x, double reveal, out double[] source, out double[] dest)
        {
            double a = o.RevealX, d = o.RevealX + o.RevealW;
            double b = Math.Max(a, Math.Min(d, o.RevealPivot - o.RevealPivotW));
            double c = Math.Max(b, Math.Min(d, o.RevealPivot + o.RevealPivotW));
            double left = (b - a) * reveal, right = (d - c) * reveal;
            source = new[] { a, b - a, b, c - b, c, d - c };                                      // 每塊在圖上的左邊和寬度
            dest = new[] { x + a, left, x + a + left, c - b, x + a + left + (c - b), right };    // 每塊在畫面上的左邊和寬度
        }

        static double GrowingOpacity(double reveal) { return Math.Min(1, reveal * 1.6); }   // 長到六成多就完全不透明

        static void DrawGrowing(D2DTarget d2d, Overlay o, double x, double reveal, double opacity)
        {
            double[] source, dest;
            GrowingParts(o, x, reveal, out source, out dest);
            double alpha = opacity * GrowingOpacity(reveal);
            d2d.SetAliased(true);
            for (int i = 0; i < 6; i += 2)
                if (dest[i + 1] > 0.01 && source[i + 1] > 0)
                    d2d.DrawPart(o.D2DBitmap, source[i], 0, source[i + 1], o.H, dest[i], o.Y, dest[i + 1], o.H, alpha);
            d2d.SetAliased(false);
        }

        // 退回 GDI：這台電腦不支援 Direct2D 時才用
        static bool DrawOverlayGdi(Target t, long now)
        {
            IntPtr dc;
            if (t.Surface.GetDC(0, out dc) < 0) return false;
            bool animating = false;
            foreach (var o in t.Overlays.Values.OrderBy(v => v.Z))
            {
                if (IsAnimating(o, now)) animating = true;
                double fade = Animated(o.FadeFrom, o.FadeTo, o.FadeStart, o.FadeMs, now);
                if (fade <= 0.004) continue;
                if (o.ViewH == 0)
                {
                    double reveal = o.RevealW > 0 ? RevealProgress(o, now) : 1;
                    if (reveal <= 0.001) continue;
                    int offset = (int)Math.Round(Animated(o.MoveFrom, o.MoveTo, o.MoveStart, o.MoveMs, now));
                    int saved = o.Clip ? Native.SaveDC(dc) : 0;
                    if (o.Clip) Native.IntersectClipRect(dc, o.ClipX, o.ClipY, o.ClipX + o.ClipW, o.ClipY + o.ClipH);
                    if (reveal < 0.999)
                    {
                        // 展開到一半：三塊各自拉伸貼上（位置取整數，接縫才不會重疊或空一條）
                        double[] source, dest;
                        GrowingParts(o, o.X + offset, reveal, out source, out dest);
                        var growing = new Native.BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Round(255 * Math.Min(1, fade) * GrowingOpacity(reveal)), AlphaFormat = 1 };
                        for (int i = 0; i < 6; i += 2)
                        {
                            int sx = (int)Math.Round(source[i]), sw = (int)Math.Round(source[i] + source[i + 1]) - sx;
                            int dx = (int)Math.Round(dest[i]), dw = (int)Math.Round(dest[i] + dest[i + 1]) - dx;
                            if (sw > 0 && dw > 0) Native.AlphaBlend(dc, dx, o.Y, dw, o.H, o.DC, sx, 0, sw, o.H, growing);
                        }
                    }
                    else
                    {
                        var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Round(255 * Math.Min(1, fade)), AlphaFormat = 1 };
                        Native.AlphaBlend(dc, o.X + offset, o.Y, o.W, o.H, o.DC, 0, 0, o.W, o.H, blend);
                    }
                    if (o.Clip) Native.RestoreDC(dc, saved);
                    continue;
                }
                // 捲動的疊圖：每 2 列一條，各自用遮罩的亮度貼上去（中間亮、上下淡）
                int top = (int)Math.Round(Animated(o.ScrollFrom, o.ScrollTo, o.ScrollStart, o.ScrollMs, now));
                for (int row = 0; row < o.ViewH; row += 2)
                {
                    int h = Math.Min(2, o.ViewH - row), source = top + row;
                    if (source < 0 || source + h > o.H) continue;
                    double alpha = fade * o.Mask[row];
                    if (alpha <= 0.004) continue;
                    var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Round(255 * Math.Min(1, alpha)), AlphaFormat = 1 };
                    Native.AlphaBlend(dc, o.X, o.Y + row, o.W, h, o.DC, 0, source, o.W, h, blend);
                }
            }
            t.Surface.ReleaseDC(IntPtr.Zero);
            return animating;
        }

        void Render(bool newFrame)
        {
            redraw = false;
            uint vw, vh;
            if (engine.GetNativeVideoSize(out vw, out vh) < 0 || vw == 0 || vh == 0) return;
            var border = new MFARGB { Alpha = 255 };   // 黑邊
            bool anyVisible = false;
            foreach (var t in targets)
            {
                // 被視窗蓋住的螢幕就不畫，省資源（還沒畫過的除外：等第一格解碼好，先畫一格上去）
                if (!t.Visible && (t.Presented || !frameReady)) continue;
                if (t.Visible) anyVisible = true;
                MFVideoNormalizedRect src;
                D3DRect dst;
                Fit(vw, vh, t.Width, t.Height, stretch, out src, out dst);
                int hr = engine.TransferVideoFrame(t.VideoTexture != IntPtr.Zero ? t.VideoTexture : t.BackBuffer, ref src, ref dst, ref border);
                if (hr < 0)
                {
                    // 有新畫面卻一直複製失敗（約 2 秒）：多半是顯示卡資源失效，整個重建
                    if (newFrame && ++transferFailures > 120) { Lost(Lang.T("複製影片畫面一直失敗 0x", "Copying video frames keeps failing 0x") + hr.ToString("X8")); return; }
                    // 第一格還沒畫上去（影片可能還沒解碼好）：暫停中也要再試，最多試約 3 秒
                    if (!t.Presented && ++t.FirstFrameTries < 200) redraw = true;
                    continue;
                }
                transferFailures = 0;
                if (t.VideoTexture != IntPtr.Zero) copyResource(context, t.BackBuffer, t.VideoTexture);   // 乾淨的影片整張蓋上去，再貼疊圖
                if (DrawOverlay(t)) redraw = true;   // 歌詞捲動、淡入淡出中：下一次螢幕更新再畫一次
                hr = t.SwapChain.Present(0, 0);
                if (hr < 0) { Lost(Lang.T("輸出畫面失敗 0x", "Presenting the frame failed 0x") + hr.ToString("X8")); return; }
                if (hr == 0) t.Presented = true;   // DXGI_STATUS_OCCLUDED（例如鎖定中）時其實沒畫上去，不算
                lastHealthyTick = Environment.TickCount;
                if (hr == 0 && newFrame) presentedFrames++;
            }
            if (!anyVisible) lastHealthyTick = Environment.TickCount;   // 全部被蓋住時本來就不畫
        }

        // 引擎已經不能用了：停下來，請 UI 執行緒整個重建
        void Lost(string why)
        {
            stopping = true;
            ui.BeginInvoke(onLost);
        }

        // 依縮放方式算出要取影片的哪一塊（src，0～1）、畫到畫面的哪一塊（dst，像素）
        static void Fit(uint vw, uint vh, int tw, int th, Stretch mode, out MFVideoNormalizedRect src, out D3DRect dst)
        {
            src = new MFVideoNormalizedRect { Left = 0, Top = 0, Right = 1, Bottom = 1 };
            dst = new D3DRect { Left = 0, Top = 0, Right = tw, Bottom = th };
            if (mode == Stretch.Fill) return;
            double videoAspect = (double)vw / vh, targetAspect = (double)tw / th;
            if (mode == Stretch.Uniform)   // 完整顯示：縮小放在中間，旁邊留黑邊
            {
                if (videoAspect > targetAspect) { int h = (int)Math.Round(tw / videoAspect); dst.Top = (th - h) / 2; dst.Bottom = dst.Top + h; }
                else { int w = (int)Math.Round(th * videoAspect); dst.Left = (tw - w) / 2; dst.Right = dst.Left + w; }
            }
            else                           // 填滿：裁掉影片多出來的邊
            {
                if (videoAspect > targetAspect) { float keep = (float)(targetAspect / videoAspect); src.Left = (1 - keep) / 2; src.Right = src.Left + keep; }
                else { float keep = (float)(videoAspect / targetAspect); src.Top = (1 - keep) / 2; src.Bottom = src.Top + keep; }
            }
        }

        internal void OnEngineEvent(uint meEvent, uint param2)
        {
            switch (meEvent)
            {
                case 1009:  // FIRSTFRAMEREADY：第一格解碼好了，暫停中也要把它畫出來
                    frameReady = true;
                    RequestRedraw();
                    break;
                case 10:    // LOADEDMETADATA
                case 17:    // SEEKED
                    RequestRedraw();
                    break;
                case 5:     // ERROR
                    ReportError(Lang.T("無法播放這個影片（建議使用 H.264 編碼的 .mp4），錯誤碼 0x", "Can't play this video (an H.264 .mp4 is recommended), error code 0x") + param2.ToString("X8"));
                    break;
                case 1012:  // RESOURCELOST
                case 1014:  // STREAMRENDERINGERROR
                    ui.BeginInvoke(onLost);
                    break;
            }
        }

        void ReportError(string message)
        {
            ui.BeginInvoke(new Action(delegate { onError(message); }));
        }

        static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + Lang.T(" 失敗（0x", " failed (0x") + hr.ToString("X8") + Lang.T("）", ")"), hr);
        }

        void Cleanup()
        {
            try
            {
                if (engine != null) { engine.Shutdown(); Marshal.ReleaseComObject(engine); engine = null; }
                foreach (var t in targets) ReleaseTarget(t);
                targets.Clear();
                if (output != null) Marshal.ReleaseComObject(output);
                if (d2dFactory != IntPtr.Zero) { Marshal.Release(d2dFactory); d2dFactory = IntPtr.Zero; }
                if (factory != null) Marshal.ReleaseComObject(factory);
                if (manager != null) Marshal.ReleaseComObject(manager);
                if (deviceObject != null) Marshal.ReleaseComObject(deviceObject);
                if (context != IntPtr.Zero) Marshal.Release(context);
                if (device != IntPtr.Zero) Marshal.Release(device);
            }
            catch { }
            if (mfStarted) MFShutdown();
        }
    }

    // ================= 鎖定畫面（仿 ChromeOS）=================

    class WeatherInfo
    {
        public double TempC;
        public int Code;        // WMO 天氣代碼
        public bool IsDay;
        public DateTime Fetched;
    }

    // 目前的天氣（攝氏）：先用 IP 查大概的位置，再向 Open-Meteo 查目前天氣。兩個都是免費、不用帳號的服務。
    // 設定檔可以用 weatherlocation=緯度,經度 指定位置，就不會用 IP 查。
    static class Weather
    {
        static WeatherInfo current;
        static double lat = double.NaN, lon = double.NaN;
        static volatile bool fetching;
        static int failedTick;   // 上次查詢失敗的時間（0 = 沒失敗）：失敗後隔 2 分鐘才再試，斷網、服務限流時不要每秒一直查

        public static WeatherInfo Current { get { return current; } }

        public static void SetFixedLocation(string text)
        {
            var parts = (text ?? "").Split(',');
            double a, b;
            if (parts.Length == 2 && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b)) { lat = a; lon = b; }
        }

        // 超過 20 分鐘沒更新才重新查（上次失敗的話隔 2 分鐘）；查到後在 UI 執行緒呼叫 onUpdated
        public static void Refresh(Action onUpdated)
        {
            if (fetching || (current != null && (DateTime.Now - current.Fetched).TotalMinutes < 20)) return;
            if (failedTick != 0 && Environment.TickCount - failedTick < 120000) return;
            fetching = true;
            var ui = Dispatcher.CurrentDispatcher;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
                    if (double.IsNaN(lat)) Locate();
                    if (double.IsNaN(lat)) return;
                    var inv = CultureInfo.InvariantCulture;
                    var data = Json(Download("https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(inv) + "&longitude=" + lon.ToString(inv)
                        + "&current=temperature_2m,weather_code,is_day&timezone=auto"));
                    var now = (Dictionary<string, object>)data["current"];
                    current = new WeatherInfo
                    {
                        TempC = Convert.ToDouble(now["temperature_2m"], inv),
                        Code = Convert.ToInt32(now["weather_code"], inv),
                        IsDay = Convert.ToInt32(now["is_day"], inv) == 1,
                        Fetched = DateTime.Now,
                    };
                    ok = true;
                    if (onUpdated != null) ui.BeginInvoke(onUpdated);
                }
                catch { }
                finally
                {
                    failedTick = ok ? 0 : Environment.TickCount | 1;   // | 1：剛好算出 0 時也要記得失敗過
                    fetching = false;
                }
            });
        }

        static void Locate()
        {
            foreach (var url in new[] { "https://ipapi.co/json/", "https://ipwho.is/" })
            {
                try
                {
                    var d = Json(Download(url));
                    object a, b;
                    if (d.TryGetValue("latitude", out a) && d.TryGetValue("longitude", out b) && a != null && b != null)
                    {
                        lat = Convert.ToDouble(a, CultureInfo.InvariantCulture);
                        lon = Convert.ToDouble(b, CultureInfo.InvariantCulture);
                        return;
                    }
                }
                catch { }
            }
        }

        static string Download(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "VideoWallpaper/1.0";
            request.Timeout = 8000;
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        static Dictionary<string, object> Json(string text)
        {
            return (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(text);
        }
    }

    // 正在播放的歌（Spotify、瀏覽器裡的 YouTube Music 等）：透過 Windows 的「系統媒體控制」讀取（跟音量浮動視窗上的歌名是同一份資料）。
    // 這些是 WinRT 介面，這裡用的舊版 C# 編譯器沒辦法直接引用（要另外裝 Windows SDK），所以執行時才用反射呼叫。
    static class NowPlaying
    {
        public class Track
        {
            public string Title, Artist, Album, Key;
            public string App;       // 哪個程式在播（Windows 的應用程式識別碼，例如 Spotify、Chrome）
            public bool Playing;
            public bool IsSpotify { get { return App != null && App.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0; } }
            public byte[] Art;   // 專輯封面圖檔
            public TimeSpan Position, Duration;   // Spotify 回報的播放位置、歌曲長度
            public DateTime Updated;              // 回報位置的時間（UTC）
        }

        // 推算現在播到第幾秒：Spotify 只在播放、暫停、跳轉時回報位置，播放中就從回報的時間往後推
        public static double PositionSeconds(Track t)
        {
            double p = t.Position.TotalSeconds;
            double since = (DateTime.UtcNow - t.Updated).TotalSeconds;
            if (t.Playing && since > 0 && since < 86400) p += since;
            if (t.Duration.TotalSeconds > 0) p = Math.Min(p, t.Duration.TotalSeconds);
            return Math.Max(0, p);
        }

        static readonly object gate = new object();
        static Track current;
        public static Track Current { get { lock (gate) return current; } }
        public static string Key { get { var t = Current; return t == null ? "" : t.Key; } }

        static Assembly bridge;
        static Type managerType, sessionType, propsType, infoType, timelineType, streamRefType;
        static object manager, session;
        static System.Threading.Timer timer;
        static Action changed;
        static int polling, artTries;
        static string artKey, shownApp;
        static byte[] artCache;

        // 開始每秒讀一次；有變化時在 UI 執行緒呼叫 onChanged
        public static void Start(Action onChanged)
        {
            var ui = Dispatcher.CurrentDispatcher;
            changed = delegate { ui.BeginInvoke(onChanged); };
            if (timer == null) timer = new System.Threading.Timer(delegate { Poll(); }, null, 0, 1000);
            else timer.Change(0, 1000);
        }

        public static void Stop()
        {
            if (timer != null) timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // 最近一次按的是哪個按鈕、什麼時候按的（換歌動畫用來決定往哪邊滑）
        public static string LastCommand;
        public static int LastCommandTick;

        // "prev" 上一首 / "toggle" 播放暫停 / "next" 下一首
        public static void Command(string action)
        {
            LastCommand = action;
            LastCommandTick = Environment.TickCount;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    object s = session;
                    if (s == null) return;
                    string method = action == "prev" ? "TrySkipPreviousAsync" : action == "next" ? "TrySkipNextAsync" : "TryTogglePlayPauseAsync";
                    Type rt;
                    Await(Call(s, sessionType, method, out rt), rt);
                    Thread.Sleep(250);
                    Poll();   // 馬上更新畫面上的圖示
                }
                catch { }
            });
        }

        static void Poll()
        {
            if (Interlocked.Exchange(ref polling, 1) == 1) return;
            try
            {
                if (!Init()) return;
                Type rt;
                // 選要顯示哪個播放器（Spotify、瀏覽器裡的 YouTube Music…）：正在播放的優先；
                // 上次顯示的那個還在播就繼續顯示它（同時開好幾個時才不會跳來跳去）；都沒在播就用 Windows 認定的「目前」那個
                string currentApp = null;
                try
                {
                    object currentSession = Call(manager, managerType, "GetCurrentSession", out rt);
                    if (currentSession != null) currentApp = Get(currentSession, sessionType, "SourceAppUserModelId") as string;
                }
                catch { }
                object found = null;
                string foundApp = null;
                int best = -1;
                foreach (object s in (IEnumerable)Call(manager, managerType, "GetSessions", out rt))
                {
                    string app = Get(s, sessionType, "SourceAppUserModelId") as string ?? "";
                    bool playing = false;
                    try { playing = Get(Call(s, sessionType, "GetPlaybackInfo", out rt), infoType, "PlaybackStatus").ToString() == "Playing"; }
                    catch { }
                    int score = (playing ? 4 : 0) + (app == shownApp ? 2 : 0) + (app == currentApp ? 1 : 0);
                    if (score > best) { best = score; found = s; foundApp = app; }
                }
                session = found;
                shownApp = foundApp;

                Track track = null;
                if (found != null)
                {
                    object props = Await(Call(found, sessionType, "TryGetMediaPropertiesAsync", out rt), rt);
                    object info = Call(found, sessionType, "GetPlaybackInfo", out rt);
                    string title = Get(props, propsType, "Title") as string, artist = Get(props, propsType, "Artist") as string ?? "";
                    if (!string.IsNullOrEmpty(title))
                    {
                        // 換歌後封面有時會晚一點才更新，所以換歌後的前幾次都重新讀一次封面
                        string songKey = title + "\n" + artist;
                        if (songKey != artKey) { artKey = songKey; artCache = null; artTries = 0; }
                        // 換歌後前 3 次都重新讀封面（有時會晚一點才更新）；還沒讀到的話最多試 10 次
                        if (artTries < 3 || (artCache == null && artTries < 10))
                        {
                            artTries++;
                            var art = ReadArt(Get(props, propsType, "Thumbnail"));
                            if (art != null) artCache = art;
                        }
                        bool playing = Get(info, infoType, "PlaybackStatus").ToString() == "Playing";
                        track = new Track
                        {
                            Title = title, Artist = artist, Album = Get(props, propsType, "AlbumTitle") as string ?? "", Playing = playing, Art = artCache,
                            App = foundApp,
                            Key = songKey + "\n" + playing + "\n" + (artCache == null ? 0 : artCache.Length) + "\n" + foundApp,
                        };
                        try
                        {
                            object timeline = Call(found, sessionType, "GetTimelineProperties", out rt);
                            track.Position = (TimeSpan)Get(timeline, timelineType, "Position");
                            track.Duration = (TimeSpan)Get(timeline, timelineType, "EndTime");
                            track.Updated = ((DateTimeOffset)Get(timeline, timelineType, "LastUpdatedTime")).UtcDateTime;
                        }
                        catch { }
                    }
                }

                string oldKey;
                lock (gate) { oldKey = current == null ? null : current.Key; current = track; }
                if (oldKey != (track == null ? null : track.Key) && changed != null) changed();
            }
            catch { }
            finally { polling = 0; }
        }

        static bool Init()
        {
            if (manager != null) return true;
            try
            {
                bridge = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                managerType = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager", "Windows.Media");
                sessionType = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSession", "Windows.Media");
                propsType = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties", "Windows.Media");
                infoType = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackInfo", "Windows.Media");
                timelineType = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionTimelineProperties", "Windows.Media");
                streamRefType = WinRT("Windows.Storage.Streams.IRandomAccessStreamReference", "Windows.Storage");
                Type rt;
                manager = Await(Call(null, managerType, "RequestAsync", out rt), rt);
                return true;
            }
            catch { return false; }
        }

        static byte[] ReadArt(object thumbnail)
        {
            if (thumbnail == null) return null;
            try
            {
                Type rt;
                // 封面最多等 0.8 秒（Spotify 有時要從網路下載），還沒好就先不要，下一秒再試；歌名不用等封面
                object winStream = Await(Call(thumbnail, streamRefType, "OpenReadAsync", out rt), rt, 800);
                var asStream = bridge.GetType("System.IO.WindowsRuntimeStreamExtensions").GetMethods()
                    .First(m => m.Name == "AsStreamForRead" && m.GetParameters().Length == 1);
                using (var stream = (Stream)asStream.Invoke(null, new[] { winStream }))
                using (var copy = new MemoryStream()) { stream.CopyTo(copy); return copy.ToArray(); }
            }
            catch { return null; }
        }

        static Type WinRT(string name, string contract) { return Type.GetType(name + ", " + contract + ", ContentType=WindowsRuntime", true); }

        // 等待 WinRT 的 IAsyncOperation<T>：用方法宣告的回傳型別找出 T，再呼叫 AsTask<T>
        // 最多等 timeout 毫秒；等不到就放棄（丟出例外），不然 Spotify 沒回應時會一直卡住，之後每秒的讀取都會被擋住
        static object Await(object operation, Type declared, int timeout = 5000)
        {
            var asTask = bridge.GetType("System.WindowsRuntimeSystemExtensions").GetMethods()
                .First(m => m.Name == "AsTask" && m.IsGenericMethod && m.GetParameters().Length == 1
                         && m.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1")
                .MakeGenericMethod(declared.GetGenericArguments()[0]);
            var task = (System.Threading.Tasks.Task)asTask.Invoke(null, new[] { operation });
            if (!task.Wait(timeout)) throw new TimeoutException();
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        static object Call(object target, Type type, string method, out Type returnType)
        {
            var info = type.GetMethod(method, Type.EmptyTypes);
            returnType = info.ReturnType;
            return info.Invoke(target, null);
        }

        static object Get(object target, Type type, string property) { return type.GetProperty(property).GetValue(target, null); }
    }

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

    // 歌詞：向 LRCLIB（https://lrclib.net，免費公開的社群歌詞資料庫）查詢。
    // 有時間標記的「同步歌詞」會跟著播放進度一行一行捲動；只有一般歌詞的話，就照歌曲長度平均分配時間慢慢捲。
    static class Lyrics
    {
        public class Line { public double Time; public string Text; }

        public class Result
        {
            public List<Line> Lines;   // null = 沒有歌詞（純音樂、查不到）
            public bool Synced;        // 有時間標記
            public bool Failed;        // 網路錯誤：過一分鐘再查一次
            public DateTime When;
            // 整首歌畫好的長圖（查到歌詞後在背景先畫好，按下歌詞按鈕就能馬上淡入）；還沒畫好是 null
            public int[] Sheet;
            public int SheetWidth, SheetHeight, SheetFor;   // SheetFor：照多高的螢幕畫的
            public double[] Centers;
            public double Spacing;
        }

        static readonly Dictionary<string, Result> cache = new Dictionary<string, Result>();
        static readonly Queue<string> cacheOrder = new Queue<string>();   // 查過的歌（先查的在前面），只留最近 50 首
        const int CacheLimit = 50;
        static readonly HashSet<string> loading = new HashSet<string>();
        static readonly List<Result> sheets = new List<Result>();   // 有長圖的歌（一張好幾 MB，只留最近畫的 3 首；同一首只會出現一次）
        static readonly object gate = new object();

        public static string SongKey(NowPlaying.Track track) { return track.Title + "\n" + track.Artist; }

        public static bool Has(Result r) { return r != null && r.Lines != null && r.Lines.Count > 0; }

        public static bool SheetReady(Result r, int screenHeight) { return Has(r) && r.Sheet != null && r.SheetFor == screenHeight; }

        // 回傳 null 代表還在查；查完（有歌詞的話連長圖也畫好）會在 UI 執行緒呼叫 loaded
        public static Result Get(NowPlaying.Track track, int screenHeight, Action loaded)
        {
            string key = SongKey(track);
            Result cached;
            lock (gate)
            {
                bool have = cache.TryGetValue(key, out cached) && !(cached.Failed && (DateTime.UtcNow - cached.When).TotalSeconds > 60);
                if (have && (!Has(cached) || SheetReady(cached, screenHeight))) return cached;
                if (loading.Contains(key)) return have ? cached : null;
                loading.Add(key);
                if (!have) cached = null;
            }
            var ui = Dispatcher.CurrentDispatcher;
            string title = track.Title, artist = track.Artist, album = track.Album ?? "";
            double duration = track.Duration.TotalSeconds;
            var known = cached;   // 已經查過、只是長圖被清掉或螢幕高度變了：只要重畫
            ThreadPool.QueueUserWorkItem(delegate
            {
                Result r = known;
                if (r == null)
                {
                    try { r = Fetch(title, artist, album, duration); }
                    catch { r = new Result { Failed = true }; }
                    r.When = DateTime.UtcNow;
                }
                if (Has(r)) DrawSheet(r, screenHeight);
                lock (gate)
                {
                    if (!cache.ContainsKey(key)) cacheOrder.Enqueue(key);
                    cache[key] = r;
                    loading.Remove(key);
                    // 查過的歌太多了：最早查的拿掉（程式開很久時記憶體才不會一直變大）
                    while (cache.Count > CacheLimit && cacheOrder.Count > 0)
                    {
                        Result old;
                        string oldest = cacheOrder.Dequeue();
                        if (oldest == key) { cacheOrder.Enqueue(key); continue; }   // 現在這首不拿掉，排回最後面
                        if (!cache.TryGetValue(oldest, out old)) continue;
                        cache.Remove(oldest);
                        if (sheets.Remove(old)) old.Sheet = null;
                    }
                }
                ui.BeginInvoke(loaded);
            });
            return cached;
        }

        // 在另一條執行緒畫長圖（WPF 要在 STA 執行緒上畫），不會卡住鎖定畫面
        static void DrawSheet(Result r, int screenHeight)
        {
            var thread = new Thread(delegate()
            {
                try
                {
                    int w, h;
                    double[] centers;
                    double spacing;
                    var pixels = LockClock.RenderLyricsSheet(screenHeight, r.Lines, out w, out h, out centers, out spacing);
                    lock (gate)
                    {
                        r.SheetWidth = w; r.SheetHeight = h; r.Centers = centers; r.Spacing = spacing; r.SheetFor = screenHeight;
                        r.Sheet = pixels;
                        // 移到最後面（重畫的歌原本就在清單裡的話先拿掉，才不會有兩筆、清掉舊的那筆時把剛畫好的長圖也清掉）
                        sheets.Remove(r);
                        sheets.Add(r);
                        while (sheets.Count > 3) { sheets[0].Sheet = null; sheets.RemoveAt(0); }
                    }
                }
                catch { }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
        }

        static Result Fetch(string title, string artist, string album, double duration)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2

            // 1. 用歌名、歌手、專輯、長度精確查詢（太長的通常是 Podcast，資料庫不收）
            if (duration > 0 && duration < 3600)
            {
                string exact = Download("https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title)
                    + "&artist_name=" + Uri.EscapeDataString(artist) + "&album_name=" + Uri.EscapeDataString(album)
                    + "&duration=" + Math.Round(duration).ToString(CultureInfo.InvariantCulture));
                var r = exact == null ? null : FromEntry(Json(exact) as Dictionary<string, object>, duration);
                if (r != null) return r;
            }

            // 2. 搜尋，挑長度接近、有同步歌詞的；歌名有「 - Remastered 2011」「(feat. …)」之類的，去掉再搜一次
            foreach (string name in new[] { title, Simplify(title) }.Distinct())
            {
                if (name.Length == 0) continue;
                string found = Download("https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(name) + "&artist_name=" + Uri.EscapeDataString(artist));
                var r = found == null ? null : FromEntry(Pick(Json(found) as object[], duration), duration);
                if (r != null) return r;
            }
            return new Result();   // 沒有歌詞
        }

        // 搜尋結果裡挑最適合的：同步歌詞優先，再來是一般歌詞，長度差太多（多半是別的版本）的不要
        static Dictionary<string, object> Pick(object[] list, double duration)
        {
            if (list == null) return null;
            Dictionary<string, object> best = null;
            double bestScore = double.MaxValue;
            foreach (object o in list)
            {
                var e = o as Dictionary<string, object>;
                if (e == null) continue;
                bool synced = Text(e, "syncedLyrics").Length > 0, plain = Text(e, "plainLyrics").Length > 0, instrumental = Flag(e, "instrumental");
                if (!synced && !plain && !instrumental) continue;
                double d = Number(e, "duration");
                double diff = duration > 0 && d > 0 ? Math.Abs(d - duration) : 0;
                if (diff > 10) continue;
                double score = diff + (synced ? 0 : plain ? 100 : 200);
                if (score < bestScore) { bestScore = score; best = e; }
            }
            return best;
        }

        static Result FromEntry(Dictionary<string, object> e, double duration)
        {
            if (e == null) return null;
            if (Flag(e, "instrumental")) return new Result();   // 純音樂
            var synced = ParseLrc(Text(e, "syncedLyrics"));
            if (synced.Count > 0) return new Result { Lines = synced, Synced = true };

            // 只有一般歌詞：平均分配到歌曲長度的 5%～95% 之間（不知道長度就每行 4 秒）
            var plain = Text(e, "plainLyrics").Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
            while (plain.Count > 0 && plain[plain.Count - 1].Length == 0) plain.RemoveAt(plain.Count - 1);
            if (plain.Count == 0) return null;
            var lines = new List<Line>();
            for (int i = 0; i < plain.Count; i++)
                lines.Add(new Line { Text = plain[i], Time = duration > 0 ? duration * (0.05 + 0.9 * i / plain.Count) : i * 4.0 });
            return new Result { Lines = lines, Synced = false };
        }

        // LRC 格式：「[分:秒.百分秒] 歌詞」，一行可以有好幾個時間；[ar:…] 之類的資訊標籤略過
        static List<Line> ParseLrc(string text)
        {
            var lines = new List<Line>();
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                var times = new List<double>();
                int i = 0;
                while (i < raw.Length && raw[i] == '[')
                {
                    int close = raw.IndexOf(']', i);
                    if (close < 0) break;
                    string[] parts = raw.Substring(i + 1, close - i - 1).Split(':');
                    int minutes; double seconds;
                    if (parts.Length == 2 && int.TryParse(parts[0], out minutes)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                        times.Add(minutes * 60 + seconds);
                    i = close + 1;
                }
                string content = raw.Substring(i).Trim();
                foreach (double t in times) lines.Add(new Line { Time = t, Text = content });
            }
            lines.Sort((a, b) => a.Time.CompareTo(b.Time));
            return lines;
        }

        static string Simplify(string title)
        {
            string t = title;
            int dash = t.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) t = t.Substring(0, dash);
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*[\(\[](feat\.?|ft\.?|with)\s[^\)\]]*[\)\]]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return t.Trim();
        }

        // 查不到（404 / 400）回傳 null；連不上之類的錯誤直接丟出去
        static string Download(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "VideoWallpaper/1.0";
            request.Timeout = 8000;
            try
            {
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                if (http != null && ((int)http.StatusCode == 404 || (int)http.StatusCode == 400)) return null;
                throw;
            }
        }

        static object Json(string text)
        {
            return new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);
        }

        static string Text(Dictionary<string, object> e, string key) { object v; return e.TryGetValue(key, out v) && v is string ? (string)v : ""; }
        static bool Flag(Dictionary<string, object> e, string key) { object v; return e.TryGetValue(key, out v) && v is bool && (bool)v; }
        static double Number(Dictionary<string, object> e, string key)
        {
            object v;
            if (!e.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
    }

    // 左下角的時鐘和天氣：用 WPF 畫成一張半透明圖，再交給播放引擎每格貼到影片上
    static class LockClock
    {
        // 跟 ChromeOS 一樣用 Google Sans（fonts 資料夾裡的 Google Sans Flex，開源授權；拉丁字母含各種帶符號的字母都有）。
        // Google Sans 沒有的字依序找：俄文、希臘文用 Segoe UI（有真的半粗體，粗細才跟旁邊的字一致），
        // 中文和日文假名用微軟正黑體，韓文用 Malgun Gothic
        static readonly FontFamily ClockFont = new FontFamily(
            new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts") + "\\"), "./#Google Sans Flex, Segoe UI, Microsoft JhengHei UI, Malgun Gothic");
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        // 時間或天氣有變才需要重畫時鐘那張圖
        // now：同一個時間點（時間、AM / PM、日期都用它，跨過整點或午夜的那一瞬間才不會兜不起來）
        public static string ClockKey(DateTime now)
        {
            return TimeText(now) + AmPmText(now) + DateText(now) + (Weather.Current == null ? "" : Weather.Current.Fetched.Ticks.ToString());
        }

        // 時間下面的日期（英文）：「Friday, October 3」
        static string DateText(DateTime now)
        {
            return now.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
        }

        // 12 小時制：「9:02」＋ 後面小一號的「AM / PM」
        static string TimeText(DateTime now)
        {
            return now.ToString("h:mm", CultureInfo.InvariantCulture);
        }

        static string AmPmText(DateTime now)
        {
            return now.Hour < 12 ? "AM" : "PM";
        }

        // 左下角：時鐘和天氣，下面一行日期（now 跟算 ClockKey 時用同一個時間點）
        public static int[] RenderClock(DateTime now, int screenHeight, out int width, out int height, out int x, out int y)
        {
            double s = screenHeight / 1080.0;
            double timeSize = Math.Round(92 * s), ampmSize = Math.Round(34 * s), tempSize = Math.Round(40 * s);
            double pad = Math.Ceiling(30 * s);   // 陰影需要的留白

            var text = new TextBlock { FontFamily = ClockFont, Foreground = Brushes.White };
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);
            text.Inlines.Add(new Run(TimeText(now)) { FontSize = timeSize });
            // AM / PM 跟時間數字的底部對齊。字小，用中等粗細才不會比大數字顯得單薄；
            // 前面的空格用較大的字級，讓它跟數字之間多留一點距離
            text.Inlines.Add(new Run(" ") { FontSize = Math.Round(48 * s) });
            text.Inlines.Add(new Run(AmPmText(now)) { FontSize = ampmSize, FontWeight = FontWeights.Medium });
            var weather = Weather.Current;
            if (weather != null)
            {
                double iconSize = Math.Round(tempSize * 1.1);
                var icon = WeatherIcon(weather.Code, weather.IsDay, iconSize);
                // 圖示中心對齊溫度文字的中間
                icon.Margin = new Thickness(Math.Round(timeSize * 0.3), 0, Math.Round(tempSize * 0.2), Math.Round(tempSize * 0.36 - iconSize / 2));
                text.Inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Baseline });
                text.Inlines.Add(new Run(Math.Round(weather.TempC).ToString("0", CultureInfo.InvariantCulture) + "°") { FontSize = tempSize });
            }
            // 時間下面一行小字的日期（大數字的行高下面留很多空，往上拉近一點）
            var date = new TextBlock
            {
                Text = DateText(now), FontFamily = ClockFont, FontSize = Math.Round(26 * s), FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)),
                Margin = new Thickness(Math.Round(4 * s), -Math.Round(12 * s), 0, 0),
            };
            TextOptions.SetTextFormattingMode(date, TextFormattingMode.Ideal);
            var lines = new StackPanel();
            lines.Children.Add(text);
            lines.Children.Add(date);
            Border root;
            var pixels = ToPixels(lines, s, pad, out root, out width, out height);
            x = (int)Math.Round(64 * s - pad);                       // 左下角
            y = (int)Math.Round(screenHeight - 52 * s - height + pad);
            return pixels;
        }

        // 圖的四周留給陰影的空間
        public static int ShadowPad(int screenHeight) { return (int)Math.Ceiling(30 * screenHeight / 1080.0); }

        // 卡片內距；歌詞跟卡片裡的內容左右對齊，所以寬度＝卡片寬度扣掉兩邊內距
        public static double CardPadding(double s) { return Math.Round(12 * s); }
        public static double CardRadius(double s) { return Math.Round(14 * s); }
        public static double LyricsWidth(double s) { return Math.Round(284 * s); }

        // 音樂卡片：四張同樣大小、疊在同樣位置的圖
        public class MusicCard
        {
            public int[] Chrome;   // 毛玻璃底＋邊框（不動）
            public int[] Info;     // 封面、歌名、歌手、播放按鈕（換歌時整頁滑動）
            public int[] Mic;      // 歌詞按鈕（全亮畫好，亮度交給播放引擎慢慢調）
            public int[] Volume;   // 喇叭（音量變了只重畫這張和 VolumeBar）
            public int[] VolumeBar;   // 音量滑桿（平常收起來，滑鼠停在喇叭上才展開）
            public int[] Color;       // 跟著專輯封面的漸層顏色（疊在毛玻璃上）；封面幾乎是黑白的、沒有封面時是 null
            public string ColorKey;   // 顏色有沒有變（換歌時顏色變了才淡入淡出）
            public int Width, Height, X, Y;
            public double TrackLeft, TrackWidth;   // 音量滑桿在螢幕上的左端和長度（點或拖曳時換算成音量）
            public int RevealX, RevealW;           // 音量滑桿在圖上的範圍（展開動畫從左邊長出來）
            public double KnobRadius;              // 把手的半徑（展開時把手保持原樣，不跟著拉長）

            // 音量是 volume 時，把手中心在圖上的位置
            public double KnobX(double volume) { return TrackLeft - X + TrackWidth * Math.Max(0, Math.Min(1, volume)); }
        }

        // 最近一次畫的卡片版面（音量變了只改滑桿再畫一次音量那兩張，不用整個重排）
        static Border cardRoot;
        static List<UIElement>[] cardLayers;   // chrome、info、mic、喇叭、滑桿
        static VolumeControl cardVolume;
        static int cardWidth, cardHeight;

        // 右下角：正在播放的歌（沒在播就回傳 null）。
        // buttons：回傳各個按鈕在螢幕上的位置，讓鎖定畫面知道滑鼠點到哪個；volume：Spotify 現在的音量（0～1）
        public static MusicCard RenderMusic(int screenWidth, int screenHeight, double volume, List<KeyValuePair<Rect, string>> buttons)
        {
            cardRoot = null;
            var track = NowPlaying.Current;
            if (track == null) return null;
            double s = screenHeight / 1080.0;
            double pad = ShadowPad(screenHeight);
            var controls = new List<KeyValuePair<FrameworkElement, string>>();
            var infoParts = new List<UIElement>();
            var chromeParts = new List<UIElement>();
            UIElement micPart;
            VolumeControl volumeControl;
            Border root;
            var card = new MusicCard();
            var content = BuildNowPlaying(track, s, controls, infoParts, chromeParts, out micPart, out volumeControl);
            Wrap(content, s, pad, out root, out card.Width, out card.Height);
            // 跟著專輯封面的漸層顏色（另外畫，不加陰影，免得卡片外面多一圈影子）
            var hues = AlbumHues(track.Art);
            if (hues != null)
            {
                card.Color = RenderCardColor(hues, card.Width, card.Height, pad, content.ActualWidth, content.ActualHeight, s);
                card.ColorKey = string.Join(",", hues.Select(v => v.ToString("0.000", CultureInfo.InvariantCulture)));
            }
            // 離右邊和下面一樣是 52（下緣跟左下角的時鐘對齊）
            card.X = (int)Math.Round(screenWidth - 52 * s - card.Width + pad);
            card.Y = (int)Math.Round(screenHeight - 52 * s - card.Height + pad);
            foreach (var c in controls)
            {
                var p = c.Key.TransformToAncestor(root).Transform(new Point(0, 0));
                buttons.Add(new KeyValuePair<Rect, string>(new Rect(p.X + card.X, p.Y + card.Y, c.Key.RenderSize.Width, c.Key.RenderSize.Height), c.Value));
            }
            double trackLeft = volumeControl.Panel.TransformToAncestor(root).Transform(new Point(volumeControl.TrackLeft, 0)).X;
            card.TrackLeft = trackLeft + card.X;
            card.TrackWidth = volumeControl.TrackWidth;
            // 展開的範圍：滑桿左右各多留把手的半徑和一點空間
            double margin = volumeControl.KnobRadius + Math.Round(3 * s);
            card.RevealX = (int)Math.Floor(trackLeft - margin);
            card.RevealW = (int)Math.Ceiling(volumeControl.TrackWidth + 2 * margin);
            card.KnobRadius = volumeControl.KnobRadius;

            // 同一個版面畫五次，每次只留一部分（Hidden 會保留位置，五張圖完全對齊）
            cardLayers = new[] { chromeParts, infoParts, new List<UIElement> { micPart }, volumeControl.IconParts, volumeControl.BarParts };
            cardRoot = root; cardVolume = volumeControl; cardWidth = card.Width; cardHeight = card.Height;
            card.Chrome = SnapshotLayer(0);
            card.Info = SnapshotLayer(1);
            card.Mic = SnapshotLayer(2);
            card.Volume = RenderVolume(volume, out card.VolumeBar);
            return card;
        }

        // 音量變了：只改滑桿和喇叭圖示，重畫喇叭和滑桿那兩張（跟卡片其他圖一樣大小、一樣位置）
        public static int[] RenderVolume(double volume, out int[] bar)
        {
            bar = null;
            if (cardRoot == null) return null;
            cardVolume.Show(volume);
            bar = SnapshotLayer(4);
            return SnapshotLayer(3);
        }

        // 只顯示第 index 層、藏起其他層，畫一張
        static int[] SnapshotLayer(int index)
        {
            for (int i = 0; i < cardLayers.Length; i++)
                foreach (var e in cardLayers[i]) e.Visibility = i == index ? Visibility.Visible : Visibility.Hidden;
            cardRoot.UpdateLayout();
            return Snapshot(cardRoot, cardWidth, cardHeight);
        }

        // 把內容加上陰影畫成一張半透明圖（周圍留 pad 的空間給陰影）
        static int[] ToPixels(FrameworkElement content, double s, double pad, out Border root, out int width, out int height)
        {
            Wrap(content, s, pad, out root, out width, out height);
            return Snapshot(root, width, height);
        }

        static void Wrap(FrameworkElement content, double s, double pad, out Border root, out int width, out int height)
        {
            content.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18 * s, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black };
            root = new Border { Padding = new Thickness(pad), Child = content, UseLayoutRounding = true };
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            width = (int)Math.Ceiling(root.DesiredSize.Width);
            height = (int)Math.Ceiling(root.DesiredSize.Height);
        }

        static int[] Snapshot(Border root, int width, int height)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var pixels = new int[width * height];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        // 歌詞：整首歌一次畫成一張長圖（白字、靠左、跟音樂卡片一樣寬、太長自動折行，帶陰影），
        // 捲動和淡入淡出交給播放引擎，這樣每一格只是貼圖，不用重畫，捲動才會順。
        // centers：每一行中心在長圖上的高度；spacing：單行歌詞的行距（亮度遮罩、還沒開始唱時的位置用）
        public static int[] RenderLyricsSheet(int screenHeight, List<Lyrics.Line> lines, out int width, out int height, out double[] centers, out double spacing)
        {
            double s = screenHeight / 1080.0;
            double pad = ShadowPad(screenHeight);
            double boxWidth = LyricsWidth(s), gap = Math.Round(7 * s);
            var canvas = new Canvas { Width = boxWidth };
            centers = new double[lines.Count];
            double next = 0;
            for (int j = 0; j < lines.Count; j++)
            {
                var block = LyricsBlock(lines[j].Text.Length == 0 ? "♪" : lines[j].Text, s, boxWidth);
                double h = block.DesiredSize.Height;
                Canvas.SetTop(block, next);
                canvas.Children.Add(block);
                centers[j] = pad + next + h / 2;
                next += h + gap;
            }
            canvas.Height = Math.Max(1, next - gap);
            spacing = LyricsBlock("A", s, boxWidth).DesiredSize.Height + gap;
            Border root;
            return ToPixels(canvas, s, pad, out root, out width, out height);
        }

        static TextBlock LyricsBlock(string text, double s, double width)
        {
            var block = new TextBlock
            {
                Text = text, FontFamily = ClockFont, FontSize = Math.Round(20 * s), FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left, Width = width,
            };
            TextOptions.SetTextFormattingMode(block, TextFormattingMode.Ideal);
            block.Measure(new Size(width, double.PositiveInfinity));
            return block;
        }

        // 整體的不透明度：目前這行也是半透明的 8 成，其他行照比例更淡
        const double LyricsOpacity = 0.8;

        // 歌詞露出那一段每一列的亮度：正中間（目前這行）最亮，往上下越來越淡，到邊緣完全消失。
        // highlight = false：一般歌詞沒有時間標記，不強調哪一行，中間一段一樣亮
        public static float[] LyricsMask(int viewHeight, double spacing, bool highlight)
        {
            var mask = new float[viewHeight];
            for (int row = 0; row < viewHeight; row++)
            {
                double lines = Math.Abs(row + 1 - viewHeight / 2.0) / spacing;          // 離中間幾行
                double edge = Math.Min(1, Math.Min(row, viewHeight - 1 - row) / (spacing * 0.8));   // 靠近邊緣再淡出
                double level = highlight ? Fade(lines) : 0.75 * Math.Max(0, Math.Min(1, (3 - lines) / 1.5));
                mask[row] = (float)(level * edge * LyricsOpacity);
            }
            return mask;
        }

        // 離目前這行越遠越淡：0 行 100%、1 行 60%、2 行 38%、3 行 18%，再遠就看不到
        static double Fade(double distance)
        {
            double[] levels = { 1.0, 0.60, 0.38, 0.18, 0 };
            if (distance <= 0) return levels[0];
            if (distance >= levels.Length - 1) return 0;
            int i = (int)Math.Floor(distance);
            return levels[i] + (levels[i + 1] - levels[i]) * (distance - i);
        }

        // 橫向的卡片（半透明深色圓角長方形）：左邊專輯封面；右邊上面歌名、下面歌手，
        // 再下面一排上一首、播放暫停、下一首、音量，最右邊是歌詞按鈕
        // info：封面、歌名、歌手、播放按鈕（換歌時整頁滑動）；mic：歌詞按鈕（也跟著滑，亮度另外調）；
        // volume：喇叭和音量滑桿（也跟著滑，音量變了單獨重畫）；chrome：卡片的毛玻璃底和邊框（不動）
        static FrameworkElement BuildNowPlaying(NowPlaying.Track track, double s, List<KeyValuePair<FrameworkElement, string>> controls,
            List<UIElement> info, List<UIElement> chrome, out UIElement mic, out VolumeControl volume)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };

            double artSize = Math.Round(76 * s);
            var cover = new Border { Width = artSize, Height = artSize, CornerRadius = new CornerRadius(8 * s), Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)) };
            BitmapImage art = null;
            if (track.Art != null)
            {
                try
                {
                    art = new BitmapImage();
                    art.BeginInit();
                    art.StreamSource = new MemoryStream(track.Art);
                    art.CacheOption = BitmapCacheOption.OnLoad;
                    art.DecodePixelWidth = (int)(artSize * 2);
                    art.EndInit();
                    art.Freeze();
                }
                catch { art = null; }
            }
            if (art != null) cover.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            else cover.Child = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 30 * s, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(cover);
            info.Add(cover);

            // 右邊固定寬度：不管歌名長短，卡片大小和按鈕位置都一樣（太長的歌名會顯示「…」）
            double buttonSize = Math.Round(32 * s);
            var right = new StackPanel { Margin = new Thickness(12 * s, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = Math.Round(196 * s) };
            var title = new TextBlock { Text = track.Title, FontFamily = ClockFont, FontSize = 16 * s, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
            var artist = new TextBlock { Text = track.Artist, FontFamily = ClockFont, FontSize = 13 * s, Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 255, 255, 255)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1 * s, 0, 0) };
            right.Children.Add(title);
            right.Children.Add(artist);
            info.Add(title);
            info.Add(artist);

            // 按鈕列：左邊上一首、播放暫停、下一首（第一個圖示跟文字左邊對齊），右邊歌詞
            double inset = (buttonSize - 15 * s) / 2;
            var bar = new Grid { Margin = new Thickness(-inset, 3 * s, -inset, -inset / 2) };
            var playback = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            AddControl(playback, "", "prev", s, buttonSize, controls);
            AddControl(playback, track.Playing ? "" : "", "toggle", s, buttonSize, controls);
            AddControl(playback, "", "next", s, buttonSize, controls);
            bar.Children.Add(playback);
            info.Add(playback);
            // 歌詞（斜放的麥克風）：畫成全亮，亮度（顯示中全白、有歌詞但沒開半亮、沒有歌詞很暗）交給播放引擎慢慢變
            var lyricsHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var lyrics = AddControl(lyricsHost, "", "lyrics", s, buttonSize, controls);
            lyrics.Child = MicIcon(s);
            mic = lyricsHost;
            bar.Children.Add(lyricsHost);
            // 音量：下一首和歌詞按鈕中間的空間
            double barWidth = Math.Round(196 * s) + 2 * inset;
            volume = BuildVolume(s, buttonSize, barWidth - 4 * buttonSize, controls);
            volume.Panel.HorizontalAlignment = HorizontalAlignment.Left;
            volume.Panel.Margin = new Thickness(3 * buttonSize, 0, 0, 0);
            bar.Children.Add(volume.Panel);
            right.Children.Add(bar);
            row.Children.Add(right);
            // 按鈕跟著一起翻頁（固定不動的話，滑過去的封面會蓋到按鈕）：播放按鈕在 info、歌詞按鈕在 mic、音量在 volume，三張一起滑

            // 毛玻璃：最底下是播放引擎糊掉的影片（看得到後面燈光的模糊輪廓），
            // 上面只疊很淡的霧白、上緣一道反光、細微的顆粒，讓後面的顏色透出來
            var radius = new CornerRadius(CardRadius(s));
            var card = new Grid();
            var tint = new Border { CornerRadius = radius, Background = new SolidColorBrush(Color.FromArgb(0x0D, 255, 255, 255)) };   // 5% 霧白
            var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 255, 255, 255), 0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.55));
            var shine = new Border { CornerRadius = radius, Background = sheen };                                                     // 上緣反光
            var grain = new Border { CornerRadius = radius, Background = NoiseBrush() };                                              // 顆粒
            var edge = new Border { CornerRadius = radius, BorderBrush = new SolidColorBrush(Color.FromArgb(0x48, 255, 255, 255)), BorderThickness = new Thickness(1) };   // 玻璃邊緣
            card.Children.Add(tint);
            card.Children.Add(shine);
            card.Children.Add(grain);
            card.Children.Add(edge);
            card.Children.Add(new Border { Child = row, Padding = new Thickness(CardPadding(s) + 1) });   // +1：邊框的寬度
            chrome.Add(tint); chrome.Add(shine); chrome.Add(grain); chrome.Add(edge);
            return card;
        }

        // ---------- 卡片顏色（跟著專輯封面）----------
        // 封面縮小後依色相統計（越鮮豔、越亮的點越算數，接近黑、白、灰的不算），找出最多的色相，
        // 再找第二個顏色：有明顯的另一個色系（差 60° 以上）就用它，不然用旁邊 20°～50° 的相近色相。
        // 回傳 {主色相, 主飽和度, 第二色相, 第二飽和度, 是不是兩個色系（1 / 0）}；封面幾乎是黑白的就回傳 null
        static byte[] huesArt;
        static double[] huesCache;
        static double[] AlbumHues(byte[] art)
        {
            if (art == null) return null;
            if (art == huesArt) return huesCache;
            double[] result = null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = new MemoryStream(art);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 48;
                image.EndInit();
                var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight;
                var pixels = new int[w * h];
                bgra.CopyPixels(pixels, w * 4, 0);

                const int bins = 36;   // 每 10° 一格
                var weight = new double[bins];
                var cosSum = new double[bins];
                var sinSum = new double[bins];
                var satSum = new double[bins];
                double total = 0;
                foreach (int p in pixels)
                {
                    double r = ((p >> 16) & 255) / 255.0, g = ((p >> 8) & 255) / 255.0, b = (p & 255) / 255.0;
                    double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
                    double sat = max <= 0 ? 0 : d / max;
                    if (sat < 0.18 || max < 0.18) continue;
                    double hue = max == r ? 60 * ((g - b) / d) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
                    if (hue < 0) hue += 360;
                    double wgt = sat * max;
                    int bin = Math.Min(bins - 1, (int)(hue / 10));
                    weight[bin] += wgt;
                    cosSum[bin] += wgt * Math.Cos(hue * Math.PI / 180);
                    sinSum[bin] += wgt * Math.Sin(hue * Math.PI / 180);
                    satSum[bin] += wgt * sat;
                    total += wgt;
                }
                if (total / pixels.Length >= 0.05)
                {
                    // 相鄰的格子一起算，找最多的那一段
                    Func<int, double> around = i => weight[(i + bins - 1) % bins] * 0.5 + weight[i] + weight[(i + 1) % bins] * 0.5;
                    int peak = 0;
                    for (int i = 1; i < bins; i++) if (around(i) > around(peak)) peak = i;
                    // 另一個色系：離主色相 60° 以上、份量至少有主色的四分之一（例如藍色封面上的黃字）
                    int other = -1;
                    for (int i = 0; i < bins; i++)
                    {
                        int distance = Math.Min(Math.Abs(i - peak), bins - Math.Abs(i - peak));
                        if (distance >= 6 && (other < 0 || around(i) > around(other))) other = i;
                    }
                    bool two = other >= 0 && around(other) >= 0.25 * around(peak);
                    // 沒有另一個色系的話，找第二多的相近色相：離主色相 2～5 格（20°～50°）
                    int near = -1;
                    for (int k = 2; k <= 5; k++)
                        foreach (int i in new[] { (peak + k) % bins, (peak - k + bins) % bins })
                            if (near < 0 || around(i) > around(near)) near = i;
                    Func<int, double[]> mean = c =>
                    {
                        double cx = 0, sy = 0, ss = 0, ww = 0;
                        for (int j = -1; j <= 1; j++) { int i = (c + j + bins) % bins; cx += cosSum[i]; sy += sinSum[i]; ss += satSum[i]; ww += weight[i]; }
                        double hue = Math.Atan2(sy, cx) * 180 / Math.PI;
                        return new[] { hue < 0 ? hue + 360 : hue, ww > 0 ? ss / ww : 0.5 };
                    };
                    var main = mean(peak);
                    // 封面上沒有明顯的第二個顏色：自己往旁邊偏 28° 配一個
                    var second = two ? mean(other) : around(near) >= 0.2 * around(peak) ? mean(near) : new[] { (main[0] + 28) % 360, main[1] };
                    result = new[] { main[0], main[1], second[0], second[1], two ? 1.0 : 0.0 };
                }
            }
            catch { result = null; }
            huesArt = art;
            huesCache = result;
            return result;
        }

        // 色相、飽和度、亮度（HSL）→ 顏色
        static Color Hsl(double hue, double sat, double light, byte alpha)
        {
            hue = ((hue % 360) + 360) % 360;
            double c = (1 - Math.Abs(2 * light - 1)) * sat, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = light - c / 2;
            double r = 0, g = 0, b = 0;
            if (hue < 60) { r = c; g = x; } else if (hue < 120) { r = x; g = c; } else if (hue < 180) { g = c; b = x; }
            else if (hue < 240) { g = x; b = c; } else if (hue < 300) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromArgb(alpha, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        // 卡片的顏色層（顏色都偏深、半透明：白字看得清楚，後面糊掉的影片也還透得出來）：
        // 兩個色系：左上主色、右下另一個顏色，各佔一角，中間墊一層偏深的混色，柔和地接起來；
        // 一個色系：左上淺（相近色）→ 中間主色 → 右下深，左上角再一圈柔和的亮光。
        // 邊框也帶一點旁邊卡片的顏色（淡很多、很亮，看得出是邊框）
        static int[] RenderCardColor(double[] hues, int width, int height, double pad, double cardW, double cardH, double s)
        {
            var radius = new CornerRadius(CardRadius(s));
            var layers = new Grid { Width = cardW, Height = cardH, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            if (hues.Length > 4 && hues[4] > 0)
            {
                double sat1 = Math.Max(0.35, Math.Min(0.8, hues[1] * 0.9)), sat2 = Math.Max(0.35, Math.Min(0.8, hues[3] * 0.9));
                Color first = Hsl(hues[0], sat1, CornerLight(hues[0]), 255), second = Hsl(hues[2], sat2, CornerLight(hues[2]), 255);
                var mix = Color.FromArgb(0x80, (byte)((first.R + second.R) / 2 * 0.42), (byte)((first.G + second.G) / 2 * 0.42), (byte)((first.B + second.B) / 2 * 0.42));
                layers.Children.Add(new Border { CornerRadius = radius, Background = new SolidColorBrush(mix) });
                layers.Children.Add(new Border { CornerRadius = radius, Background = CornerGlow(first, 0, 0) });
                layers.Children.Add(new Border { CornerRadius = radius, Background = CornerGlow(second, 1, 1) });
                edge.GradientStops.Add(new GradientStop(Hsl(hues[0], sat1 * 0.75, 0.80, 0x90), 0));
                edge.GradientStops.Add(new GradientStop(Hsl(hues[2], sat2 * 0.75, 0.80, 0x90), 1));
            }
            else
            {
                double mainSat = Math.Max(0.3, Math.Min(0.75, hues[1] * 0.85)), nearSat = Math.Max(0.3, Math.Min(0.75, hues[3] * 0.85));
                double darkHue = hues[0] - 8 * Math.Sign(((hues[2] - hues[0] + 540) % 360) - 180);   // 深色往相近色的反方向偏一點
                var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                gradient.GradientStops.Add(new GradientStop(Hsl(hues[2], nearSat, 0.46, 0x9A), 0));
                gradient.GradientStops.Add(new GradientStop(Hsl(hues[0], mainSat, 0.34, 0x8C), 0.55));
                gradient.GradientStops.Add(new GradientStop(Hsl(darkHue, Math.Min(0.8, mainSat + 0.05), 0.18, 0xA8), 1));
                var glow = new RadialGradientBrush { Center = new Point(0.12, 0), GradientOrigin = new Point(0.12, 0), RadiusX = 0.75, RadiusY = 1.4 };
                glow.GradientStops.Add(new GradientStop(Hsl(hues[2], nearSat, 0.62, 0x40), 0));
                glow.GradientStops.Add(new GradientStop(Hsl(hues[2], nearSat, 0.62, 0x00), 1));
                layers.Children.Add(new Border { CornerRadius = radius, Background = gradient });
                layers.Children.Add(new Border { CornerRadius = radius, Background = glow });
                edge.GradientStops.Add(new GradientStop(Hsl(hues[2], nearSat * 0.7, 0.82, 0x88), 0));
                edge.GradientStops.Add(new GradientStop(Hsl(hues[0], mainSat * 0.7, 0.72, 0x78), 1));
            }
            layers.Children.Add(new Border { CornerRadius = radius, BorderBrush = edge, BorderThickness = new Thickness(1) });
            var root = new Border { Padding = new Thickness(pad), Child = layers, UseLayoutRounding = true };
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            return Snapshot(root, width, height);
        }

        // 兩個色系時每個角的亮度：黃、橘這類顏色太暗會變成土色，亮一點；藍紫色看起來比較暗，也亮一點
        static double CornerLight(double hue)
        {
            hue = ((hue % 360) + 360) % 360;
            return hue >= 35 && hue <= 80 ? 0.46 : hue > 200 && hue < 290 ? 0.42 : 0.38;
        }

        // 從卡片的一個角（cx, cy 是 0 或 1）往內擴散、越來越淡的顏色；範圍各大約佔卡片的一半
        static Brush CornerGlow(Color color, double cx, double cy)
        {
            var glow = new RadialGradientBrush { Center = new Point(cx, cy), GradientOrigin = new Point(cx, cy), RadiusX = 0.78, RadiusY = 1.6 };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0xC8, color.R, color.G, color.B), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1));
            return glow;
        }

        // 毛玻璃的細微顆粒（跟 Windows 壓克力效果一樣的雜訊）：固定的亂數種子，每次畫出來都一樣，不會閃
        static ImageBrush noise;
        static ImageBrush NoiseBrush()
        {
            if (noise != null) return noise;
            const int size = 96;
            var random = new Random(12345);
            var pixels = new int[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                int a = random.Next(0, 14);                  // 0～5% 不透明
                int c = random.Next(2) == 0 ? 0 : a;         // 一半白點、一半黑點（預先乘好 alpha）
                pixels[i] = (a << 24) | (c << 16) | (c << 8) | c;
            }
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
            bitmap.Freeze();
            noise = new ImageBrush(bitmap) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, size, size), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
            noise.Freeze();
            return noise;
        }

        // 斜放的麥克風（線條）：右上是圓形的頭，握把往左下斜、越來越細（只畫一條線的話很像放大鏡）
        static FrameworkElement MicIcon(double s)
        {
            double size = Math.Round(18 * s), stroke = Math.Max(1, 1.4 * s);
            var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            double r = 3.8 * s, cx = size - r - 1.4 * s, cy = r + 1.4 * s;
            var head = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Stroke = Brushes.White, StrokeThickness = stroke };
            Canvas.SetLeft(head, cx - r); Canvas.SetTop(head, cy - r);
            canvas.Children.Add(head);

            // 握把的中線往左下（u），寬度方向跟它垂直（n）；靠近頭的那端寬、尾端窄，頭蓋住的那條邊不畫
            double k = Math.Sqrt(0.5), ux = -k, uy = k, nx = k, ny = k;
            double sx = cx + ux * r * 0.8, sy = cy + uy * r * 0.8;      // 握把起點（在頭的圓圈上）
            double length = 9.5 * s, w1 = 2.2 * s, w2 = 1.2 * s;        // 長度、起點半寬、尾端半寬
            double ex = sx + ux * length, ey = sy + uy * length;
            var body = new System.Windows.Shapes.Polyline
            {
                Points = new PointCollection
                {
                    new Point(sx + nx * w1, sy + ny * w1), new Point(ex + nx * w2, ey + ny * w2),
                    new Point(ex - nx * w2, ey - ny * w2), new Point(sx - nx * w1, sy - ny * w1),
                },
                Stroke = Brushes.White, StrokeThickness = stroke, StrokeLineJoin = PenLineJoin.Round,
            };
            canvas.Children.Add(body);
            return canvas;
        }

        // 音量：左邊喇叭（按了靜音 / 取消靜音；停 0.5 秒展開滑桿），右邊滑桿（點或拖曳調整）
        class VolumeControl
        {
            public Canvas Panel;
            public List<UIElement> IconParts = new List<UIElement>(), BarParts = new List<UIElement>();   // 喇叭、滑桿各畫成一張
            public double TrackLeft, TrackWidth;   // 滑桿在 Panel 裡的左端和長度
            public TextBlock Icon;
            public Border Fill;
            public FrameworkElement Knob;
            public double KnobRadius;

            public void Show(double volume)
            {
                volume = Math.Max(0, Math.Min(1, volume));
                // 靜音是喇叭打叉，其他依音量大小顯示一到三道聲波
                Icon.Text = volume <= 0 ? "" : volume < 0.34 ? "" : volume < 0.67 ? "" : "";
                Fill.Width = TrackWidth * volume;
                Canvas.SetLeft(Knob, TrackLeft + TrackWidth * volume - KnobRadius);
            }
        }

        static VolumeControl BuildVolume(double s, double buttonSize, double width, List<KeyValuePair<FrameworkElement, string>> controls)
        {
            var v = new VolumeControl { Panel = new Canvas { Width = width, Height = buttonSize }, KnobRadius = Math.Round(5 * s) };

            // 喇叭：跟播放按鈕一樣大，圖示的間距也跟它們一樣
            v.Icon = new TextBlock { FontFamily = IconFont, FontSize = 15 * s, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var mute = new Border { Width = buttonSize, Height = buttonSize, Background = Brushes.Transparent, Child = v.Icon };
            v.Panel.Children.Add(mute);
            v.IconParts.Add(mute);
            controls.Add(new KeyValuePair<FrameworkElement, string>(mute, "mute"));

            // 滑桿：從喇叭圖示右邊一點，到歌詞按鈕的麥克風前面（把手拉到最右邊也不會碰到麥克風）
            double inset = (buttonSize - 15 * s) / 2, gap = Math.Round(7 * s);
            double micLeft = width + (buttonSize - Math.Round(18 * s)) / 2;
            v.TrackLeft = buttonSize - inset + gap;
            v.TrackWidth = micLeft - gap - v.KnobRadius - v.TrackLeft;
            double thickness = Math.Max(2, Math.Round(4 * s)), top = (buttonSize - thickness) / 2;
            var track = new Border { Width = v.TrackWidth, Height = thickness, CornerRadius = new CornerRadius(thickness / 2), Background = new SolidColorBrush(Color.FromArgb(0x4D, 255, 255, 255)) };
            v.Fill = new Border { Height = thickness, CornerRadius = new CornerRadius(thickness / 2), Background = Brushes.White };
            v.Knob = new System.Windows.Shapes.Ellipse { Width = v.KnobRadius * 2, Height = v.KnobRadius * 2, Fill = Brushes.White };
            Canvas.SetLeft(track, v.TrackLeft); Canvas.SetTop(track, top);
            Canvas.SetLeft(v.Fill, v.TrackLeft); Canvas.SetTop(v.Fill, top);
            Canvas.SetTop(v.Knob, buttonSize / 2 - v.KnobRadius);
            v.Panel.Children.Add(track);
            v.Panel.Children.Add(v.Fill);
            v.Panel.Children.Add(v.Knob);

            // 點或拖曳的範圍：喇叭右邊到歌詞按鈕前面，整排按鈕的高度（比滑桿本身好點）
            var hit = new Border { Width = width - buttonSize, Height = buttonSize, Background = Brushes.Transparent };
            Canvas.SetLeft(hit, buttonSize);
            v.Panel.Children.Add(hit);
            v.BarParts.AddRange(new UIElement[] { track, v.Fill, v.Knob, hit });
            controls.Add(new KeyValuePair<FrameworkElement, string>(hit, "volume"));
            v.Show(0);
            return v;
        }

        static Border AddControl(Panel panel, string glyph, string action, double s, double size, List<KeyValuePair<FrameworkElement, string>> controls)
        {
            var button = new Border
            {
                Width = size, Height = size, Background = Brushes.Transparent,
                Child = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 15 * s, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            panel.Children.Add(button);
            controls.Add(new KeyValuePair<FrameworkElement, string>(button, action));
            return button;
        }

        // 天氣圖示（WMO 天氣代碼）
        static FrameworkElement WeatherIcon(int code, bool day, double size)
        {
            var canvas = new Canvas { Width = size, Height = size };
            var sun = Color.FromRgb(0xFD, 0xD6, 0x63);
            var moon = Color.FromRgb(0xFF, 0xE9, 0xA8);
            var cloud = Color.FromRgb(0xF1, 0xF3, 0xF4);
            var rain = Color.FromRgb(0x8A, 0xB4, 0xF8);

            if (code == 0)                       // 晴
            {
                if (day) AddSun(canvas, size / 2, size / 2, size * 0.34, sun); else AddMoon(canvas, size / 2, size / 2, size * 0.36, moon);
            }
            else if (code == 1 || code == 2)     // 晴時多雲
            {
                if (day) AddSun(canvas, size * 0.36, size * 0.34, size * 0.25, sun); else AddMoon(canvas, size * 0.36, size * 0.32, size * 0.27, moon);
                AddCloud(canvas, size * 0.24, size * 0.46, size * 0.76, cloud);
            }
            else if (code == 45 || code == 48)   // 霧
            {
                for (int i = 0; i < 3; i++)
                    AddBar(canvas, size * (0.1 + 0.08 * (i % 2)), size * (0.3 + 0.2 * i), size * 0.72, size * 0.09, cloud);
            }
            else
            {
                AddCloud(canvas, size * 0.08, size * 0.18, size * 0.84, cloud);
                if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82))            // 雨
                    for (int i = 0; i < 3; i++) AddBar(canvas, size * (0.27 + 0.2 * i), size * 0.74, size * 0.07, size * 0.2, rain);
                else if ((code >= 71 && code <= 77) || code == 85 || code == 86)          // 雪
                    for (int i = 0; i < 3; i++) AddDot(canvas, size * (0.3 + 0.2 * i), size * 0.84, size * 0.06, cloud);
                else if (code >= 95)                                                       // 雷雨
                    canvas.Children.Add(new System.Windows.Shapes.Path
                    {
                        Fill = new SolidColorBrush(sun),
                        Data = Geometry.Parse(string.Format(CultureInfo.InvariantCulture, "M{0},{1} L{2},{3} L{4},{3} L{5},{6} L{7},{8} L{9},{8} Z",
                            size * 0.52, size * 0.6, size * 0.4, size * 0.8, size * 0.5, size * 0.44, size, size * 0.62, size * 0.8, size * 0.52)),
                    });
            }
            return canvas;
        }

        static void AddSun(Canvas c, double cx, double cy, double r, Color color)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Fill = new SolidColorBrush(color) };
            dot.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = color, BlurRadius = r * 1.2, ShadowDepth = 0, Opacity = 0.55 };
            Canvas.SetLeft(dot, cx - r); Canvas.SetTop(dot, cy - r);
            c.Children.Add(dot);
        }

        static void AddMoon(Canvas c, double cx, double cy, double r, Color color)
        {
            var shape = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(cx, cy), r, r),
                new EllipseGeometry(new Point(cx + r * 0.55, cy - r * 0.35), r * 0.8, r * 0.8));
            c.Children.Add(new System.Windows.Shapes.Path { Data = shape, Fill = new SolidColorBrush(color) });
        }

        static void AddCloud(Canvas c, double x, double y, double w, Color color)
        {
            double h = w * 0.62;
            var shape = new GeometryGroup { FillRule = FillRule.Nonzero };
            shape.Children.Add(new RectangleGeometry(new Rect(x + w * 0.1, y + h * 0.5, w * 0.8, h * 0.42), h * 0.21, h * 0.21));
            shape.Children.Add(new EllipseGeometry(new Point(x + w * 0.3, y + h * 0.62), w * 0.2, w * 0.2));
            shape.Children.Add(new EllipseGeometry(new Point(x + w * 0.54, y + h * 0.45), w * 0.27, w * 0.27));
            shape.Children.Add(new EllipseGeometry(new Point(x + w * 0.74, y + h * 0.66), w * 0.17, w * 0.17));
            // 淡淡的深色邊，讓雲跟後面的太陽 / 月亮分得開
            c.Children.Add(new System.Windows.Shapes.Path
            {
                Data = shape.GetOutlinedPathGeometry(),   // 合成一個外框，邊線只畫在雲的外圍
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0x20, 0x24, 0x2A)), StrokeThickness = Math.Max(1, w * 0.045),
            });
        }

        static void AddBar(Canvas c, double x, double y, double w, double h, Color color)
        {
            var bar = new System.Windows.Shapes.Rectangle { Width = w, Height = h, RadiusX = Math.Min(w, h) / 2, RadiusY = Math.Min(w, h) / 2, Fill = new SolidColorBrush(color) };
            Canvas.SetLeft(bar, x); Canvas.SetTop(bar, y);
            c.Children.Add(bar);
        }

        static void AddDot(Canvas c, double cx, double cy, double r, Color color)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Fill = new SolidColorBrush(color) };
            Canvas.SetLeft(dot, cx - r); Canvas.SetTop(dot, cy - r);
            c.Children.Add(dot);
        }
    }

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

    // 仿 ChromeOS 的鎖定畫面：從上面滑下來蓋住桌面，左下角是時鐘和天氣；
    // 按空白鍵或滑鼠就往上滑走，回到桌面
    class LockScreen
    {
        const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        static readonly int[] HotKeys = { 0x20, 0x0D, 0x1B };   // 空白鍵、Enter、Esc（用按鍵代碼當登記編號）

        readonly List<LockWindow> windows = new List<LockWindow>();
        readonly List<KeyValuePair<Rect, string>> buttons = new List<KeyValuePair<Rect, string>>();   // 播放按鈕在主螢幕上的位置
        LockWindow primary;
        VideoEngine engine;
        DispatcherTimer clockTimer;
        string shownClock, shownMusic;
        string shownSong;   // 卡片上目前顯示的是哪首歌（換歌時封面和文字要翻頁）
        double shownMicLevel = -1;   // 歌詞按鈕現在的亮度
        string shownColorKey;        // 卡片上現在是哪個顏色（跟著專輯封面）
        bool colorShown;             // 卡片的顏色已經放上去了（之後顏色變了才淡入淡出）
        bool busy;

        // 音量（Spotify 自己的音量）
        LockClock.MusicCard card;          // 卡片目前的位置（音量變了只換音量那張圖）
        double shownVolume = -1;           // 卡片上顯示的音量（0～1）
        double shownVolumeLevel = -1;      // 音量那張圖的亮度（找不到 Spotify 的聲音時變暗）
        double audibleVolume = 0.5;        // 最近一次不是 0 的音量（按喇叭取消靜音時回到這裡）
        bool volumeFound, draggingVolume;
        bool volumeExpanded;               // 音量滑桿展開中（平常只有喇叭）
        DispatcherTimer hoverTimer, collapseTimer;
        DispatcherTimer dragWatch;         // 拖曳中每 0.1 秒看左鍵還有沒有按著（在別的螢幕上放開時，放開的訊息收不到）
        int volumeSetTick, wheel;          // 最近一次調音量的時間；滾輪還沒用掉的量
        int spotifyCommandTick;            // 最近一次真的送音量 / 靜音指令給 Spotify 的時間（它會因此跳到前景）

        // 歌詞
        bool lyricsOn;                     // 按了歌詞按鈕才顯示（換歌也繼續顯示）；鎖定畫面收起來就關掉，下次要再按一次
        DispatcherTimer lyricsTimer;
        Lyrics.Result lyrics;              // 目前這首歌的歌詞（null = 查詢中）
        string lyricsKey;                  // lyrics 是哪首歌的
        int musicTop, musicLeft;           // 音樂卡片的上緣、左緣（螢幕座標），歌詞放在它上面、跟卡片內容對齊
        // 畫面上的歌詞長圖（每首歌一張；換歌、關掉時舊的淡出）
        string sheetName, sheetSong;
        Lyrics.Result sheetLyrics;
        double[] lineCenters;
        double lineSpacing;
        int viewHeight, shownLine, sheetCount;

        public bool Active { get; private set; }
        public Action ActiveChanged;

        public void Open(VideoEngine videoEngine)
        {
            if (Active) return;
            Active = true;
            busy = true;
            engine = videoEngine;
            foreach (var screen in WinForms.Screen.AllScreens)
            {
                var b = screen.Bounds;
                var w = new LockWindow(b, b.Y - b.Height);   // 先放在螢幕正上方看不到的地方
                w.Dismiss = OnDismiss;
                w.Leave = OnLeave;
                w.Exposed = delegate { if (engine != null) engine.RequestRedraw(); };
                windows.Add(w);
                if (screen.Primary || primary == null) primary = w;
            }
            primary.Click = OnClick;
            primary.Hover = OnHover;
            primary.Drag = OnDrag;
            primary.Drop = OnDrop;
            primary.Wheel = OnWheel;
            LockWindow.ResetCursor();
            Attach(engine);
            if (ActiveChanged != null) ActiveChanged();
            NowPlaying.Start(delegate { if (Active) UpdateClock(true); });   // 開始讀正在播放的歌
            SpotifyVolume.Start();                                            // 和 Spotify 的音量

            // 趁控制面板（我們自己的視窗）還在前景，先把前景交給鎖定畫面，鍵盤輸入才會進來；
            // 視窗這時在螢幕正上方，看不到。面板失去前景後會自己收起來。
            Move(1);
            Native.SetForegroundWindow(primary.Handle);
            // 保險：直接向 Windows 登記這三個鍵，就算前景被搶走也收得到（收起時會取消）
            foreach (var hotkey in HotKeys) Native.RegisterHotKey(primary.Handle, hotkey, 0x4000 /* MOD_NOREPEAT */, (uint)hotkey);

            Weather.Refresh(delegate { if (Active) UpdateClock(true); });
            clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clockTimer.Tick += delegate
            {
                UpdateClock(false);
                Weather.Refresh(delegate { if (Active) UpdateClock(true); });
                // 滑鼠停 2.5 秒就把游標藏起來
                if (LockWindow.CursorShown && (DateTime.Now - LockWindow.LastMove).TotalSeconds > 2.5)
                {
                    LockWindow.CursorShown = false;
                    Native.SetCursor(IntPtr.Zero);
                }
            };
            clockTimer.Start();

            // 音量滑桿：滑鼠在喇叭上停 0.5 秒展開，離開 1 秒收起來
            hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            hoverTimer.Tick += delegate { hoverTimer.Stop(); if (Active && volumeFound && PointerOver(false)) ExpandVolume(true); };
            collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            collapseTimer.Tick += delegate { collapseTimer.Stop(); if (Active && !draggingVolume && !PointerOver(true)) ExpandVolume(false); };
            dragWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            dragWatch.Tick += delegate { if (!draggingVolume) dragWatch.Stop(); else if (!LockWindow.LeftButtonDown()) OnDrop(); };

            // 稍等影片第一格畫好，再從上面滑下來
            After(150, delegate
            {
                if (!Active) return;
                foreach (var w in windows) w.PaintBlack = false;
                Animate(1, 0, 500, delegate
                {
                    if (!Active) return;
                    Native.SetForegroundWindow(primary.Handle);   // 讓鍵盤輸入進到鎖定畫面
                    busy = false;
                });
            });
        }

        // 播放引擎重建時（例如顯示卡重設），把鎖定畫面重新接到新的引擎上
        public void Attach(VideoEngine videoEngine)
        {
            engine = videoEngine;
            if (engine == null) return;
            foreach (var w in windows) engine.AddTarget(w.Handle, w.Bounds.Width, w.Bounds.Height, w == primary);
            sheetName = null;   // 新引擎上還沒有歌詞的圖，要重新放上去
            colorShown = false; // 卡片的顏色也是
            UpdateClock(true);
            engine.RequestRedraw();
        }

        void UpdateClock(bool force)
        {
            if (!Active || engine == null) return;
            int w, h, x, y;

            // 左下角的時鐘和天氣：時間或天氣變了才重畫
            var now = DateTime.Now;
            string clockKey = LockClock.ClockKey(now);
            if (force || clockKey != shownClock)
            {
                shownClock = clockKey;
                var clock = LockClock.RenderClock(now, primary.Bounds.Height, out w, out h, out x, out y);
                engine.SetOverlay(primary.Handle, "clock", clock, w, h, x, y);
            }

            // 這首歌的歌詞（第一次會在背景查，查完再呼叫一次這裡）
            var track = NowPlaying.Current;
            lyrics = track == null ? null : Lyrics.Get(track, primary.Bounds.Height, delegate { if (Active) UpdateClock(true); });
            lyricsKey = track == null ? null : Lyrics.SongKey(track);
            // 歌詞按鈕的亮度：顯示中全白、有歌詞但沒開半亮、沒有歌詞很暗
            double micLevel = !Lyrics.Has(lyrics) ? 0.25 : lyricsOn ? 1.0 : 0.6;

            // Spotify 的音量（卡片上是 Spotify 時背景每半秒讀一次）：拖曳中、剛調過的 1.5 秒內不看（Spotify 還沒反應過來，讀到的會是舊的）
            SpotifyVolume.SetWanted(track != null && track.IsSpotify);
            double volume = Math.Max(0, shownVolume);
            if (track != null && !draggingVolume && Environment.TickCount - volumeSetTick > 1500)
            {
                volumeFound = SpotifyVolume.Found && track.IsSpotify;   // 卡片上是別的播放器（例如瀏覽器）時，音量按鈕變暗
                if (volumeFound)
                {
                    volume = Math.Round(SpotifyVolume.Level * 100) / 100;
                    if (volume > 0) audibleVolume = volume;
                }
            }
            double volumeLevel = volumeFound ? 1.0 : 0.25;   // 找不到 Spotify 的音量條（例如 Spotify 的視窗關掉了）就變暗

            // 右下角的音樂卡片：換歌、播放或暫停、封面變了才重畫；沒在播就拿掉
            string musicKey = NowPlaying.Key;
            if (force || musicKey != shownMusic)
            {
                shownMusic = musicKey;
                var found = new List<KeyValuePair<Rect, string>>();
                card = LockClock.RenderMusic(primary.Bounds.Width, primary.Bounds.Height, volume, found);
                var c = card ?? new LockClock.MusicCard();   // 沒在播：每張圖都是 null，拿掉
                if (card == null)
                {
                    // 卡片拿掉了：音量滑桿回到收起來的狀態，下次卡片出現時才不會一出來就是展開的
                    volumeExpanded = false;
                    if (hoverTimer != null) { hoverTimer.Stop(); collapseTimer.Stop(); }
                }
                // 卡片底下是毛玻璃：卡片範圍＝圖扣掉四周留給陰影的空間
                int shadow = LockClock.ShadowPad(primary.Bounds.Height);
                engine.SetGlassOverlay(primary.Handle, "music", c.Chrome, c.Width, c.Height, c.X, c.Y,
                    c.X + shadow, c.Y + shadow, c.Width - 2 * shadow, c.Height - 2 * shadow, LockClock.CardRadius(primary.Bounds.Height / 1080.0));
                // 毛玻璃上的顏色（跟著專輯封面）：換歌顏色變了就慢慢換過去（跟翻頁一樣 0.45 秒），卡片剛出現或拿掉時直接換
                if (card == null || c.ColorKey != shownColorKey || !colorShown)
                {
                    engine.SetFadeOverlay(primary.Handle, "musicColor", c.Color, c.Width, c.Height, c.X, c.Y, 1, card != null && colorShown ? 450 : 0);
                    shownColorKey = c.ColorKey;
                    colorShown = card != null;
                }

                // 封面、歌名、歌手：換歌時像翻頁一樣，舊的往左滑出、新的從右邊滑進來，兩張一起移動、間距不變，
                // 移動距離剛好一張卡片寬，舊的完全離開卡片時新的剛好就定位（按「上一首」換回來的話方向相反）
                string song = track == null ? null : Lyrics.SongKey(track);
                int shift = 0;
                if (card != null && shownSong != null && song != shownSong)
                {
                    bool back = NowPlaying.LastCommand == "prev" && Environment.TickCount - NowPlaying.LastCommandTick < 5000;
                    shift = (back ? -1 : 1) * (c.Width - 2 * shadow);
                }
                SetCardLayer("musicInfo", c.Info, shift, 1, 0);
                // 歌詞按鈕、音量跟著一起滑；亮度變了的話慢慢變亮或變暗
                SetCardLayer("musicMic", c.Mic, shift, micLevel, 300);
                SetCardLayer("musicVolume", c.Volume, shift, volumeLevel, 300);
                SetVolumeBarLayer(c.VolumeBar, shift, volumeLevel, volume);
                shownMicLevel = micLevel;
                shownVolume = volume;
                shownVolumeLevel = volumeLevel;
                shownSong = card == null ? null : song;
                if (card != null)
                {
                    musicTop = c.Y + shadow;
                    musicLeft = c.X + shadow;
                }
                buttons.Clear();
                buttons.AddRange(found);
            }
            else
            {
                // 只有歌詞按鈕的狀態變了（開關歌詞、查到歌詞）：不用重畫卡片，亮度花 0.3 秒慢慢變
                if (micLevel != shownMicLevel)
                {
                    engine.FadeOverlay(primary.Handle, "musicMic", micLevel, 300, false);
                    shownMicLevel = micLevel;
                }
                // 找不到 / 又找到 Spotify 的音量條：喇叭和滑桿慢慢變暗或變亮
                if (volumeLevel != shownVolumeLevel)
                {
                    engine.FadeOverlay(primary.Handle, "musicVolume", volumeLevel, 300, false);
                    engine.FadeOverlay(primary.Handle, "musicVolumeBar", volumeLevel, 300, false);
                    shownVolumeLevel = volumeLevel;
                }
                // 音量在別的地方被調了（在 Spotify 裡調的）：只重畫音量那兩張
                ShowVolume(volume);
            }
            // 音量按鈕變暗了（卡片換成別的播放器、找不到 Spotify 的音量條）：展開的滑桿收起來
            if (volumeExpanded && !volumeFound && !draggingVolume) ExpandVolume(false);
            UpdateLyrics();
        }

        // 卡片上跟著換歌一起滑動的圖（只在卡片範圍內看得到）；pixels 是 null 就拿掉。
        // revealW > 0：可以展開 / 收起的圖（音量滑桿），reveal 是新放上去時展開的程度
        void SetCardLayer(string name, int[] pixels, int shift, double opacity, int fadeMs,
            int revealX = 0, int revealW = 0, double reveal = 1, double pivot = 0, double pivotW = 0)
        {
            var c = card ?? new LockClock.MusicCard();
            int shadow = LockClock.ShadowPad(primary.Bounds.Height);
            engine.SetSlideOverlay(primary.Handle, name, pixels, c.Width, c.Height, c.X, c.Y, 2,
                c.X + shadow, c.Y + shadow, c.Width - 2 * shadow, c.Height - 2 * shadow, shift, 450, opacity, fadeMs, revealX, revealW, reveal, pivot, pivotW);
        }

        // 音量滑桿那張：可以展開 / 收起，展開時從喇叭旁邊長出來（把手的位置跟著音量，展開時把手保持原樣）
        void SetVolumeBarLayer(int[] pixels, int shift, double opacity, double volume)
        {
            var c = card ?? new LockClock.MusicCard();
            SetCardLayer("musicVolumeBar", pixels, shift, opacity, 300, c.RevealX, c.RevealW, volumeExpanded ? 1 : 0,
                c.KnobX(volume), c.KnobRadius + Math.Max(1, Math.Round(primary.Bounds.Height / 1080.0 * 2)));
        }

        // 卡片上的音量改成 volume（只重畫喇叭和滑桿那兩張）
        void ShowVolume(double volume)
        {
            if (card == null || engine == null || volume == shownVolume) return;
            shownVolume = volume;
            int[] bar;
            SetCardLayer("musicVolume", LockClock.RenderVolume(volume, out bar), 0, shownVolumeLevel, 300);
            SetVolumeBarLayer(bar, 0, shownVolumeLevel, volume);
        }

        // 展開 / 收起音量滑桿（從喇叭旁邊長出來、淡入；收起時縮回喇叭、淡出）
        void ExpandVolume(bool expand)
        {
            if (hoverTimer != null) hoverTimer.Stop();
            if (collapseTimer != null) collapseTimer.Stop();
            if (expand == volumeExpanded || card == null || engine == null) return;
            volumeExpanded = expand;
            engine.RevealOverlay(primary.Handle, "musicVolumeBar", expand ? 1 : 0, expand ? 380 : 260);
        }

        // 滑鼠在喇叭上停 0.5 秒展開滑桿；展開後離開喇叭和滑桿 1 秒就收起來
        void OnHover(int x, int y)
        {
            if (card == null || busy || hoverTimer == null) return;
            if (!volumeExpanded)
            {
                if (ButtonAt("mute", x, y)) { if (!hoverTimer.IsEnabled && volumeFound) hoverTimer.Start(); }
                else hoverTimer.Stop();
            }
            else if (draggingVolume || ButtonAt("mute", x, y) || ButtonAt("volume", x, y)) collapseTimer.Stop();
            else if (!collapseTimer.IsEnabled) collapseTimer.Start();
        }

        bool ButtonAt(string action, int x, int y)
        {
            foreach (var b in buttons) if (b.Value == action && b.Key.Contains(x, y)) return true;
            return false;
        }

        // 游標現在在不在喇叭上（includeBar：或展開的滑桿上）
        bool PointerOver(bool includeBar)
        {
            if (primary == null || !LockWindow.CursorShown) return false;
            Native.POINT p;
            Native.GetCursorPos(out p);
            int x = p.X - primary.Bounds.X, y = p.Y - primary.Bounds.Y;
            return ButtonAt("mute", x, y) || (includeBar && ButtonAt("volume", x, y));
        }

        // 按喇叭：靜音，或回到靜音前的音量（用 Spotify 自己的靜音鈕，它記得確切的音量）
        void ToggleMute()
        {
            if (hoverTimer != null) hoverTimer.Stop();   // 按了就不要再展開
            double expected = shownVolume > 0 ? 0 : audibleVolume;
            volumeSetTick = spotifyCommandTick = Environment.TickCount;
            SpotifyVolume.ToggleMute(expected);
            ShowVolume(expected);
        }

        // 調 Spotify 的音量（0～1，一格 10%，跟 Spotify 音量條從外面調的單位一樣），卡片上的滑桿馬上跟著動
        void SetVolume(double volume)
        {
            volume = Math.Round(Math.Round(Math.Max(0, Math.Min(1, volume)) / SpotifyVolume.Step) * SpotifyVolume.Step, 2);
            volumeSetTick = Environment.TickCount;
            if (volume > 0) audibleVolume = volume;
            if (volume == shownVolume) return;
            spotifyCommandTick = Environment.TickCount;
            SpotifyVolume.Set(volume);
            ShowVolume(volume);
        }

        // 點到「正在播放」的按鈕：控制 Spotify、切換歌詞或調音量，鎖定畫面不滑走（變暗的按鈕按了也不會滑走）
        bool OnClick(int x, int y)
        {
            if (busy) return false;
            foreach (var b in buttons)
                if (b.Key.Contains(x, y))
                {
                    if (b.Value == "lyrics")
                    {
                        if (Lyrics.Has(lyrics))
                        {
                            // 先讓歌詞開始淡入 / 淡出，再更新按鈕的樣子（只重畫「正在播放」那一塊）
                            lyricsOn = !lyricsOn;
                            UpdateLyrics();
                            UpdateClock(false);
                        }
                    }
                    else if (b.Value == "mute")
                    {
                        if (volumeFound) ToggleMute();
                    }
                    else if (b.Value == "volume")
                    {
                        if (!volumeExpanded) continue;   // 滑桿收起來時這裡是空的，當作沒點到按鈕
                        if (volumeFound)
                        {
                            // 點到哪裡就調到哪裡，按著不放可以左右拖曳（每跨過一格 Spotify 就跟著變）
                            draggingVolume = true;
                            primary.StartDrag();
                            if (dragWatch != null) dragWatch.Start();
                            SetVolume((x - card.TrackLeft) / card.TrackWidth);
                        }
                    }
                    else NowPlaying.Command(b.Value);
                    return true;
                }
            return false;
        }

        void OnDrag(int x, int y)
        {
            if (!draggingVolume || card == null || card.TrackWidth <= 0) return;
            // 左鍵已經放開（放開的訊息被別的視窗收走了，例如在別的螢幕上放開）：拖曳結束
            if (!LockWindow.LeftButtonDown()) { OnDrop(); return; }
            SetVolume((x - card.TrackLeft) / card.TrackWidth);
        }

        void OnDrop()
        {
            if (!draggingVolume) return;
            draggingVolume = false;
            volumeSetTick = Environment.TickCount;
            if (dragWatch != null) dragWatch.Stop();
            if (primary != null) primary.EndDrag();
            if (collapseTimer != null && !PointerOver(true)) collapseTimer.Start();   // 在滑桿外面放開：1 秒後收起來
        }

        // 滑鼠滾輪在卡片上：轉一格（120）調 10%（觸控板轉得比較細，累積到一格再調）
        bool OnWheel(int x, int y, int delta)
        {
            if (busy || card == null || !volumeFound) return false;
            int shadow = LockClock.ShadowPad(primary.Bounds.Height);
            if (x < card.X + shadow || x >= card.X + card.Width - shadow || y < card.Y + shadow || y >= card.Y + card.Height - shadow) return false;
            // 滾輪也會展開滑桿（看得到調到多少），停下來、游標不在喇叭和滑桿上的話 1 秒後收起來
            ExpandVolume(true);
            if (collapseTimer != null && !PointerOver(true)) collapseTimer.Start();
            wheel += delta;
            int steps = wheel / 120;
            if (steps != 0)
            {
                wheel -= steps * 120;
                SetVolume(shownVolume + steps * SpotifyVolume.Step);
            }
            return true;
        }

        // 歌詞要不要顯示：要的話放上這首歌的長圖（淡入）、開計時器跟著播放進度捲動；
        // 關掉、換歌、Spotify 沒在播時，舊的長圖淡出
        void UpdateLyrics()
        {
            var track = NowPlaying.Current;
            bool show = Active && engine != null && primary != null && lyricsOn && Lyrics.SheetReady(lyrics, primary.Bounds.Height)
                && track != null && Lyrics.SongKey(track) == lyricsKey;
            if (sheetName != null && (!show || sheetSong != lyricsKey)) HideSheet();
            if (!show)
            {
                if (lyricsTimer != null) lyricsTimer.Stop();
                return;
            }
            if (sheetName == null) ShowSheet();
            if (lyricsTimer == null)
            {
                // 只是看要不要換行，真正的捲動動畫由播放引擎跟著螢幕更新頻率畫
                lyricsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                lyricsTimer.Tick += delegate { LyricsTick(); };
            }
            if (!lyricsTimer.IsEnabled) lyricsTimer.Start();
        }

        // 放上這首歌的長圖（背景已經畫好了），淡入
        void ShowSheet()
        {
            double s = primary.Bounds.Height / 1080.0;
            sheetName = "lyrics" + (++sheetCount);
            sheetSong = lyricsKey;
            sheetLyrics = lyrics;
            lineCenters = lyrics.Centers;
            lineSpacing = lyrics.Spacing;
            viewHeight = (int)Math.Round(200 * s);   // 大約露出 5 行
            // 卡片正上方，文字左邊跟卡片裡的專輯封面對齊（長圖四周有留給陰影的空間，要扣掉）
            int x = musicLeft + (int)LockClock.CardPadding(s) - LockClock.ShadowPad(primary.Bounds.Height);
            int y = musicTop - (int)Math.Round(12 * s) - viewHeight;
            shownLine = CurrentLine(NowPlaying.Current);
            var mask = LockClock.LyricsMask(viewHeight, lineSpacing, lyrics.Synced);
            engine.SetScrollOverlay(primary.Handle, sheetName, lyrics.Sheet, lyrics.SheetWidth, lyrics.SheetHeight, x, y, viewHeight, mask, ScrollFor(shownLine), 300);
        }

        void HideSheet()
        {
            if (engine != null && primary != null) engine.FadeOverlay(primary.Handle, sheetName, 0, 300, true);   // 淡出，淡完就拿掉
            sheetName = null;
            sheetSong = null;
        }

        void LyricsTick()
        {
            if (sheetName == null || engine == null || primary == null) return;
            var track = NowPlaying.Current;
            if (track == null || Lyrics.SongKey(track) != sheetSong) return;
            int line = CurrentLine(track);
            if (line == shownLine) return;
            // 換行：平順地往上捲；一次跳好幾行（拖曳進度）就捲快一點
            int ms = Math.Abs(line - shownLine) > 2 ? 250 : 420;
            shownLine = line;
            engine.ScrollOverlay(primary.Handle, sheetName, ScrollFor(line), ms);
        }

        // 現在唱到第幾行（-1 = 還沒開始唱）；稍微提早 0.2 秒，捲動完剛好對上
        int CurrentLine(NowPlaying.Track track)
        {
            if (track == null) return -1;
            double now = NowPlaying.PositionSeconds(track) + 0.2;
            var lines = sheetLyrics.Lines;
            int line = -1;
            while (line + 1 < lines.Count && lines[line + 1].Time <= now) line++;
            return line;
        }

        // 要讓第 line 行停在露出那一段的正中間，長圖要從第幾列開始露出；還沒開始唱時，第一行放在中間下面一行
        double ScrollFor(int line)
        {
            double center = line >= 0 ? lineCenters[line] : lineCenters[0] - lineSpacing;
            return center - viewHeight / 2.0;
        }

        // 空白鍵 / 滑鼠：往上滑走，回到桌面
        void OnDismiss()
        {
            if (!Active || busy) return;
            busy = true;
            Animate(0, 1, 420, CloseNow);   // 往上滑走，滑完再收起來
        }

        // 切到別的程式（Alt+Tab、Win 鍵）、Alt+F4：一樣滑走
        void OnLeave(int activatedThread)
        {
            // 剛送了音量指令給 Spotify、切過去的又正好是 Spotify：是它自己跳到前景（從外面設定它的音量時會這樣），不是使用者切走。
            // 鎖定畫面本來就蓋在最上層，不收起來，稍等一下把前景拿回來（拿不回來也沒關係，空白鍵 / Enter / Esc 有另外登記）。
            // 使用者自己按 Win 鍵、Alt+Tab 切到別的程式時照樣滑走
            if (Active && Environment.TickCount - spotifyCommandTick < 2000 && IsSpotifyThread(activatedThread))
            {
                After(150, TakeForeground);
                return;
            }
            OnDismiss();
        }

        // 這條執行緒是不是 Spotify 的
        static bool IsSpotifyThread(int threadId)
        {
            if (threadId == 0) return false;
            IntPtr thread = Native.OpenThread(0x0800 /* THREAD_QUERY_LIMITED_INFORMATION */, false, threadId);
            if (thread == IntPtr.Zero) return false;
            try
            {
                int pid = Native.GetProcessIdOfThread(thread);
                using (var process = System.Diagnostics.Process.GetProcessById(pid))
                    return string.Equals(process.ProcessName, "Spotify", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
            finally { Native.CloseHandle(thread); }
        }

        // 把前景拿回鎖定畫面。Windows 不讓背景程式直接搶前景，
        // 所以暫時把這條執行緒的輸入接到現在的前景視窗（Spotify）上，再設定前景
        void TakeForeground()
        {
            if (!Active || primary == null) return;
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground == primary.Handle) return;
            int pid, me = Native.GetCurrentThreadId();
            int thread = Native.GetWindowThreadProcessId(foreground, out pid);
            bool attached = thread != 0 && thread != me && Native.AttachThreadInput(me, thread, true);
            Native.SetForegroundWindow(primary.Handle);
            if (attached) Native.AttachThreadInput(me, thread, false);
        }

        // 收起來（滑走之後，或電腦被 Win+L 鎖定、睡眠時直接收掉）
        public void CloseNow()
        {
            if (!Active) return;
            Active = false;
            busy = false;
            if (clockTimer != null) clockTimer.Stop();
            if (lyricsTimer != null) lyricsTimer.Stop();
            sheetName = null;
            sheetSong = null;
            lyricsOn = false;   // 下次打開鎖定畫面時歌詞先不出現，等按了按鈕再顯示
            shownSong = null;   // 下次打開時卡片直接出現，不播換歌動畫
            shownMicLevel = -1;
            card = null;
            colorShown = false;
            shownColorKey = null;
            shownVolume = shownVolumeLevel = -1;
            volumeFound = draggingVolume = volumeExpanded = false;
            if (hoverTimer != null) { hoverTimer.Stop(); collapseTimer.Stop(); dragWatch.Stop(); }
            wheel = 0;
            NowPlaying.Stop();
            SpotifyVolume.Stop();
            buttons.Clear();
            if (primary != null) foreach (var hotkey in HotKeys) Native.UnregisterHotKey(primary.Handle, hotkey);   // 還給其他程式
            primary = null;
            var all = new List<LockWindow>(windows);
            windows.Clear();
            foreach (var w in all) Native.ShowWindow(w.Handle, 0);
            if (engine != null)
            {
                // 等引擎放開這些視窗的輸出之後才刪掉視窗
                foreach (var w in all) engine.RemoveTarget(w.Handle);
                engine.WhenIdle(delegate { foreach (var w in all) w.DestroyHandle(); });
            }
            else foreach (var w in all) w.DestroyHandle();
            engine = null;
            if (ActiveChanged != null) ActiveChanged();
        }

        // 移動所有鎖定視窗：hidden = 0 完全蓋住螢幕，1 完全在螢幕上方
        void Move(double hidden)
        {
            IntPtr batch = Native.BeginDeferWindowPos(windows.Count);
            foreach (var w in windows)
                batch = Native.DeferWindowPos(batch, w.Handle, IntPtr.Zero, w.Bounds.X, w.Bounds.Y - (int)Math.Round(w.Bounds.Height * hidden), 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Native.EndDeferWindowPos(batch);
        }

        // 每次螢幕垂直同步就移動一次視窗（120Hz 螢幕每秒 120 次），不會被影片的更新頻率綁住。
        // 視窗屬於這條執行緒，要在這裡移動才會立刻生效（從別的執行緒移動，Windows 會排隊交給這條執行緒，反而變慢）。
        void Animate(double from, double to, int ms, Action done)
        {
            using (var vblank = new VBlank())
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    double t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / ms);
                    Move(from + (to - from) * StandardCurve(t));
                    if (t >= 1) break;
                    vblank.Wait();
                }
            }
            done();
        }

        // Material Design / ChromeOS 的標準動畫曲線 cubic-bezier(0.4, 0, 0.2, 1)：開頭和結尾都柔和
        public static double StandardCurve(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            double s = t;   // 用牛頓法找出貝茲曲線上 x = t 的參數
            for (int i = 0; i < 8; i++)
            {
                double x = Bezier(s, 0.4, 0.2) - t;
                double dx = BezierSlope(s, 0.4, 0.2);
                if (Math.Abs(x) < 1e-6 || Math.Abs(dx) < 1e-6) break;
                s = Math.Max(0, Math.Min(1, s - x / dx));
            }
            return Bezier(s, 0.0, 1.0);
        }

        static double Bezier(double s, double p1, double p2)
        {
            double u = 1 - s;
            return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
        }

        static double BezierSlope(double s, double p1, double p2)
        {
            double u = 1 - s;
            return 3 * u * u * p1 + 6 * u * s * (p2 - p1) + 3 * s * s * (1 - p2);
        }

        static void After(int ms, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += delegate { timer.Stop(); action(); };
            timer.Start();
        }
    }

    // 跟隨 Windows 的深淺色模式與強調色
    static class Theme
    {
        public static bool IsDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("SystemUsesLightTheme");
                    if (v is int) return (int)v == 0;
                }
            }
            catch { }
            return false;
        }

        // 系統強調色：深色模式用較亮的變體、淺色模式用較深的變體（跟 Windows 11 一樣）
        static Color Accent(bool dark)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var palette = key == null ? null : key.GetValue("AccentPalette") as byte[];
                    if (palette != null && palette.Length >= 32)
                    {
                        int i = (dark ? 1 : 4) * 4;
                        return Color.FromRgb(palette[i], palette[i + 1], palette[i + 2]);
                    }
                }
            }
            catch { }
            return dark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0);
        }

        static SolidColorBrush B(long argb)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            return brush;
        }

        public static void Apply(ResourceDictionary r, bool dark)
        {
            var accent = new SolidColorBrush(Accent(dark));
            accent.Freeze();
            r["Accent"] = accent;
            r["TextOnAccent"]    = B(dark ? 0xFF000000 : 0xFFFFFFFF);
            r["TextPrimary"]     = B(dark ? 0xFFFFFFFF : 0xE4000000);
            r["TextSecondary"]   = B(dark ? 0xC5FFFFFF : 0x9E000000);
            r["CardFill"]        = B(dark ? 0x0DFFFFFF : 0xB3FFFFFF);
            r["CardStroke"]      = B(dark ? 0x12FFFFFF : 0x0F000000);
            r["ControlFill"]     = B(dark ? 0x0FFFFFFF : 0xB3FFFFFF);
            r["ControlStroke"]   = B(dark ? 0x12FFFFFF : 0x0F000000);
            r["HoverOverlay"]    = B(dark ? 0x0FFFFFFF : 0x09000000);
            r["AccentOverlay"]   = B(dark ? 0x1A000000 : 0x1AFFFFFF);
            r["SegmentTrack"]    = B(dark ? 0x19000000 : 0x06000000);
            r["SegmentSelected"] = B(dark ? 0x15FFFFFF : 0xFFFFFFFF);
            r["Divider"]         = B(dark ? 0x15FFFFFF : 0x0F000000);
            r["FooterFill"]      = B(dark ? 0x33000000 : 0x0C000000);
            r["ToggleOffFill"]   = B(dark ? 0x19000000 : 0x06000000);
            r["ToggleOffStroke"] = B(dark ? 0x8BFFFFFF : 0x72000000);
            r["TooltipFill"]     = B(dark ? 0xFF2C2C2C : 0xFFF9F9F9);
            r["WindowFallback"]  = B(dark ? 0xFF202020 : 0xFFF3F3F3);
        }
    }

    // 點系統匣圖示後彈出的控制面板
    class TrayPanel
    {
        // 面板上的中文（XAML 裡整個屬性值）在非中文 Windows 換成英文
        static readonly string[,] XamlEnglish =
        {
            { "尚未選擇影片", "No video selected" },
            { "鎖定畫面", "Lock screen" },
            { "選擇影片", "Choose video" },
            { "畫面縮放", "Scaling" },
            { "填滿", "Fill" },
            { "完整顯示", "Fit" },
            { "拉伸", "Stretch" },
            { "選項", "Options" },
            { "自動暫停", "Auto-pause" },
            { "有視窗最大化或全螢幕時暫停，節省資源", "Pause when windows cover the desktop" },
            { "開機時自動啟動", "Start with Windows" },
            { "登入 Windows 後自動播放動態桌布", "Start playing when you sign in" },
            { "工作列透明", "Transparent taskbar" },
            { "動態桌布", "Motion Desktop" },
            { "關閉動態桌布，並還原原本的桌布", "Close Motion Desktop and restore your original wallpaper" },
            { "結束", "Exit" },
        };

        static string Localize(string xaml)
        {
            if (Lang.Chinese) return xaml;
            for (int i = 0; i < XamlEnglish.GetLength(0); i++)
                xaml = xaml.Replace("'" + XamlEnglish[i, 0] + "'", "'" + XamlEnglish[i, 1] + "'");   // 只換整個屬性值，不會換到別的字串裡的一部分
            return xaml;
        }

        const string Xaml = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
      xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
      Width='360' UseLayoutRounding='True' SnapsToDevicePixels='True'
      TextElement.FontFamily='Segoe UI, Microsoft JhengHei UI'
      TextElement.FontSize='13'
      TextElement.Foreground='{DynamicResource TextPrimary}'
      TextOptions.TextFormattingMode='Display'>
  <Grid.Resources>
    <FontFamily x:Key='IconFont'>Segoe Fluent Icons, Segoe MDL2 Assets</FontFamily>

    <Style x:Key='Btn' TargetType='Button'>
      <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
      <Setter Property='Background' Value='{DynamicResource ControlFill}'/>
      <Setter Property='BorderBrush' Value='{DynamicResource ControlStroke}'/>
      <Setter Property='Tag' Value='{DynamicResource HoverOverlay}'/>
      <Setter Property='BorderThickness' Value='1'/>
      <Setter Property='Padding' Value='14,0'/>
      <Setter Property='Height' Value='36'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bd' CornerRadius='6' Background='{TemplateBinding Background}'
                    BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
              <Grid>
                <Border x:Name='Hover' CornerRadius='5' Opacity='0'
                        Background='{Binding Tag, RelativeSource={RelativeSource TemplatedParent}}'/>
                <ContentPresenter x:Name='Cp' Margin='{TemplateBinding Padding}'
                                  HorizontalAlignment='Center' VerticalAlignment='Center'/>
              </Grid>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Hover' Property='Opacity' Value='1'/>
              </Trigger>
              <Trigger Property='IsPressed' Value='True'>
                <Setter TargetName='Hover' Property='Opacity' Value='0.5'/>
                <Setter TargetName='Cp' Property='Opacity' Value='0.75'/>
              </Trigger>
              <Trigger Property='IsEnabled' Value='False'>
                <Setter TargetName='Bd' Property='Opacity' Value='0.4'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='AccentBtn' TargetType='Button' BasedOn='{StaticResource Btn}'>
      <Setter Property='Background' Value='{DynamicResource Accent}'/>
      <Setter Property='Foreground' Value='{DynamicResource TextOnAccent}'/>
      <Setter Property='Tag' Value='{DynamicResource AccentOverlay}'/>
      <Setter Property='BorderThickness' Value='0'/>
    </Style>
    <Style x:Key='SubtleBtn' TargetType='Button' BasedOn='{StaticResource Btn}'>
      <Setter Property='Background' Value='Transparent'/>
      <Setter Property='BorderThickness' Value='0'/>
    </Style>

    <Style x:Key='Segment' TargetType='RadioButton'>
      <Setter Property='Foreground' Value='{DynamicResource TextSecondary}'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='RadioButton'>
            <!-- 選取的底色與小橫條由共用的 SegmentThumb 負責，切換時會滑過去 -->
            <Border x:Name='Bd' CornerRadius='4' Height='32' Background='Transparent'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='0,0,0,1'/>
            </Border>
            <ControlTemplate.Triggers>
              <MultiTrigger>
                <MultiTrigger.Conditions>
                  <Condition Property='IsMouseOver' Value='True'/>
                  <Condition Property='IsChecked' Value='False'/>
                </MultiTrigger.Conditions>
                <Setter TargetName='Bd' Property='Background' Value='{DynamicResource HoverOverlay}'/>
              </MultiTrigger>
              <Trigger Property='IsChecked' Value='True'>
                <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key='ToggleRow' TargetType='CheckBox'>
      <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='CheckBox'>
            <Border x:Name='Row' CornerRadius='5' Background='Transparent' Padding='12,10'>
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition/>
                  <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <ContentPresenter VerticalAlignment='Center'/>
                <Grid Grid.Column='1' Width='40' Height='20' Margin='16,0,0,0' VerticalAlignment='Center'>
                  <Border x:Name='Track' CornerRadius='10' BorderThickness='1'
                          Background='{DynamicResource ToggleOffFill}' BorderBrush='{DynamicResource ToggleOffStroke}'/>
                  <Ellipse x:Name='Knob' Width='12' Height='12' Margin='4,0,0,0'
                           HorizontalAlignment='Left' VerticalAlignment='Center' Fill='{DynamicResource ToggleOffStroke}'/>
                </Grid>
              </Grid>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Row' Property='Background' Value='{DynamicResource HoverOverlay}'/>
                <Setter TargetName='Knob' Property='Width' Value='14'/>
                <Setter TargetName='Knob' Property='Height' Value='14'/>
              </Trigger>
              <Trigger Property='IsChecked' Value='True'>
                <Setter TargetName='Track' Property='Background' Value='{DynamicResource Accent}'/>
                <Setter TargetName='Track' Property='BorderBrush' Value='{DynamicResource Accent}'/>
                <Setter TargetName='Knob' Property='Fill' Value='{DynamicResource TextOnAccent}'/>
                <Trigger.EnterActions>
                  <BeginStoryboard>
                    <Storyboard>
                      <ThicknessAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='Margin'
                                          To='24,0,0,0' Duration='0:0:0.18'>
                        <ThicknessAnimation.EasingFunction><CubicEase EasingMode='EaseOut'/></ThicknessAnimation.EasingFunction>
                      </ThicknessAnimation>
                    </Storyboard>
                  </BeginStoryboard>
                </Trigger.EnterActions>
                <Trigger.ExitActions>
                  <BeginStoryboard>
                    <Storyboard>
                      <ThicknessAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='Margin'
                                          To='4,0,0,0' Duration='0:0:0.18'>
                        <ThicknessAnimation.EasingFunction><CubicEase EasingMode='EaseOut'/></ThicknessAnimation.EasingFunction>
                      </ThicknessAnimation>
                    </Storyboard>
                  </BeginStoryboard>
                </Trigger.ExitActions>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType='ToolTip'>
      <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
      <Setter Property='FontFamily' Value='Segoe UI, Microsoft JhengHei UI'/>
      <Setter Property='FontSize' Value='12'/>
      <Setter Property='HasDropShadow' Value='False'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ToolTip'>
            <Border Background='{DynamicResource TooltipFill}' BorderBrush='{DynamicResource ControlStroke}'
                    BorderThickness='1' CornerRadius='4' Padding='8,5'>
              <ContentPresenter/>
            </Border>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Grid.Resources>

  <Grid.RowDefinitions>
    <RowDefinition Height='Auto'/>
    <RowDefinition Height='Auto'/>
  </Grid.RowDefinitions>

  <StackPanel Margin='16,16,16,14'>
    <!-- 影片預覽 -->
    <Border x:Name='PreviewCard' Height='186' CornerRadius='8' BorderBrush='{DynamicResource CardStroke}' BorderThickness='1'>
      <Grid>
        <Grid x:Name='PreviewVideo'/>
        <StackPanel x:Name='PreviewEmpty' HorizontalAlignment='Center' VerticalAlignment='Center'>
          <TextBlock Text='&#xE714;' FontFamily='{StaticResource IconFont}' FontSize='32'
                     Foreground='{DynamicResource TextSecondary}' HorizontalAlignment='Center'/>
          <TextBlock Text='尚未選擇影片' Foreground='{DynamicResource TextSecondary}' Margin='0,10,0,0' HorizontalAlignment='Center'/>
        </StackPanel>
        <Border x:Name='Caption' VerticalAlignment='Bottom' CornerRadius='0,0,7,7' Padding='14,30,14,11'>
          <Border.Background>
            <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'>
              <GradientStop Color='#00000000' Offset='0'/>
              <GradientStop Color='#B4000000' Offset='1'/>
            </LinearGradientBrush>
          </Border.Background>
          <StackPanel>
            <TextBlock x:Name='FileName' Foreground='White' FontSize='14' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/>
            <StackPanel Orientation='Horizontal' Margin='0,4,0,0'>
              <Ellipse x:Name='StatusDot' Width='7' Height='7' VerticalAlignment='Center'/>
              <TextBlock x:Name='StatusText' Foreground='#DDFFFFFF' FontSize='12' Margin='7,0,0,0' VerticalAlignment='Center'/>
            </StackPanel>
          </StackPanel>
        </Border>
      </Grid>
    </Border>

    <!-- 播放控制 -->
    <Grid Margin='0,12,0,0'>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='Auto'/>
        <ColumnDefinition Width='Auto'/>
        <ColumnDefinition Width='Auto'/>
        <ColumnDefinition Width='*'/>
      </Grid.ColumnDefinitions>
      <Button x:Name='PlayButton' Style='{StaticResource AccentBtn}' Width='56' Padding='0'>
        <TextBlock x:Name='PlayIcon' FontFamily='{StaticResource IconFont}' FontSize='16'/>
      </Button>
      <Button x:Name='MuteButton' Grid.Column='1' Style='{StaticResource Btn}' Width='44' Padding='0' Margin='8,0,0,0'>
        <TextBlock x:Name='MuteIcon' FontFamily='{StaticResource IconFont}' FontSize='16'/>
      </Button>
      <Button x:Name='LockButton' Grid.Column='2' Style='{StaticResource Btn}' Width='44' Padding='0' Margin='8,0,0,0'
              ToolTip='鎖定畫面'>
        <TextBlock Text='&#xE72E;' FontFamily='{StaticResource IconFont}' FontSize='16'/>
      </Button>
      <Button x:Name='ChooseButton' Grid.Column='3' Style='{StaticResource Btn}' Margin='8,0,0,0'>
        <StackPanel Orientation='Horizontal'>
          <TextBlock Text='&#xE838;' FontFamily='{StaticResource IconFont}' FontSize='15' VerticalAlignment='Center'/>
          <TextBlock Text='選擇影片' Margin='8,0,0,0' VerticalAlignment='Center'/>
        </StackPanel>
      </Button>
    </Grid>

    <!-- 畫面縮放 -->
    <TextBlock Text='畫面縮放' FontWeight='SemiBold' Margin='2,18,0,8'/>
    <Border CornerRadius='7' Background='{DynamicResource SegmentTrack}' BorderBrush='{DynamicResource ControlStroke}'
            BorderThickness='1' Padding='3'>
      <Grid x:Name='SegmentHost'>
        <Border x:Name='SegmentThumb' HorizontalAlignment='Left' CornerRadius='4' IsHitTestVisible='False'
                Background='{DynamicResource SegmentSelected}' BorderBrush='{DynamicResource ControlStroke}' BorderThickness='1'>
          <Border.RenderTransform><TranslateTransform/></Border.RenderTransform>
          <Border x:Name='SegmentPill' Width='16' Height='3' CornerRadius='1.5' Background='{DynamicResource Accent}'
                  VerticalAlignment='Bottom' Margin='0,0,0,2' RenderTransformOrigin='0.5,0.5'>
            <Border.RenderTransform><ScaleTransform/></Border.RenderTransform>
          </Border>
        </Border>
        <!-- 三個按鈕之間的間隔（3）跟它們離外框的距離（外框的 Padding 3）一樣 -->
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width='*'/><ColumnDefinition Width='3'/>
            <ColumnDefinition Width='*'/><ColumnDefinition Width='3'/>
            <ColumnDefinition Width='*'/>
          </Grid.ColumnDefinitions>
          <RadioButton x:Name='StretchFill' Grid.Column='0' Style='{StaticResource Segment}' Content='填滿'/>
          <RadioButton x:Name='StretchUniform' Grid.Column='2' Style='{StaticResource Segment}' Content='完整顯示'/>
          <RadioButton x:Name='StretchStretch' Grid.Column='4' Style='{StaticResource Segment}' Content='拉伸'/>
        </Grid>
      </Grid>
    </Border>
    <TextBlock x:Name='StretchHint' FontSize='12' Foreground='{DynamicResource TextSecondary}' Margin='2,7,0,0'/>

    <!-- 選項 -->
    <TextBlock Text='選項' FontWeight='SemiBold' Margin='2,18,0,8'/>
    <Border CornerRadius='8' Background='{DynamicResource CardFill}' BorderBrush='{DynamicResource CardStroke}'
            BorderThickness='1' Padding='4'>
      <StackPanel>
        <CheckBox x:Name='AutoPauseToggle' Style='{StaticResource ToggleRow}'>
          <StackPanel>
            <TextBlock Text='自動暫停'/>
            <TextBlock Text='有視窗最大化或全螢幕時暫停，節省資源' FontSize='12'
                       Foreground='{DynamicResource TextSecondary}' Margin='0,2,0,0'/>
          </StackPanel>
        </CheckBox>
        <CheckBox x:Name='StartupToggle' Style='{StaticResource ToggleRow}' Margin='0,2,0,0'>
          <StackPanel>
            <TextBlock Text='開機時自動啟動'/>
            <TextBlock Text='登入 Windows 後自動播放動態桌布' FontSize='12'
                       Foreground='{DynamicResource TextSecondary}' Margin='0,2,0,0'/>
          </StackPanel>
        </CheckBox>
        <CheckBox x:Name='TaskbarToggle' Style='{StaticResource ToggleRow}' Margin='0,2,0,0'>
          <StackPanel>
            <TextBlock Text='工作列透明'/>
            <TextBlock x:Name='TaskbarHint' FontSize='12' TextWrapping='Wrap'
                       Foreground='{DynamicResource TextSecondary}' Margin='0,2,0,0'/>
          </StackPanel>
        </CheckBox>
      </StackPanel>
    </Border>
  </StackPanel>

  <!-- 底部列 -->
  <Border Grid.Row='1' Background='{DynamicResource FooterFill}' BorderBrush='{DynamicResource Divider}'
          BorderThickness='0,1,0,0' Padding='16,8,10,8'>
    <Grid>
      <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
        <Border Width='20' Height='15' CornerRadius='4'>
          <Border.Background>
            <LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
              <GradientStop Color='#3B82F6' Offset='0'/>
              <GradientStop Color='#8B5CF6' Offset='1'/>
            </LinearGradientBrush>
          </Border.Background>
          <Path Data='M0,0 L0,7 L6,3.5 Z' Fill='White' HorizontalAlignment='Center' VerticalAlignment='Center' Margin='1,0,0,0'/>
        </Border>
        <TextBlock Text='動態桌布' Margin='9,0,0,0' VerticalAlignment='Center' FontWeight='SemiBold'/>
      </StackPanel>
      <Button x:Name='QuitButton' Style='{StaticResource SubtleBtn}' HorizontalAlignment='Right' Height='32' Padding='10,0'
              ToolTip='關閉動態桌布，並還原原本的桌布'>
        <StackPanel Orientation='Horizontal'>
          <TextBlock Text='&#xE7E8;' FontFamily='{StaticResource IconFont}' FontSize='14' VerticalAlignment='Center'/>
          <TextBlock Text='結束' Margin='8,0,0,0' VerticalAlignment='Center'/>
        </StackPanel>
      </Button>
    </Grid>
  </Border>
</Grid>";

        readonly WallpaperApp app;
        readonly Window window;
        readonly Grid root;
        readonly bool backdrop = Environment.OSVersion.Version.Build >= 22000;   // Windows 11 才開毛玻璃（Win10 移動視窗時會卡）
        readonly Border previewCard, caption, segmentThumb, segmentPill;
        readonly Grid previewVideo, segmentHost;
        readonly MediaElement previewMedia;
        readonly FrameworkElement previewEmpty;
        readonly TextBlock fileName, statusText, playIcon, muteIcon, stretchHint, taskbarHint;
        readonly System.Windows.Shapes.Ellipse statusDot;
        readonly Button playButton, muteButton;
        readonly RadioButton stretchFill, stretchUniform, stretchStretch;
        readonly CheckBox autoPauseToggle, startupToggle, taskbarToggle;
        IntPtr hwnd;
        string previewPath;
        bool? previewPlaying;
        Stretch? shownStretch;
        double restingTop;
        DateTime lastHidden = DateTime.MinValue;
        bool open, dark, refreshing;

        public TrayPanel(WallpaperApp app)
        {
            this.app = app;
            root = (Grid)XamlReader.Parse(Localize(Xaml));
            previewCard = Find<Border>("PreviewCard");
            previewVideo = Find<Grid>("PreviewVideo");
            previewEmpty = Find<FrameworkElement>("PreviewEmpty");
            caption = Find<Border>("Caption");
            fileName = Find<TextBlock>("FileName");
            statusText = Find<TextBlock>("StatusText");
            statusDot = Find<System.Windows.Shapes.Ellipse>("StatusDot");
            playIcon = Find<TextBlock>("PlayIcon");
            muteIcon = Find<TextBlock>("MuteIcon");
            stretchHint = Find<TextBlock>("StretchHint");
            playButton = Find<Button>("PlayButton");
            muteButton = Find<Button>("MuteButton");
            stretchFill = Find<RadioButton>("StretchFill");
            stretchUniform = Find<RadioButton>("StretchUniform");
            stretchStretch = Find<RadioButton>("StretchStretch");
            autoPauseToggle = Find<CheckBox>("AutoPauseToggle");
            startupToggle = Find<CheckBox>("StartupToggle");
            taskbarToggle = Find<CheckBox>("TaskbarToggle");
            taskbarHint = Find<TextBlock>("TaskbarHint");
            segmentHost = Find<Grid>("SegmentHost");
            segmentThumb = Find<Border>("SegmentThumb");
            segmentPill = Find<Border>("SegmentPill");
            segmentHost.SizeChanged += delegate { MoveThumb(false); };

            playButton.Click += delegate { app.TogglePause(); };
            muteButton.Click += delegate { app.ToggleMute(); };
            Find<Button>("ChooseButton").Click += delegate { Hide(); app.ChooseVideo(); };
            Find<Button>("QuitButton").Click += delegate { Hide(); app.Quit(); };
            stretchFill.Checked += delegate { if (!refreshing) app.SetStretch(Stretch.UniformToFill); };
            stretchUniform.Checked += delegate { if (!refreshing) app.SetStretch(Stretch.Uniform); };
            stretchStretch.Checked += delegate { if (!refreshing) app.SetStretch(Stretch.Fill); };
            // 用 Checked / Unchecked（不管是滑鼠、鍵盤或輔助工具切換都會觸發），refreshing 期間是程式自己在更新畫面，不算
            RoutedEventHandler autoPauseChanged = delegate { if (!refreshing) app.SetAutoPause(autoPauseToggle.IsChecked == true); };
            autoPauseToggle.Checked += autoPauseChanged;
            autoPauseToggle.Unchecked += autoPauseChanged;
            RoutedEventHandler startupChanged = delegate { if (!refreshing) app.SetStartup(startupToggle.IsChecked == true); };
            startupToggle.Checked += startupChanged;
            startupToggle.Unchecked += startupChanged;
            RoutedEventHandler taskbarChanged = delegate { if (!refreshing) app.SetTaskbarFix(taskbarToggle.IsChecked == true); };
            taskbarToggle.Checked += taskbarChanged;
            taskbarToggle.Unchecked += taskbarChanged;

            // 鎖定畫面：先開（面板這時還在前景，鎖定畫面才拿得到鍵盤），再把面板直接收掉（不播動畫，反正會被蓋住）
            Find<Button>("LockButton").Click += delegate { app.ShowLockScreen(); HideNow(); };
            Find<Button>("LockButton").ToolTip = Lang.T("鎖定畫面（" + WallpaperApp.LockHotKeyText + "）", "Lock screen (" + WallpaperApp.LockHotKeyText + ")");

            // 預覽用自己的小播放器（靜音），只在面板開著時播放
            // ScrubbingEnabled：暫停中跳到某個位置時也要畫出那一格（不開的話暫停時預覽是黑的）
            previewMedia = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, IsMuted = true, ScrubbingEnabled = true };
            previewMedia.MediaOpened += delegate
            {
                previewMedia.Position = app.PlaybackPosition;   // 從桌布目前的位置開始，跟桌面上看到的同一格
                if (previewPlaying == true) previewMedia.Play(); else previewMedia.Pause();
            };
            previewMedia.MediaEnded += delegate { previewMedia.Position = TimeSpan.Zero; previewMedia.Play(); };
            previewVideo.Children.Add(previewMedia);
            previewVideo.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                previewVideo.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
            };

            window = new Window
            {
                Title = Lang.AppName,
                Content = root,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true,
                Background = Brushes.Transparent,
            };
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(backdrop ? -1 : 0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
            window.SourceInitialized += delegate
            {
                var source = (HwndSource)PresentationSource.FromVisual(window);
                hwnd = source.Handle;
                if (backdrop) source.CompositionTarget.BackgroundColor = Colors.Transparent;
                ApplyWindowTheme();
            };
            window.Deactivated += delegate { Hide(); };
            window.PreviewKeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Hide(); };
            // Alt+F4 之類的關閉要求：只收起來。視窗真的關掉的話就再也打不開了（之後按系統匣圖示會出錯）
            window.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); };
        }

        T Find<T>(string name) where T : class
        {
            return root.FindName(name) as T;
        }

        public bool IsOpen { get { return open; } }

        public void Toggle()
        {
            if (open) { Hide(); return; }
            // 面板開著時點系統匣圖示，會先因失去焦點而關閉，這時不要又馬上打開
            if ((DateTime.Now - lastHidden).TotalMilliseconds < 400) return;
            Show();
        }

        public void Show()
        {
            dark = Theme.IsDark();
            Theme.Apply(root.Resources, dark);
            if (!backdrop) window.Background = (Brush)root.Resources["WindowFallback"];
            ApplyWindowTheme();
            open = true;
            WallpaperApp.IsStartupEnabled(true);   // 打開面板時重新查一次開機自動啟動（例如在工作排程器裡被改過）
            Refresh();

            // 放在工作列旁邊（右下角，跟 Windows 11 的快速設定一樣）
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = root.DesiredSize;
            var area = SystemParameters.WorkArea;
            const double margin = 12;
            double left = area.Right - size.Width - margin, top = area.Bottom - size.Height - margin;
            if (area.Top > 0) top = area.Top + margin;     // 工作列在上方
            if (area.Left > 0) left = area.Left + margin;  // 工作列在左方
            restingTop = top;

            // 整個面板由下往上滑入、同時淡入（如果正在關閉的動畫中被重新打開，就從目前位置接著滑回來）
            if (!window.IsVisible)
            {
                window.BeginAnimation(Window.TopProperty, null);
                root.BeginAnimation(UIElement.OpacityProperty, null);
                window.Left = left;
                window.Top = top + SlideDistance;
                root.Opacity = 0;
                window.Show();
            }
            window.Activate();
            Animate(restingTop, 1, 260, EasingMode.EaseOut, null);
        }

        const double SlideDistance = 20;

        public void Hide()
        {
            if (!open) return;
            open = false;
            lastHidden = DateTime.Now;

            // 跟打開時相反：往下滑出、同時淡出，動畫結束才真正隱藏
            Animate(restingTop + SlideDistance, 0, 170, EasingMode.EaseIn, delegate
            {
                if (open) return;   // 動畫途中又被打開了
                window.Hide();
                UpdatePreview();
            });
        }

        void Animate(double top, double opacity, int ms, EasingMode easing, Action done)
        {
            var duration = TimeSpan.FromMilliseconds(ms);
            var move = new DoubleAnimation(top, duration) { EasingFunction = new CubicEase { EasingMode = easing } };
            var fade = new DoubleAnimation(opacity, duration) { EasingFunction = new CubicEase { EasingMode = easing } };
            if (done != null) move.Completed += delegate { done(); };
            window.BeginAnimation(Window.TopProperty, move);
            root.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // 畫面縮放的選取框：切換時滑到新的位置，底下的小橫條滑動中會先拉長再縮回
        void MoveThumb(bool animate)
        {
            const double gap = 3;   // 按鈕之間的間隔（跟 XAML 裡的一樣）
            double width = (segmentHost.ActualWidth - 2 * gap) / 3;
            if (width <= 0) return;
            segmentThumb.Width = width;   // 選取框剛好跟一個按鈕一樣大
            int index = app.Stretch == Stretch.Uniform ? 1 : app.Stretch == Stretch.Fill ? 2 : 0;
            double x = index * (width + gap);
            var shift = (TranslateTransform)segmentThumb.RenderTransform;

            if (!animate)
            {
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.X = x;
                return;
            }

            var duration = TimeSpan.FromMilliseconds(320);
            shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });

            var stretchPill = new DoubleAnimationUsingKeyFrames { Duration = duration };
            stretchPill.KeyFrames.Add(new EasingDoubleKeyFrame(2.4, KeyTime.FromPercent(0.45), new CubicEase { EasingMode = EasingMode.EaseOut }));
            stretchPill.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            ((ScaleTransform)segmentPill.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, stretchPill);
        }

        // 不播動畫，直接收起來
        public void HideNow()
        {
            if (!open) return;
            open = false;
            window.BeginAnimation(Window.TopProperty, null);
            root.BeginAnimation(UIElement.OpacityProperty, null);
            window.Hide();
            UpdatePreview();
            lastHidden = DateTime.Now;
        }

        void UpdatePreview()
        {
            if (!open || !app.HasVideo)
            {
                if (previewPath != null) { previewMedia.Close(); previewMedia.Source = null; previewPath = null; }
                return;
            }
            if (previewPath != app.VideoPath)
            {
                previewPath = app.VideoPath;
                previewPlaying = null;
                previewMedia.Source = new Uri(previewPath);
            }
            previewMedia.Stretch = app.Stretch;
            bool play = !app.UserPaused;
            if (previewPlaying == play) return;
            bool alreadyLoaded = previewPlaying.HasValue;
            previewPlaying = play;
            if (play) previewMedia.Play();
            else
            {
                previewMedia.Pause();
                if (alreadyLoaded) previewMedia.Position = app.PlaybackPosition;   // 對齊桌布停住的那一格
            }
        }

        void ApplyWindowTheme()
        {
            if (hwnd == IntPtr.Zero) return;
            Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
            Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2);   // 圓角
            Native.SetDwm(hwnd, Native.DWMWA_TRANSITIONS_FORCEDISABLED, 1);  // 用自己的滑入動畫
            if (!backdrop) return;
            // 系統內建的 Acrylic 疊色很厚、視窗沒在前景時還會變純色，
            // 改用可以自訂透明度的 Acrylic 模糊，毛玻璃效果明顯很多；失敗時才退回系統內建的
            Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 1);
            if (!Native.SetAcrylic(hwnd, dark ? AcrylicTintDark : AcrylicTintLight))
                Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 3);
        }

        // 毛玻璃的疊色（AABBGGRR）：最前面兩位是不透明度，數字越小越透
        const uint AcrylicTintDark = 0x60202020;
        const uint AcrylicTintLight = 0x70F3F3F3;

        public void Refresh()
        {
            refreshing = true;
            try
            {
                bool has = app.HasVideo;
                previewEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
                caption.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                previewCard.Background = has ? Brushes.Black : (Brush)root.Resources["CardFill"];
                fileName.Text = has ? Path.GetFileNameWithoutExtension(app.VideoPath) : "";

                UpdatePreview();

                string status; Color dot;
                if (app.LastError != null) { status = Lang.T("無法播放這個影片", "Can't play this video"); dot = Color.FromRgb(0xFF, 0x6B, 0x6B); }
                else if (app.UserPaused) { status = Lang.T("已暫停", "Paused"); dot = Color.FromRgb(0xA0, 0xA0, 0xA0); }
                else if (app.AutoPaused) { status = Lang.T("自動暫停中・有視窗遮住桌面", "Auto-paused · A window covers the desktop"); dot = Color.FromRgb(0xFF, 0xB9, 0x00); }
                else { status = Lang.T("播放中", "Playing"); dot = Color.FromRgb(0x6C, 0xCB, 0x5F); }
                statusText.Text = status;
                statusDot.Fill = new SolidColorBrush(dot);

                playIcon.Text = app.UserPaused ? "" : "";
                playButton.ToolTip = app.UserPaused ? Lang.T("播放", "Play") : Lang.T("暫停", "Pause");
                muteIcon.Text = app.Muted ? "" : "";
                muteButton.ToolTip = app.Muted ? Lang.T("取消靜音", "Unmute") : Lang.T("靜音", "Mute");
                playButton.IsEnabled = has;
                muteButton.IsEnabled = has;

                stretchFill.IsChecked = app.Stretch == Stretch.UniformToFill;
                stretchUniform.IsChecked = app.Stretch == Stretch.Uniform;
                stretchStretch.IsChecked = app.Stretch == Stretch.Fill;
                MoveThumb(open && window.IsVisible && shownStretch.HasValue && shownStretch.Value != app.Stretch);
                shownStretch = app.Stretch;
                stretchHint.Text = app.Stretch == Stretch.Uniform ? Lang.T("完整顯示整個畫面，比例不同時會有黑邊", "Shows the whole video; may add black bars")
                                 : app.Stretch == Stretch.Fill ? Lang.T("拉伸到跟螢幕一樣大，比例可能變形", "Stretches to the screen; may look distorted")
                                 : Lang.T("填滿整個螢幕，比例不同時會裁掉邊緣", "Fills the screen; may crop the edges");

                autoPauseToggle.IsChecked = app.AutoPause;
                startupToggle.IsChecked = WallpaperApp.IsStartupEnabled();
                taskbarToggle.IsChecked = app.TaskbarFixEnabled;
                taskbarHint.Text = TaskbarFix.TranslucentTBRunning
                    ? Lang.T("搭配 TranslucentTB：開機時提早啟動它，工作列變黑時自動修正", "Works with TranslucentTB: starts it early and fixes a black taskbar")
                    : Lang.T("需要開著 TranslucentTB 才有作用（Microsoft Store 免費下載）", "Requires TranslucentTB to be running (free on the Microsoft Store)");
            }
            finally { refreshing = false; }
        }
    }

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

    class WallpaperApp
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "VideoWallpaper";

        readonly Settings settings;
        readonly List<ScreenPlayer> players = new List<ScreenPlayer>();
        readonly DispatcherTimer timer, coverageCheck, taskbarCheck;
        int taskbarTicks;
        readonly Native.WinEventProc winEventProc;   // 要一直留著參考，不然會被 GC 回收導致當掉
        readonly List<IntPtr> winEventHooks = new List<IntPtr>();
        readonly WinForms.NotifyIcon tray;
        readonly TrayPanel panel;
        DesktopHost host;
        VideoEngine engine;   // 所有螢幕共用一個播放引擎
        readonly LockScreen lockScreen = new LockScreen();
        HwndSource powerWindow;   // 接收螢幕開關通知用
        string screenSignature = "";
        string lastError;
        bool userPaused, locked, suspended, displayOff, choosing;
        bool SystemPaused { get { return locked || suspended || displayOff; } }   // 鎖定、睡眠、螢幕關閉時都暫停
        bool enginePlaying, engineLost;
        int ticksSinceRebuild;

        public WallpaperApp(string initialPath)
        {
            settings = Settings.Load();
            if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
            {
                settings.VideoPath = Path.GetFullPath(initialPath);
                settings.Save();
            }
            RefreshStartupEntry();

            // 鎖定畫面：開著的時候影片要播（就算桌布被視窗蓋住）
            lockScreen.ActiveChanged = delegate { UpdateCoverage(); ApplyPlayState(); };
            Weather.SetFixedLocation(settings.WeatherLocation);
            Weather.Refresh(null);   // 先把天氣查好，打開鎖定畫面時就有

            panel = new TrayPanel(this);
            tray = new WinForms.NotifyIcon { Icon = MakeIcon(), Visible = true };
            tray.MouseUp += delegate { panel.Toggle(); };   // 左鍵、右鍵都打開面板
            RefreshTray();

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += delegate { Tick(); };
            timer.Start();

            // 視窗有變化時 50ms 內重新判斷要不要暫停（每秒一次的 Tick 只是備援）
            coverageCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            coverageCheck.Tick += delegate
            {
                coverageCheck.Stop();
                UpdateCoverage();
                ApplyPlayState();
                if (panel.IsOpen) panel.Refresh();
            };
            // 工作列透明：切換前景視窗（開關開始選單、搜尋也算）之後稍等一下就檢查工作列有沒有變黑
            taskbarCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            taskbarCheck.Tick += delegate
            {
                taskbarCheck.Stop();
                CheckTaskbar();
            };
            taskbarCheck.Start();   // 啟動後先檢查一次
            winEventProc = OnWinEvent;
            HookWindowEvents();

            // 鎖定、睡眠、螢幕關閉時暫停（省電），恢復時立刻繼續播放
            var dispatcher = Dispatcher.CurrentDispatcher;
            SystemEvents.SessionSwitch += delegate(object s, SessionSwitchEventArgs e)
            {
                if (e.Reason == SessionSwitchReason.SessionLock)
                    dispatcher.BeginInvoke(new Action(delegate { SetSystemState(SystemState.Locked, true); }));
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                    dispatcher.BeginInvoke(new Action(delegate { SetSystemState(SystemState.Locked, false); }));
            };
            SystemEvents.PowerModeChanged += delegate(object s, PowerModeChangedEventArgs e)
            {
                if (e.Mode == PowerModes.Suspend)
                    dispatcher.BeginInvoke(new Action(delegate { SetSystemState(SystemState.Suspended, true); }));
                else if (e.Mode == PowerModes.Resume)
                    dispatcher.BeginInvoke(new Action(delegate { SetSystemState(SystemState.Suspended, false); }));
            };

            // 螢幕關閉 / 開啟：用一個看不到的訊息視窗接收 Windows 的電源通知
            powerWindow = new HwndSource(new HwndSourceParameters("VideoWallpaperPower") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });   // HWND_MESSAGE
            powerWindow.AddHook(PowerHook);
            var displayState = GUID_CONSOLE_DISPLAY_STATE;
            Native.RegisterPowerSettingNotification(powerWindow.Handle, ref displayState, 0);
            // 同一個視窗也接收打開鎖定畫面的快速鍵（MOD_WIN | MOD_SHIFT | MOD_NOREPEAT）
            Native.RegisterHotKey(powerWindow.Handle, LockHotKeyId, 0x8 | 0x4 | 0x4000, 'L');

            if (HasVideo)
                Rebuild();
            else if (string.IsNullOrEmpty(settings.VideoPath))
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(delegate
                {
                    ChooseVideo();
                    if (!HasVideo) panel.Show();
                }));
            // 有設定影片、只是暫時找不到（例如開機時放影片的磁碟還沒準備好）：不跳視窗，每秒檢查，找到就開始播
        }

        // ---------- 給面板用的狀態與操作 ----------

        public bool HasVideo { get { return !string.IsNullOrEmpty(settings.VideoPath) && File.Exists(settings.VideoPath); } }
        public string VideoPath { get { return settings.VideoPath; } }
        public bool UserPaused { get { return userPaused; } }
        public bool Muted { get { return settings.Muted; } }
        public bool AutoPause { get { return settings.AutoPause; } }
        public bool TaskbarFixEnabled { get { return settings.TaskbarFix; } }
        public Stretch Stretch { get { return settings.Stretch; } }
        public string LastError { get { return lastError; } }
        public bool AutoPaused { get { return settings.AutoPause && players.Exists(p => p.Covered); } }
        public TimeSpan PlaybackPosition { get { return engine != null ? engine.Position : TimeSpan.Zero; } }

        public void ShowPanel() { panel.Show(); }

        // 控制面板的「鎖定畫面」按鈕
        public void ShowLockScreen()
        {
            if (engine == null)
            {
                tray.ShowBalloonTip(5000, Lang.AppName, Lang.T("要先選一段影片，才能開啟鎖定畫面。", "Choose a video first to open the lock screen."), WinForms.ToolTipIcon.Info);
                return;
            }
            lockScreen.Open(engine);
        }

        public void TogglePause()
        {
            userPaused = !userPaused;
            ApplyPlayState();
            panel.Refresh();
        }

        public void ToggleMute()
        {
            settings.Muted = !settings.Muted;
            settings.Save();
            if (engine != null) engine.SetMuted(settings.Muted);
            panel.Refresh();
        }

        public void SetStretch(Stretch stretch)
        {
            settings.Stretch = stretch;
            settings.Save();
            if (engine != null) engine.SetStretch(stretch);
            panel.Refresh();
        }

        public void SetAutoPause(bool enable)
        {
            settings.AutoPause = enable;
            settings.Save();
            UpdateCoverage();
            ApplyPlayState();
            panel.Refresh();
        }

        public void SetStartup(bool enable)
        {
            SetStartupEntry(enable);
            panel.Refresh();
        }

        public void SetTaskbarFix(bool enable)
        {
            settings.TaskbarFix = enable;
            settings.Save();
            CheckTaskbar();
            panel.Refresh();
        }

        // 鎖定、睡眠、螢幕關閉、或鎖定畫面開著（蓋住工作列）時不用檢查
        void CheckTaskbar()
        {
            if (!settings.TaskbarFix || SystemPaused || lockScreen.Active) return;
            try { TaskbarFix.Check(); } catch { }   // 只是輔助功能，出錯也不能讓程式當掉
        }

        public void ChooseVideo()
        {
            if (choosing) return;
            choosing = true;
            try
            {
                using (var dlg = new WinForms.OpenFileDialog())
                {
                    dlg.Title = Lang.T("選擇要當桌布的影片", "Choose a video for your wallpaper");
                    dlg.Filter = Lang.T("影片檔", "Video files") + " (*.mp4;*.wmv;*.mov;*.m4v;*.avi;*.mkv;*.webm)|*.mp4;*.wmv;*.mov;*.m4v;*.avi;*.mkv;*.webm|"
                        + Lang.T("所有檔案", "All files") + " (*.*)|*.*";
                    dlg.InitialDirectory = HasVideo
                        ? Path.GetDirectoryName(settings.VideoPath)
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                    if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
                    settings.VideoPath = dlg.FileName;
                    settings.Save();
                    Rebuild();
                    RefreshTray();
                }
            }
            finally { choosing = false; }
        }

        public void Quit()
        {
            timer.Stop();
            coverageCheck.Stop();
            taskbarCheck.Stop();
            foreach (var hook in winEventHooks) Native.UnhookWinEvent(hook);
            bool legacy = host != null && !host.Raised;
            DestroyPlayers();
            if (legacy) RefreshStaticWallpaper();
            tray.Visible = false;
            tray.Dispose();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }

        // 開機自動啟動：用「工作排程器」在登入的當下直接啟動。
        // 以前用登錄檔的 Run 清單，但 Explorer 開機時會一個一個、等系統有空才啟動那份清單，常常要等好幾分鐘。
        // 查工作排程器要連線到系統服務（UI 執行緒上要等好幾毫秒），控制面板開著時每秒都會問，
        // 所以記住結果：打開面板時（fresh）、切換開關之後才重新查
        static bool? startupCache;
        public static bool IsStartupEnabled(bool fresh = false)
        {
            if (fresh || startupCache == null) startupCache = StartupTaskExists() || RunEntryExists();
            return startupCache.Value;
        }

        static bool StartupTaskExists()
        {
            try { TaskFolder().GetTask(RunName); return true; }
            catch { return false; }
        }

        static bool RunEntryExists()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunName) != null;
        }

        static void SetStartupEntry(bool enable)
        {
            bool taskCreated = false;
            try
            {
                dynamic folder = TaskFolder();
                if (enable)
                {
                    folder.RegisterTask(RunName, StartupTaskXml(), 6, null, null, 3);   // 6 = 建立或更新；3 = 以登入的使用者身分執行
                    taskCreated = true;
                }
                else if (StartupTaskExists()) folder.DeleteTask(RunName, 0);
            }
            catch { }

            // 排程建立成功就移除舊的 Run 設定；萬一建立失敗，才退回用 Run
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable && !taskCreated) key.SetValue(RunName, "\"" + WinForms.Application.ExecutablePath + "\"");
                else key.DeleteValue(RunName, false);
            }
            startupCache = null;   // 下次問的時候重新查
        }

        // 已經開啟自動啟動時：舊的 Run 設定換成排程；exe 被搬走時更新路徑；
        // 排程被別的程式停用了（例如防毒軟體檢查新版 exe 時）就重新啟用，不然開機不會自動播放
        static void RefreshStartupEntry()
        {
            if (!IsStartupEnabled()) return;
            bool upToDate = false;
            try
            {
                dynamic task = TaskFolder().GetTask(RunName);
                string xml = task.Xml;
                bool enabled = task.Enabled;
                upToDate = enabled && !RunEntryExists() && xml.Contains(System.Security.SecurityElement.Escape(WinForms.Application.ExecutablePath));
            }
            catch { }
            if (!upToDate) SetStartupEntry(true);
        }

        static dynamic TaskFolder()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            return service.GetFolder("\\");
        }

        static string StartupTaskXml()
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            string exe = System.Security.SecurityElement.Escape(WinForms.Application.ExecutablePath);
            string dir = System.Security.SecurityElement.Escape(Path.GetDirectoryName(WinForms.Application.ExecutablePath));
            return
@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>" + System.Security.SecurityElement.Escape(Lang.T("登入 Windows 後啟動動態桌布", "Starts Motion Desktop when you sign in to Windows")) + @"</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>" + user + @"</UserId></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author""><UserId>" + user + @"</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>""" + exe + @"""</Command><WorkingDirectory>" + dir + @"</WorkingDirectory></Exec>
  </Actions>
</Task>";
        }

        void RefreshTray()
        {
            string tip = Lang.AppName + " - " + (HasVideo ? Path.GetFileName(settings.VideoPath) : Lang.T("尚未選擇影片", "No video selected"));
            if (tip.Length > 63) tip = tip.Substring(0, 60) + "...";   // NotifyIcon.Text 上限 63 字
            tray.Text = tip;
        }

        // ---------- 播放視窗 ----------

        void Rebuild()
        {
            ticksSinceRebuild = 0;
            lastError = null;
            DestroyPlayers();
            screenSignature = ScreenSignature();
            if (!HasVideo) return;

            host = DesktopHost.Find();
            if (host == null) return;   // Explorer 還沒準備好，計時器稍後會重試

            foreach (var screen in WinForms.Screen.AllScreens)
            {
                try { players.Add(CreatePlayer(screen)); }
                catch (Exception ex) { ShowError(Lang.T("建立播放視窗失敗：", "Failed to create the playback window: ") + ex.Message); }
            }
            if (players.Count == 0) return;

            engineLost = false;
            enginePlaying = false;
            engine = new VideoEngine(settings.VideoPath, settings.Muted, settings.Stretch, OnEngineError, OnEngineLost);
            foreach (var p in players) engine.AddTarget(p.Video.Handle, p.Width, p.Height);
            if (lockScreen.Active) lockScreen.Attach(engine);
            FixZOrder();
            UpdateCoverage();
            ApplyPlayState();
        }

        ScreenPlayer CreatePlayer(WinForms.Screen screen)
        {
            // 螢幕座標 → 父視窗的用戶區座標（多螢幕時原點可能是負的）
            var b = screen.Bounds;
            var pts = new[] { new Native.POINT { X = b.Left, Y = b.Top }, new Native.POINT { X = b.Right, Y = b.Bottom } };
            Native.MapWindowPoints(IntPtr.Zero, host.Parent, pts, 2);
            int width = pts[1].X - pts[0].X, height = pts[1].Y - pts[0].Y;

            var container = new ContainerWindow(host.Parent, pts[0].X, pts[0].Y, width, height, host.Raised);
            var video = new VideoWindow(container.Handle, width, height);
            video.Exposed = delegate { if (engine != null) engine.RequestRedraw(); };

            return new ScreenPlayer
            {
                Screen = screen,
                Monitor = Native.MonitorFromPoint(new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, Native.MONITOR_DEFAULTTONEAREST),
                Container = container,
                Video = video,
                Width = width,
                Height = height,
                Hwnd = container.Handle,
            };
        }

        void DestroyPlayers()
        {
            // 先停掉引擎（放開 Direct3D 資源），再關視窗
            if (lockScreen.Active) lockScreen.Attach(null);   // 鎖定畫面先跟舊引擎脫離，新引擎建好再接回去
            if (engine != null) { engine.Dispose(); engine = null; }
            foreach (var p in players)
            {
                try { p.Video.DestroyHandle(); } catch { }
                try { p.Container.DestroyHandle(); } catch { }
            }
            players.Clear();
        }

        void OnEngineError(string message)
        {
            lastError = message;
            ShowError(message);
            if (panel.IsOpen) panel.Refresh();
        }

        // 顯示卡驅動重設之類的情況：交給每秒一次的檢查重建整個播放引擎
        void OnEngineLost()
        {
            engineLost = true;
        }

        // 24H2+：確保順序（上到下）是 圖示層 → 影片 → 原本的桌布 WorkerW
        void FixZOrder()
        {
            if (host == null || !host.Raised || players.Count == 0) return;
            IntPtr shell = Native.FindWindowEx(host.Parent, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr worker = Native.FindWindowEx(host.Parent, IntPtr.Zero, "WorkerW", null);

            int shellIdx = -1, workerIdx = int.MaxValue, i = 0;
            var order = new Dictionary<IntPtr, int>();
            for (IntPtr c = Native.GetWindow(host.Parent, Native.GW_CHILD); c != IntPtr.Zero; c = Native.GetWindow(c, Native.GW_HWNDNEXT), i++)
            {
                if (c == shell) shellIdx = i;
                else if (c == worker) workerIdx = i;
                else order[c] = i;
            }

            bool ok = true;
            foreach (var p in players)
            {
                int idx;
                if (!order.TryGetValue(p.Hwnd, out idx) || idx < shellIdx || idx > workerIdx) ok = false;
            }
            if (ok) return;

            const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
            foreach (var p in players)
                Native.SetWindowPos(p.Hwnd, shell != IntPtr.Zero ? shell : Native.HWND_TOP, 0, 0, 0, 0, flags);
            if (worker != IntPtr.Zero)
                Native.SetWindowPos(worker, Native.HWND_BOTTOM, 0, 0, 0, 0, flags);
        }

        // 有視窗最大化、或前景程式全螢幕時，那個螢幕的影片就看不到了，順便暫停省 GPU

        void UpdateCoverage()
        {
            foreach (var p in players) p.Covered = false;
            if (!settings.AutoPause || players.Count == 0) return;

            IntPtr foreground = Native.GetForegroundWindow();
            Native.EnumWindows(delegate(IntPtr h, IntPtr lParam)
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                bool zoomed = Native.IsZoomed(h);
                if (!zoomed && h != foreground) return true;
                if (Native.IsCloaked(h)) return true;   // 其他虛擬桌面上的視窗
                string cls = Native.ClassOf(h);
                if (cls == "Progman" || cls == "WorkerW" || cls.StartsWith("Shell_")) return true;

                Native.RECT r;
                Native.GetWindowRect(h, out r);
                IntPtr monitor = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
                foreach (var p in players)
                {
                    if (p.Monitor != monitor) continue;
                    var b = p.Screen.Bounds;
                    bool fullscreen = r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
                    if (zoomed || fullscreen) p.Covered = true;
                }
                return true;
            }, IntPtr.Zero);
        }

        // 請 Windows 在這些情況通知我們：切換前景視窗、最小化 / 還原、視窗顯示 / 隱藏（開啟或關閉）、
        // 視窗位置大小改變（最大化、還原、進出全螢幕）、切換虛擬桌面
        void HookWindowEvents()
        {
            uint[,] ranges = {
                { 0x0003, 0x0003 },   // EVENT_SYSTEM_FOREGROUND
                { 0x0016, 0x0017 },   // EVENT_SYSTEM_MINIMIZESTART ~ MINIMIZEEND
                { 0x8002, 0x8003 },   // EVENT_OBJECT_SHOW ~ HIDE
                { 0x800B, 0x800B },   // EVENT_OBJECT_LOCATIONCHANGE
                { 0x8017, 0x8018 },   // EVENT_OBJECT_CLOAKED ~ UNCLOAKED
            };
            for (int i = 0; i < ranges.GetLength(0); i++)
            {
                IntPtr hook = Native.SetWinEventHook(ranges[i, 0], ranges[i, 1], IntPtr.Zero, winEventProc, 0, 0,
                    Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
                if (hook != IntPtr.Zero) winEventHooks.Add(hook);
            }
        }

        void OnWinEvent(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0 || hWnd == IntPtr.Zero) return;   // 只看視窗本身，滑鼠游標、文字游標等略過
            if (eventType == 0x0003 && settings.TaskbarFix && !taskbarCheck.IsEnabled) taskbarCheck.Start();
            if (!settings.AutoPause || players.Count == 0) return;
            if (Native.GetAncestor(hWnd, Native.GA_ROOT) != hWnd) return;        // 只看頂層視窗
            if (!coverageCheck.IsEnabled) coverageCheck.Start();
        }

        enum SystemState { Locked, Suspended, DisplayOff }

        void SetSystemState(SystemState state, bool on)
        {
            bool wasPaused = SystemPaused;
            if (state == SystemState.Locked) locked = on;
            else if (state == SystemState.Suspended) suspended = on;
            else displayOff = on;
            if (on) lockScreen.CloseNow();   // 已經真正鎖定 / 睡眠 / 螢幕關閉：鎖定畫面的任務完成了
            if (SystemPaused == wasPaused) return;

            UpdateCoverage();
            ApplyPlayState();
            if (!SystemPaused)
            {
                if (engine != null) engine.RequestRedraw();
                VerifySoon();   // 回來之後確認畫面真的有在動
            }
        }

        static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");

        IntPtr PowerHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0218 && wParam.ToInt64() == 0x8013)   // WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE
            {
                // POWERBROADCAST_SETTING：GUID(16) + 資料長度(4) + 資料；0 = 螢幕關閉、1 = 開啟、2 = 變暗
                int state = Marshal.ReadInt32(lParam, 20);
                SetSystemState(SystemState.DisplayOff, state == 0);
            }
            else if (msg == 0x0312 && wParam.ToInt32() == LockHotKeyId)   // WM_HOTKEY：Win+Shift+L 打開鎖定畫面
            {
                if (!lockScreen.Active) ShowLockScreen();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // 全域快速鍵 Win+Shift+L：在任何地方都能直接打開鎖定畫面（被別的程式先登記走的話就沒有）
        const int LockHotKeyId = 0x4C53;
        public static readonly string LockHotKeyText = "Win+Shift+L";

        // 解鎖、喚醒、螢幕開啟後約 2.5 秒，直接看桌布視窗實際顯示的畫面：
        // 引擎說有輸出新畫面、畫面卻完全沒變，代表畫面沒送到桌面上 → 整個重建
        void VerifySoon()
        {
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            wait.Tick += delegate
            {
                wait.Stop();
                try { VerifyVisible(); } catch { }   // 驗證只是保險，出錯也不能讓程式當掉
            };
            wait.Start();
        }

        void VerifyVisible()
        {
            if (engine == null || !enginePlaying) return;
            var p = players.Find(x => x.Screen.Primary && !(settings.AutoPause && x.Covered))
                 ?? players.Find(x => !(settings.AutoPause && x.Covered));
            if (p == null) return;
            var checkedEngine = engine;
            int[] first = CaptureSample(p);
            int framesBefore = checkedEngine.PresentedFrames;
            var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            later.Tick += delegate
            {
                later.Stop();
                if (engine != checkedEngine || !enginePlaying || !Native.IsWindow(p.Hwnd)) return;
                int[] second;
                try { second = CaptureSample(p); } catch { return; }
                int frames = checkedEngine.PresentedFrames - framesBefore;
                long change = 0, brightness = 0;
                for (int i = 0; i < first.Length; i++) { change += Math.Abs(first[i] - second[i]); brightness += second[i]; }
                if (brightness == 0) return;   // 抓到全黑＝沒抓到內容，無法判斷，就不動作
                if (frames >= 5 && change == 0) engineLost = true;
            };
            later.Start();
        }

        static int[] CaptureSample(ScreenPlayer p)
        {
            using (var bmp = new System.Drawing.Bitmap(p.Width, p.Height))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr dc = g.GetHdc();
                    Native.PrintWindow(p.Hwnd, dc, 2);   // PW_RENDERFULLCONTENT：取得實際合成到桌面上的內容
                    g.ReleaseHdc(dc);
                }
                var sample = new int[240];
                int k = 0;
                for (int y = 0; y < 12; y++)
                    for (int x = 0; x < 20; x++)
                    {
                        var c = bmp.GetPixel(x * p.Width / 20 + p.Width / 40, y * p.Height / 12 + p.Height / 24);
                        sample[k++] = c.R + c.G + c.B;
                    }
                return sample;
            }
        }

        // 被視窗蓋住的螢幕不畫；所有螢幕都被蓋住（或手動暫停、鎖定中）才讓引擎整個暫停
        void ApplyPlayState()
        {
            if (engine == null) return;
            bool anyVisible = false;
            for (int i = 0; i < players.Count; i++)
            {
                bool visible = !(settings.AutoPause && players[i].Covered);
                engine.SetTargetVisible(i, visible);
                anyVisible |= visible;
            }
            bool play = !SystemPaused && (lockScreen.Active || (!userPaused && anyVisible));
            if (play == enginePlaying) return;
            enginePlaying = play;
            engine.SetPlaying(play);
        }

        static string ScreenSignature()
        {
            var sb = new StringBuilder();
            foreach (var s in WinForms.Screen.AllScreens) sb.Append(s.Bounds).Append(s.Primary).Append(';');
            return sb.ToString();
        }

        void Tick()
        {
            ticksSinceRebuild++;
            if (++taskbarTicks % 2 == 0) CheckTaskbar();   // 每 2 秒看一次工作列有沒有變黑
            if (HasVideo)
            {
                // 監控：引擎應該在播，卻超過 5 秒都沒成功輸出畫面 → 重建
                if (engine != null && enginePlaying && engine.MillisecondsSinceHealthy > 5000) engineLost = true;

                // Explorer 重啟、螢幕解析度或數量改變、引擎失效時重新建立
                bool broken = host == null || !Native.IsWindow(host.Parent) || players.Count == 0 || engineLost
                    || players.Exists(p => !Native.IsWindow(p.Hwnd))
                    || ScreenSignature() != screenSignature;
                if (broken)
                {
                    if (ticksSinceRebuild >= 3)
                    {
                        Rebuild();
                    }
                    return;
                }
            }
            FixZOrder();
            UpdateCoverage();
            ApplyPlayState();
            if (panel.IsOpen) panel.Refresh();
        }

        void ShowError(string message)
        {
            tray.ShowBalloonTip(8000, Lang.AppName, message, WinForms.ToolTipIcon.Warning);
        }

        // 舊版結構下 WorkerW 可能殘留最後一格畫面，重設一次目前的桌布讓它重畫
        static void RefreshStaticWallpaper()
        {
            var path = new StringBuilder(1024);
            if (Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)path.Capacity, path, 0))
                Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, path, 0);
        }

        static System.Drawing.Icon MakeIcon()
        {
            using (var bmp = new System.Drawing.Bitmap(32, 32))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new System.Drawing.Rectangle(0, 0, 32, 32),
                    System.Drawing.Color.FromArgb(0x3B, 0x82, 0xF6), System.Drawing.Color.FromArgb(0x8B, 0x5C, 0xF6), 45f))
                {
                    const float x = 1, y = 4, w = 30, h = 24, r = 10;
                    path.AddArc(x, y, r, r, 180, 90);
                    path.AddArc(x + w - r, y, r, r, 270, 90);
                    path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
                    path.AddArc(x, y + h - r, r, r, 90, 90);
                    path.CloseFigure();
                    g.FillPath(brush, path);
                }
                g.FillPolygon(System.Drawing.Brushes.White, new[] {
                    new System.Drawing.PointF(12.5f, 10), new System.Drawing.PointF(12.5f, 22), new System.Drawing.PointF(22.5f, 16) });

                IntPtr hIcon = bmp.GetHicon();
                var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
                Native.DestroyIcon(hIcon);
                return icon;
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }   // Per-Monitor V2（manifest 已宣告，這裡是保險）

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
                ThreadPool.RegisterWaitForSingleObject(showSignal,
                    delegate { dispatcher.BeginInvoke(new Action(app.ShowPanel)); }, null, -1, false);
                Dispatcher.Run();
            }
            Environment.Exit(0);
        }
    }
}
