using System;
using System.Windows.Threading;

namespace VideoWallpaper
{
    // 鎖定畫面音樂卡片上的音量（Spotify 自己的音量）：喇叭和滑桿的狀態、滑鼠停留展開、點 / 拖曳 / 滾輪調整、按喇叭靜音
    class LockVolume
    {
        readonly LockScreen owner;
        double shown = -1;           // 卡片上顯示的音量（0～1）
        double shownLevel = -1;      // 喇叭和滑桿的亮度（找不到 Spotify 的音量條時變暗）
        double audible = 0.5;        // 最近一次不是 0 的音量（按喇叭取消靜音時回到這裡）
        bool found, dragging;
        bool expanded;               // 滑桿展開中（平常只有喇叭）
        DispatcherTimer hoverTimer, collapseTimer;
        DispatcherTimer dragWatch;   // 拖曳中每 0.1 秒看左鍵還有沒有按著（在別的螢幕上放開時，放開的訊息收不到）
        int setTick, wheel;          // 最近一次調音量的時間；滾輪還沒用掉的量
        int commandTick;             // 最近一次真的送音量 / 靜音指令給 Spotify 的時間（它會因此跳到前景）

        public LockVolume(LockScreen owner) { this.owner = owner; }

        // 剛送了指令給 Spotify（2 秒內）：Spotify 這時跳到前景是它自己跳的，不是使用者切走
        public bool RecentlyCommanded { get { return Environment.TickCount - commandTick < 2000; } }

        // 鎖定畫面打開時：滑鼠在喇叭上停 0.5 秒展開，離開 1 秒收起來
        public void Start()
        {
            hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            hoverTimer.Tick += delegate { hoverTimer.Stop(); if (owner.Active && found && PointerOver(false)) Expand(true); };
            collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            collapseTimer.Tick += delegate { collapseTimer.Stop(); if (owner.Active && !dragging && !PointerOver(true)) Expand(false); };
            dragWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            dragWatch.Tick += delegate { if (!dragging) dragWatch.Stop(); else if (!LockWindow.LeftButtonDown()) OnDrop(); };
        }

        // 鎖定畫面收起來時：全部回到初始狀態
        public void Stop()
        {
            shown = shownLevel = -1;
            found = dragging = expanded = false;
            if (hoverTimer != null) { hoverTimer.Stop(); collapseTimer.Stop(); dragWatch.Stop(); }
            wheel = 0;
        }

        // 讀 Spotify 的音量（卡片上是 Spotify 時背景每半秒讀一次）：拖曳中、剛調過的 1.5 秒內不看（Spotify 還沒反應過來，讀到的會是舊的）。
        // level：喇叭和滑桿的亮度（找不到 Spotify 的音量條，例如 Spotify 的視窗關掉了，就變暗）
        public void Read(NowPlaying.Track track, out double volume, out double level)
        {
            SpotifyVolume.SetWanted(track != null && track.IsSpotify);
            volume = Math.Max(0, shown);
            if (track != null && !dragging && Environment.TickCount - setTick > 1500)
            {
                found = SpotifyVolume.Found && track.IsSpotify;   // 卡片上是別的播放器（例如瀏覽器）時，音量按鈕變暗
                if (found)
                {
                    volume = Math.Round(SpotifyVolume.Level * 100) / 100;
                    if (volume > 0) audible = volume;
                }
            }
            level = found ? 1.0 : 0.25;
        }

        // 卡片拿掉了：滑桿回到收起來的狀態，下次卡片出現時才不會一出來就是展開的
        public void CardRemoved()
        {
            expanded = false;
            if (hoverTimer != null) { hoverTimer.Stop(); collapseTimer.Stop(); }
        }

        // 整張卡片重畫了：放上喇叭和滑桿（跟著換歌一起滑；亮度變了的話慢慢變亮或變暗）
        public void ShowOnCard(LockClock.MusicCard c, int shift, double volume, double level)
        {
            owner.SetCardLayer("musicVolume", c.Volume, shift, level, 300);
            SetBarLayer(c.VolumeBar, shift, level, volume);
            shown = volume;
            shownLevel = level;
        }

        // 卡片沒重畫：找不到 / 又找到 Spotify 的音量條時，喇叭和滑桿慢慢變暗或變亮；
        // 音量在別的地方被調了（在 Spotify 裡調的）就只重畫音量那兩張
        public void Refresh(double volume, double level)
        {
            if (level != shownLevel)
            {
                owner.Engine.FadeOverlay(owner.Primary.Handle, "musicVolume", level, 300, false);
                owner.Engine.FadeOverlay(owner.Primary.Handle, "musicVolumeBar", level, 300, false);
                shownLevel = level;
            }
            Show(volume);
        }

        // 音量按鈕變暗了（卡片換成別的播放器、找不到 Spotify 的音量條）：展開的滑桿收起來
        public void CollapseIfLost()
        {
            if (expanded && !found && !dragging) Expand(false);
        }

        // 滑桿那張：可以展開 / 收起，展開時從喇叭旁邊長出來（把手的位置跟著音量，展開時把手保持原樣）
        void SetBarLayer(int[] pixels, int shift, double opacity, double volume)
        {
            var c = owner.Card ?? new LockClock.MusicCard();
            owner.SetCardLayer("musicVolumeBar", pixels, shift, opacity, 300, c.RevealX, c.RevealW, expanded ? 1 : 0,
                c.KnobX(volume), c.KnobRadius + Math.Max(1, Math.Round(owner.Primary.Bounds.Height / 1080.0 * 2)));
        }

