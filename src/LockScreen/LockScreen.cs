using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace VideoWallpaper
{
    // 仿 ChromeOS 的鎖定畫面：從上面滑下來蓋住桌面，左下角是時鐘和天氣，右下角是正在播放的歌（液態玻璃的卡片）；
    // 按空白鍵或滑鼠就往上滑走，回到桌面。卡片上的進度條交給 LockProgress、歌詞交給 LockLyrics
    partial class LockScreen
    {
        const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        static readonly int[] HotKeys = { 0x20, 0x0D, 0x1B };   // 空白鍵、Enter、Esc（用按鍵代碼當登記編號）

        readonly List<LockWindow> windows = new List<LockWindow>();
        readonly List<KeyValuePair<Rect, string>> buttons = new List<KeyValuePair<Rect, string>>();   // 卡片上的按鈕在主螢幕上的位置
        readonly LockProgress progress;
        readonly LockLyrics lyrics;
        LockWindow primary;
        VideoEngine engine;
        LockClock.MusicCard card;    // 卡片目前的樣子和位置
        DispatcherTimer clockTimer;
        string shownClock, shownMusic;
        LockClock.ClockImage lastClock;   // 上次畫的時鐘（換分鐘的動畫從它開始；收起來時清掉，下次打開不播動畫）
        string shownSong;            // 卡片上目前顯示的是哪首歌（換歌時封面和文字要翻頁）
        double shownMicLevel = -1;   // 歌詞按鈕現在的亮度
        bool shownPlaying;           // 播放鍵現在畫的是暫停（播放中）嗎
        bool? shownShuffle;          // 隨機播放、循環現在畫的樣子
        int? shownRepeat;
        bool? expectShuffle;         // 剛按了隨機播放 / 循環：播放器回報之前，照按下去的樣子顯示（最多 2 秒）
        int? expectRepeat;
        int expectModeTick;
        bool? expectPlaying;         // 剛按了播放暫停：播放器回報之前，照按下去的樣子顯示（最多 2 秒）
        int expectTick;
        bool touching;               // 手指正按在卡片上（白光從那裡擴散）
        int pressed = -1;            // 正按著的玻璃按鈕（歌詞圓鈕；-1 沒有）
        DispatcherTimer pressWatch;  // 按著時每 0.1 秒看左鍵還有沒有按著（放開的訊息被別的視窗收走時）
        string shownColorKey;        // 卡片上現在是哪個顏色（跟著專輯封面）
        bool colorShown;             // 卡片的顏色已經放上去了（之後顏色變了才淡入淡出）
        bool busy;                   // 正在滑進來 / 滑出去
        int openCount;               // 第幾次打開（開場動畫的計時器認得是不是同一次）

        public LockScreen()
        {
            progress = new LockProgress(this);
            lyrics = new LockLyrics(this);
        }

        public bool Active { get; private set; }
        // 完全蓋住所有螢幕（滑進來之後、開始滑走之前）：這段時間桌面上的影片看不到，不用畫
        public bool Covering { get; private set; }
        public Action ActiveChanged;   // 打開、收起來、開始 / 不再完全蓋住桌面時

        // 卡片跟著重拍發光（控制面板的開關）：關掉就完全不聽電腦的聲音，光暈也不亮
        bool beatGlow = true;
        public bool BeatGlow
        {
            get { return beatGlow; }
            set
            {
                if (beatGlow == value) return;
                beatGlow = value;
                if (!Active) return;
                if (value) Beat.Start(); else Beat.Stop();
                if (engine != null)
                {
                    engine.PulseLevel = value ? Beat.Glow : (Func<long, double>)null;
                    engine.RequestRedraw();   // 關掉時正亮著的光暈馬上拿掉
                }
            }
        }

        // 給 LockProgress / LockLyrics 用
        internal bool Busy { get { return busy; } }
        internal VideoEngine Engine { get { return engine; } }
        internal LockWindow Primary { get { return primary; } }
        internal LockClock.MusicCard Card { get { return card; } }

        public void Open(VideoEngine videoEngine)
        {
            if (Active) return;
            Active = true;
            int opening = ++openCount;
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
            primary.Drag = delegate(int x, int y)
            {
                if (touching && engine != null) engine.TouchLiquid(primary.Handle, x, y, true);   // 白光跟著手指（顯示卡重設的空檔沒有引擎）
                if (progress.Scrubbing) progress.OnDrag(x);
            };
            primary.Drop = delegate { progress.OnDrop(); Release(); };
            LockWindow.ResetCursor();
            progress.Start();
            pressWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            pressWatch.Tick += delegate { if (!touching) pressWatch.Stop(); else if (!LockWindow.LeftButtonDown()) Release(); };
            Attach(engine);
            if (ActiveChanged != null) ActiveChanged();
            NowPlaying.SongChanged = Beat.Reset;                         // 換歌：邊框光暈也從頭重新聽
            NowPlaying.Start(delegate { if (Active) Refresh(false); });  // 開始讀正在播放的歌（變了的才重畫：只是暫停就只換播放鍵）
            if (beatGlow) Beat.Start();                                   // 聽正在播的聲音，卡片的邊框跟著重拍亮

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
                // 滑鼠停 2.5 秒就把游標藏起來（停在音樂卡片上時不藏：可能正要按按鈕）
                if (LockWindow.CursorShown && (DateTime.Now - LockWindow.LastMove).TotalSeconds > 2.5 && !CursorOnCard())
                {
                    LockWindow.CursorShown = false;
                    Native.SetCursor(IntPtr.Zero);
                }
            };
            clockTimer.Start();

            // 稍等影片第一格畫好，再從上面滑下來
            UiTimer.After(150, delegate
            {
                if (!Active || opening != openCount) return;   // 這 150 毫秒內被關掉又重開過：交給新的那次
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
            engine.PulseLevel = beatGlow ? Beat.Glow : (Func<long, double>)null;   // 卡片邊框的光暈跟著抓到的重拍亮
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
            ShowMusic(track, force, micLevel);
            lyrics.Update();
        }

        // 左下角的時鐘和天氣：時間或天氣變了才重畫
        void ShowClock(bool force)
        {
            var now = DateTime.Now;
            string clockKey = LockClock.ClockKey(now);
            if (!force && clockKey == shownClock) return;
            shownClock = clockKey;
            var clock = LockClock.RenderClock(now, primary.Bounds.Width, primary.Bounds.Height);
            // 時間的數字：引擎畫得出液態玻璃就畫玻璃，畫不了才用實心字；換分鐘時變了的字有動畫（剛打開時沒有）
            engine.SetClock(primary.Handle, clock.Solid, clock.Field, clock.Width, clock.Height, clock.X, clock.Y, clock.Scale,
                LockClock.ClockSlots(lastClock, clock), clock.TimeTop, clock.DigitCenterY, clock.DigitHeight);
            lastClock = clock;
        }

        // 右下角的音樂卡片：換歌、播放或暫停、封面變了才整張重畫；沒在播就拿掉。
        // micLevel：歌詞按鈕的亮度
        void ShowMusic(NowPlaying.Track track, bool force, double micLevel)
        {
            string musicKey = track == null ? "" : track.CardKey;   // 跟 Refresh 讀到的是同一首（背景隨時可能換歌）；不含播放 / 暫停
            bool playing = Playing(track);
            if (!force && musicKey == shownMusic)
            {
                // 只是播放 / 暫停變了：只換播放鍵那張圖（很快）
                bool? shuffle; int? repeat;
                Modes(track, out shuffle, out repeat);
                if (card != null && (playing != shownPlaying || shuffle != shownShuffle || repeat != shownRepeat)) ShowButtons(playing, shuffle, repeat);
                // 只有歌詞按鈕的狀態變了（開關歌詞、查到歌詞）：不用重畫卡片，花 0.3 秒慢慢變
                if (micLevel != shownMicLevel) ShowMic(null, micLevel);
                progress.Update(track, playing, false);
                return;
            }

            shownMusic = musicKey;
            shownPlaying = playing;
            Modes(track, out shownShuffle, out shownRepeat);   // 剛按了隨機 / 循環的 2 秒內，照按下去的樣子畫（不會跳回舊的）
            var found = new List<KeyValuePair<Rect, string>>();
            card = LockClock.RenderMusic(track, playing, shownShuffle, shownRepeat, primary.Bounds.Width, primary.Bounds.Height, found);
            var c = card ?? new LockClock.MusicCard();   // 沒在播：每張圖都是 null，拿掉
            // 卡片本身是播放引擎畫的液態玻璃；舊顯示卡畫不了的話，退回毛玻璃（底下的影片糊掉，再貼這張霧白和邊框）
            engine.SetLiquidCard(primary.Handle, c.Spec);
            engine.SetGlassOverlay(primary.Handle, "music", c.Chrome, c.Width, c.Height, c.X, c.Y, c.InnerX, c.InnerY, c.InnerW, c.InnerH, c.Radius);
            ShowCardColor(c);

            // 換歌：只有內容（封面、歌名、歌手）在動——舊的往左滑一小段淡出、新的從右邊滑進來淡入（按「上一首」換回來的話方向相反）；
            // 按鈕、歌詞圓鈕、進度條和時間都不動
            string song = track == null ? null : Lyrics.SongKey(track);
            int shift = 0;
            if (card != null && shownSong != null && song != shownSong)
            {
                bool back = NowPlaying.LastCommand == "prev" && Environment.TickCount - NowPlaying.LastCommandTick < 5000;
                shift = (back ? -1 : 1) * (int)Math.Round(c.InnerW * 0.15);
            }
            SetCardLayer("musicInfo", c.Info, shift, 1, 0);
            SetCardLayer("musicCover", c.Cover, shift, 1, 0);
            SetCardLayer("musicButtons", c.Buttons, 0, 1, 0);
            ShowMic(c, micLevel);
            progress.Update(track, playing, true);
            shownSong = card == null ? null : song;
            if (card != null) lyrics.Anchor(c.InnerY, c.InnerX);
            buttons.Clear();
            buttons.AddRange(found);
        }

        // 歌詞按鈕：沒開時白色圖示（有歌詞半亮、沒有歌詞很暗），開著時圓鈕的底變白、圖示變深色。
        // c 不是 null：卡片重畫了，兩張圖放上去；c 是 null：只是狀態變了，0.3 秒慢慢換過去
        void ShowMic(LockClock.MusicCard c, double micLevel)
        {
            bool on = micLevel >= 1;
            double off = on ? 0 : micLevel;
            if (c != null)
            {
                SetCardLayer("musicMic", c.Mic, 0, off, 300);
                SetCardLayer("musicMicOn", c.MicOn, 0, on ? 1 : 0, 300);
            }
            else
            {
                engine.FadeOverlay(primary.Handle, "musicMic", off, 300, false);
                engine.FadeOverlay(primary.Handle, "musicMicOn", on ? 1 : 0, 300, false);
            }
            shownMicLevel = micLevel;
            SendLyrics();
        }

        void SendLyrics()
        {
            if (engine != null && primary != null && card != null)
                engine.SetLiquidLyrics(primary.Handle, shownMicLevel < 0 ? 0.6 : shownMicLevel, shownMicLevel >= 1);
        }

        // 播放鍵要畫成播放中嗎：剛按了播放暫停的 2 秒內，播放器還沒回報新狀態就照按下去的樣子
        bool Playing(NowPlaying.Track track)
        {
            if (track == null) return false;
            if (expectPlaying.HasValue)
            {
                if (track.Playing != expectPlaying.Value && Environment.TickCount - expectTick < 2000) return expectPlaying.Value;
                expectPlaying = null;   // 回報了（或等太久）：照播放器的
            }
            return track.Playing;
        }

        // 隨機播放、循環要畫成什麼樣子：剛按了的 2 秒內，播放器還沒回報就照按下去的樣子
        void Modes(NowPlaying.Track track, out bool? shuffle, out int? repeat)
        {
            shuffle = track == null ? null : track.Shuffle;
            repeat = track == null ? null : track.Repeat;
            if (Environment.TickCount - expectModeTick >= 2000) { expectShuffle = null; expectRepeat = null; }
            if (expectShuffle.HasValue && shuffle.HasValue) { if (shuffle == expectShuffle) expectShuffle = null; else shuffle = expectShuffle; }
            if (expectRepeat.HasValue && repeat.HasValue) { if (repeat == expectRepeat) expectRepeat = null; else repeat = expectRepeat; }
        }

        void ShowButtons(bool playing, bool? shuffle, int? repeat)
        {
            shownPlaying = playing; shownShuffle = shuffle; shownRepeat = repeat;
            SetCardLayer("musicButtons", LockClock.RenderButtons(card, playing, shuffle, repeat), 0, 1, 0);
        }

        void ShowPlaying(bool playing) { ShowButtons(playing, shownShuffle, shownRepeat); }

        // 按隨機播放 / 循環：圖示馬上換，不等播放器回報。循環照 Apple 的順序：關 → 全部 → 單曲 → 關
        void ToggleMode(string which)
        {
            if (card == null || !(which == "shuffle" ? shownShuffle.HasValue : shownRepeat.HasValue)) return;   // 播放器不支援
            expectModeTick = Environment.TickCount;
            if (which == "shuffle")
            {
                expectShuffle = !(shownShuffle == true);
                NowPlaying.SetShuffle(expectShuffle.Value);
                ShowButtons(shownPlaying, expectShuffle, shownRepeat);
            }
            else
            {
                int now = shownRepeat ?? 0;
                expectRepeat = now == 0 ? 2 : now == 2 ? 1 : 0;
                NowPlaying.SetRepeat(expectRepeat.Value);
                ShowButtons(shownPlaying, shownShuffle, expectRepeat);
            }
        }

        // 按播放暫停：圖示馬上換、進度條馬上停住或接著走，不等播放器回報（它要過大半秒才會回報）
        void TogglePlaying()
        {
            var track = NowPlaying.Current;
            if (track == null || card == null) return;
            bool now = !shownPlaying;
            expectPlaying = now;
            expectTick = Environment.TickCount;
            ShowPlaying(now);
            progress.Toggle(track, now);
        }

        // 按到卡片上的東西：從手指下面亮起來，白光往整張卡片擴散（跟原型一樣）；按的是歌詞圓鈕的話它也整顆亮起來、放大一點。
        // 放開才慢慢退（抓住滑鼠，放開的訊息才收得到）
        void Touch(int x, int y, int button)
        {
            Release();
            touching = true;
            pressed = button;
            engine.TouchLiquid(primary.Handle, x, y, true);
            if (button >= 0) engine.PressLiquidButton(primary.Handle, button, true);
            primary.StartDrag();
            if (pressWatch != null) pressWatch.Start();
        }

        void Release()
        {
            if (!touching) return;
            if (engine != null && primary != null)
            {
                engine.TouchLiquid(primary.Handle, 0, 0, false);
                if (pressed >= 0) engine.PressLiquidButton(primary.Handle, pressed, false);
                primary.EndDrag();
            }
            touching = false;
            pressed = -1;
            if (pressWatch != null) pressWatch.Stop();
        }

        // 重拍時邊框的光暈（跟著專輯封面的顏色）：換歌顏色變了就慢慢換過去（跟翻頁一樣 0.45 秒），卡片剛出現或拿掉時直接換
        void ShowCardColor(LockClock.MusicCard c)
        {
            if (card != null && c.ColorKey == shownColorKey && colorShown) return;
            int fade = card != null && colorShown ? 450 : 0;
            engine.SetFadeOverlay(primary.Handle, "musicColorGlow", c.Color == null ? null : c.Color.Glow, c.Width, c.Height, c.X, c.Y, OverlayLayer.CardEdge, fade, true);
            shownColorKey = c.ColorKey;
            colorShown = card != null;
        }

        // 卡片上的圖（只在卡片範圍內看得到）；shift ≠ 0 是換歌的動畫（見 SetSlideOverlay）；pixels 是 null 就拿掉
        internal void SetCardLayer(string name, int[] pixels, int shift, double opacity, int fadeMs)
        {
            var c = card ?? new LockClock.MusicCard();
            engine.SetSlideOverlay(primary.Handle, name, pixels, c.Width, c.Height, c.X, c.Y, OverlayLayer.CardContent,
                c.InnerX, c.InnerY, c.InnerW, c.InnerH, shift, 450, opacity, fadeMs);
        }

        internal bool ButtonAt(string action, int x, int y)
        {
            foreach (var b in buttons) if (b.Value == action && b.Key.Contains(x, y)) return true;
            return false;
        }

        // 游標在音樂卡片上嗎（卡片的位置以主螢幕視窗的左上角為 0）
        bool CursorOnCard()
        {
            if (card == null || primary == null) return false;
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return false;
            int x = p.X - primary.Bounds.X, y = p.Y - primary.Bounds.Y;
            return x >= card.InnerX && x < card.InnerX + card.InnerW && y >= card.InnerY && y < card.InnerY + card.InnerH;
        }

        // 點到「正在播放」的按鈕：控制播放器、切換歌詞或拖進度條，鎖定畫面不滑走（變暗的按鈕按了也不會滑走）
        bool OnClick(int x, int y)
        {
            if (busy) return false;
            if (engine == null) return true;   // 顯示卡重設的空檔（還沒接上新的引擎）：點了不理，也不滑走
            foreach (var b in buttons)
                if (b.Key.Contains(x, y))
                {
                    if (b.Value == "progress" && !progress.CanScrub)
                    {
                        if (progress.Shown) return true;   // 有進度條但播放器不接受跳轉：點了不理，也不滑走
                        continue;                          // 不知道歌曲長度時沒有進度條，當作沒點到按鈕
                    }
                    Touch(x, y, b.Value == "lyrics" ? VideoEngine.LyricsButton : -1);
                    if (b.Value == "lyrics")
                    {
                        if (lyrics.Toggle()) Refresh(false);   // 歌詞先開始淡入 / 淡出，再更新按鈕的樣子
                    }
                    else if (b.Value == "progress") progress.OnPressed(x);
                    else if (b.Value == "shuffle" || b.Value == "repeat") ToggleMode(b.Value);
                    else
                    {
                        if (b.Value == "toggle") TogglePlaying();
                        NowPlaying.Command(b.Value);
                    }
                    return true;
                }
            return false;
        }

        // 空白鍵 / 滑鼠：往上滑走，回到桌面
        void OnDismiss()
        {
            if (!Active || busy) return;
            bool blocked = false;
            BeforeDismiss(ref blocked);
            if (blocked) return;
            busy = true;
            SetCovering(false);             // 桌面要露出來了，桌面上的影片先接著畫
            Animate(0, 1, 420, CloseNow);   // 往上滑走，滑完再收起來
        }

        // 其他檔案可以決定要不要滑走、切走時怎麼處理（沒有實作就照平常）
        partial void BeforeDismiss(ref bool blocked);
        partial void BeforeLeave(int activatedThread, ref bool handled);

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
            bool handled = false;
            BeforeLeave(activatedThread, ref handled);
            if (handled) return;
            OnDismiss();
        }

        // 把前景拿回鎖定畫面。Windows 不讓背景程式直接搶前景，
        // 所以暫時把這條執行緒的輸入接到現在的前景視窗上，再設定前景（其他檔案要把前景拿回來時用）
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
            lastClock = null;
            lyrics.Close();
            shownSong = null;   // 下次打開時卡片直接出現，不播換歌動畫
            shownMicLevel = -1;
            expectPlaying = null;
            expectShuffle = null; expectRepeat = null;
            touching = false;
            pressed = -1;
            if (pressWatch != null) pressWatch.Stop();
            progress.Stop();
            card = null;
            colorShown = false;
            shownColorKey = null;
            NowPlaying.Stop();
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
            // 卡片跟著頁面一起動，但有一點慣性（開始時慢半拍、停下時往前衝一點再彈回、移動時沿方向拉長一點），由播放引擎照同一條曲線算
            if (engine != null && primary != null)
                engine.SlideCard(primary.Handle, from, to, ms, AnimationClock.Now, primary.Bounds.Height, primary.Bounds.Height / 1080.0);
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
