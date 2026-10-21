using System;
using System.Windows.Threading;

namespace VideoWallpaper
{
    // 鎖定畫面音樂卡片上的進度條：兩端的時間（秒數變了才重畫）、按住拖曳（放開時才真的跳過去）。
    // 進度條本身由播放引擎畫（播放中自己往後推，一格一格很順），這裡只把位置告訴它
    class LockProgress
    {
        readonly LockScreen owner;
        bool scrubbing;
        double scrubSeconds, duration;
        bool canSeek;         // 播放器接受跳轉嗎
        bool holding;         // 剛跳過去、剛按了播放暫停：1.5 秒內播放器回報的還是舊的，先照我們知道的位置往後推（暫停中就停住）
        double holdSeconds;
        int heldAt;
        bool playing;         // 卡片上顯示的是播放中嗎（剛按了播放暫停時是按下去的樣子）
        string shownTime;     // 卡片上現在的時間（「已播 / 剩下」，沒變就不重畫）
        DispatcherTimer dragWatch;   // 拖曳中每 0.1 秒看左鍵還有沒有按著（在別的螢幕上放開時，放開的訊息收不到）

        public LockProgress(LockScreen owner) { this.owner = owner; }

        public bool Scrubbing { get { return scrubbing; } }

        // 卡片上有進度條嗎（知道歌曲長度才有）
        public bool Shown { get { return owner.Card != null && duration > 0; } }

        // 有進度條可以拖嗎（不知道歌曲長度時沒有進度條；播放器不接受跳轉時只看不能拖）
        public bool CanScrub { get { return owner.Card != null && duration > 0 && canSeek && owner.Card.ProgressWidth > 0; } }

        public void Start()
        {
            dragWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            dragWatch.Tick += delegate { if (!scrubbing) dragWatch.Stop(); else if (!LockWindow.LeftButtonDown()) OnDrop(); };
        }

        public void Stop()
        {
            scrubbing = false;
            if (dragWatch != null) dragWatch.Stop();
            shownTime = null;
            holding = false;
        }

        // 每秒、換歌重畫卡片時：位置告訴播放引擎，時間變了就重畫那張圖（force：卡片剛重畫，一定要放）。
        // playing：卡片上顯示的是不是播放中（剛按了播放暫停時，照按下去的樣子，不等播放器回報）
        public void Update(NowPlaying.Track track, bool playing, bool force)
        {
            this.playing = playing;
            var card = owner.Card;
            if (owner.Engine == null || owner.Primary == null) return;
            if (card == null || track == null)
            {
                if (force) owner.SetCardLayer("musicTime", null, 0, 1, 0);
                shownTime = null;
                return;
            }
            duration = track.Duration.TotalSeconds;
            canSeek = track.CanSeek;
            double position = scrubbing ? scrubSeconds : Position(track);
            owner.Engine.SetLiquidProgress(owner.Primary.Handle, position, duration, playing, scrubbing);
            string key = duration > 0 ? Math.Floor(position) + "/" + Math.Floor(duration) : "";
            if (!force && key == shownTime) return;
            shownTime = key;
            owner.SetCardLayer("musicTime", LockClock.RenderTime(card, position, duration), 0, 1, 0);
        }

        // 現在播到第幾秒：剛跳過去、剛按了播放暫停的 1.5 秒內，照我們知道的位置往後推（播放器還沒回報）
        double Position(NowPlaying.Track track)
        {
            int since = Environment.TickCount - heldAt;
            if (holding && since < 1500) return holdSeconds + (playing ? since / 1000.0 : 0);
            holding = false;
            return NowPlaying.PositionSeconds(track);
        }

        // 剛按了播放暫停：從現在的位置開始照新的狀態走（暫停就停在這裡）
        public void Toggle(NowPlaying.Track track, bool nowPlaying)
        {
            if (track == null) return;
            holdSeconds = Position(track);
            heldAt = Environment.TickCount;
            holding = true;
            Update(track, nowPlaying, false);
        }

        // 按到進度條：開始拖曳（不知道歌曲長度就不是按鈕，回傳 false）
        public bool OnPressed(int x)
        {
            if (!CanScrub) return false;
            scrubbing = true;
            owner.Primary.StartDrag();
            if (dragWatch != null) dragWatch.Start();
            Scrub(x);
            return true;
        }

        public void OnDrag(int x)
        {
            if (!scrubbing) return;
            if (!LockWindow.LeftButtonDown()) { OnDrop(); return; }
            Scrub(x);
        }

        // 放開：真的跳過去
        public void OnDrop()
        {
            if (!scrubbing) return;
            scrubbing = false;
            if (dragWatch != null) dragWatch.Stop();
            if (owner.Primary != null) owner.Primary.EndDrag();
            holding = true;
            holdSeconds = scrubSeconds;
            heldAt = Environment.TickCount;
            NowPlaying.Seek(scrubSeconds);
            Update(NowPlaying.Current, playing, false);
        }

        void Scrub(int x)
        {
            var card = owner.Card;
            if (card == null) return;
            scrubSeconds = Math.Max(0, Math.Min(1, (x - card.ProgressLeft) / card.ProgressWidth)) * duration;
            Update(NowPlaying.Current, playing, false);
        }
    }
}
