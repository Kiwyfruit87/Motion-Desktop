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
        // 間奏的三個點（Apple Music 的樣子）：現在顯示在第幾行（-1 = 沒顯示）、那時播放中嗎，
        // 上次告訴播放引擎間奏過了幾秒、是什麼時候說的（拿來推算引擎現在畫到哪，跟播放位置差太多就重新對）
        int dotsLine = -1, dotsSentAt;
        bool dotsPlaying;
        double dotsElapsed;

        public LockLyrics(LockScreen owner) { this.owner = owner; }

        // 歌詞顯示時卡片的玻璃往上延伸多少（螢幕像素，把歌詞包進來；沒顯示是 0）
        public int Extension { get; private set; }

        // 讀這首歌的歌詞（第一次會在背景查，查完呼叫 loaded）。
        // 回傳歌詞按鈕的亮度：顯示中全白、有歌詞但沒開半亮、沒有歌詞很暗
        public double Load(NowPlaying.Track track, Action loaded)
        {
            lyrics = track == null ? null : Lyrics.Get(track, owner.Primary.Bounds.Height, loaded);
            key = track == null ? null : Lyrics.SongKey(track);
            if (Pending(track)) return 1.0;   // 換歌、新的歌詞還在查：按鈕先維持開著的樣子（查完沒有歌詞才變暗）
            return !Lyrics.Has(lyrics) ? 0.25 : on ? 1.0 : 0.6;
        }

        // 歌詞開著、這首歌的歌詞還在查（或長圖還在畫）：還不知道有沒有歌詞，先不要收起來
        bool Pending(NowPlaying.Track track)
        {
            return on && track != null && owner.Primary != null && (lyrics == null || (Lyrics.Has(lyrics) && !Lyrics.SheetReady(lyrics, owner.Primary.Bounds.Height)));
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
            Extension = 0;
        }

        // 歌詞要不要顯示：要的話放上這首歌的長圖（淡入）、開計時器跟著播放進度捲動；
        // 關掉、換歌、播放器沒在播時，舊的長圖淡出
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
                // 換歌時新的歌詞還在查：舊的歌詞淡出，玻璃先撐著不收（新歌有歌詞就直接換上去，沒有才收起來）
                if (!(owner.Active && owner.Engine != null && primary != null && Pending(track) && Lyrics.SongKey(track) == key)) Retract();
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

        // 歌詞不顯示了（關掉、換到沒有歌詞或還在查的歌、播放器換了）：卡片的玻璃縮回去
        void Retract()
        {
            if (Extension == 0) return;
            Extension = 0;
            if (owner.Engine != null && owner.Primary != null) owner.Engine.SetLiquidExtend(owner.Primary.Handle, 0);
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
            // 緊接在卡片內容（封面）的上面，下緣淡出的那段疊進卡片上方的內距，跟卡片接成一整塊
            int y = cardTop + (int)Math.Round(12 * s) - viewHeight;
            shownLine = CurrentLine(NowPlaying.Current);
            // 卡片的玻璃往上長，把歌詞包進來：歌詞本身（扣掉疊進卡片的 12）、上面再留 16（跟卡片的內距一樣）
            Extension = (int)Math.Round(viewHeight + 4 * s);
            owner.Engine.SetLiquidExtend(primary.Handle, Extension);
            var mask = LockClock.LyricsMask(viewHeight, lineSpacing, lyrics.Synced);
            owner.Engine.SetScrollOverlay(primary.Handle, sheetName, lyrics.Sheet, lyrics.SheetBlur, lyrics.SheetWidth, lyrics.SheetHeight, x, y, viewHeight, mask,
                ShadeFor(shownLine), BlurFor(shownLine), ScrollFor(shownLine), 300);
            dotsLine = -1;
            UpdateDots(NowPlaying.Current, shownLine);
        }

        void HideSheet()
        {
            if (owner.Engine != null && owner.Primary != null) owner.Engine.FadeOverlay(owner.Primary.Handle, sheetName, 0, 300, true);   // 淡出，淡完就拿掉
            sheetName = null;
            sheetSong = null;
            dotsLine = -1;
        }

        void Tick()
        {
            if (sheetName == null || owner.Engine == null || owner.Primary == null) return;
            var track = NowPlaying.Current;
            if (track == null || Lyrics.SongKey(track) != sheetSong) return;
            int line = CurrentLine(track);
            if (line != shownLine)
            {
                // 換行：平順地往上捲，亮的那行（和糊的程度）同時換過去；一次跳好幾行（拖曳進度）就捲快一點
                int ms = Math.Abs(line - shownLine) > 2 ? 250 : 420;
                shownLine = line;
                owner.Engine.ScrollOverlay(owner.Primary.Handle, sheetName, ScrollFor(line), ms, ShadeFor(line), BlurFor(line));
            }
            UpdateDots(track, line);
        }

        // 唱到間奏（空的那一行，3 秒以上；前奏也算，見 Lyrics）：在那一行畫三個點，跟著間奏的時間依序亮起來。
        // 換行、播放 / 暫停、或播放位置跟引擎正在畫的差超過 0.25 秒（拖進度條、在同一段間奏裡往回跳）時馬上告訴播放引擎
        void UpdateDots(NowPlaying.Track track, int line)
        {
            if (sheetName == null || track == null) return;
            var lines = sheetLyrics.Lines;
            double start = 0, end = 0;
            bool gap = sheetLyrics.Synced && line >= 0 && line < lines.Count && lines[line].Text.Length == 0;
            if (gap)
            {
                start = lines[line].Time;
                end = line + 1 < lines.Count ? lines[line + 1].Time : track.Duration.TotalSeconds;
                gap = end - start >= 3;
            }
            var primary = owner.Primary;
            if (!gap)
            {
                if (dotsLine >= 0) owner.Engine.SetLyricsDots(primary.Handle, sheetName, false, 0, 0, false, 0, 0, 0);
                dotsLine = -1;
                return;
            }
            double elapsed = NowPlaying.PositionSeconds(track) - start;
            double drawing = dotsElapsed + (dotsPlaying ? (Environment.TickCount - dotsSentAt) / 1000.0 : 0);   // 引擎現在畫到間奏的第幾秒
            if (line == dotsLine && track.Playing == dotsPlaying && Math.Abs(elapsed - drawing) < 0.25) return;
            dotsLine = line; dotsPlaying = track.Playing; dotsElapsed = elapsed; dotsSentAt = Environment.TickCount;
            double s = primary.Bounds.Height / 1080.0;
            owner.Engine.SetLyricsDots(primary.Handle, sheetName, true, elapsed, end - start, track.Playing,
                LockClock.ShadowPad(primary.Bounds.Height), 10 * s, 7 * s);
        }

        // 有時間標記的歌詞：唱到第 line 行時哪一行亮（整行一起亮，不管折成幾列）；一般歌詞不強調哪一行（null）
        float[] ShadeFor(int line)
        {
            return sheetLyrics.Synced ? LockClock.LyricsShade(sheetLyrics.SheetHeight, lineCenters, lineHeights, lineSpacing, line) : null;
        }

        // 同上：唱到第 line 行時長圖每一列要糊多少（離目前這行越遠越糊）；一般歌詞不糊（null）
        float[] BlurFor(int line)
        {
            return sheetLyrics.Synced ? LockClock.LyricsBlur(sheetLyrics.SheetHeight, lineCenters, lineHeights, lineSpacing, line) : null;
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
            return Math.Round(center - viewHeight / 2.0);   // 取整數：停下時字剛好對齊像素才不會糊（行的中心是小數的行高累加出來的）
        }
    }
}
