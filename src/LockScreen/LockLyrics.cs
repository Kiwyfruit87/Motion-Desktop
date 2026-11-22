using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace VideoWallpaper
{
    // 鎖定畫面音樂卡片上方的歌詞：按了歌詞按鈕才顯示（換歌也繼續顯示），整首歌一張長圖，跟著播放進度一行一行捲動
    partial class LockLyrics
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
            lyrics = track == null ? null : Lyrics.Get(track, owner.LyricsHeight, loaded, owner.LyricsWidth);
            key = track == null ? null : Lyrics.SongKey(track);
            // 歌詞開著：另一種大小（放大 / 一般）的長圖也先在背景畫好，按放大鈕時就能馬上換過去
            if (on && Lyrics.Has(lyrics) && owner.OtherLyricsHeight > 0) Lyrics.Get(track, owner.OtherLyricsHeight, delegate { }, owner.OtherLyricsWidth);
            if (Pending(track)) return 1.0;   // 換歌、新的歌詞還在查：按鈕先維持開著的樣子（查完沒有歌詞才變暗）
            return !Lyrics.Has(lyrics) ? 0.25 : on ? 1.0 : 0.6;
        }

        // 歌詞開著、這首歌的歌詞還在查（或長圖還在畫）：還不知道有沒有歌詞，先不要收起來
        bool Pending(NowPlaying.Track track)
        {
            return on && track != null && owner.Primary != null && (lyrics == null || (Lyrics.Has(lyrics) && !Lyrics.SheetReady(lyrics, owner.LyricsHeight)));
        }

        // 歌詞放在卡片的正上方
        public void Anchor(int top, int left) { cardTop = top; cardLeft = left; }

        // 歌詞開著沒、能不能開關（這首有歌詞）
        public bool On { get { return on; } }
        public bool CanToggle { get { return Lyrics.Has(lyrics); } }
        // 這首有歌詞、或還在查（放大歌詞時，換到沒有歌詞的歌就縮回來）
        public bool Available { get { return lyrics == null || Lyrics.Has(lyrics); } }

        // 按歌詞按鈕：有歌詞才開關（先讓歌詞開始淡入 / 淡出，按鈕的樣子由呼叫的人更新）；回傳有沒有切換
        public bool Toggle()
        {
            if (!Lyrics.Has(lyrics)) return false;
            on = !on;
            Update();
            return true;
        }

        // 播放引擎重建了：新引擎上還沒有歌詞的圖，要重新放上去
        public void Detach() { sheetName = null; zoomShown = false; manual = false; sweepLine = -1; syncShown = false; syncRect = Rect.Empty; }

        // 放大 / 縮回歌詞（卡片換大小）：畫面上的歌詞、放大鈕馬上拿掉（不淡出），下一次 Update 照新的大小直接放上去（不淡入、玻璃直接到新的高度）。
        // 換大小的動畫由播放引擎整塊做（見 VideoEngine.ZoomCard）
        public void Switch()
        {
            instant = sheetName != null;   // 歌詞本來就顯示著才直接換上去；關著的（例如關掉歌詞才縮回來）之後打開時照常長上去、淡入
            if (owner.Engine != null && owner.Primary != null)
            {
                if (sheetName != null) owner.Engine.RemoveOverlay(owner.Primary.Handle, sheetName);
                if (zoomShown) owner.Engine.RemoveOverlay(owner.Primary.Handle, ZoomName);
            }
            ResetManual(true);
            sheetName = null;
            sheetSong = null;
            zoomShown = false;
            dotsLine = -1;
            sweepLine = -1;
        }

        bool instant;      // 下一次放上歌詞時不淡入（見 Switch）

        // ---------- 放大歌詞時：滾輪自己捲、點某一行跳過去 ----------
        // 自己捲過之後歌詞不跟著播放捲（亮的那行照樣換），歌詞下面出現「同步歌詞」，按了（或 10 秒沒捲）就回去跟著播放
        bool manual;
        double manualScroll;          // 自己捲到哪（長圖的第幾列在露出那段的最上面）
        int manualAt;                 // 上次捲的時間
        const int ManualIdleMs = 10000;
        int viewX, viewY, viewW;      // 露出那段在畫面上的位置（字的範圍，不含長圖四周留給陰影的空間）
        const string SyncName = "lyricsSync";
        bool syncShown;
        Rect syncRect = Rect.Empty;
        int[] syncPixels;             // 同步鈕的圖（照螢幕大小畫一次就好）
        int syncW, syncH, syncPad, syncFor;

        // 滾輪（delta：往上滾是正的，一格 120）：放大歌詞、歌詞顯示中才有用
        public void Wheel(int delta)
        {
            if (!owner.Big || sheetName == null || owner.Engine == null || owner.Primary == null || lineCenters == null || lineCenters.Length == 0) return;
            bool first = !manual;
            if (first) { manual = true; manualScroll = ScrollFor(shownLine); }
            manualAt = Environment.TickCount;
            double step = viewHeight * 0.12;   // 一格大約捲半行
            double min = ScrollFor(-1), max = ScrollFor(lineCenters.Length - 1);
            manualScroll = Math.Max(min, Math.Min(max, manualScroll - delta / 120.0 * step));
            // 剛開始自己捲：其他行不再糊（要看得清楚才能挑），亮的那行照舊
            if (first) owner.Engine.ScrollOverlay(owner.Primary.Handle, sheetName, Math.Round(manualScroll), 220, BrowseShade(shownLine), FlatBlur(), ZoomFor(shownLine));
            else owner.Engine.ScrollOverlay(owner.Primary.Handle, sheetName, Math.Round(manualScroll), 220);
            ShowSync(true);
        }

        // 回去跟著播放（按了同步鈕、點了某一行跳過去、或一陣子沒捲）：捲回現在唱的那行，其他行照舊糊
        public void Follow()
        {
            bool was = manual;
            manual = false;
            ShowSync(false);
            if (sheetName == null || owner.Engine == null || owner.Primary == null) return;
            int line = CurrentLine(NowPlaying.Current);
            if (!was && line == shownLine) return;
            shownLine = line;
            owner.Engine.ScrollOverlay(owner.Primary.Handle, sheetName, ScrollFor(line), 600, ShadeFor(line), BlurFor(line), ZoomFor(line));
            UpdateDots(NowPlaying.Current, line);
            UpdateSweep(NowPlaying.Current, line);
        }

        public bool SyncHit(int x, int y) { return syncShown && syncRect.Contains(x, y); }

        // 點在露出的歌詞那段裡面嗎（點在行跟行中間也算，放大歌詞時點了不縮回去）
        public bool InView(int x, int y)
        {
            return owner.Big && sheetName != null && x >= viewX && x <= viewX + viewW && y >= viewY && y <= viewY + viewHeight;
        }

        // 點到第幾行（有時間標記的歌詞才有）：time 是那行開始唱的時間；沒點到回傳 false
        public bool LineAt(int x, int y, out double time)
        {
            time = 0;
            if (!InView(x, y) || !sheetLyrics.Synced || lineCenters == null) return false;
            var track = NowPlaying.Current;
            if (track == null || Lyrics.SongKey(track) != sheetSong) return false;   // 剛換歌、畫面上還是舊歌的歌詞：不要把新歌跳到舊歌的時間
            double row = (manual ? Math.Round(manualScroll) : ScrollFor(shownLine)) + (y - viewY);
            int best = -1;
            double bestD = double.MaxValue;
            for (int j = 0; j < lineCenters.Length && j < sheetLyrics.Lines.Count; j++)
            {
                double d = Math.Abs(row - lineCenters[j]);
                if (d <= lineHeights[j] / 2 + lineSpacing / 2 && d < bestD) { best = j; bestD = d; }
            }
            if (best < 0) return false;
            time = sheetLyrics.Lines[best].Time + 0.05;   // 稍微晚一點點：播放器落點早一點也還是這一行
            return true;
        }

        // ---------- 放大歌詞時歌詞的顏色：白色參一點專輯的主要顏色（跟卡片玻璃、放大時背景的色調同一個顏色，見 LockClock.RenderMusic）----------
        const double TintMix = 0.32;
        double tintR = 1, tintG = 1, tintB = 1;          // 畫面上這張長圖乘上的顏色
        int[] tintedSheet, tintedBlur, tintedFrom;       // 乘好顏色的長圖（tintedFrom：是從哪張原圖做的）
        double tintedR, tintedG, tintedB;

        // 現在卡片的顏色換算成歌詞要乘的顏色（沒有卡片就是白）
        void TintNow(out double r, out double g, out double b)
        {
            r = g = b = 1;
            var card = owner.Card;
            if (!owner.Big || card == null || card.Spec == null) return;   // 一般卡片上的歌詞維持白色，放大歌詞才參封面的顏色
            var spec = card.Spec;
            // 只拿封面主色的色相，飽和度自己拉高（卡片的顏色是照封面的飽和度，常常只有 0.4 多，參進白色裡幾乎看不出來）
            double cr = spec.TintR, cg = spec.TintG, cb = spec.TintB;
            double max = Math.Max(cr, Math.Max(cg, cb)), min = Math.Min(cr, Math.Min(cg, cb)), d = max - min;
            if (d < 1e-3) return;   // 灰色：維持白
            double hue = max == cr ? 60 * ((cg - cb) / d) : max == cg ? 60 * ((cb - cr) / d + 2) : 60 * ((cr - cg) / d + 4);
            hue = (hue % 360 + 360) % 360;
            // 純色（飽和度約 0.75）：最亮的那個顏色是 1，最暗的約 0.15，中間的照色相
            double lo = 0.15, f = 1 - Math.Abs(hue / 60 % 2 - 1), mid = lo + (1 - lo) * f;
            double vr, vg, vb;
            if (hue < 60) { vr = 1; vg = mid; vb = lo; } else if (hue < 120) { vr = mid; vg = 1; vb = lo; } else if (hue < 180) { vr = lo; vg = 1; vb = mid; }
            else if (hue < 240) { vr = lo; vg = mid; vb = 1; } else if (hue < 300) { vr = mid; vg = lo; vb = 1; } else { vr = 1; vg = lo; vb = mid; }
            r = 1 - TintMix * (1 - vr);
            g = 1 - TintMix * (1 - vg);
            b = 1 - TintMix * (1 - vb);
        }

        // 長圖乘上顏色（同一張原圖、同一個顏色就用上次做好的）
        void Tint(Lyrics.Result r, double cr, double cg, double cb, out int[] sheet, out int[] blur)
        {
            if (cr >= 0.999 && cg >= 0.999 && cb >= 0.999) { sheet = r.Sheet; blur = r.SheetBlur; return; }
            if (tintedFrom != r.Sheet || cr != tintedR || cg != tintedG || cb != tintedB)
            {
                tintedSheet = Multiply(r.Sheet, cr, cg, cb);
                tintedBlur = Multiply(r.SheetBlur, cr, cg, cb);
                tintedFrom = r.Sheet; tintedR = cr; tintedG = cg; tintedB = cb;
            }
            sheet = tintedSheet; blur = tintedBlur;
        }

        // 每個像素的紅綠藍各乘上 (r, g, b)（預乘過透明度的，乘小於 1 的數還是對的；黑色的柔光不受影響）
        static int[] Multiply(int[] src, double r, double g, double b)
        {
            if (src == null) return null;
            var dst = new int[src.Length];
            int ir = (int)Math.Round(r * 256), ig = (int)Math.Round(g * 256), ib = (int)Math.Round(b * 256);
            int chunk = 1 << 16;
            System.Threading.Tasks.Parallel.For(0, (src.Length + chunk - 1) / chunk, k =>
            {
                int end = Math.Min(src.Length, (k + 1) * chunk);
                for (int i = k * chunk; i < end; i++)
                {
                    int p = src[i];
                    if (p == 0) continue;
                    int pb = (p & 0xFF) * ib >> 8, pg = ((p >> 8) & 0xFF) * ig >> 8, pr = ((p >> 16) & 0xFF) * ir >> 8;
                    dst[i] = (int)((uint)p & 0xFF000000) | pr << 16 | pg << 8 | pb;
                }
            });
            return dst;
        }

        // 自己捲歌詞時每一列的亮度：每一行都看得清楚（平常離正在唱的那行三、四行以上就看不到了），正在唱的那行最亮
        float[] BrowseShade(int line)
        {
            return sheetLyrics.Synced ? LockClock.LyricsBrowseShade(sheetLyrics.SheetHeight, lineCenters, lineHeights, lineSpacing, line) : null;
        }

        // 每一列都不糊（自己捲歌詞時）
        float[] FlatBlur() { return sheetLyrics.Synced ? new float[sheetLyrics.SheetHeight] : null; }

        // 歌詞下面的同步鈕：跟歌詞的字左邊對齊，淡入 / 淡出
        void ShowSync(bool show)
        {
            if (show == syncShown || owner.Engine == null || owner.Primary == null) return;
            syncShown = show;
            var handle = owner.Primary.Handle;
            if (!show)
            {
                owner.Engine.FadeOverlay(handle, SyncName, 0, 250, true);
                owner.Engine.SetSyncGlass(handle, false, 0, 0, 0, 0, 250);
                syncRect = Rect.Empty;
                return;
            }
            double s0 = owner.Primary.Bounds.Height / 1080.0;
            if (syncPixels == null || syncFor != owner.Primary.Bounds.Height)
            {
                syncPixels = LockClock.RenderSyncButton(1.1 * s0, out syncW, out syncH, out syncPad);
                syncFor = owner.Primary.Bounds.Height;
            }
            int x = viewX - syncPad, y = viewY + viewHeight + (int)Math.Round(28 * s0) - syncPad;
            owner.Engine.SetFadeOverlay(handle, SyncName, syncPixels, syncW, syncH, x, y, OverlayLayer.Lyrics, 200);
            syncRect = new Rect(x + syncPad, y + syncPad, syncW - 2 * syncPad, syncH - 2 * syncPad);
            owner.Engine.SetSyncGlass(handle, true, syncRect.X, syncRect.Y, syncRect.Width, syncRect.Height, 200);   // 膠囊本身是液態玻璃（引擎畫），圖示和字疊在上面
            syncRect.Inflate(6 * s0, 6 * s0);   // 點的範圍比膠囊大一點
        }

        // 換版面、收起來：不再自己捲，同步鈕拿掉（remove：直接拿掉，不淡出）
        void ResetManual(bool remove)
        {
            manual = false;
            if (syncShown && owner.Engine != null && owner.Primary != null)
            {
                if (remove) owner.Engine.RemoveOverlay(owner.Primary.Handle, SyncName);
                else owner.Engine.FadeOverlay(owner.Primary.Handle, SyncName, 0, 250, true);
                owner.Engine.SetSyncGlass(owner.Primary.Handle, false, 0, 0, 0, 0, remove ? 0 : 250);
            }
            syncShown = false;
            syncRect = Rect.Empty;
        }

        // 放大鈕（歌詞區的右上角，歌詞顯示時才有）：畫面上的位置（點的範圍），沒顯示時是空的
        const string ZoomName = "lyricsZoom";
        bool zoomShown;
        Rect zoomRect = Rect.Empty;
        public bool ZoomHit(int x, int y) { return zoomShown && zoomRect.Contains(x, y); }

        // 鎖定畫面收起來：下次打開時歌詞先不出現，等按了按鈕再顯示
        public void Close()
        {
            if (timer != null) timer.Stop();
            ResetManual(true);
            sheetName = null;
            sheetSong = null;
            zoomShown = false;
            instant = false;
            on = false;
            Extension = 0;
        }

        // 歌詞要不要顯示：要的話放上這首歌的長圖（淡入）、開計時器跟著播放進度捲動；
        // 關掉、換歌、播放器沒在播時，舊的長圖淡出
        public void Update()
        {
            var track = NowPlaying.Current;
            var primary = owner.Primary;
            bool show = owner.Active && owner.Engine != null && primary != null && on && Lyrics.SheetReady(lyrics, owner.LyricsHeight)
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
            // 放大歌詞中關掉歌詞：卡片接著就整塊縮回去（見 LockScreen.SetBig），玻璃直接收起來，不要兩個動畫疊在一起
            if (owner.Engine != null && owner.Primary != null) owner.Engine.SetLiquidExtend(owner.Primary.Handle, 0, owner.Big);
        }

        // 放上這首歌的長圖（背景已經畫好了），淡入
        void ShowSheet()
        {
            Pronounce.Preload();   // 逐字亮估算用的發音字典：先在背景讀
            var primary = owner.Primary;
            int height = owner.LyricsHeight;   // 歌詞照這個高度的螢幕畫（放大時比螢幕高，字跟著放大）
            double s = height / 1080.0;
            int fade = instant ? 0 : 300;
            instant = false;
            sheetName = "lyrics" + (++sheetCount);
            sheetSong = key;
            sheetLyrics = lyrics;
            lineCenters = lyrics.Centers;
            lineHeights = lyrics.Heights;
            lineSpacing = lyrics.Spacing;
            // 卡片的縮放（放大歌詞時卡片上的東西沒有歌詞放得那麼大）：內距、疊進卡片的那段照卡片的
            double sc = owner.Card != null ? owner.Card.Scale : s;
            int x, y;
            if (owner.Big)
            {
                // 放大歌詞：沒有框，直接排在桌布上（位置見 LockClock.BigLyricsLeft），卡片（底部的膠囊）不用往上長
                double s0 = primary.Bounds.Height / 1080.0;
                viewHeight = (int)Math.Round(LockClock.BigViewH * s0);
                x = (int)Math.Round(LockClock.BigLyricsLeft * s0) - LockClock.ShadowPad(height);
                y = (int)Math.Round(LockClock.BigViewTop * s0);
                Extension = 0;
            }
            else
            {
                viewHeight = LockClock.LyricsView(s);   // 大約露出 5 行
                // 卡片正上方，文字左邊跟卡片的內距對齊（長圖四周有留給陰影的空間，要扣掉）
                x = cardLeft + (int)LockClock.CardPadding(sc) - LockClock.ShadowPad(height);
                // 緊接在卡片內容（封面）的上面，下緣淡出的那段疊進卡片上方的內距，跟卡片接成一整塊
                y = cardTop + (int)Math.Round(12 * sc) - viewHeight;
                // 卡片的玻璃往上長，把歌詞包進來：歌詞本身（扣掉疊進卡片的 12）、上面再留 16（跟卡片的內距一樣）
                Extension = LockClock.LyricsExtension(s, sc);
            }
            int pad = LockClock.ShadowPad(height);
            viewX = x + pad; viewY = y; viewW = lyrics.SheetWidth - 2 * pad;
            ResetManual(true);
            shownLine = CurrentLine(NowPlaying.Current);
            owner.Engine.SetLiquidExtend(primary.Handle, Extension, fade == 0 || owner.Big);
            var mask = LockClock.LyricsMask(viewHeight, lineSpacing, lyrics.Synced);
            // 每一行的大小：照那行的中心、字的左邊（長圖四周留給陰影的空間）縮放
            var anchors = lyrics.Synced ? LockClock.LyricsAnchors(lyrics.SheetHeight, lineCenters, lineHeights) : null;
            int[] sheetPixels, blurPixels;
            TintNow(out tintR, out tintG, out tintB);
            Tint(lyrics, tintR, tintG, tintB, out sheetPixels, out blurPixels);
            owner.Engine.SetScrollOverlay(primary.Handle, sheetName, sheetPixels, blurPixels, lyrics.SheetWidth, lyrics.SheetHeight, x, y, viewHeight, mask,
                ShadeFor(shownLine), BlurFor(shownLine), ZoomFor(shownLine), anchors, LockClock.ShadowPad(height), ScrollFor(shownLine), fade);
            dotsLine = -1;
            UpdateDots(NowPlaying.Current, shownLine);
            sweepLine = -1;
            UpdateSweep(NowPlaying.Current, shownLine);
            ShowZoomButton(sc, fade);
        }

        // 放大鈕：歌詞區的右上角（跟卡片上的歌詞圓鈕同一條直線），歌詞淡入時一起淡入；放大時畫成縮回來的樣子。s 是卡片的縮放
        void ShowZoomButton(double s, int fade)
        {
            var card = owner.Card;
            if (card == null) return;
            int size;
            double cx, cy;
            if (owner.Big)
            {
                // 放大歌詞：畫面右上角（跟播放控制一起，滑鼠不動就淡出，見 LockScreen.ShowControls）
                double s0 = owner.Primary.Bounds.Height / 1080.0;
                s = 1.3 * s0;
                cx = owner.Primary.Bounds.Width - 64 * s0; cy = 64 * s0;
            }
            else
            {
                cx = cardLeft + card.InnerW - 30 * s; cy = cardTop - Extension + 30 * s;
            }
            var pixels = LockClock.RenderZoomButton(s, owner.Big, out size);
            int x = (int)Math.Round(cx - size / 2.0), y = (int)Math.Round(cy - size / 2.0);
            // 放大歌詞時跟播放控制同一層（滑鼠不動就一起淡出）
            owner.Engine.SetFadeOverlay(owner.Primary.Handle, ZoomName, pixels, size, size, x, y, owner.Big ? OverlayLayer.CardContent : OverlayLayer.Lyrics, fade);
            double r = 18 * s;   // 點的範圍比圓大一點
            zoomRect = new Rect(cx - r, cy - r, 2 * r, 2 * r);
            zoomShown = true;
        }

        void HideSheet()
        {
            if (owner.Engine != null && owner.Primary != null)
            {
                owner.Engine.FadeOverlay(owner.Primary.Handle, sheetName, 0, 300, true);   // 淡出，淡完就拿掉
                if (zoomShown) owner.Engine.FadeOverlay(owner.Primary.Handle, ZoomName, 0, 300, true);
            }
            ResetManual(false);
            zoomShown = false;
            sheetName = null;
            sheetSong = null;
            dotsLine = -1;
            sweepLine = -1;
        }

        void Tick()
        {
            if (sheetName == null || owner.Engine == null || owner.Primary == null) return;
            var track = NowPlaying.Current;
            if (track == null || Lyrics.SongKey(track) != sheetSong) return;
            // 封面比歌詞晚到（卡片的顏色變了）：照新的顏色直接換上去（不淡入）
            double cr, cg, cb;
            TintNow(out cr, out cg, out cb);
            if (owner.Card != null && (Math.Abs(cr - tintR) > 0.01 || Math.Abs(cg - tintG) > 0.01 || Math.Abs(cb - tintB) > 0.01))
            {
                string old = sheetName;
                sheetName = null;
                instant = true;
                ShowSheet();
                owner.Engine.RemoveOverlay(owner.Primary.Handle, old);
                return;
            }
            if (manual &&Environment.TickCount - manualAt > ManualIdleMs) Follow();   // 一陣子沒捲：回去跟著播放
            int line = CurrentLine(track);
            if (line != shownLine && manual)
            {
                // 自己捲著：停在捲到的地方，只換亮的那行（其他行照樣不糊）
                shownLine = line;
                owner.Engine.ScrollOverlay(owner.Primary.Handle, sheetName, Math.Round(manualScroll), 420, BrowseShade(line), FlatBlur(), ZoomFor(line));
            }
            else if (line != shownLine)
            {
                // 換行：平順地往上捲，亮的那行（和糊的程度）同時換過去。
                // 一次跳好幾行（放開進度條、播放器跳轉）：慢慢地滑過去再停下（0.6 秒），不是一下子跳到；
                // 正在拖進度條：歌詞稍微落後手指、緩緩地跟上（0.45 秒；還沒捲完又換行時從現在的位置接著捲），不會一格一格卡住
                int ms = owner.Scrubbing ? 450 : Math.Abs(line - shownLine) > 2 ? 600 : 420;
                shownLine = line;
                owner.Engine.ScrollOverlay(owner.Primary.Handle, sheetName, ScrollFor(line), ms, ShadeFor(line), BlurFor(line), ZoomFor(line));
            }
            UpdateDots(track, line);
            UpdateSweep(track, line);
            ModelTick(track);
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
            double elapsed = owner.SongPosition(track) - start;
            bool running = track.Playing && !owner.Scrubbing;   // 拖進度條時三個點停在拖到的地方
            double drawing = dotsElapsed + (dotsPlaying ? (Environment.TickCount - dotsSentAt) / 1000.0 : 0);   // 引擎現在畫到間奏的第幾秒
            if (line == dotsLine && running == dotsPlaying && Math.Abs(elapsed - drawing) < 0.25) return;
            dotsLine = line; dotsPlaying = running; dotsElapsed = elapsed; dotsSentAt = Environment.TickCount;
            int height = owner.LyricsHeight;
            double s = height / 1080.0 * (owner.Big ? 0.6 : 1);   // 放大時點不用跟字一樣放那麼大（太搶眼）
            owner.Engine.SetLyricsDots(primary.Handle, sheetName, true, elapsed, end - start, running,
                LockClock.ShadowPad(height), 10 * s, 7 * s);
        }

        // ---------- 逐字亮（放大歌詞）：正在唱的那一行唱到哪亮到哪 ----------
        // 歌詞只有每一行開始的時間：從這行開始到下一行之間，照每個字的長短（英文照字母數、中日韓一個字一個音節）把時間分下去，
        // 估算每個字什麼時候唱；字的位置是畫長圖時量好的（Lyrics.Result.Glyphs）
        const double SweepDim = 0.5;   // 還沒唱到的字多亮（跟下一行一樣亮）
        int sweepLine = -1, sweepSentAt;
        bool sweepPlaying;
        double sweepElapsed;
        float[] sweepCells;

        void UpdateSweep(NowPlaying.Track track, int line)
        {
            if (sheetName == null || track == null) return;
            var glyphs = sheetLyrics.Glyphs;
            var lines = sheetLyrics.Lines;
            bool on = owner.Big && sheetLyrics.Synced && glyphs != null && line >= 0 && line < lines.Count && line < glyphs.Length
                && glyphs[line] != null && lines[line].Text.Trim().Length > 0;
            var handle = owner.Primary.Handle;
            if (!on)
            {
                if (sweepLine >= 0) owner.Engine.SetLyricsSweep(handle, sheetName, null, null, 0, false, 0);
                sweepLine = -1;
                return;
            }
            double pos = owner.SongPosition(track), length = track.Duration.TotalSeconds;
            bool changed = false;
            if (line != sweepLine)
            {
                sweepCells = SweepCells(line, length);
                holdState = 0; holdVotes = 0;
                ResetPace();
                changed = true;
            }
            // 每一行照歌詞標記的時間開始掃（不另外猜整首歌的偏差：試過用聽到的開口推算，常常判斷得偏晚，鼓重的歌每句都落後）
            bool voice = owner.Big && track.Playing && !owner.Scrubbing;
            if (changed) lineStart = lines[line].Time;
            double elapsed = pos - lineStart;
            bool running = track.Playing && !owner.Scrubbing;
            if (running && voice)
            {
                if (!sweepModel && VocalPace(elapsed, line, length)) changed = true;      // 句中的停頓、長音（校準過的行不用）
                if (AdjustHold(elapsed, line, length)) changed = true;     // 句尾的長音
            }
            else { ResetPaceVotes(); if (owner.Scrubbing) alignNext = int.MaxValue; }   // 拖進度條：這一行不再對（聽到的開口對不上了）
            double drawing = sweepElapsed + (sweepPlaying ? (Environment.TickCount - sweepSentAt) / 1000.0 : 0);   // 引擎現在畫到這行的第幾秒
            // 跟引擎畫的差一點點不用重新對（每次重對都會跳一下）：落後 0.25 秒以上、或超前 0.5 秒以上（播放器往回修正、往回跳）才對
            if (!changed && running == sweepPlaying && elapsed - drawing < 0.25 && drawing - elapsed < 0.5) return;
            sweepLine = line; sweepPlaying = running; sweepElapsed = elapsed; sweepSentAt = Environment.TickCount;
            owner.Engine.SetLyricsSweep(handle, sheetName, glyphs[line].Rows, sweepCells, elapsed, running, SweepDim);
        }

        // 一行拆成一個一個「字」，每個字的份量（1 大約是一個音節）、拉長的倍數（多出來的時間給母音）、後面要不要停一下（逗號）
        class Words
        {
            public List<int> Starts = new List<int>(), Ends = new List<int>();
            public List<double> Weights = new List<double>(), Holds = new List<double>(), Pauses = new List<double>();
            public double Units;   // 份量加起來
        }

        // 下面這些份量、倍數拿 68 首英文歌的逐字時間調過（一半調、一半驗證：平均誤差 0.378 → 0.330 秒），見 E:w-tests	ools	extmodel.py
        // 字跟字之間、逗號後面停多久（秒）。逗號：拿 11 首已校準歌曲裡模型很確定的字量的，逗號後實際比估算再晚 0.05～0.1 秒最準
        // （0.1 → 0.15；調其他份量的 68 首歌的逐字時間幾乎沒有逗號，當初的 0.1 沒有資料可以調）
        const double WordGap = 0, CommaPause = 0.15;
        const double MaxPace = 0.4, MaxPaceCjk = 0.6;    // 一個份量最多唱幾秒（見 SweepCells；中日韓沒有拿資料調過，照原本的）
        const double MinWord = 0.07;                     // 比這短的字不自己掃一段（見 SweepCells）

        static Words SplitWords(string text)
        {
            var w = new Words();
            bool japanese = false;   // 有假名的就是日文：漢字照「拍」算（一個漢字大約 1.8 拍）
            foreach (char c in text) if (c >= 0x3040 && c <= 0x30FF) { japanese = true; break; }
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                double cjk = Syllable(c, japanese);
                if (cjk > 0) { w.Starts.Add(i); w.Ends.Add(i + 1); w.Weights.Add(cjk); i++; continue; }
                if (char.IsLetterOrDigit(c))
                {
                    int a = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '\'' || text[i] == '’' || char.IsSurrogate(text[i])) && Syllable(text[i], japanese) == 0
                        && !(text[i] >= 0x3040 && text[i] <= 0x30FF)) i++;
                    if (i == a) { i++; continue; }
                    w.Starts.Add(a); w.Ends.Add(i); w.Weights.Add(WordWeight(text.Substring(a, i - a)));
                    continue;
                }
                i++;
            }
            int n = w.Starts.Count;
            for (int k = 0; k < n; k++)
            {
                string word = text.Substring(w.Starts[k], w.Ends[k] - w.Starts[k]);
                // 後面接著逗號、破折號（一句裡的小段落）、或是這行最後一個字：句尾通常拖長
                int after = w.Ends[k];
                while (after < text.Length && text[after] == ' ') after++;
                bool comma = after < text.Length && ",，、;；-—–…".IndexOf(text[after]) >= 0 && k < n - 1;
                bool last = k == n - 1;
                double hold = Held(word, last || comma);
                double scale = Function(word) ? 0.8 : 1;    // the、a、to、I、的、了…唱得短一點
                if (last) scale = Math.Max(scale, 1);         // 句尾的字不另外拉長（真的拖長音由聽到的聲音決定，見 AdjustHold）
                w.Weights[k] *= Math.Min(4, scale * hold);   // 句尾又是拉長音的字（yeah × 3 × 1.6）：最多 4 倍，不要吃掉整行
                w.Holds.Add(hold);
                // 字跟字之間停一下；虛字前後、常見的連音（want to、give me、I am…）連著唱，不停
                string nextWord = last ? null : text.Substring(w.Starts[k + 1], w.Ends[k + 1] - w.Starts[k + 1]);
                bool linked = !last && !comma && (Function(word) || Function(nextWord) || Linked(word, nextWord));
                w.Pauses.Add(last ? 0 : comma ? CommaPause : linked ? 0 : WordGap);
                w.Units += w.Weights[k];
            }
            return w;
        }

        // 一行裡每個字估計唱多久（秒；字跟字之間的停頓在 w.Pauses）。沒有字回傳 null
        static double[] WordLengths(string text, double available, out Words w)
        {
            w = SplitWords(text);
            int n = w.Starts.Count;
            if (n == 0 || w.Units <= 0) return null;
            // 這行唱多久：能用的時間的八成（最後一個字唱完、留兩成換氣）。拿 11 首英文歌的逐字時間量過：
            // 最後一個字大約在六成的地方開始、拖到八成多；照整首歌的速度推算反而比較不準（每一行快慢差很多）。
            // 一個份量最多唱 0.6 秒（後面接長間奏的行不會慢慢拖到間奏裡）
            double pauses = 0;
            foreach (double p in w.Pauses) pauses += p;
            bool cjk = false;
            foreach (char ch in text) if (Syllable(ch, true) > 0) { cjk = true; break; }
            double sing = Math.Min(Math.Min(available - 0.05, 0.8 * available), w.Units * (cjk ? MaxPaceCjk : MaxPace) + pauses);
            double scale = Math.Max(0.1, sing - pauses) / w.Units;   // 一個份量幾秒
            var len = new double[n];
            for (int k = 0; k < n; k++) len[k] = w.Weights[k] * scale;
            return len;
        }

        // 校準結果（src\Private 才有）：把這一行每個字的長度、停頓改成聽到的時間，改了 used = true
        partial void ModelLengths(int line, Words w, double[] len, double available, ref bool used);
        // 歌詞顯示中每 50 毫秒一次（src\Private 才有：讀校準結果）
        partial void ModelTick(NowPlaying.Track track);
        bool sweepModel;   // 這一行用的是校準過的時間（不再聽開口對，見 VocalPace）

        // 第 line 行每個字的位置和唱的時間（五個一組：列、左、右、開始、結束；秒，從這行開始算）
        float[] SweepCells(int line, double songLength)
        {
            var lines = sheetLyrics.Lines;
            var g = sheetLyrics.Glyphs[line];
            string text = lines[line].Text;
            double next = line + 1 < lines.Count ? lines[line + 1].Time : songLength;
            double available = next - lines[line].Time;
            if (available <= 0.05) available = 3;   // 時間標記怪怪的：當作 3 秒
            Words w;
            var len = WordLengths(text, available, out w);
            groupFrom.Clear(); groupTo.Clear(); groupComma.Clear();
            sweepModel = false;
            if (len == null) return null;
            int n = len.Length;
            ModelLengths(line, w, len, available, ref sweepModel);   // 有這首歌的校準結果（只在自己的電腦上，見 src\Private）：照聽到的時間
            // 太短的字（不到 0.07 秒，眼睛看不出它自己掃過去）不自己一段：跟前一個字併成一段一起掃（第一個字就跟下一個字），中間不停
            var groupEnd = new List<int>();   // 每一段最後一個字是第幾個
            double groupLen = 0;
            for (int k = 0; k < n; k++)
            {
                bool join = groupEnd.Count > 0 && w.Pauses[k - 1] < CommaPause && (len[k] < MinWord || groupLen < MinWord);
                if (join) { groupEnd[groupEnd.Count - 1] = k; groupLen += len[k]; }
                else { groupEnd.Add(k); groupLen = len[k]; }
            }
            var cells = new List<float>();
            double t = 0;
            int prevEnd = 0, first = 0;
            var lw = LetterWeights(text, w);
            foreach (int last in groupEnd)
            {
                if (last == n - 1) { sweepLastFrom = cells.Count; sweepLastT0 = t; }   // 最後一段（長音結束的偵測只改它，見 AdjustHold）
                // 這一段前面的空白、標點：跟前一段唱完時一起亮
                for (int p = prevEnd; p < w.Starts[first]; p++) AddCell(cells, g, p, t, t);
                groupFrom.Add(cells.Count);
                groupComma.Add(first > 0 && w.Pauses[first - 1] >= CommaPause);
                // 段裡面照讀音：母音那段慢、子音那段快；拉長的字多出來的時間都給母音（段裡字跟字之間的空白也算一點點）
                double span = 0, total = 0;
                for (int k = first; k <= last; k++) span += len[k] + (k < last ? w.Pauses[k] : 0);
                for (int k = first; k <= last; k++)
                    for (int p = w.Starts[k]; p < (k < last ? w.Starts[k + 1] : w.Ends[k]); p++) total += lw[p];
                double at = t;
                for (int k = first; k <= last; k++)
                    for (int p = w.Starts[k]; p < (k < last ? w.Starts[k + 1] : w.Ends[k]); p++)
                    {
                        double d = total > 0 ? span * lw[p] / total : 0;
                        AddCell(cells, g, p, at, at + d);
                        at += d;
                    }
                groupTo.Add(cells.Count);
                t += span + w.Pauses[last];
                prevEnd = w.Ends[last];
                first = last + 1;
            }
            sweepLastT1 = t - w.Pauses[n - 1];
            for (int p = prevEnd; p < text.Length; p++) AddCell(cells, g, p, sweepLastT1, sweepLastT1);
            return cells.Count > 0 ? cells.ToArray() : null;
        }

        int sweepLastFrom;                 // 最後一段（最後一個字）在 sweepCells 裡從第幾個數字開始
        double sweepLastT0, sweepLastT1;   // 最後一段開始、結束唱的時間（秒，從這行開始算；長音偵測會改結束的時間）

        // ---------- 聽人聲（Beat.Vocal.cs）----------
        // 1. 每個字對準聽到的開口：見 VocalPace。
        // 2. 長音：掃到最後一個字時人聲還很大聲 → 繼續拉長（最多到下一行之前）；人聲已經停了 → 很快掃完（AdjustHold）。
        // 聽不出來（太吵、太小聲、資料不夠）一律照原本估的
        int holdState, holdVotes;   // 最後一個字：0 還沒動、1 拉長過、2 已經決定快速掃完（之後不再改）；連續幾次同一個判斷
        double lineStart;                   // 這一行從歌曲的第幾秒開始掃
        readonly long[] vocalTimes = new long[1000];
        readonly float[] vocalLevels = new float[1000];

        // 最近的人聲：一共幾筆、平常安靜的時候多大聲（最小的兩成）、唱歌的時候多大聲（最大的一成）；資料不夠回傳 0
        int VocalRecent(out double quiet, out double loud)
        {
            quiet = loud = 0;
            int n = Beat.VocalSince(AnimationClock.Now - 8000, vocalTimes, vocalLevels);
            if (n < 200) return 0;   // 不到 2 秒
            if (AnimationClock.Now - vocalTimes[n - 1] > 200) return 0;   // 最近沒有聲音進來（暫停、斷了）
            var sorted = new float[n];
            Array.Copy(vocalLevels, sorted, n);
            Array.Sort(sorted);
            quiet = sorted[n / 5]; loud = sorted[n * 9 / 10];
            if (loud - quiet < 6) return 0;   // 幾乎沒有起伏：分不出唱跟沒唱
            return n;
        }

        // 歌曲第幾秒 ↔ 播放引擎的時間：現在是歌曲的 pos 秒
        static double SongTime(long clock, double pos) { return pos - (AnimationClock.Now - clock) / 1000.0; }

        // 最近 150 毫秒的人聲有多大（0 = 平常安靜時、1 = 唱歌時）；聽不出來回傳 NaN
        double VocalActivity(double pos)
        {
            double quiet, loud;
            int n = VocalRecent(out quiet, out loud);
            if (n < 15) return double.NaN;
            double sum = 0;
            for (int k = n - 15; k < n; k++) sum += vocalLevels[k];
            return Math.Max(0, Math.Min(1, (sum / 15 - quiet) / (loud - quiet)));
        }

        // 最後一個字的長音：還在唱就拉長、停了就很快掃完；有改回傳 true（sweepCells 換成新的一份，引擎手上那份不能動）
        bool AdjustHold(double elapsed, int line, double songLength)
        {
            if (sweepCells == null || holdState == 2 || sweepLastT1 <= sweepLastT0 || elapsed < sweepLastT0 + 0.1 || elapsed >= sweepLastT1) return false;
            double act = VocalActivity(0);
            if (double.IsNaN(act)) { holdVotes = 0; return false; }
            double maxEnd = MaxEnd(line, songLength);
            double end = sweepLastT1;
            int vote = 0;
            if (act > 0.55 && elapsed > sweepLastT1 - 0.25 && sweepLastT1 < maxEnd) vote = 1;   // 快掃完了，人聲還很大聲：拉長
            // 人聲已經停了：很快掃完。比較保守（判斷錯會在還在唱的時候整個字先亮完）：這個字估的時間過了一半以後才看
            else if (act < 0.2 && elapsed > (sweepLastT0 + sweepLastT1) / 2 && elapsed < sweepLastT1 - 0.1) vote = -1;
            // 連續好幾次（拉長 0.1 秒、掃完 0.15 秒）同一個判斷才算（人聲忽大忽小時不會一下拉長、一下縮短）
            holdVotes = vote == 0 ? 0 : Math.Sign(holdVotes) == vote ? holdVotes + vote : vote;
            if (holdVotes < 2 && holdVotes > -3) return false;
            if (vote > 0) { end = Math.Min(maxEnd, elapsed + 0.4); holdState = 1; }
            else { end = elapsed + 0.08; holdState = 2; }
            holdVotes = 0;
            if (Math.Abs(end - sweepLastT1) < 0.02) return false;
            // 只改還沒掃到的那部分（以現在為支點），已經亮的不會退回去
            var cells = (float[])sweepCells.Clone();
            Stretch(cells, sweepLastFrom, cells.Length, elapsed, sweepLastT1, end);
            UseCells(cells);
            return true;
        }

        // 唱得很短的虛字（英文的冠詞、介系詞、代名詞；中文的助詞）
        static readonly HashSet<string> FunctionWords = new HashSet<string>
        {
            "the", "a", "an", "to", "of", "and", "in", "on", "at", "for", "i", "im", "you", "my", "me", "it", "its", "is", "be", "we", "he", "she",
            "your", "but", "or", "as", "by", "with", "that", "this", "if", "so", "do", "dont", "can", "just", "all", "up", "da", "ya", "gonna", "wanna",
        };
        static readonly string FunctionChars = "的了著着地得之嗎嘛呢吧";

        // 常見的連音（兩個字幾乎唱成一串音）
        static readonly HashSet<string> LinkedPairs = new HashSet<string>
        {
            "want to", "going to", "got to", "have to", "has to", "need to", "used to", "ought to", "give me", "let me", "tell me", "show me", "take me",
            "i am", "you are", "we are", "they are", "it is", "kind of", "sort of", "out of", "a lot", "all of", "one of",
        };

        static bool Linked(string a, string b)
        {
            return LinkedPairs.Contains(a.ToLowerInvariant() + " " + b.ToLowerInvariant());
        }

        static bool Function(string word)
        {
            if (word.Length == 1 && FunctionChars.IndexOf(word[0]) >= 0) return true;
            return FunctionWords.Contains(word.ToLowerInvariant().Replace("'", "").Replace("’", ""));
        }

        // ---------- 聽人聲對齊一行裡的每個字：照文字估的時間往前走，每一段字開始前後找「開口」（Beat.Vocal 的開口強度突然變大的峰值）----------
        // 下一段字預計 p 秒開始：p 前 0.3 秒到 p 後 0.1 秒之間聽到開口 → 這段改從聽到的那一刻開始（早了就讓前一段快點掃完、晚了前一段掃完稍等一下），
        // 後面的字整串跟著挪；都沒聽到 → 等滿 0.1 秒照樣開始。整行最多偏離原本估的 0.6 秒，一定在下一行之前唱完。
        // 拿 8 首歌的逐字時間模擬過：平均誤差 0.35 → 0.31 秒、差 0.15 秒以內的字 35% → 45%（停頓、長音也一起跟著聽到的對）
        readonly List<int> groupFrom = new List<int>(), groupTo = new List<int>();   // 每一段（一個字、或併在一起的幾個短字）在 sweepCells 裡的範圍
        readonly List<bool> groupComma = new List<bool>();                            // 這一段前面是不是逗號
        const double AlignEarly = 0.3, AlignLate = 0.1, AlignLatency = 0.06, AlignMaxShift = 0.6, PeakK = 3;
        int alignNext;                       // 下一個要對的是第幾段（第 0 段是這行的開頭，照行的時間）
        double alignPrev;                    // 上一段實際從這行的第幾秒開始
        bool alignHeld;                      // 下一段已經先往後挪到 p + 0.1 等開口了
        double[] alignOrig;                  // 每一段原本估的開始時間（偏離最多 0.6 秒）
        readonly List<long> linePeaks = new List<long>();   // 這一行聽到的開口（播放引擎的時間；用的時候才換成這行的第幾秒）
        double paceLastElapsed = double.NaN; // 上一次看的時候是這行的第幾秒（跳了就不對了）
        readonly float[] vocalFluxBuf = new float[1000];

        void ResetPace()
        {
            alignNext = 1; alignPrev = 0; alignHeld = false; alignOrig = null; alignP = double.NaN;
            linePeaks.Clear();
            paceLastElapsed = double.NaN;
        }
        void ResetPaceVotes() { paceLastElapsed = double.NaN; }

        // 這一行最晚要在第幾秒唱完（從這行開始算；下一行開口之前）
        double MaxEnd(int line, double songLength)
        {
            var lines = sheetLyrics.Lines;
            double next = line + 1 < lines.Count ? lines[line + 1].Time : songLength;
            return next - lines[line].Time - 0.12;
        }

        // 第 g 段開始、結束唱的時間（這一段沒有畫出來的字：NaN）
        static double GroupT0(float[] c, List<int> from, List<int> to, int g) { return from[g] < to[g] ? c[from[g] + 3] : double.NaN; }
        static double GroupT1(float[] c, List<int> from, List<int> to, int g)
        {
            double t = double.NaN;
            for (int i = from[g]; i < to[g]; i += 5) t = double.IsNaN(t) ? c[i + 4] : Math.Max(t, c[i + 4]);
            return t;
        }

        // 從 start 秒開始（含）的字整串挪到 newStart 開始，長度乘上 k
        static void Retime(float[] c, double start, double newStart, double k)
        {
            for (int i = 0; i + 4 < c.Length; i += 5)
            {
                if (c[i + 3] < start - 1e-4) continue;
                c[i + 3] = (float)(newStart + (c[i + 3] - start) * k);
                c[i + 4] = (float)(newStart + (c[i + 4] - start) * k);
            }
        }

        // c 的 from～to（不含）這些字：現在（p 秒）以後的部分拉長或縮短，讓原本 t1 結束的變成 end 結束；正在掃的字掃到的比例不變（亮的地方不會跳）
        static void Stretch(float[] c, int from, int to, double p, double t1, double end)
        {
            double k = (end - p) / Math.Max(1e-3, t1 - p);
            for (int i = from; i + 4 < c.Length && i < to; i += 5)
            {
                double a = c[i + 3], b = c[i + 4];
                if (b <= p) continue;   // 已經掃完的字
                double nb = p + (b - p) * k;
                if (a >= p) { c[i + 3] = (float)(p + (a - p) * k); c[i + 4] = (float)nb; continue; }
                double frac = (p - a) / (b - a);
                c[i + 3] = (float)((p - frac * nb) / (1 - frac));
                c[i + 4] = (float)nb;
            }
        }

        // 改好的一份換上去，最後一段的時間跟著更新（AdjustHold 用）
        void UseCells(float[] c)
        {
            sweepCells = c;
            int last = groupFrom.Count - 1;
            if (last < 0 || groupTo.Count != groupFrom.Count) return;
            double t0 = GroupT0(c, groupFrom, groupTo, last), t1 = GroupT1(c, groupFrom, groupTo, last);
            if (!double.IsNaN(t0) && !double.IsNaN(t1)) { sweepLastT0 = t0; sweepLastT1 = t1; }
        }

        // 整行最晚唱完的時間
        static double LineEnd(float[] c)
        {
            double t = 0;
            for (int i = 0; i + 4 < c.Length; i += 5) t = Math.Max(t, c[i + 4]);
            return t;
        }

        // 最近 3 秒聽到的開口：開口強度（三筆平均）在前後 40 毫秒裡最大、而且比之前 2 秒的中位數高出 3 倍中位差的地方，
        // 換成這行的第幾秒加進 linePeaks（最後 4 筆還不知道後面會不會更大，下次再看）
        void UpdatePeaks()
        {
            int n = Beat.VocalSince(AnimationClock.Now - 3000, vocalTimes, vocalLevels, vocalFluxBuf);
            if (n < 20 || AnimationClock.Now - vocalTimes[n - 1] > 200) return;
            var f = new double[n];
            for (int i = 0; i < n; i++) f[i] = (vocalFluxBuf[Math.Max(0, i - 1)] + vocalFluxBuf[i] + vocalFluxBuf[Math.Min(n - 1, i + 1)]) / 3;
            long last = linePeaks.Count > 0 ? linePeaks[linePeaks.Count - 1] : long.MinValue;
            var win = new List<double>();
            for (int i = 4; i < n - 4; i++)
            {
                if (f[i] <= 0) continue;
                bool top = true;
                for (int k = i - 4; k <= i + 4 && top; k++) if (f[k] > f[i]) top = false;
                if (!top) continue;
                if (vocalTimes[i] <= last + 20) continue;
                win.Clear();
                for (int k = Math.Max(0, i - 200); k <= i; k++) win.Add(f[k]);
                win.Sort();
                double med = win[win.Count / 2];
                for (int k = 0; k < win.Count; k++) win[k] = Math.Abs(win[k] - med);
                win.Sort();
                if (f[i] <= med + PeakK * (win[win.Count / 2] + 1e-3)) continue;
                linePeaks.Add(vocalTimes[i]); last = vocalTimes[i];
            }
        }

        double alignP = double.NaN;   // 正在對的這一段原本預計的開始（先往後挪等開口時，找的範圍還是照這個）

        // 第 g 段（和後面全部）改從 d 秒開始：後面整串照原本的長度（放不下就壓短，在下一行之前唱完）；
        // 前一段還沒掃完的話讓它在 d 之前快點掃完（不會一下跳過去）。回傳改好的新一份（c 不動）
        float[] MoveRest(float[] c, int g, double d, double elapsed, double maxEnd)
        {
            double s0 = GroupT0(c, groupFrom, groupTo, g);
            if (double.IsNaN(s0) || Math.Abs(d - s0) < 0.005) return c;
            var nc = (float[])c.Clone();
            double rest = Math.Max(0.05, LineEnd(nc) - s0);
            Retime(nc, s0, d, Math.Min(1, Math.Max(0.3, (maxEnd - d) / rest)));
            for (int pg = g - 1; pg >= 0; pg--)
            {
                double t1 = GroupT1(nc, groupFrom, groupTo, pg);
                if (double.IsNaN(t1)) continue;
                if (t1 > d && t1 > elapsed) Stretch(nc, groupFrom[pg], groupTo[pg], elapsed, t1, Math.Max(elapsed + 0.02, d));
                break;
            }
            return nc;
        }

        // 有改回傳 true（sweepCells 換成新的一份，引擎手上那份不能動）
        bool VocalPace(double elapsed, int line, double songLength)
        {
            int groups = groupFrom.Count;
            if (sweepCells == null || groups < 2 || groupTo.Count != groups) return false;
            // 跳了（拖進度條、播放器往回修正）：這一行不再對（聽到的開口對不上了）
            bool jumped = !double.IsNaN(paceLastElapsed) && (elapsed < paceLastElapsed - 0.05 || elapsed > paceLastElapsed + 0.4);
            paceLastElapsed = elapsed;
            if (jumped) { alignNext = groups; return false; }
            if (alignNext >= groups) return false;
            var c = sweepCells;
            if (alignOrig == null)
            {
                alignOrig = new double[groups];
                for (int g = 0; g < groups; g++) alignOrig[g] = GroupT0(c, groupFrom, groupTo, g);
            }
            UpdatePeaks();
            double maxEnd = MaxEnd(line, songLength);
            while (alignNext < groups)
            {
                int g = alignNext;
                double p = GroupT0(c, groupFrom, groupTo, g);
                if (double.IsNaN(p) || double.IsNaN(alignOrig[g])) { alignNext++; alignP = double.NaN; continue; }
                if (double.IsNaN(alignP)) alignP = p;
                double p0 = alignP, q = double.NaN;
                double pos = elapsed + lineStart;
                foreach (long clock in linePeaks)
                {
                    double pk = SongTime(clock, pos) - lineStart;
                    if (pk >= p0 - AlignEarly && pk <= p0 + AlignLate && pk > alignPrev + 0.08) { q = pk; break; }
                }
                double d;
                if (!double.IsNaN(q) && p <= elapsed + 0.01)
                {
                    d = p;   // 這一段已經開始亮了：不往後推（亮的不會退回去），照現在的
                }
                else if (!double.IsNaN(q))
                {
                    // 聽到開口了：從那一刻開始（已經過了就從現在開始，不往回跳）
                    d = Math.Max(alignOrig[g] - AlignMaxShift, Math.Min(alignOrig[g] + AlignMaxShift, q + AlignLatency));
                    d = Math.Max(d, Math.Max(alignPrev + 0.05, elapsed + 0.03));
                    c = MoveRest(c, g, d, elapsed, maxEnd);
                }
                else if (elapsed >= p0 + AlignLate + AlignLatency)
                {
                    d = p;   // 等滿了沒聽到：照現在排的（已經先挪到 p + 0.1 了）
                }
                else
                {
                    // 還沒聽到、快到了：先往後挪到 p + 0.1 等（前一段掃完稍停一下，聽到了再提前）
                    if (!alignHeld && elapsed >= p0 - 0.05 && p > elapsed + 0.02)
                    {
                        double held = Math.Min(alignOrig[g] + AlignMaxShift, p0 + AlignLate);
                        if (held > p + 0.005) c = MoveRest(c, g, held, elapsed, maxEnd);
                        alignHeld = true;
                    }
                    break;
                }
                alignPrev = d; alignNext++; alignHeld = false; alignP = double.NaN;
            }
            if (ReferenceEquals(c, sweepCells)) return false;
            UseCells(c);
            return true;
        }

        static void AddCell(List<float> cells, LockClock.LyricsGlyphs g, int p, double t0, double t1)
        {
            if (p >= g.CharRow.Length || g.CharRow[p] < 0) return;
            cells.Add(g.CharRow[p]); cells.Add(g.X0[p]); cells.Add(g.X1[p]); cells.Add((float)t0); cells.Add((float)t1);
        }

        // 常拉長音的中文字（語助詞）
        static readonly string HeldChars = "啊阿喔哦噢呀呦嗚喲耶唷啦呢哎唉嘿哼嗯";

        // 這個字唱得比一般久多少倍（1 = 照讀音）。atEnd：在句尾或逗號前（oh、yeah 在句中通常不拖，句尾才拖很長）
        static double Held(string word, bool atEnd)
        {
            double h = HeldRaw(word);
            return atEnd ? h : Math.Min(h, 1.8);
        }

        static double HeldRaw(string word)
        {
            if (word.Length == 1 && HeldChars.IndexOf(word[0]) >= 0) return 2;
            var sb = new System.Text.StringBuilder();
            bool repeated = false;   // 有同一個字母連續三次以上（ooooh、yeahhh）
            int run = 0;
            char last = '\0';
            foreach (char ch in word.ToLowerInvariant())
            {
                if (ch == '\'' || ch == '’') continue;
                if (ch == last) { run++; if (run >= 3) repeated = true; continue; }
                last = ch; run = 1;
                sb.Append(ch);
            }
            string key = sb.ToString();
            if (repeated) return 2.5;
            if (key == "oh" || key == "o" || key == "ah" || key == "uh" || key == "woah" || key == "whoa" || key == "yeah" || key == "mm" || key == "hm") return 1.5;   // 常拉長（資料上 1.5 倍最準）
            return 1;
        }

        // 中日韓的字的份量；其他的字回傳 0（英文那種照讀音估，見 WordWeight）：
        //   中文：一個字一個音節（1）；韓文：一個字一個音節，有收音的稍微長一點（1.1）；
        //   日文照「拍」：假名一個一拍（小字的ゃゅょ、ぁぃぅぇぉ併進前一個，算 0）、っ ー ん 也各一拍；漢字大約 1.8 拍。
        //   一拍比一個音節短：乘 0.6（整首歌的速度另外會照中位數校正）
        static double Syllable(char c, bool japanese = false)
        {
            if (c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF || c >= 0xF900 && c <= 0xFAFF) return japanese ? 1.8 * 0.6 : 1;   // 漢字
            if (c >= 0xAC00 && c <= 0xD7AF) return (c - 0xAC00) % 28 != 0 ? 1.1 : 1;   // 韓文（收音）
            if (c >= 0x3040 && c <= 0x30FF)
            {
                if ("ぁぃぅぇぉゃゅょゎァィゥェォャュョヮ".IndexOf(c) >= 0) return 0.0001;   // 小字：併進前一個（幾乎不佔時間，但還是自己一格）
                return 0.6;
            }
            return 0;
        }

        static bool Vowel(char c) { return "aeiouyàáâäèéêëìíîïòóôöùúûü".IndexOf(char.ToLowerInvariant(c)) >= 0; }

        // 英文一個字唱多久（份量 1 大約是一個音節）：照母音的組數估音節（字尾不發音的 e、-ed 扣掉），
        // 長母音（ee、oo、ay、igh…）多一點，子音多的字也稍微長一點；數字一個大約一個音節
        static double WordWeight(string word)
        {
            string w = word.ToLowerInvariant().Replace("'", "").Replace("’", "");
            if (w.Length == 0) return 0.5;
            // 字典查得到：每個音節照重音、母音長短（份量見 data/cmudict.gz 的產生方式：重音 0.8～1、次重音 0.6～0.8、輕讀 0.5～0.8），加上子音
            int dictConsonants;
            var dict = Pronounce.Syllables(word, out dictConsonants);
            if (dict != null)
            {
                double sum = 0;
                foreach (double sy in dict) sum += sy;
                return sum + 0.03 * dictConsonants;
            }
            int digits = 0, consonants = 0, groups = 0;
            bool inVowel = false;
            for (int i = 0; i < w.Length; i++)
            {
                char c = w[i];
                if (char.IsDigit(c)) { digits++; inVowel = false; continue; }
                bool v = Vowel(c) && !(c == 'y' && i == 0);   // 字首的 y 是子音（yeah、you）
                if (v && !inVowel) groups++;
                if (!v && char.IsLetter(c)) consonants++;
                inVowel = v;
            }
            if (groups > 1 && w.EndsWith("e") && !w.EndsWith("le") && !w.EndsWith("ee") && w != "maybe") groups--;   // love、time 的 e 不發音（maybe 的有）
            if (groups > 2 && (w.EndsWith("ately") || w.EndsWith("ely"))) groups--;   // desperately、lonely：中間的 e 不發音
            if (groups > 1 && w.EndsWith("ed") && w.Length > 3 && "td".IndexOf(w[w.Length - 3]) < 0) groups--;   // loved、burned
            double syllables = Math.Max(groups, 0) + digits;
            if (syllables < 1) syllables = 1;
            // 長母音（ee、oo、ay、igh…）唱得長一點：用乘的（加的會把很短的字放大太多）
            bool longVowel = false;
            foreach (var d in new[] { "ee", "oo", "ea", "ai", "ay", "oa", "ow", "ou", "igh", "ie", "oy", "oi", "ue", "aw" })
                if (w.IndexOf(d) >= 0) { longVowel = true; break; }
            return (syllables + 0.03 * consonants) * (longVowel ? 1.15 : 1);
        }

        // 一行裡每個字母佔多少時間（見 LetterWeight）。字典查得到的英文字：每串母音照它那個音節的份量再調
        // （重音、長母音的那段掃得慢，輕讀的 schwa 那段快；例如 beautiful 的 eau 比後面的 i、u 久）。
        // 母音串數跟音節數對不起來（拼字跟讀音差很多）就照原本的
        static double[] LetterWeights(string text, Words w)
        {
            var lw = new double[text.Length];
            int n = w.Starts.Count;
            for (int k = 0; k < n; k++)
            {
                int a = w.Starts[k], b = w.Ends[k], end = k + 1 < n ? w.Starts[k + 1] : b;
                for (int p = a; p < end; p++) lw[p] = LetterWeight(text, p, w.Holds[k]);
                if (b - a < 2 || Syllable(text[a], true) > 0) continue;
                int cons;
                var syl = Pronounce.Syllables(text.Substring(a, b - a), out cons);
                if (syl == null) continue;
                // 母音字母一串一串（跟 WordWeight 一樣：字首的 y 是子音）
                var gs = new List<int>();
                var ge = new List<int>();
                for (int p = a; p < b; p++)
                {
                    char c = text[p];
                    if (!Vowel(c) || (p == a && char.ToLowerInvariant(c) == 'y')) continue;
                    if (ge.Count > 0 && ge[ge.Count - 1] == p) ge[ge.Count - 1] = p + 1;
                    else { gs.Add(p); ge.Add(p + 1); }
                }
                // 字尾不發音的 e（love、time、loved、loves）：那個 e 當子音
                int lastGroup = gs.Count - 1;
                if (gs.Count == syl.Length + 1 && ge[lastGroup] - gs[lastGroup] == 1 && char.ToLowerInvariant(text[gs[lastGroup]]) == 'e' && gs[lastGroup] >= b - 2)
                {
                    lw[gs[lastGroup]] = 0.55;
                    gs.RemoveAt(lastGroup); ge.RemoveAt(lastGroup);
                }
                if (gs.Count != syl.Length) continue;
                double mean = 0;
                foreach (double sy in syl) mean += sy;
                mean /= syl.Length;
                for (int i = 0; i < gs.Count; i++)
                    for (int p = gs[i]; p < ge[i]; p++) lw[p] *= syl[i] / mean;
            }
            return lw;
        }

        // 字裡面每個字母佔多少時間：母音最久、y 次之、子音很短（唱歌時拉長的是母音）；中日韓的字整個字一份。
        // hold：這個字拉長的倍數，多出來的時間都給母音
        static double LetterWeight(string text, int p, double hold)
        {
            char c = text[p];
            if (Syllable(c, true) > 0) return 1;
            if (char.IsDigit(c)) return 1;
            if (!char.IsLetter(c)) return 0.2;
            if (char.ToLowerInvariant(c) == 'y') return 1.1 * hold;
            return Vowel(c) ? 1.6 * hold : 0.55;
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

        // 同上：唱到第 line 行時長圖每一列的大小（目前這行正常、其他行小一點）；一般歌詞都一樣大（null）
        float[] ZoomFor(int line)
        {
            return sheetLyrics.Synced ? LockClock.LyricsZoom(sheetLyrics.SheetHeight, lineCenters, lineHeights, line) : null;
        }

        // 現在唱到第幾行（-1 = 還沒開始唱）；播放中稍微提早 0.2 秒，捲動完剛好對上（拖進度條時就照拖到的地方）。
        // 位置跟卡片上的進度條一樣（剛跳過去、播放器還沒回報時也是新的位置）
        int CurrentLine(NowPlaying.Track track)
        {
            if (track == null) return -1;
            double now = owner.SongPosition(track) + (owner.Scrubbing ? 0 : 0.2);
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
