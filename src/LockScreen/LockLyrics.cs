using System;
using System.Windows.Threading;

namespace VideoWallpaper
{
    // 鎖定畫面音樂卡片上方的歌詞：按了歌詞按鈕才顯示（換歌也繼續顯示），整首歌一張長圖，跟著播放進度一行一行捲動
    class LockLyrics
    {
        readonly LockScreen owner;
        bool on;                     // 按了歌詞按鈕才顯示；鎖定畫面收起來就關掉，下次要再按一次
        DispatcherTimer timer;
        Lyrics.Result lyrics;        // 目前這首歌的歌詞（null = 查詢中）
        string key;                  // lyrics 是哪首歌的
        int cardTop, cardLeft;       // 音樂卡片的上緣、左緣（螢幕座標），歌詞放在它上面、跟卡片內容對齊
        // 畫面上的歌詞長圖（每首歌一張；換歌、關掉時舊的淡出）
        string sheetName, sheetSong;
        Lyrics.Result sheetLyrics;
        double[] lineCenters, lineHeights;
        double lineSpacing;
        int viewHeight, shownLine, sheetCount;

        public LockLyrics(LockScreen owner) { this.owner = owner; }

        // 讀這首歌的歌詞（第一次會在背景查，查完呼叫 loaded）。
        // 回傳歌詞按鈕的亮度：顯示中全白、有歌詞但沒開半亮、沒有歌詞很暗
        public double Load(NowPlaying.Track track, Action loaded)
        {
            lyrics = track == null ? null : Lyrics.Get(track, owner.Primary.Bounds.Height, loaded);
            key = track == null ? null : Lyrics.SongKey(track);
            return !Lyrics.Has(lyrics) ? 0.25 : on ? 1.0 : 0.6;
        }

        // 歌詞放在卡片的正上方
        public void Anchor(int top, int left) { cardTop = top; cardLeft = left; }

        // 按歌詞按鈕：有歌詞才開關（先讓歌詞開始淡入 / 淡出，按鈕的樣子由呼叫的人更新）；回傳有沒有切換
        public bool Toggle()
        {
            if (!Lyrics.Has(lyrics)) return false;
            on = !on;
            Update();
            return true;
        }

        // 播放引擎重建了：新引擎上還沒有歌詞的圖，要重新放上去
        public void Detach() { sheetName = null; }

        // 鎖定畫面收起來：下次打開時歌詞先不出現，等按了按鈕再顯示
        public void Close()
        {
            if (timer != null) timer.Stop();
            sheetName = null;
            sheetSong = null;
            on = false;
        }

        // 歌詞要不要顯示：要的話放上這首歌的長圖（淡入）、開計時器跟著播放進度捲動；
        // 關掉、換歌、Spotify 沒在播時，舊的長圖淡出
        public void Update()
        {
            var track = NowPlaying.Current;
            var primary = owner.Primary;
            bool show = owner.Active && owner.Engine != null && primary != null && on && Lyrics.SheetReady(lyrics, primary.Bounds.Height)
                && track != null && Lyrics.SongKey(track) == key;
            if (sheetName != null && (!show || sheetSong != key)) HideSheet();
            if (!show)
            {
                if (timer != null) timer.Stop();
                return;
            }
            if (sheetName == null) ShowSheet();
            if (timer == null)
            {
                // 只是看要不要換行，真正的捲動動畫由播放引擎跟著螢幕更新頻率畫
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                timer.Tick += delegate { Tick(); };
            }
            if (!timer.IsEnabled) timer.Start();
        }

        // 放上這首歌的長圖（背景已經畫好了），淡入
        void ShowSheet()
        {
            var primary = owner.Primary;
            double s = primary.Bounds.Height / 1080.0;
            sheetName = "lyrics" + (++sheetCount);
            sheetSong = key;
            sheetLyrics = lyrics;
            lineCenters = lyrics.Centers;
            lineHeights = lyrics.Heights;
            lineSpacing = lyrics.Spacing;
            viewHeight = (int)Math.Round(200 * s);   // 大約露出 5 行
            // 卡片正上方，文字左邊跟卡片的內距對齊（長圖四周有留給陰影的空間，要扣掉）
            int x = cardLeft + (int)LockClock.CardPadding(s) - LockClock.ShadowPad(primary.Bounds.Height);
            int y = cardTop - (int)Math.Round(12 * s) - viewHeight;
            shownLine = CurrentLine(NowPlaying.Current);
            var mask = LockClock.LyricsMask(viewHeight, lineSpacing, lyrics.Synced);
            owner.Engine.SetScrollOverlay(primary.Handle, sheetName, lyrics.Sheet, lyrics.SheetWidth, lyrics.SheetHeight, x, y, viewHeight, mask, ShadeFor(shownLine), ScrollFor(shownLine), 300);
        }

        void HideSheet()
        {
            if (owner.Engine != null && owner.Primary != null) owner.Engine.FadeOverlay(owner.Primary.Handle, sheetName, 0, 300, true);   // 淡出，淡完就拿掉
            sheetName = null;
            sheetSong = null;
        }

        void Tick()
        {
            if (sheetName == null || owner.Engine == null || owner.Primary == null) return;
            var track = NowPlaying.Current;
            if (track == null || Lyrics.SongKey(track) != sheetSong) return;
            int line = CurrentLine(track);
            if (line == shownLine) return;
            // 換行：平順地往上捲，亮的那行同時換過去；一次跳好幾行（拖曳進度）就捲快一點
            int ms = Math.Abs(line - shownLine) > 2 ? 250 : 420;
            shownLine = line;
            owner.Engine.ScrollOverlay(owner.Primary.Handle, sheetName, ScrollFor(line), ms, ShadeFor(line));
        }

        // 有時間標記的歌詞：唱到第 line 行時哪一行亮（整行一起亮，不管折成幾列）；一般歌詞不強調哪一行（null）
        float[] ShadeFor(int line)
        {
            return sheetLyrics.Synced ? LockClock.LyricsShade(sheetLyrics.SheetHeight, lineCenters, lineHeights, lineSpacing, line) : null;
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
    }
}