        // 卡片上的音量改成 volume（只重畫喇叭和滑桿那兩張）
        void Show(double volume)
        {
            var card = owner.Card;
            if (card == null || owner.Engine == null || volume == shown) return;
            shown = volume;
            int[] bar;
            owner.SetCardLayer("musicVolume", card.RenderVolume(volume, out bar), 0, shownLevel, 300);
            SetBarLayer(bar, 0, shownLevel, volume);
        }

        // 展開 / 收起滑桿（從喇叭旁邊長出來、淡入；收起時縮回喇叭、淡出）
        void Expand(bool expand)
        {
            if (hoverTimer != null) hoverTimer.Stop();
            if (collapseTimer != null) collapseTimer.Stop();
            if (expand == expanded || owner.Card == null || owner.Engine == null) return;
            expanded = expand;
            owner.Engine.RevealOverlay(owner.Primary.Handle, "musicVolumeBar", expand ? 1 : 0, expand ? 380 : 260);
        }

        // 滑鼠在喇叭上停 0.5 秒展開滑桿；展開後離開喇叭和滑桿 1 秒就收起來
        public void OnHover(int x, int y)
        {
            if (owner.Card == null || owner.Busy || hoverTimer == null) return;
            if (!expanded)
            {
                if (owner.ButtonAt("mute", x, y)) { if (!hoverTimer.IsEnabled && found) hoverTimer.Start(); }
                else hoverTimer.Stop();
            }
            else if (dragging || owner.ButtonAt("mute", x, y) || owner.ButtonAt("volume", x, y)) collapseTimer.Stop();
            else if (!collapseTimer.IsEnabled) collapseTimer.Start();
        }

        // 游標現在在不在喇叭上（includeBar：或展開的滑桿上）
        bool PointerOver(bool includeBar)
        {
            var primary = owner.Primary;
            if (primary == null || !LockWindow.CursorShown) return false;
            Native.POINT p;
            Native.GetCursorPos(out p);
            int x = p.X - primary.Bounds.X, y = p.Y - primary.Bounds.Y;
            return owner.ButtonAt("mute", x, y) || (includeBar && owner.ButtonAt("volume", x, y));
        }

        // 按喇叭：靜音，或回到靜音前的音量（用 Spotify 自己的靜音鈕，它記得確切的音量）；變暗的喇叭按了沒反應
        public void OnMuteClicked()
        {
            if (!found) return;
            if (hoverTimer != null) hoverTimer.Stop();   // 按了就不要再展開
            double expected = shown > 0 ? 0 : audible;
            setTick = commandTick = Environment.TickCount;
            SpotifyVolume.ToggleMute(expected);
            Show(expected);
        }

        // 點到滑桿：點到哪裡就調到哪裡，按著不放可以左右拖曳（每跨過一格 Spotify 就跟著變）。
        // 滑桿收起來時那裡是空的，回傳 false（當作沒點到按鈕）
        public bool OnBarClicked(int x)
        {
            if (!expanded) return false;
            if (found)
            {
                dragging = true;
                owner.Primary.StartDrag();
                if (dragWatch != null) dragWatch.Start();
                var card = owner.Card;
                Set((x - card.TrackLeft) / card.TrackWidth);
            }
            return true;
        }

        public void OnDrag(int x, int y)
        {
            var card = owner.Card;
            if (!dragging || card == null || card.TrackWidth <= 0) return;
            // 左鍵已經放開（放開的訊息被別的視窗收走了，例如在別的螢幕上放開）：拖曳結束
            if (!LockWindow.LeftButtonDown()) { OnDrop(); return; }
            Set((x - card.TrackLeft) / card.TrackWidth);
        }

        public void OnDrop()
        {
            if (!dragging) return;
            dragging = false;
            setTick = Environment.TickCount;
            if (dragWatch != null) dragWatch.Stop();
            if (owner.Primary != null) owner.Primary.EndDrag();
            if (collapseTimer != null && !PointerOver(true)) collapseTimer.Start();   // 在滑桿外面放開：1 秒後收起來
        }

        // 滑鼠滾輪在卡片上：轉一格（120）調 10%（觸控板轉得比較細，累積到一格再調）
        public bool OnWheel(int x, int y, int delta)
        {
            var card = owner.Card;
            if (owner.Busy || card == null || !found) return false;
            if (!card.InnerContains(x, y)) return false;
            // 滾輪也會展開滑桿（看得到調到多少），停下來、游標不在喇叭和滑桿上的話 1 秒後收起來
            Expand(true);
            if (collapseTimer != null && !PointerOver(true)) collapseTimer.Start();
            wheel += delta;
            int steps = wheel / 120;
            if (steps != 0)
            {
                wheel -= steps * 120;
                Set(shown + steps * SpotifyVolume.Step);
            }
            return true;
        }

        // 調 Spotify 的音量（0～1，一格 10%，跟 Spotify 音量條從外面調的單位一樣），卡片上的滑桿馬上跟著動
        void Set(double volume)
        {
            volume = Math.Round(Math.Round(Math.Max(0, Math.Min(1, volume)) / SpotifyVolume.Step) * SpotifyVolume.Step, 2);
            setTick = Environment.TickCount;
            if (volume > 0) audible = volume;
            if (volume == shown) return;
            commandTick = Environment.TickCount;
            SpotifyVolume.Set(volume);
            Show(volume);
        }
    }
}
