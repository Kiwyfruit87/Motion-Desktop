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
        static readonly int[] HotKeys = { 0x20, 0x0D, 0x1B, 0x4C, 0x46 };   // 空白鍵、Enter、Esc（滑走）、L（歌詞）、F（放大歌詞）（用按鍵代碼當登記編號）

        readonly List<LockWindow> windows = new List<LockWindow>();
        readonly List<KeyValuePair<Rect, string>> buttons = new List<KeyValuePair<Rect, string>>();   // 卡片上的按鈕在主螢幕上的位置
        readonly LockProgress progress;
        readonly LockLyrics lyrics;
        LockWindow primary;
        VideoEngine engine;
        LockClock.MusicCard card;    // 卡片目前的樣子和位置
        DispatcherTimer clockTimer;
        string shownClock, shownMusic;
        LockClock.ClockImage lastClock;   // 上次畫的時鐘（只留時間和每個字的位置；換分鐘的動畫從它開始；收起來時清掉，下次打開不播動畫）
        VideoEngine clockEngine;          // 時鐘交給了哪個播放引擎（換了引擎要重畫）
        int clockGen;                     // 背景算距離圖時又有更新的時鐘：舊的算完不用
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
        LockClock.EdgeStrip[] shownEdges;   // 螢幕四邊的光暈現在放的是哪一份（放大歌詞時才有）
        bool busy;                   // 正在滑進來 / 滑出去
        int openCount;               // 第幾次打開（開場動畫的計時器認得是不是同一次）

        public LockScreen()
        {
            progress = new LockProgress(this);
            lyrics = new LockLyrics(this);
        }

        public bool Active { get; private set; }

        // 卡片上顯示的播放位置（秒；拖進度條時是拖到的地方）、是不是正在拖：歌詞照這個捲
        public double SongPosition(NowPlaying.Track track) { return progress.ShownSeconds(track); }
        public bool Scrubbing { get { return progress.Scrubbing; } }
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

        // ---------- 放大歌詞 ----------
        // 歌詞開著時按歌詞區右上角的放大鈕：音樂卡片連同上面的歌詞整塊移到畫面正中間放大（字照放大後的大小重畫，才清楚），
        // 背景糊掉、暗一點，時鐘淡出（見 VideoEngine.Zoom.cs）。再按一次、按 Esc / 空白鍵、點卡片以外的地方、關掉歌詞就縮回來
        bool big;
        bool wantBig;   // 按了放大，放大的歌詞還在畫：畫好就放大
        bool autoBig;   // 放大中因為換到沒有歌詞的歌（廣告）自己縮回來：之後又有歌詞就自己放大回去（自己縮回、關掉歌詞就不會）

        internal bool Big { get { return big; } }
        // 歌詞長圖照多高的螢幕畫（字的大小）、多寬（版面見 LockClock.BigControls）；另一種大小的（先在背景畫好）
        internal int LyricsHeight { get { return LyricsHeightFor(big); } }
        internal int LyricsWidth { get { return LyricsWidthFor(big); } }
        internal int OtherLyricsHeight { get { return LyricsHeightFor(!big); } }
        internal int OtherLyricsWidth { get { return LyricsWidthFor(!big); } }
        int LyricsHeightFor(bool b) { return primary == null ? 0 : b ? LockClock.BigLyricsHeight(primary.Bounds.Height) : primary.Bounds.Height; }
        int LyricsWidthFor(bool b) { return primary == null || !b ? 0 : LockClock.BigLyricsWidth(primary.Bounds.Width, primary.Bounds.Height); }

        // 卡片連同往上延伸的歌詞區（玻璃整塊）在畫面上的範圍
        static double[] Block(LockClock.MusicCard c, int ext) { return new double[] { c.InnerX, c.InnerY - ext, c.InnerW, c.InnerH + ext }; }

        // 放大 / 縮回來：卡片、歌詞照新的大小重畫（重畫的那幾個指令送完前先別畫，不會有一格新舊混在一起），
        // 再讓播放引擎把新的那塊從舊的位置、大小平順地移過去，背景同時糊掉 / 變清楚
        bool switching;   // 正在照新的大小重畫（重畫的途中不要又自己縮回來）
        bool shrinking;   // 正在換大小（還沒照新的大小重畫），這段時間點了不理
        const int ZoomMs = 700;   // 卡片換大小（有彈性，約 0.7 秒停好）；背景糊掉 / 變清楚快一點
        const int BackdropMs = 450;

        // 放大 / 縮回來：玻璃從原本的範圍有彈性地變成新的範圍（圓角跟著變），裡面空著；
        // 玻璃停好之後，照新的大小重畫好的內容淡入
        string wantSong;    // 按放大時是哪首歌（放大的歌詞畫好時已經換歌了就不放大）
        int growStart;      // 上次開始放大的時間：玻璃還在長的這 0.7 秒內不能縮回去（不然玻璃會從動畫中途跳到終點再縮）

        void SetBig(bool on)
        {
            wantBig = false;
            if (on == big || switching || shrinking || !Active || engine == null || primary == null) return;
            if (!on && Environment.TickCount - growStart < ZoomMs) return;
            if (card == null)
            {
                // 沒在播了（卡片拿掉了）：沒有東西可以縮回去，背景直接變清楚
                if (on) return;
                big = false;
                Beat.Vocal(false);
                engine.SetBackdrop(primary.Handle, 0, BackdropMs);
                return;
            }
            Release();
            progress.OnDrop();   // 拖著進度條時換大小：先放開
            if (on)
            {
                if (!lyrics.On) return;
                var track = NowPlaying.Current;
                if (track == null) return;
                int bigHeight = LyricsHeightFor(true), bigWidth = LyricsWidthFor(true);
                string song = Lyrics.SongKey(track);
                var ready = Lyrics.Get(track, bigHeight, delegate
                {
                    var playing = NowPlaying.Current;
                    if (wantBig && Active && !big && lyrics.On && playing != null && Lyrics.SongKey(playing) == wantSong) SetBig(true);
                }, bigWidth);
                if (!Lyrics.SheetReady(ready, bigHeight))
                {
                    // 還在畫：畫好再放大（這首沒有歌詞、或放大的長圖畫不出來就算了）
                    wantBig = ready == null || (Lyrics.Has(ready) && ready.SheetFailedFor != bigHeight);
                    wantSong = song;
                    if (!wantBig) autoBig = false;   // 放大的長圖畫不出來：不要一直重試
                    return;
                }
                growStart = Environment.TickCount;
                autoBig = false;
                Beat.Vocal(true);   // 一按放大就開始聽人聲（要先聽 2 秒才判斷得出來）
            }
            // 按下去玻璃就開始變形（圓角跟著變），卡片上的東西很快淡掉；
            // 等它淡完（玻璃空著、正在變形）才照新的大小重畫，畫好等玻璃停好再淡入——重畫要一點時間，但不會讓按下去之後愣一下
            // 放大的版面沒有玻璃（播放控制浮在桌布上）：放大時卡片的玻璃往整個畫面化開、一邊淡掉（卡片融進場景）；
            // 縮回時從整個畫面收回卡片、一邊浮現
            double[] screen = { 0, 0, primary.Bounds.Width, primary.Bounds.Height };
            double screenRadius = 60 * primary.Bounds.Height / 1080.0;
            var from = on ? Block(card, lyrics.Extension) : screen;
            var to = on ? screen : TargetBlock(false, lyrics.On);
            engine.MorphCard(primary.Handle, from[0], from[1], from[2], from[3], to[0], to[1], to[2], to[3],
                on ? card.Radius : screenRadius, on ? screenRadius : TargetRadius(false), on ? 1 : 0, on ? 0 : 1, ZoomMs);
            engine.SetBackdrop(primary.Handle, on ? 1 : 0, BackdropMs);
            shrinking = true;
            int opened = openCount;
            // 放大：等玻璃撐滿整個畫面、完全淡掉才換圖（放上大張的歌詞約 50 毫秒，玻璃還在動的時候換會頓一下）；縮回：卡片上的東西淡完就換
            UiTimer.After(on ? 600 : SwapMs, delegate
            {
                shrinking = false;
                if (!Active || opened != openCount || engine == null || primary == null) return;
                engine.SetStage(primary.Handle, on);   // 放大：歌詞不在玻璃裡、播放控制會自動淡出
                Redraw(on);
                // 放大的版面沒有玻璃，不用等玻璃停好：早一點淡入；縮回來等卡片的玻璃大致停好再淡入
                engine.MorphSwap(primary.Handle, on ? 620 : 380, 220);
                if (!on) engine.SetBackdrop(primary.Handle, 0, BackdropMs);   // 縮回途中換了引擎（新引擎背景還是糊的）：一起變清楚
                if (on) ShowControls(true);
            });
        }

        // 換到 b 這種版面時，玻璃整塊的範圍（一般：卡片連往上延伸的歌詞區；放大：底部的膠囊）、圓角（跟重畫之後的一樣）
        double[] TargetBlock(bool b, bool withLyrics)
        {
            int x, y, w, h;
            LockClock.CardPlace(primary.Bounds.Width, primary.Bounds.Height, b, out x, out y, out w, out h);
            double s0 = primary.Bounds.Height / 1080.0;
            int ext = withLyrics && !b ? LockClock.LyricsExtension(s0, s0) : 0;
            return new double[] { x, y - ext, w, h + ext };
        }

        double TargetRadius(bool b)
        {
            int x, y, w, h;
            LockClock.CardPlace(primary.Bounds.Width, primary.Bounds.Height, b, out x, out y, out w, out h);
            return b ? h / 2.0 : LockClock.CardRadius(primary.Bounds.Height / 1080.0);   // 膠囊：圓角是高度的一半
        }

        // ---------- 放大歌詞時的播放控制：滑鼠不動約 3 秒就淡出，一動（或按鍵、點一下）又出來 ----------
        bool controlsShown = true;
        int controlsAt;   // 上次有動作的時間
        const int ControlsIdleMs = 3000;

        void ShowControls(bool show)
        {
            if (show) controlsAt = Environment.TickCount;
            if (!big || engine == null || primary == null || show == controlsShown) return;
            controlsShown = show;
            engine.SetStageControls(primary.Handle, show, show ? 200 : 450);
        }

        // 時鐘的計時器每秒看一次：放大歌詞、播放控制開著、一陣子沒動作（也沒在按、沒在拖進度條）就淡出
        void CheckControlsIdle()
        {
            if (big && controlsShown && !touching && !progress.Scrubbing && !shrinking && Environment.TickCount - controlsAt > ControlsIdleMs) ShowControls(false);
        }

        const int SwapMs = 130;          // 卡片上的東西淡完（變形開始後 15%）就重畫

        // 照新的大小重畫卡片和歌詞（卡片上的東西這時是淡掉的，見 MorphCard）
        void Redraw(bool on)
        {
            big = on;
            Beat.Vocal(on);   // 放大歌詞：聽人聲（逐字亮對準開口時間、長音結束）
            controlsShown = true;   // 換了版面：播放控制先是看得到的（見 SetStage）
            lyrics.Switch();
            colorShown = false;   // 光暈的圖也要照新的大小重畫（螢幕四邊的光不用：沒放大時 Edges 是 null，會淡出拿掉）
            switching = true;
            try { Refresh(true); }
            finally { switching = false; }
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
                w.Wheel = OnWheel;   // 滾輪可能送到任何一個螢幕的視窗（焦點在哪就給誰）：都照主螢幕的歌詞捲
                windows.Add(w);
                if (screen.Primary || primary == null) primary = w;
            }
            primary.Click = OnClick;
            primary.LyricsKey = OnLyricsKey;
            primary.ZoomKey = delegate { if (!busy && engine != null && (big || lyrics.On)) SetBig(!big); };   // F：歌詞開著時放大 / 縮回
            primary.Moved = delegate { ShowControls(true); };   // 放大歌詞時滑鼠一動：播放控制出來
            primary.Drag = delegate(int x, int y)
            {
                if (touching && engine != null) engine.TouchLiquid(primary.Handle, x, y, true);   // 白光跟著手指（顯示卡重設的空檔沒有引擎）
                if (progress.Scrubbing) progress.OnDrag(x);
                if (pageDrag != null) PageMove();
            };
            primary.Drop = delegate { progress.OnDrop(); Release(); if (pageDrag != null) PageDrop(false); };
            primary.Press = OnPress;
            pageWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            pageWatch.Tick += delegate { if (pageDrag == null) pageWatch.Stop(); else if (!LockWindow.LeftButtonDown()) PageDrop(true); };
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
                CheckControlsIdle();
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
                // 主螢幕照 iPhone 從主畫面拉下鎖定畫面：視窗不動，頁面在畫面裡滑下來（見 VideoEngine.SlideSheet）。
                // 鎖定畫面這時還在螢幕上方，直接拍就是底下的桌面
                Slide(1, 0, 500, false, delegate
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
            if (pageDrag != null) pageDrag.Pinned = null;   // 拖著頁面時換了引擎：新引擎上沒有在畫面裡滑的頁面，剩下的改成移動整個視窗
            if (engine == null) return;
            engine.PulseLevel = beatGlow ? Beat.Glow : (Func<long, double>)null;   // 卡片邊框的光暈跟著抓到的重拍亮
            foreach (var w in windows) engine.AddTarget(w.Handle, w.Bounds.Width, w.Bounds.Height, w == primary);
            lyrics.Detach();      // 新引擎上還沒有歌詞的圖，要重新放上去
            if (big && !shrinking)
            {
                // 放大歌詞中換了引擎（換桌布、顯示卡重設）：新引擎不知道現在是放大的版面，要重新告訴它——
                // 不然卡片的玻璃又畫出來、歌詞和左上角的歌名被當成在玻璃裡面而整個裁掉。背景照樣糊（正在縮回去的不用）
                engine.SetStage(primary.Handle, true);
                engine.SetBackdrop(primary.Handle, 1, 0);
                controlsShown = true;   // SetStage 之後播放控制是看得到的
                controlsAt = Environment.TickCount;
            }
            colorShown = false;   // 卡片的顏色也是
            shownEdges = null;
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
            // 放大中歌詞不見了（關掉歌詞、換到沒有歌詞的歌、沒在播了）：縮回來
            if (big && !switching && !shrinking && (card == null || !lyrics.On || !lyrics.Available))
            {
                // 換到沒有歌詞的歌（例如廣告）、沒在播了：記著，之後換到有歌詞的歌就自己再放大；自己把歌詞關掉的不算
                autoBig = lyrics.On;
                SetBig(false);
            }
            if (!lyrics.On) autoBig = false;
            else if (autoBig && !big && !switching && !shrinking && !wantBig && card != null && lyrics.CanToggle) SetBig(true);   // 廣告完了、又有歌詞：放大回去
            if (wantBig && !lyrics.On) wantBig = false;   // 等放大的歌詞畫好時把歌詞關了：不放大了
        }

        // 上方的時鐘、日期和天氣：時間、日期、天氣變了才重畫（或換了播放引擎）。歌詞查完、天氣回來的整個重整不重畫時鐘
        // （字形距離圖在 4K 上要零點幾秒，也會把正在跑的換分鐘動畫切掉）。
        // 字的形狀在畫面執行緒畫好，距離圖在背景算完再交給引擎；剛打開時直接算（時鐘才不會晚一下才出現）
        void ShowClock(bool force)
        {
            var now = DateTime.Now;
            string clockKey = LockClock.ClockKey(now);
            if (clockKey == shownClock && engine == clockEngine) return;
            shownClock = clockKey;
            clockEngine = engine;
            var clock = LockClock.RenderClock(now, primary.Bounds.Width, primary.Bounds.Height);
            int gen = ++clockGen;
            if (lastClock == null) { LockClock.ComputeField(clock); SendClock(clock); return; }
            var ui = Dispatcher.CurrentDispatcher;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { LockClock.ComputeField(clock); } catch { clock.Field = null; clock.Shape = null; }   // 算不出來：只用實心字
                ui.BeginInvoke(new Action(delegate { if (Active && gen == clockGen && engine != null) SendClock(clock); }));
            });
        }

        // 時鐘交給引擎（時間的數字：畫得出液態玻璃就畫玻璃，畫不了才用實心字）；換分鐘時變了的字有動畫，
        // 時間沒變（只有天氣、日期變）時正在跑的動畫留著
        void SendClock(LockClock.ClockImage clock)
        {
            bool sameTime = lastClock != null && lastClock.Time == clock.Time;
            engine.SetClock(primary.Handle, clock.Solid, clock.Field, clock.Width, clock.Height, clock.X, clock.Y, clock.Scale,
                LockClock.AllSlots(lastClock, clock), clock.TimeTop, clock.DigitCenterY, clock.DigitHeight, sameTime, clock.DateCenterY, clock.DateHeight);
            // 下次換分鐘只需要時間、日期那一行的內容和每一段的位置（整張圖留著太佔記憶體）
            lastClock = new LockClock.ClockImage { Time = clock.Time, CharX = clock.CharX, DateParts = clock.DateParts, DateX = clock.DateX };
        }

        // 右下角的音樂卡片：換歌、播放或暫停、封面變了才整張重畫；沒在播就拿掉。
        // micLevel：歌詞按鈕的亮度
        void ShowMusic(NowPlaying.Track track, bool force, double micLevel)
        {
            // 跟 Refresh 讀到的是同一首（背景隨時可能換歌）；不含播放 / 暫停。歌曲長度跨過一小時的話進度條的長度不一樣，也要重畫
            string musicKey = track == null ? "" : track.CardKey + "|" + LockClock.ProgressLayout(track.Duration.TotalSeconds);
            bool playing = Playing(track);
            if (!force && musicKey == shownMusic)
            {
                // 只是播放 / 暫停變了：只換播放鍵那張圖（很快）
                bool? shuffle; int? repeat;
                Modes(track, out shuffle, out repeat);
                // 上一首 / 下一首能不能用變了（換歌的空檔播放器常常閃一下）：也只換按鈕那張
                bool canChanged = card != null && track != null && (card.CanPrev != track.CanPrev || card.CanNext != track.CanNext);
                if (canChanged) { card.CanPrev = track.CanPrev; card.CanNext = track.CanNext; }
                if (card != null && (canChanged || shuffle != shownShuffle || repeat != shownRepeat)) ShowButtons(shuffle, repeat);
                if (card != null && playing != shownPlaying) ShowPlaying(playing);
                // 只有歌詞按鈕的狀態變了（開關歌詞、查到歌詞）：不用重畫卡片，花 0.3 秒慢慢變
                if (micLevel != shownMicLevel) ShowMic(null, micLevel);
                progress.Update(track, playing, false);
                return;
            }

            shownMusic = musicKey;
            shownPlaying = playing;
            Modes(track, out shownShuffle, out shownRepeat);   // 剛按了隨機 / 循環的 2 秒內，照按下去的樣子畫（不會跳回舊的）
            var found = new List<KeyValuePair<Rect, string>>();
            card = LockClock.RenderMusic(track, playing, shownShuffle, shownRepeat, primary.Bounds.Width, primary.Bounds.Height, found, big);
            var c = card ?? new LockClock.MusicCard();   // 沒在播：每張圖都是 null，拿掉
            // 卡片本身是播放引擎畫的液態玻璃；舊顯示卡畫不了的話，退回毛玻璃（底下的影片糊掉，再貼這張霧白和邊框）
            engine.SetLiquidCard(primary.Handle, c.Spec);
            engine.SetGlassOverlay(primary.Handle, "music", c.Chrome, c.Width, c.Height, c.X, c.Y, c.InnerX, c.InnerY, c.InnerW, c.InnerH, c.Radius);
            ShowCardColor(c);
            // 放大歌詞：節奏、換歌的光在螢幕四邊（顏色變了慢慢換過去；放大、縮回時淡入淡出）
            if (!ReferenceEquals(c.Edges, shownEdges) || c.Edges == null && shownEdges != null)
            {
                engine.SetEdgeGlow(primary.Handle, c.Edges, c.Edges != null && shownEdges != null ? 700 : 300);
                shownEdges = c.Edges;
            }

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
            // 放大歌詞：左上角的封面、歌名、歌手（沒有玻璃；平常沒有）。換歌時跟卡片一樣：舊的往左滑一小段淡出、新的從右邊滑進來
            // （上一首反過來）；其他時候（放大、縮回、重畫）淡入淡出
            if (shift != 0 && c.StageInfo != null)
                engine.SetSlideOverlay(primary.Handle, "lyricsStage", c.StageInfo, c.StageW, c.StageH, c.StageX, c.StageY, OverlayLayer.Lyrics,
                    0, 0, primary.Bounds.Width, primary.Bounds.Height, shift, 450, 1, 0);
            else
                engine.SetFadeOverlay(primary.Handle, "lyricsStage", c.StageInfo, c.StageW, c.StageH, c.StageX, c.StageY, OverlayLayer.Lyrics, switching ? 0 : 300);
            SetCardLayer("musicButtons", c.Buttons, 0, 1, 0);
            SetCardLayer("musicPlay", c.PlayKey, 0, playing ? 0 : 1, 0);
            SetCardLayer("musicPause", c.PauseKey, 0, playing ? 1 : 0, 0);
            SetMarqueeLayer("musicTitle", c.Title, shift);
            SetMarqueeLayer("musicSubtitle", c.Subtitle, shift);
            if (card != null) ZoomPlaying(playing);
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

        void ShowButtons(bool? shuffle, int? repeat)
        {
            shownShuffle = shuffle; shownRepeat = repeat;
            SetCardLayer("musicButtons", LockClock.RenderButtons(card, shuffle, repeat), 0, 1, 0);
        }

        // 播放 / 暫停換了：▶ 和 ⏸ 兩張都在，舊的很快縮小淡出、新的從小放大彈出來（SF Symbols 換圖的樣子）；封面跟著縮放
        void ShowPlaying(bool playing)
        {
            if (card == null) return;
            shownPlaying = playing;
            engine.FadeOverlay(primary.Handle, playing ? "musicPlay" : "musicPause", 0, 120, false);
            engine.FadeOverlay(primary.Handle, playing ? "musicPause" : "musicPlay", 1, 180, false);
            ZoomPlaying(playing);
        }

        // 播放鍵、封面的大小：播放鍵現在那張正常大小、另一張縮在 0.55 倍；
        // 暫停時封面縮到 0.9 倍（Apple Music：不用看按鈕也知道停了），播放時帶一點彈性回到原本的大小
        void ZoomPlaying(bool playing)
        {
            string show = playing ? "musicPause" : "musicPlay", hide = playing ? "musicPlay" : "musicPause";
            engine.ZoomOverlay(primary.Handle, hide, 0.55, card.KeyX, card.KeyY, 0.25, 1);
            engine.ZoomOverlay(primary.Handle, show, 1, card.KeyX, card.KeyY, 0.4, 0.55);
            engine.ZoomOverlay(primary.Handle, "musicCover", playing ? 1 : 0.9, card.ArtX, card.ArtY, 0.5, playing ? 0.62 : 0.9);
        }

        // 跑馬燈的那行字（line 是 null：這首放得下，拿掉）：換歌時跟封面、歌名一起滑動
        void SetMarqueeLayer(string name, LockClock.MarqueeLine line, int shift)
        {
            var c = card ?? new LockClock.MusicCard();
            if (line == null)
            {
                engine.SetSlideOverlay(primary.Handle, name, null, 0, 0, 0, 0, OverlayLayer.CardContent, c.InnerX, c.InnerY, c.InnerW, c.InnerH, shift, 450, 1, 0);
                return;
            }
            engine.SetSlideOverlay(primary.Handle, name, line.Pixels, line.W, line.H, line.X, line.Y, OverlayLayer.CardContent, c.InnerX, c.InnerY, c.InnerW, c.InnerH, shift, 450, 1, 0);
            // 停 2.5 秒、每秒約 30 像素（1080p）往左捲，兩份相隔 40、兩端淡出 16
            double s = c.Scale;
            engine.SetMarquee(primary.Handle, name, line.TextW, 40 * s, line.ViewX, line.ViewW, 16 * s, 30 * s, 2500);
        }

        // 按隨機播放 / 循環：圖示馬上換，不等播放器回報。循環照 Apple 的順序：關 → 全部 → 單曲 → 關
        void ToggleMode(string which)
        {
            if (card == null || !(which == "shuffle" ? shownShuffle.HasValue : shownRepeat.HasValue)) return;   // 播放器不支援
            expectModeTick = Environment.TickCount;
            if (which == "shuffle")
            {
                expectShuffle = !(shownShuffle == true);
                NowPlaying.SetShuffle(expectShuffle.Value);
                ShowButtons(expectShuffle, shownRepeat);
            }
            else
            {
                int now = shownRepeat ?? 0;
                expectRepeat = now == 0 ? 2 : now == 2 ? 1 : 0;
                NowPlaying.SetRepeat(expectRepeat.Value);
                ShowButtons(shownShuffle, expectRepeat);
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
            if (lyricsArmed)
            {
                // 放開歌詞圓鈕：照按了多久決定力道（0.09 秒內是輕點、力道 0；到 0.6 秒蓄滿，先快後慢），按在哪一邊決定往哪邊鼓
                lyricsArmed = false;
                double held = Math.Max(0, Math.Min(1, (Environment.TickCount - lyricsArmedAt - 90) / 510.0));
                if (engine != null && primary != null)
                {
                    engine.LaunchLiquid(primary.Handle, 1 - (1 - held) * (1 - held), lyricsArmedDir);
                    if (lyrics.Toggle()) Refresh(false);
                    else engine.ChargeLiquid(primary.Handle, false);   // 放開前歌詞不見了（換歌）：取消蓄力
                }
            }
        }

        // 按住歌詞圓鈕準備打開歌詞：什麼時候按的、按在圓鈕的哪一邊（−1 左緣、1 右緣）
        bool lyricsArmed;
        int lyricsArmedAt;
        double lyricsArmedDir;

        // 重拍時邊框的光暈（跟著專輯封面的顏色）：換歌顏色變了就慢慢換過去（0.7 秒，跟玻璃的色調一起），卡片剛出現或拿掉時直接換
        void ShowCardColor(LockClock.MusicCard c)
        {
            if (card != null && c.ColorKey == shownColorKey && colorShown) return;
            int fade = card != null && colorShown ? 700 : 0;
            engine.SetFadeOverlay(primary.Handle, "musicColorGlow", c.Color == null ? null : c.Color.Glow, c.Width, c.Height, c.X, c.Y, OverlayLayer.CardEdge, fade, true);
            if (fade > 0 && c.HasArt) engine.SwellCardGlow(primary.Handle);   // 換歌：邊框亮一下，看得到顏色換過去（封面還沒讀到時的顏色是暫時的，等封面來了再亮）
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
            return OnCard(p.X - primary.Bounds.X, p.Y - primary.Bounds.Y);
        }

        // (x, y)（主螢幕鎖定視窗裡的座標）在卡片的玻璃上
        bool OnCard(int x, int y)
        {
            if (card == null) return false;
            int top = card.InnerY - lyrics.Extension;   // 歌詞顯示時玻璃往上延伸的那段也算
            return x >= card.InnerX && x < card.InnerX + card.InnerW && y >= top && y < card.InnerY + card.InnerH;
        }

        // L：打開 / 關掉歌詞，跟按歌詞圓鈕一樣（圓鈕也亮一下，像被按了；沒有歌詞時沒反應）
        void OnLyricsKey()
        {
            if (busy || engine == null || card == null) return;
            engine.PressLiquidButton(primary.Handle, VideoEngine.LyricsButton, true);
            engine.PressLiquidButton(primary.Handle, VideoEngine.LyricsButton, false);   // 放開：還是會亮滿最短的 0.12 秒
            if (lyrics.Toggle()) Refresh(false);
        }

        // 點到「正在播放」的按鈕：控制播放器、切換歌詞或拖進度條，鎖定畫面不滑走（變暗的按鈕按了也不會滑走）
        bool OnClick(int x, int y)
        {
            // 上一次按下的放開沒收到（還沒被 pageWatch 清掉）：已經在拖的照放開處理（滑走或彈回），這一下不算；還沒拖動的直接丟掉
            if (pageDrag != null)
            {
                if (pageDrag.Moving) { PageDrop(true); return true; }
                EndPageDrag();
            }
            if (busy) return false;
            if (engine == null) return true;   // 顯示卡重設的空檔（還沒接上新的引擎）：點了不理，也不滑走
            if (shrinking) return true;   // 正在換大小：點了不理
            if (big)
            {
                // 放大歌詞：同步鈕回去跟著播放；點某一行跳到那行開始唱的地方；點在歌詞那段的其他地方不理（不縮回去）
                if (lyrics.SyncHit(x, y)) { lyrics.Follow(); return true; }
                double time;
                if (lyrics.LineAt(x, y, out time))
                {
                    if (progress.SeekTo(NowPlaying.Current, time)) lyrics.Follow();
                    return true;
                }
                if (lyrics.InView(x, y)) return true;
            }
            if (big && !controlsShown) { ShowControls(true); return true; }   // 放大歌詞、播放控制淡掉了：點一下先把它叫出來
            if (big) ShowControls(true);
            if (lyrics.ZoomHit(x, y)) { SetBig(!big); return true; }   // 歌詞區右上角的放大鈕
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
                        // 打開：先蓄力（卡片上緣往下壓），放開時照按了多久、按在圓鈕的哪一邊彈上去（見 Release）；關掉：馬上收回
                        if (!lyrics.On && lyrics.CanToggle)
                        {
                            lyricsArmed = true;
                            lyricsArmedAt = Environment.TickCount;
                            lyricsArmedDir = Math.Max(-1, Math.Min(1, (x - (b.Key.X + b.Key.Width / 2)) / (b.Key.Width / 2)));
                            engine.ChargeLiquid(primary.Handle, true);
                        }
                        else if (lyrics.Toggle()) Refresh(false);   // 歌詞先開始淡入 / 淡出，再更新按鈕的樣子
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
            if (!OnCard(x, y)) return false;
            Touch(x, y, -1);   // 點在卡片上的空白處（歌名、封面、按鈕之間）：玻璃一樣有按下的光，但不做事、也不滑走
            return true;
        }

        // 滾輪：放大歌詞時自己捲歌詞
        void OnWheel(int delta)
        {
            if (Active && !busy && big && !shrinking && engine != null) lyrics.Wheel(delta);
        }

        // 空白鍵 / 滑鼠：往上滑走，回到桌面
        void OnDismiss()
        {
            if (!Active || busy) return;
            wantBig = false;
            autoBig = false;   // 廣告中按了 Esc / 空白鍵：之後不要再自己放大
            if (big) { EndPageDrag(); SetBig(false); return; }   // 放大歌詞中：先縮回來（再按一次才滑走）
            EndPageDrag();   // 按著還沒拖動時按了空白鍵之類的：不用等放開
            bool blocked = false;
            BeforeDismiss(ref blocked);
            if (blocked) return;
            busy = true;
            SetCovering(false);             // 桌面要露出來了，桌面上的影片先接著畫
            Slide(0, 1, 420, true, CloseAfterSlide);   // 往上滑走，滑完再收起來
        }

        // 照曲線從 from 滑到 to（0 = 蓋住畫面、1 = 在上方），滑完呼叫 done（參數是在畫面裡滑的那個視窗，見 PrepareSheet；沒有是 null）
        void Slide(double from, double to, int ms, bool hideSelf, Action<LockWindow> done)
        {
            var pinned = PrepareSheet(from, to, hideSelf);
            // 頁面在畫面裡滑的話，下一次畫面更新才開始（跟視窗移到原位同一格）
            var path = PagePath.Curve(from, to, AnimationClock.Now + (pinned != null ? 17 : 0), ms);
            if (pinned != null) engine.SheetPath(primary.Handle, path);
            Animate(path, pinned, delegate { done(pinned); });
        }

        // 收起滑完：頁面在畫面裡滑的話，露出來的桌面（快照）還暗著，等引擎把它亮回原樣（約 0.3 秒，照 iPhone）再收掉視窗，
        // 收掉時看到的真正桌面才跟快照一樣亮、不會突然一亮。視窗本身滑走的（沒有快照）直接收
        void CloseAfterSlide(LockWindow pinned)
        {
            if (pinned == null || !Active) { CloseNow(); return; }
            var shown = primary;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(VideoEngine.SheetSlide.DimOut + 40) };
            timer.Tick += delegate
            {
                timer.Stop();
                if (Active && primary == shown) CloseNow();   // 這段時間裡被 Win+L 收掉、又重新打開的話不要動到新的
            };
            timer.Start();
        }

        // ---------- 用滑鼠把頁面往上拖 ----------
        // 按在按鈕、卡片以外的地方往上拖：頁面跟著游標走；放開時照位置和速度決定滑走還是彈回來（往上甩一下也會走）。
        // 按了沒拖動就放開，跟以前一樣點一下就滑走
        class PageDrag
        {
            public int PressY;            // 按下去的地方（螢幕座標）
            public bool Moving;           // 已經往上拖超過一點點，頁面開始跟著走
            public int OriginY;           // 開始跟著走的地方（頁面位置從這裡算，開始時不會跳一下）
            public int LastY;             // 最後一次移動時游標在哪（頁面停在這裡；放開時從這裡彈）
            public LockWindow Pinned;     // 主螢幕的頁面在畫面裡滑（不是 null 時這個視窗不動）
            public PagePath Path;         // 最後一次移動時給的位置（從上一次畫到的地方補過去，見 PagePath.Hold）
            public double Interval;       // 滑鼠事件平均隔多久（毫秒）
            public readonly List<KeyValuePair<long, double>> Samples = new List<KeyValuePair<long, double>>();   // 最近的位置（算放開時的速度）
        }
        PageDrag pageDrag;
        DispatcherTimer pageWatch;   // 拖曳中每 0.05 秒看左鍵還有沒有按著（放開的訊息被別的視窗收走時）
        const int PageDragStart = 8; // 往上拖幾像素才算開始拖

        bool OnPress()
        {
            if (!Active || busy || primary == null) return false;   // 正在滑：照舊（OnDismiss 會不理它）
            if (big && !controlsShown) { ShowControls(true); return true; }   // 放大歌詞、播放控制淡掉了：點一下先把它叫出來
            if (big) { SetBig(false); return true; }   // 放大歌詞中點卡片以外的地方：縮回來（不拖頁面、不滑走）
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return false;
            pageDrag = new PageDrag { PressY = p.Y };
            primary.StartDrag();
            pageWatch.Start();
            return true;
        }

        // 游標現在在哪（螢幕座標；頁面沒辦法在畫面裡滑時視窗自己也在動，所以不用視窗裡的座標）
        static int CursorY()
        {
            Native.POINT p;
            Native.GetCursorPos(out p);
            return p.Y;
        }

        void PageMove()
        {
            var d = pageDrag;
            if (d == null) return;
            if (!Active || primary == null) { EndPageDrag(); return; }
            if (!LockWindow.LeftButtonDown()) { PageDrop(true); return; }
            int y = CursorY();
            if (!d.Moving)
            {
                if (d.PressY - y < PageDragStart) return;   // 還沒往上拖動（往下拖不算）：放開時當作點一下
                bool blocked = false;
                BeforeDismiss(ref blocked);
                if (blocked || busy) { EndPageDrag(); return; }
                busy = true;
                SetCovering(false);   // 桌面要露出來了，桌面上的影片先接著畫
                d.Moving = true;
                d.OriginY = y;
                d.Pinned = PrepareSheet(0, 1, true);
                if (!Active || pageDrag != d) return;   // 準備的途中被收掉了
                y = CursorY();
            }
            d.LastY = y;
            long now = AnimationClock.Now;
            double at = DragHidden(d, y), v = DragVelocity(d, y, now);
            // 從現在畫到的位置，用大約一個滑鼠事件間隔（平均過的，2～12 毫秒）等速補到這次的位置（見 PagePath.Hold）
            var prev = d.Path;
            long gap = prev == null ? 0 : now - prev.Start;
            if (gap > 0 && gap < 40) d.Interval = d.Interval <= 0 ? gap : d.Interval + (gap - d.Interval) * 0.25;
            int ms = prev == null ? 0 : (int)Math.Round(Math.Max(2, Math.Min(12, d.Interval <= 0 ? 8 : d.Interval)));
            d.Path = PagePath.Hold(prev == null ? at : prev.At(now), at, v, now, ms);
            ShowPage(d, d.Path);
        }

        // 頁面現在被拖到哪（0 = 蓋住畫面、1 = 在上方），順便記下來算速度
        double DragHidden(PageDrag d, int y)
        {
            return Math.Max(0, Math.Min(1, (d.OriginY - y) / (double)primary.Bounds.Height));
        }

        // 到 now 為止最近 0.08 秒的平均速度（每秒幾個畫面高，往上是正的；y 是 now 時游標的位置，記下來）
        double DragVelocity(PageDrag d, int y, long now)
        {
            d.Samples.Add(new KeyValuePair<long, double>(now, DragHidden(d, y)));
            while (d.Samples.Count > 2 && now - d.Samples[0].Key > 80) d.Samples.RemoveAt(0);
            var first = d.Samples[0];
            long dt = now - first.Key;
            return dt < 8 ? 0 : (DragHidden(d, y) - first.Value) / (dt / 1000.0);
        }

        // 頁面照 path 走：主螢幕的頁面（或整個視窗）、其他螢幕的視窗、卡片的慣性一起
        void ShowPage(PageDrag d, PagePath path)
        {
            if (engine != null && primary != null)
            {
                if (d.Pinned != null) engine.SheetPath(primary.Handle, path);
                engine.SlideCard(primary.Handle, path, primary.Bounds.Height, primary.Bounds.Height / 1080.0);
            }
            Move(path.To, d.Pinned);   // 其他螢幕的視窗直接到滑鼠的位置（每次移動才動一次，補不了）
        }

        // late：放開的訊息沒收到，是事後才發現的（最多晚 0.05 秒）
        void PageDrop(bool late)
        {
            var d = pageDrag;
            EndPageDrag();
            if (d == null || !Active || primary == null) return;
            if (!d.Moving) { OnDismiss(); return; }   // 沒拖動：點一下，跟以前一樣滑走
            // 從頁面現在畫的位置（最後一次移動）彈，不用放開時的游標（事後才發現放開時游標可能已經移走了，頁面會先跳一下）。
            // 速度：收到放開的訊息時再記一筆現在的時間（放開前停了一下的話，舊的移動就不算）；
            // 事後才發現的，照最後一次移動的時間算（不然這段晚發現的時間會被當成停住，往上甩一下也被算成彈回）
            int y = d.LastY;
            long at = late && d.Samples.Count > 0 ? d.Samples[d.Samples.Count - 1].Key : AnimationClock.Now;
            double raw = DragHidden(d, y), v = Math.Max(-6, Math.Min(6, DragVelocity(d, y, at)));
            // 照現在的速度再滑一小段（0.25 秒）會超過一半就滑走，不然彈回來：慢慢拖過一半、或往上甩一下都會走（照游標判斷）
            bool away = raw + 0.25 * v > 0.5;
            // 彈簧從現在畫到的位置開始（還在補最後那一段的話，從補到的地方接著走，不會跳一下）
            long now = AnimationClock.Now;
            double h = d.Path != null ? d.Path.At(now) : raw;
            var path = PagePath.Spring(h, v, away ? 1 : 0, now);
            if (engine != null && d.Pinned != null) engine.SheetPath(primary.Handle, path);
            Animate(path, d.Pinned, delegate
            {
                if (away) { CloseAfterSlide(d.Pinned); return; }
                if (!Active) return;
                if (engine != null && d.Pinned != null) engine.CancelSheet(primary.Handle);   // 回到原位：跟平常一模一樣，收掉
                busy = false;
                SetCovering(true);
                Native.SetForegroundWindow(primary.Handle);
            });
        }

        void EndPageDrag()
        {
            if (pageDrag == null) return;
            pageDrag = null;
            if (pageWatch != null) pageWatch.Stop();
            if (primary != null) primary.EndDrag();
        }

        // 主螢幕的頁面在畫面裡滑（視窗不動）：拍底下桌面的快照交給引擎，等引擎準備好，回傳不用移動的那個視窗；
        // 之後由呼叫的人用 VideoEngine.SheetPath 告訴引擎頁面怎麼走。拍不到、這台電腦不支援就回傳 null，照原本移動整個視窗。
        // hideSelf：鎖定畫面正蓋著（收起時）——只在拍的那一兩格把視窗排除在截圖之外（其他時候照樣截得到），拍完馬上恢復
        LockWindow PrepareSheet(double from, double to, bool hideSelf)
        {
            if (engine == null || primary == null || !VideoEngine.SheetCanSlide) return null;
            int[] snapshot = SnapScreen(primary, hideSelf, windows);
            if (snapshot == null) return null;
            // 等引擎把快照放上顯示卡、畫出還沒開始滑的第一格（打開時那一格就是桌面本身，視窗這時才移下來，看不出接縫）；
            // 準備不起來、或等超過 0.3 秒，就照原本移動整個視窗
            var request = engine.SlideSheet(primary.Handle, snapshot, primary.Bounds.Width, primary.Bounds.Height, from, to);
            long giveUp = AnimationClock.Now + 300;
            using (var vblank = new VBlank())
                while (request.State == 0 && AnimationClock.Now < giveUp) vblank.Wait();
            if (engine == null || primary == null) return null;
            if (request.State != 1) { engine.CancelSheet(primary.Handle); return null; }
            return primary;
        }

        // 拍 w 那個螢幕現在的樣子（BGRA，跟螢幕一樣大）；失敗回傳 null
        // 其他螢幕的鎖定視窗（all 裡不是 w 的）也一起排除：打開時它們停在自己螢幕的正上方，主螢幕在下面的話會蓋在主螢幕上
        static int[] SnapScreen(LockWindow w, bool hideSelf, List<LockWindow> all)
        {
            var b = w.Bounds;
            var hidden = new List<LockWindow>();
            try
            {
                if (hideSelf)
                {
                    if (!Native.SetWindowDisplayAffinity(w.Handle, 0x11 /* WDA_EXCLUDEFROMCAPTURE */)) return null;
                    hidden.Add(w);
                }
                foreach (var o in all)
                    if (o != w && Native.SetWindowDisplayAffinity(o.Handle, 0x11)) hidden.Add(o);
                if (hidden.Count > 0) { Native.DwmFlush(); Native.DwmFlush(); }   // 等 DWM 換成不含鎖定畫面的那一份
                try
                {
                    using (var bmp = new System.Drawing.Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                    {
                        // 直接 BitBlt（Graphics.CopyFromScreen 不接受 SRCCOPY 加 CAPTUREBLT 的組合）：CAPTUREBLT 讓分層視窗（選單、提示）也拍進來
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            IntPtr dst = g.GetHdc(), src = Native.GetDC(IntPtr.Zero);
                            try
                            {
                                if (src == IntPtr.Zero || !Native.BitBlt(dst, 0, 0, b.Width, b.Height, src, b.X, b.Y, 0x00CC0020 /* SRCCOPY */ | 0x40000000 /* CAPTUREBLT */)) return null;
                            }
                            finally
                            {
                                if (src != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, src);
                                g.ReleaseHdc(dst);
                            }
                        }
                        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, b.Width, b.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                            System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                        var pixels = new int[b.Width * b.Height];
                        for (int y = 0; y < b.Height; y++)
                            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * b.Width, b.Width);
                        bmp.UnlockBits(data);
                        return pixels;   // 著色器只用 rgb，alpha 不用管
                    }
                }
                finally
                {
                    // 一定要恢復（不然之後截圖、錄影都拍不到鎖定畫面）：失敗再試一次
                    foreach (var o in hidden)
                        if (!Native.SetWindowDisplayAffinity(o.Handle, 0)) Native.SetWindowDisplayAffinity(o.Handle, 0);
                }
            }
            catch { return null; }
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
            clockEngine = null; shownClock = null; clockGen++;
            lyrics.Close();
            big = false; wantBig = false; autoBig = false;   // 下次打開是一般大小
            shownSong = null;   // 下次打開時卡片直接出現，不播換歌動畫
            shownMicLevel = -1;
            expectPlaying = null;
            expectShuffle = null; expectRepeat = null;
            touching = false;
            pressed = -1;
            lyricsArmed = false;   // 按著歌詞圓鈕時被收掉：不打開歌詞
            if (pressWatch != null) pressWatch.Stop();
            EndPageDrag();         // 拖著頁面時被收掉（Win+L、睡眠）
            progress.Stop();
            card = null;
            colorShown = false;
            shownEdges = null;
            shownColorKey = null;
            NowPlaying.Stop();
            Beat.StopAll();   // 光暈、歌詞的人聲都不聽了
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
        void Move(double hidden, LockWindow pinned = null)
        {
            IntPtr batch = Native.BeginDeferWindowPos(windows.Count);
            foreach (var w in windows)
                batch = Native.DeferWindowPos(batch, w.Handle, IntPtr.Zero, w.Bounds.X, w.Bounds.Y - (w == pinned ? 0 : (int)Math.Round(w.Bounds.Height * hidden)), 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Native.EndDeferWindowPos(batch);
        }

        // 每次螢幕垂直同步就移動一次視窗（120Hz 螢幕每秒 120 次），不會被影片的更新頻率綁住。
        // 視窗屬於這條執行緒，要在這裡移動才會立刻生效（從別的執行緒移動，Windows 會排隊交給這條執行緒，反而變慢）。
        // pinned 不是 null：那個視窗不動（頁面由引擎在畫面裡滑，見 PrepareSheet）；其他螢幕照原本移動視窗。從 path.Start 才開始滑
        void Animate(PagePath path, LockWindow pinned, Action done)
        {
            using (var vblank = new VBlank())
            {
                while (AnimationClock.Now < path.Start) vblank.Wait();   // 等引擎先畫好第一格
                // 卡片跟著頁面一起動，但有一點慣性（開始時慢半拍、停下時往前衝一點再彈回、移動時沿方向拉長一點），由播放引擎照同一條曲線算
                if (engine != null && primary != null)
                    engine.SlideCard(primary.Handle, path, primary.Bounds.Height, primary.Bounds.Height / 1080.0);
                while (true)
                {
                    long now = AnimationClock.Now;
                    Move(path.At(now), pinned);
                    if (path.Done(now)) break;
                    vblank.Wait();
                }
                Move(path.To, pinned);   // 彈簧在離終點不到一像素時就算停好：最後放到剛好的位置（不然 4K 上可能差 1 像素、露出一條桌面）
            }
            done();
        }
    }
}
