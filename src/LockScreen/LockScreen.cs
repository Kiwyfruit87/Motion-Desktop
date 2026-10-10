using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace VideoWallpaper
{
    // 仿 ChromeOS 的鎖定畫面：從上面滑下來蓋住桌面，左下角是時鐘和天氣，右下角是正在播放的歌；
    // 按空白鍵或滑鼠就往上滑走，回到桌面。卡片上的音量交給 LockVolume、歌詞交給 LockLyrics
    class LockScreen
    {
        const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        static readonly int[] HotKeys = { 0x20, 0x0D, 0x1B };   // 空白鍵、Enter、Esc（用按鍵代碼當登記編號）

        readonly List<LockWindow> windows = new List<LockWindow>();
        readonly List<KeyValuePair<Rect, string>> buttons = new List<KeyValuePair<Rect, string>>();   // 卡片上的按鈕在主螢幕上的位置
        readonly LockVolume volume;
        readonly LockLyrics lyrics;
        LockWindow primary;
        VideoEngine engine;
        LockClock.MusicCard card;    // 卡片目前的樣子和位置（音量變了只換音量那張圖）
        DispatcherTimer clockTimer;
        string shownClock, shownMusic;
        string shownSong;            // 卡片上目前顯示的是哪首歌（換歌時封面和文字要翻頁）
        double shownMicLevel = -1;   // 歌詞按鈕現在的亮度
        string shownColorKey;        // 卡片上現在是哪個顏色（跟著專輯封面）
        bool colorShown;             // 卡片的顏色已經放上去了（之後顏色變了才淡入淡出）
        bool busy;                   // 正在滑進來 / 滑出去

        public LockScreen()
        {
            volume = new LockVolume(this);
            lyrics = new LockLyrics(this);
        }

        public bool Active { get; private set; }
        // 完全蓋住所有螢幕（滑進來之後、開始滑走之前）：這段時間桌面上的影片看不到，不用畫
        public bool Covering { get; private set; }
        public Action ActiveChanged;   // 打開、收起來、開始 / 不再完全蓋住桌面時

        // 給 LockVolume / LockLyrics 用
        internal bool Busy { get { return busy; } }
        internal VideoEngine Engine { get { return engine; } }
        internal LockWindow Primary { get { return primary; } }
        internal LockClock.MusicCard Card { get { return card; } }

        public void Open(VideoEngine videoEngine)
        {
            if (Active) return;
            Active = true;
            KeepAwake(true);
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
            primary.Hover = volume.OnHover;
            primary.Drag = volume.OnDrag;
            primary.Drop = volume.OnDrop;
            primary.Wheel = volume.OnWheel;
            LockWindow.ResetCursor();
            volume.Start();
            Attach(engine);
            if (ActiveChanged != null) ActiveChanged();
            NowPlaying.SongChanged = Beat.Reset;                         // 換歌：邊框光暈也從頭重新聽
            NowPlaying.Start(delegate { if (Active) Refresh(true); });   // 開始讀正在播放的歌
            SpotifyVolume.Start();                                        // 和 Spotify 的音量
            Beat.Start();                                                 // 聽正在播的聲音，卡片的邊框跟著重拍亮

            // 趁控制面板（我們自己的視窗）還在前景，先把前景交給鎖定畫面，鍵盤輸入才會進來；
            // 視窗這時在螢幕正上方，看不到。面板失去前景後會自己收起來。
            Move(1);
            Native.SetForegroundWindow(primary.Handle);
            // 保險：直接向 Windows 登記這三個鍵，就算前景被搶走也收得到（收起時會取消）
            foreach (var hotkey in HotKeys) Native.RegisterHotKey(primary.Handle, hotkey, 0x4000 /* MOD_NOREPEAT */, (uint)hotkey);

            Weather.Refresh(delegate { if (Active) Refresh(true); });
            clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clockTimer.Tick += delegate
            {
                Refresh(false);
                Weather.Refresh(delegate { if (Active) Refresh(true); });
                // 滑鼠停 2.5 秒就把游標藏起來
                if (LockWindow.CursorShown && (DateTime.Now - LockWindow.LastMove).TotalSeconds > 2.5)
                {
                    LockWindow.CursorShown = false;
                    Native.SetCursor(IntPtr.Zero);
                }
            };
            clockTimer.Start();

            // 稍等影片第一格畫好，再從上面滑下來
            UiTimer.After(150, delegate
            {
                if (!Active) return;
                foreach (var w in windows) w.PaintBlack = false;
                Animate(1, 0, 500, delegate
                {
                    if (!Active) return;
                    Native.SetForegroundWindow(primary.Handle);   // 讓鍵盤輸入進到鎖定畫面
                    busy = false;
                    SetCovering(true);
                });
            });
        }

        // 播放引擎重建時（例如顯示卡重設），把鎖定畫面重新接到新的引擎上
        public void Attach(VideoEngine videoEngine)
        {
            engine = videoEngine;
            if (engine == null) return;
            engine.PulseLevel = Beat.Glow;   // 卡片邊框的光暈跟著抓到的重拍亮
            foreach (var w in windows) engine.AddTarget(w.Handle, w.Bounds.Width, w.Bounds.Height, w == primary);
            lyrics.Detach();      // 新引擎上還沒有歌詞的圖，要重新放上去
            colorShown = false;   // 卡片的顏色也是
            Refresh(true);
            engine.RequestRedraw();
        }

        // 更新畫面上的東西（時鐘、音樂卡片、歌詞）：有變的才重畫；force = true 全部重畫
        void Refresh(bool force)
        {
            if (!Active || engine == null) return;
            ShowClock(force);
            var track = NowPlaying.Current;
            double micLevel = lyrics.Load(track, delegate { if (Active) Refresh(true); });   // 第一次會在背景查歌詞，查完再整個重畫
            double level, spotifyVolume;
            volume.Read(track, out spotifyVolume, out level);
            ShowMusic(track, force, micLevel, spotifyVolume, level);
            volume.CollapseIfLost();
            lyrics.Update();
        }

        // 左下角的時鐘和天氣：時間或天氣變了才重畫
        void ShowClock(bool force)
        {
            var now = DateTime.Now;
            string clockKey = LockClock.ClockKey(now);
            if (!force && clockKey == shownClock) return;
            shownClock = clockKey;
            int w, h, x, y;
            var clock = LockClock.RenderClock(now, primary.Bounds.Height, out w, out h, out x, out y);
            engine.SetOverlay(primary.Handle, "clock", clock, w, h, x, y);
        }

        // 右下角的音樂卡片：換歌、播放或暫停、封面變了才整張重畫；沒在播就拿掉。
        // micLevel：歌詞按鈕的亮度；spotifyVolume / volumeLevel：Spotify 的音量、喇叭和滑桿的亮度
        void ShowMusic(NowPlaying.Track track, bool force, double micLevel, double spotifyVolume, double volumeLevel)
        {
            string musicKey = track == null ? "" : track.Key;   // 跟 Refresh 讀到的是同一首（背景隨時可能換歌）
            if (!force && musicKey == shownMusic)
            {
                // 只有歌詞按鈕的狀態變了（開關歌詞、查到歌詞）：不用重畫卡片，亮度花 0.3 秒慢慢變
                if (micLevel != shownMicLevel)
                {
                    engine.FadeOverlay(primary.Handle, "musicMic", micLevel, 300, false);
                    shownMicLevel = micLevel;
                }
                volume.Refresh(spotifyVolume, volumeLevel);
                return;
            }

            shownMusic = musicKey;
            var found = new List<KeyValuePair<Rect, string>>();
            card = LockClock.RenderMusic(track, primary.Bounds.Width, primary.Bounds.Height, spotifyVolume, found);
            var c = card ?? new LockClock.MusicCard();   // 沒在播：每張圖都是 null，拿掉
            if (card == null) volume.CardRemoved();
            // 卡片底下是毛玻璃（範圍是卡片本身，不含四周留給陰影的空間）
            engine.SetGlassOverlay(primary.Handle, "music", c.Chrome, c.Width, c.Height, c.X, c.Y,
                c.InnerX, c.InnerY, c.InnerW, c.InnerH, LockClock.CardRadius(primary.Bounds.Height / 1080.0));
            ShowCardColor(c);

            // 封面、歌名、歌手：換歌時像翻頁一樣，舊的往左滑出、新的從右邊滑進來，兩張一起移動、間距不變，
            // 移動距離剛好一張卡片寬，舊的完全離開卡片時新的剛好就定位（按「上一首」換回來的話方向相反）
            string song = track == null ? null : Lyrics.SongKey(track);
            int shift = 0;
            if (card != null && shownSong != null && song != shownSong)
            {
                bool back = NowPlaying.LastCommand == "prev" && Environment.TickCount - NowPlaying.LastCommandTick < 5000;
                shift = (back ? -1 : 1) * c.InnerW;
            }
            SetCardLayer("musicInfo", c.Info, shift, 1, 0);
            SetCardLayer("musicCover", c.Cover, shift, 1, 0);   // 封面另外一張（沒有陰影），跟著一起翻頁
            // 歌詞按鈕、音量跟著一起滑；亮度變了的話慢慢變亮或變暗
            SetCardLayer("musicMic", c.Mic, shift, micLevel, 300);
            volume.ShowOnCard(c, shift, spotifyVolume, volumeLevel);
            shownMicLevel = micLevel;
            shownSong = card == null ? null : song;
            if (card != null) lyrics.Anchor(c.InnerY, c.InnerX);
            buttons.Clear();
            buttons.AddRange(found);
        }

        // 毛玻璃上的顏色（跟著專輯封面、慢慢飄動）、帶顏色的邊框和重拍時的光暈：換歌顏色變了就慢慢換過去（跟翻頁一樣 0.45 秒），
        // 卡片剛出現或拿掉時直接換
        void ShowCardColor(LockClock.MusicCard c)
        {
            if (card != null && c.ColorKey == shownColorKey && colorShown) return;
            int fade = card != null && colorShown ? 450 : 0;
            var color = c.Color;
            if (color == null)
            {
                engine.SetFadeOverlay(primary.Handle, "musicColor", null, 0, 0, 0, 0, OverlayLayer.CardColor, 0);
                engine.SetFadeOverlay(primary.Handle, "musicColorEdge", null, 0, 0, 0, 0, OverlayLayer.CardEdge, 0);
                engine.SetFadeOverlay(primary.Handle, "musicColorGlow", null, 0, 0, 0, 0, OverlayLayer.CardEdge, 0);
            }
            else
            {
                engine.SetDriftOverlay(primary.Handle, "musicColor", color.Texture, color.TextureWidth, color.TextureHeight,
                    c.InnerX - color.MarginX, c.InnerY - color.MarginY, OverlayLayer.CardColor,
                    c.InnerX, c.InnerY, c.InnerW, c.InnerH,
                    LockClock.CardRadius(primary.Bounds.Height / 1080.0), color.MarginX * 0.9, color.MarginY * 0.9, fade);
                engine.SetFadeOverlay(primary.Handle, "musicColorEdge", color.Edge, c.Width, c.Height, c.X, c.Y, OverlayLayer.CardEdge, fade);
                engine.SetFadeOverlay(primary.Handle, "musicColorGlow", color.Glow, c.Width, c.Height, c.X, c.Y, OverlayLayer.CardEdge, fade, true);   // 重拍時邊框的光暈
            }
            shownColorKey = c.ColorKey;
            colorShown = card != null;
        }

        // 卡片上跟著換歌一起滑動的圖（只在卡片範圍內看得到）；pixels 是 null 就拿掉。
        // revealW > 0：可以展開 / 收起的圖（音量滑桿），reveal 是新放上去時展開的程度
        internal void SetCardLayer(string name, int[] pixels, int shift, double opacity, int fadeMs,
            int revealX = 0, int revealW = 0, double reveal = 1, double pivot = 0, double pivotW = 0)
        {
            var c = card ?? new LockClock.MusicCard();
            engine.SetSlideOverlay(primary.Handle, name, pixels, c.Width, c.Height, c.X, c.Y, OverlayLayer.CardContent,
                c.InnerX, c.InnerY, c.InnerW, c.InnerH, shift, 450, opacity, fadeMs, revealX, revealW, reveal, pivot, pivotW);
        }

        internal bool ButtonAt(string action, int x, int y)
        {
            foreach (var b in buttons) if (b.Value == action && b.Key.Contains(x, y)) return true;
            return false;
        }

        // 點到「正在播放」的按鈕：控制播放器、切換歌詞或調音量，鎖定畫面不滑走（變暗的按鈕按了也不會滑走）
        bool OnClick(int x, int y)
        {
            if (busy) return false;
            foreach (var b in buttons)
                if (b.Key.Contains(x, y))
                {
                    if (b.Value == "lyrics")
                    {
                        if (lyrics.Toggle()) Refresh(false);   // 歌詞先開始淡入 / 淡出，再更新按鈕的樣子
                    }
                    else if (b.Value == "mute") volume.OnMuteClicked();
                    else if (b.Value == "volume")
                    {
                        if (!volume.OnBarClicked(x)) continue;   // 滑桿收起來時這裡是空的，當作沒點到按鈕
                    }
                    else NowPlaying.Command(b.Value);
                    return true;
                }
            return false;
        }

        // 空白鍵 / 滑鼠：往上滑走，回到桌面
        void OnDismiss()
        {
            if (!Active || busy) return;
            busy = true;
            SetCovering(false);             // 桌面要露出來了，桌面上的影片先接著畫
            Animate(0, 1, 420, CloseNow);   // 往上滑走，滑完再收起來
        }

        // 不管是不是前景視窗（Windows 偶爾不讓我們拿前景）、有沒有開自動暫停，都靠這個知道桌面被蓋住
        void SetCovering(bool value)
        {
            if (Covering == value) return;
            Covering = value;
            if (ActiveChanged != null) ActiveChanged();
        }

        // 切到別的程式（Alt+Tab、Win 鍵）、Alt+F4：一樣滑走
        void OnLeave(int activatedThread)
        {
            // 剛送了音量指令給 Spotify、切過去的又正好是 Spotify：是它自己跳到前景（從外面設定它的音量時會這樣），不是使用者切走。
            // 鎖定畫面本來就蓋在最上層，不收起來，稍等一下把前景拿回來（拿不回來也沒關係，空白鍵 / Enter / Esc 有另外登記）。
            // 使用者自己按 Win 鍵、Alt+Tab 切到別的程式時照樣滑走
            if (Active && volume.RecentlyCommanded && IsSpotifyThread(activatedThread))
            {
                UiTimer.After(150, TakeForeground);
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
            KeepAwake(false);
            Covering = false;
            busy = false;
            if (clockTimer != null) clockTimer.Stop();
            lyrics.Close();
            shownSong = null;   // 下次打開時卡片直接出現，不播換歌動畫
            shownMicLevel = -1;
            card = null;
            colorShown = false;
            shownColorKey = null;
            volume.Stop();
            NowPlaying.Stop();
            SpotifyVolume.Stop();
            Beat.Stop();
            buttons.Clear();
            if (primary != null) foreach (var hotkey in HotKeys) Native.UnregisterHotKey(primary.Handle, hotkey);   // 還給其他程式
            primary = null;
            var all = new List<LockWindow>(windows);
            windows.Clear();
            foreach (var w in all) Native.ShowWindow(w.Handle, 0);
            if (engine != null)
            {
                engine.PulseLevel = null;   // 光暈跟著鎖定畫面一起拿掉，引擎不用再看它的亮度
                // 等引擎放開這些視窗的輸出之後才刪掉視窗
                foreach (var w in all) engine.RemoveTarget(w.Handle);
                engine.WhenIdle(delegate { foreach (var w in all) w.DestroyHandle(); });
            }
            else foreach (var w in all) w.DestroyHandle();
            engine = null;
            if (ActiveChanged != null) ActiveChanged();
        }

        // 鎖定畫面開著時：電腦不會因為閒置而關螢幕或睡眠（跟播放影片時一樣）；收起來就恢復原本的電源設定。
        // 手動按 Win+L、闔上螢幕、按電源鍵照樣會鎖定 / 睡眠（那時鎖定畫面會直接收掉）
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

        static void KeepAwake(bool on)
        {
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
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
                    Move(from + (to - from) * Easing.StandardCurve(t));
                    if (t >= 1) break;
                    vblank.Wait();
                }
            }
            done();
        }
    }
}
