using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace VideoWallpaper
{
    class WallpaperApp
    {
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
            StartupEntry.Refresh();

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
            StartupEntry.Set(enable);
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
            UiTimer.After(2500, delegate
            {
                try { VerifyVisible(); } catch { }   // 驗證只是保險，出錯也不能讓程式當掉
            });
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
            UiTimer.After(400, delegate
            {
                if (engine != checkedEngine || !enginePlaying || !Native.IsWindow(p.Hwnd)) return;
                int[] second;
                try { second = CaptureSample(p); } catch { return; }
                int frames = checkedEngine.PresentedFrames - framesBefore;
                long change = 0, brightness = 0;
                for (int i = 0; i < first.Length; i++) { change += Math.Abs(first[i] - second[i]); brightness += second[i]; }
                if (brightness == 0) return;   // 抓到全黑＝沒抓到內容，無法判斷，就不動作
                if (frames >= 5 && change == 0) engineLost = true;
            });
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

        // 被視窗蓋住的螢幕不畫；鎖定畫面完全蓋住時桌面都不畫（不管有沒有開自動暫停）；
        // 所有螢幕都被蓋住（或手動暫停、鎖定中）才讓引擎整個暫停
        void ApplyPlayState()
        {
            if (engine == null) return;
            bool anyVisible = false;
            for (int i = 0; i < players.Count; i++)
            {
                bool visible = !lockScreen.Covering && !(settings.AutoPause && players[i].Covered);
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
}
