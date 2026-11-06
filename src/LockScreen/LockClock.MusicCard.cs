using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoWallpaper
{
    // 右下角的音樂卡片：照 macOS Tahoe 鎖定畫面「正在播放」的樣子，卡片本身是播放引擎畫的液態玻璃。
    // 以 1080p 為準：卡片 400 × 198、邊距 16、封面 72（圓角 34 − 16 = 18，跟卡片同心）；
    // 封面右邊是歌名（16 半粗）和「歌手 — 專輯」（13，白 70%），右上角歌詞圓鈕；中間是時間 — 進度條 — 剩下的時間；
    // 最下面是隨機播放、⏪ ⏯ ⏩（實心、放大）、循環。進度條、歌詞圓鈕的底和玻璃由播放引擎畫（拖曳時才順）
    static partial class LockClock
    {
        // 音樂卡片：好幾張同樣大小、疊在同樣位置的圖（播放引擎各自做動畫）
        public class MusicCard
        {
            public int[] Chrome;      // 沒有液態玻璃時（舊顯示卡）退回的毛玻璃樣子：霧白、上緣反光、顆粒、邊框
            public int[] Info;        // 歌名、「歌手 — 專輯」（換歌時跟封面一起滑動、淡入淡出）
            public int[] Cover;       // 專輯封面（帶淡淡的影子；沒有封面時是半透明方塊和音符）
            public int[] Buttons;     // 隨機播放 / 上一首 / 下一首 / 循環（換歌時不動）
            public int[] PlayKey, PauseKey;   // 播放鍵的 ▶、⏸ 各一張（兩張都在，換的時候一張縮小淡出、另一張從小放大彈出來）
            public MarqueeLine Title, Subtitle;   // 太長、要跑馬燈的歌名 / 副標（放得下就是 null，畫在 Info 裡）
            public int[] Mic, MicOn;  // 歌詞圓鈕上的圖示：白色（沒開）、深色（開著時底是白的）
            public CardColor Color;   // 重拍時邊框泛出的光暈（跟著封面的顏色）
            public string ColorKey;   // 顏色有沒有變（換歌時顏色變了才淡入淡出）
            public int Width, Height, X, Y;                // 每張圖的大小和位置（四周有留給光暈的空間）
            public int InnerX, InnerY, InnerW, InnerH;     // 卡片本身在螢幕上的範圍
            public double Scale, Radius;                   // 以 1080p 為 1 的縮放、卡片的圓角
            public double ProgressLeft, ProgressWidth;     // 進度條（拖曳時換算成秒數）
            public bool CanPrev = true, CanNext = true;    // 播放器能不能跳上一首 / 下一首（不能的話鍵很淡）
            public VideoEngine.LiquidCardSpec Spec;        // 給播放引擎畫液態玻璃和卡片上的東西
            public double ArtX, ArtY, KeyX, KeyY;          // 封面、播放鍵的中心（螢幕座標；縮放動畫的中心）
        }

        // 跑馬燈的一行字：整行不截斷畫成一張圖（四周留給柔光的空間），只在 (ViewX, 寬 ViewW) 看得到
        public class MarqueeLine
        {
            public int[] Pixels;
            public int W, H, X, Y;
            public double TextW, ViewX, ViewW;
        }

        // 卡片上的位置（1080p 的像素，以卡片左上角為 0）
        // 版面照 macOS Tahoe 鎖定畫面的「正在播放」（400 × 198，約 2 : 1；沒有音量滑桿）：
        //   上面：封面、歌名和歌手，右上角歌詞圓鈕；中間：時間 — 進度條 — 剩下的時間（同一列）；
        //   最下面：隨機播放在最左、⏪ ⏸ ⏩ 放大置中（間隔約卡寬的 18%）、循環在最右。
        // 封面圓角跟卡片同心：卡片 34 = 封面 18 + 邊距 16
        // 文字欄：左邊離封面 12（16 + 72 + 12 = 100）、右邊離歌詞圓鈕也是 12（370 − 14 − 12 = 344），跑馬燈捲到兩端都留一樣的距離
        const double CardW = 400, CardH = 198, CardPad = 16, ArtSize = 72, ColumnX = 100, ColumnW = 244;
        const double TitleY = 45, SubtitleY = 66, LyricsX = 370, LyricsY = 52, LyricsR = 14;
        const double ProgressY = 114, ProgressGap = 14;   // 兩端的時間在進度條左右，跟進度條的間隔兩邊一樣（見 ProgressEnds）
        const double ControlsY = 162, ShuffleX = 40, PrevX = 128, PlayX = 200, NextX = 272, RepeatX = 360;

        // 右下角：正在播放的歌（沒在播就回傳 null）。playing：播放鍵畫成暫停（剛按了播放暫停時，照按下去的樣子，不等播放器回報）。
        // buttons：回傳各個按鈕在螢幕上的位置，讓鎖定畫面知道滑鼠點到哪個
        public static MusicCard RenderMusic(NowPlaying.Track track, bool playing, bool? shuffle, int? repeat, int screenWidth, int screenHeight, List<KeyValuePair<Rect, string>> buttons)
        {
            if (track == null) return null;
            double s = screenHeight / 1080.0;
            int pad = ShadowPad(screenHeight);
            var card = new MusicCard { Scale = s, Radius = CardRadius(s) };
            card.InnerW = (int)Math.Round(CardW * s); card.InnerH = (int)Math.Round(CardH * s);
            card.Width = card.InnerW + 2 * pad; card.Height = card.InnerH + 2 * pad;
            // 離右邊和下面一樣是 52（下緣跟左下角的時鐘對齊）
            card.InnerX = (int)Math.Round(screenWidth - 52 * s - card.InnerW);
            card.InnerY = (int)Math.Round(screenHeight - 52 * s - card.InnerH);
            card.X = card.InnerX - pad; card.Y = card.InnerY - pad;

            var art = DecodeArt(track.Art, (int)Math.Round(300 * s));
            card.Chrome = Layer(card, dc => DrawChrome(dc, card));
            card.CanPrev = track.CanPrev; card.CanNext = track.CanNext;
            // 太長的歌名、副標拿出來做跑馬燈，Info 裡就不畫
            string subtitle = Subtitle(track);
            double titleY = string.IsNullOrEmpty(subtitle) ? CardPad + ArtSize / 2 + 6 : TitleY;
            card.Title = RenderMarquee(card, track.Title, 16, FontWeights.SemiBold, Brushes.White, titleY);
            card.Subtitle = RenderMarquee(card, subtitle, 13, FontWeights.Normal, SubtitleBrush, SubtitleY);
            card.Info = Layer(card, dc => DrawInfo(dc, card, track.Title, subtitle, titleY), true);
            card.Buttons = RenderButtons(card, shuffle, repeat);
            card.PlayKey = Layer(card, dc => DrawPlayKey(dc, s, false), true);
            card.PauseKey = Layer(card, dc => DrawPlayKey(dc, s, true), true);
            card.Cover = RenderCover(card, art);
            card.ArtX = card.InnerX + CardPad * s + Math.Round(ArtSize * s) / 2; card.ArtY = card.InnerY + CardPad * s + Math.Round(ArtSize * s) / 2;
            card.KeyX = card.InnerX + Math.Round(PlayX * s); card.KeyY = card.InnerY + ControlsY * s;
            card.Mic = Layer(card, dc => DrawLyricsIcon(dc, s, Brushes.White), true);
            card.MicOn = Layer(card, dc => DrawLyricsIcon(dc, s, new SolidColorBrush(Color.FromRgb(40, 40, 46))));

            // 卡片的顏色：有封面就照封面的色相，沒有就用歌名算一個（每首歌都有顏色）
            var hues = AlbumHues(track.Art) ?? SongHues(Lyrics.SongKey(track));
            card.Color = RenderCardColor(hues, card.Width, card.Height, pad, card.InnerW, card.InnerH, card.Radius, s);
            card.ColorKey = string.Join(",", Array.ConvertAll(hues, v => v.ToString("0.000", CultureInfo.InvariantCulture))) + "," + (art == null ? 0 : track.Art.Length);
            var tint = Hsl(hues[0], Math.Max(0.35, Math.Min(0.8, hues[1])), 0.45, 255);
            double top = Math.Max(tint.R, Math.Max(tint.G, Math.Max(tint.B, (byte)1))) / 0.75;   // 最亮的那個顏色調到 0.75

            Func<double, double> sx = x => card.InnerX + x * s;
            Func<double, double> sy = y => card.InnerY + y * s;
            double px0, px1;
            ProgressEnds(track.Duration.TotalSeconds, out px0, out px1);
            card.ProgressLeft = sx(px0); card.ProgressWidth = (px1 - px0) * s;
            card.Spec = new VideoEngine.LiquidCardSpec
            {
                X = card.InnerX, Y = card.InnerY, W = card.InnerW, H = card.InnerH, Radius = card.Radius, Scale = s,
                TintR = tint.R / top, TintG = tint.G / top, TintB = tint.B / top,
                ProgressX0 = sx(px0), ProgressX1 = sx(px1), ProgressY = sy(ProgressY),
                LyricsX = sx(LyricsX), LyricsY = sy(LyricsY), LyricsR = LyricsR * s,
            };

            // 點的範圍：按鈕 32 ～ 40 寬、36 高；進度條上下各 10
            Action<double, double, double, double, string> hit = (x, y, w, h, action) => buttons.Add(new KeyValuePair<Rect, string>(new Rect(sx(x), sy(y), w * s, h * s), action));
            hit(ShuffleX - 16, ControlsY - 18, 32, 36, "shuffle");
            hit(PrevX - 20, ControlsY - 18, 40, 36, "prev");
            hit(PlayX - 20, ControlsY - 18, 40, 36, "toggle");
            hit(NextX - 20, ControlsY - 18, 40, 36, "next");
            hit(RepeatX - 16, ControlsY - 18, 32, 36, "repeat");
            hit(LyricsX - 16, LyricsY - 16, 32, 32, "lyrics");
            hit(px0 - 6, ProgressY - 10, px1 - px0 + 12, 20, "progress");
            return card;
        }

        // 兩端的時間（已經播了多久、還剩多久）：跟卡片其他圖一樣大小、一樣位置，每秒換一次；不知道歌曲長度就是 null
        public static int[] RenderTime(MusicCard card, double position, double duration, bool halo = true)
        {
            if (card == null || duration <= 0) return null;
            position = Math.Max(0, Math.Min(duration, position));
            double s = card.Scale;
            return Layer(card, dc =>
            {
                var brush = new SolidColorBrush(Color.FromArgb(0xA6, 255, 255, 255));   // 白 65%（玻璃後面是亮的桌布也讀得清楚）
                // 跟進度條同一列（字的中線對齊進度條）：左邊已經播了多久、右邊還剩多久
                DrawText(dc, Clock(position), 13 * s, FontWeights.Medium, brush, CardPad * s, (ProgressY + 5) * s, double.MaxValue, false);
                // 剩下的時間用「歌長的秒數 − 已播的秒數」：兩邊加起來一定等於歌長（直接取 duration − position 的整數會差 1 秒）
                DrawText(dc, "−" + Clock(Math.Floor(duration) - Math.Floor(position)), 13 * s, FontWeights.Medium, brush, (CardW - CardPad) * s, (ProgressY + 5) * s, double.MaxValue, true);
            }, halo);
        }

        // 進度條的兩端（1080p 的像素，以卡片左上角為 0）：兩端的時間貼著卡片的內距，進度條在中間，
        // 跟左邊時間、右邊時間的間隔一樣是 ProgressGap（右邊多了「−」比較寬，進度條會稍微偏左一點，像 iOS 的排法）。
        // 時間的寬度量這首歌最長的樣子（每個數字都當最寬的 0 算），播放中數字一直變，進度條不會跟著動
        static void ProgressEnds(double duration, out double x0, out double x1)
        {
            string widest = System.Text.RegularExpressions.Regex.Replace(Clock(duration), "[0-9]", "0");
            x0 = CardPad + Text(widest, 13, FontWeights.Medium, Brushes.White).Width + ProgressGap;
            x1 = CardW - CardPad - Text("−" + widest, 13, FontWeights.Medium, Brushes.White).Width - ProgressGap;
        }

        // 進度條的長度什麼時候會變（歌曲長度跨過十分鐘、一小時…，時間多一位數）：這個值變了卡片要整張重畫
        public static int ProgressLayout(double duration) { return Clock(duration).Length; }

        // 秒數 → 「3:07」（超過一小時「1:03:07」）
        static string Clock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Floor(seconds));
            return t.TotalHours >= 1 ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                                     : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", t.Minutes, t.Seconds);
        }

        // 畫一張跟卡片其他圖一樣大小的圖：draw 裡的座標以卡片左上角為 0（螢幕像素）；halo：字和圖示帶很淡的暗色柔光（見 Halo）
        static int[] Layer(MusicCard card, Action<DrawingContext> draw, bool halo = false)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(card.InnerX - card.X, card.InnerY - card.Y));
                draw(dc);
                dc.Pop();
            }
            if (!halo) return Snapshot(visual, card.Width, card.Height);
            visual.Effect = Halo(card.Scale);
            var root = new ContainerVisual();   // 效果加在裡面那層（最外層的效果畫成圖時不一定會算進去）
            root.Children.Add(visual);
            return Snapshot(root, card.Width, card.Height);
        }

        static readonly Brush SubtitleBrush = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255));   // 白 70%

        // 「歌手 — 專輯」（只有一個就只放那個）
        static string Subtitle(NowPlaying.Track track)
        {
            return string.IsNullOrEmpty(track.Album) || track.Album == track.Title ? track.Artist
                : string.IsNullOrEmpty(track.Artist) ? track.Album : track.Artist + " — " + track.Album;
        }

        // 歌名、「歌手 — 專輯」（沒有副標、只有歌名的影片：歌名改放在封面的中線上，見 titleY）。要跑馬燈的那行不畫
        static void DrawInfo(DrawingContext dc, MusicCard card, string title, string subtitle, double titleY)
        {
            double s = card.Scale;
            if (card.Title == null) DrawText(dc, title, 16 * s, FontWeights.SemiBold, Brushes.White, ColumnX * s, titleY * s, ColumnW * s, false);
            if (card.Subtitle == null) DrawText(dc, subtitle, 13 * s, FontWeights.Normal, SubtitleBrush, ColumnX * s, SubtitleY * s, ColumnW * s, false);
        }

        // 一行字放不下（比 ColumnW 寬）：整行畫成一張圖給跑馬燈（Apple Music：停一下、慢慢往左捲、兩端淡出、捲完接著下一份）。
        // 放得下回傳 null。baseline 是 1080p、以卡片左上角為 0 的基線
        static MarqueeLine RenderMarquee(MusicCard card, string text, double size, FontWeight weight, Brush brush, double baseline)
        {
            if (string.IsNullOrEmpty(text)) return null;
            double s = card.Scale;
            var ft = Text(text, size * s, weight, brush);
            if (ft.Width <= ColumnW * s) return null;
            int pad = (int)Math.Ceiling(10 * s);   // 字的柔光（Halo）
            double left = card.InnerX + Math.Round(ColumnX * s), top = card.InnerY + Math.Round(baseline * s) - ft.Baseline;   // 跟 DrawText 一樣的位置
            int x = (int)left - pad, y = (int)Math.Floor(top) - pad;
            int w = (int)Math.Ceiling(ft.Width) + 2 * pad, h = (int)Math.Ceiling(ft.Height) + 2 * pad + 1;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawText(ft, new Point(pad, top - y));
            visual.Effect = Halo(s);
            var root = new ContainerVisual();
            root.Children.Add(visual);
            // 看得到的範圍就是文字欄：捲到左邊在字的起點消失（不會跑進離封面的那段距離）、右邊到 ColumnW 為止；
            // 左邊多留 1 像素（字的邊緣反鋸齒不會被切掉）
            return new MarqueeLine { Pixels = Snapshot(root, w, h), W = w, H = h, X = x, Y = y, TextW = ft.Width, ViewX = left - 1, ViewW = ColumnW * s + 1 };
        }

        // ⏪ ⏩、隨機播放、循環（跟卡片其他圖一樣大小、一樣位置；播放鍵另外兩張，見 DrawPlayKey）
        // shuffle / repeat：隨機播放、循環的狀態（null：播放器不支援，畫得很淡）
        public static int[] RenderButtons(MusicCard card, bool? shuffle, int? repeat)
        {
            return Layer(card, dc => DrawButtons(dc, card, shuffle, repeat), true);
        }

        static void DrawButtons(DrawingContext dc, MusicCard card, bool? shuffle, int? repeat)
        {
            double s = card.Scale;
            // 照 macOS 的實心符號：圓角三角形（兩個接在一起是上一首 / 下一首）、圓角直條（暫停）；▶ 最亮，旁邊兩個淡一點
            var white = Brushes.White;
            Action<double, Geometry> icon = (x, g) =>
            {
                dc.PushTransform(new TranslateTransform(x * s, ControlsY * s));
                dc.PushTransform(new ScaleTransform(1.45 * s, 1.45 * s));   // macOS 的鍵比較大
                dc.DrawGeometry(white, null, g);
                dc.Pop(); dc.Pop();
            };
            Action<double, Action> key = (alpha, draw) => { dc.PushOpacity(alpha); draw(); dc.Pop(); };
            // 上一首 / 下一首：播放器現在不能跳（例如單支影片）就很淡
            key(card.CanPrev ? 0.82 : 0.3, () => icon(PrevX, Skip(-1)));
            key(card.CanNext ? 0.82 : 0.3, () => icon(NextX, Skip(1)));
            // 隨機播放、循環（Segoe 的圖示）：開著是全白、下面一個小點；關著半透明；播放器不支援就很淡
            mode(dc, s, ShuffleX, "\uE8B1", shuffle.HasValue, shuffle == true);
            mode(dc, s, RepeatX, repeat == 1 ? "\uE8ED" : "\uE8EE", repeat.HasValue, repeat.HasValue && repeat.Value != 0);
        }

        // 播放鍵：▶ 或 ⏸（pause），各畫成一張
        static void DrawPlayKey(DrawingContext dc, double s, bool pause)
        {
            dc.PushOpacity(0.95);
            if (pause) DrawPause(dc, s);
            else
            {
                dc.PushTransform(new TranslateTransform(PlayX * s, ControlsY * s));
                dc.PushTransform(new ScaleTransform(1.45 * s, 1.45 * s));   // macOS 的鍵比較大
                dc.DrawGeometry(Brushes.White, null, RoundedTriangle(-7.5, 18, 16.5, 1, 2.2));
                dc.Pop(); dc.Pop();
            }
            dc.Pop();
        }

        // 暫停：兩條圓角直條，直接照整數像素畫（不經過 1.45 倍的縮放：縮放後四條邊都落在半個像素上，邊緣會糊）
        static void DrawPause(DrawingContext dc, double s)
        {
            double bw = Math.Round(7.25 * s), gap = 2 * Math.Round(2.9 * s), bh = Math.Round(26 * s), r = 2.2 * s;
            double cx = Math.Round(PlayX * s), top = Math.Round(ControlsY * s - bh / 2);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(cx - gap / 2 - bw, top, bw, bh), r, r);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(cx + gap / 2, top, bw, bh), r, r);
        }

        static readonly Typeface ModeIcons = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        static void mode(DrawingContext dc, double s, double x, string glyph, bool supported, bool on)
        {
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ModeIcons, 15 * s, Brushes.White, 1.0);
            dc.PushOpacity(!supported ? 0.25 : on ? 1.0 : 0.5);
            dc.DrawText(text, new Point(x * s - text.Width / 2, ControlsY * s - text.Height / 2));
            if (on) dc.DrawEllipse(Brushes.White, null, new Point(x * s, (ControlsY + 11.5) * s), 1.6 * s, 1.6 * s);
            dc.Pop();
        }

        static Geometry Skip(int dir)
        {
            var g = new GeometryGroup();
            g.Children.Add(RoundedTriangle(-11 * dir, 14, 11, dir, 1.7));
            g.Children.Add(RoundedTriangle(0, 14, 11, dir, 1.7));
            g.Freeze();
            return g;
        }

        // 三個角都是圓角（半徑 r）的三角形：直邊在 x0（高 h），尖端朝 dir（1 右、-1 左）、離直邊 w
        static Geometry RoundedTriangle(double x0, double h, double w, int dir, double r)
        {
            var pts = new[] { new Point(x0, -h / 2), new Point(x0 + dir * w, 0), new Point(x0, h / 2) };
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                for (int i = 0; i < 3; i++)
                {
                    Point prev = pts[(i + 2) % 3], p = pts[i], next = pts[(i + 1) % 3];
                    Vector a = prev - p, b = next - p;
                    a.Normalize(); b.Normalize();
                    double angle = Math.Acos(Math.Max(-1, Math.Min(1, a.X * b.X + a.Y * b.Y)));
                    double t = r / Math.Tan(angle / 2);   // 圓角跟兩條邊相切的點離角多遠
                    Point p1 = p + a * t, p2 = p + b * t;
                    if (i == 0) ctx.BeginFigure(p1, true, true); else ctx.LineTo(p1, true, true);
                    Vector e1 = p - prev, e2 = next - p;
                    bool clockwise = e1.X * e2.Y - e1.Y * e2.X > 0;   // 螢幕座標（y 往下）：往右轉就是順時針
                    ctx.ArcTo(p2, new Size(r, r), 0, false, clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
                }
            }
            g.Freeze();
            return g;
        }

        // 歌詞圓鈕上的圖示：對話框裡一個引號（Apple Music 的歌詞圖示）；圓鈕的底由播放引擎畫
        static void DrawLyricsIcon(DrawingContext dc, double s, Brush ink)
        {
            dc.PushTransform(new TranslateTransform(LyricsX * s, LyricsY * s));
            dc.PushTransform(new ScaleTransform(s, s));
            var pen = new Pen(ink, 1.3) { LineJoin = PenLineJoin.Round };
            dc.DrawRoundedRectangle(null, pen, new Rect(-6.5, -6, 13, 10), 3, 3);
            var tail = new StreamGeometry();
            using (var ctx = tail.Open())
            {
                ctx.BeginFigure(new Point(-4, 3.5), true, true);
                ctx.LineTo(new Point(-5, 7.2), true, true);
                ctx.LineTo(new Point(-0.5, 3.5), true, true);
            }
            dc.DrawGeometry(ink, null, tail);
            var quote = Text("”", 11, FontWeights.Bold, ink);
            dc.DrawText(quote, new Point(0.3 - quote.Width / 2, 6 - quote.Baseline));
            dc.Pop(); dc.Pop();
        }

        // 沒有液態玻璃時退回的毛玻璃：很淡的霧白、上緣一道反光、細微的顆粒、玻璃邊緣
        static void DrawChrome(DrawingContext dc, MusicCard card)
        {
            var rect = new Rect(0, 0, card.InnerW, card.InnerH);
            double r = card.Radius;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x0D, 255, 255, 255)), null, rect, r, r);   // 5% 霧白
            var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 255, 255, 255), 0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.55));
            dc.DrawRoundedRectangle(sheen, null, rect, r, r);
            dc.DrawRoundedRectangle(NoiseBrush(), null, rect, r, r);
            double half = EdgeWidth / 2;
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), EdgeWidth),
                new Rect(half, half, card.InnerW - EdgeWidth, card.InnerH - EdgeWidth), r - half, r - half);
        }

        // 一行字：基線在 (x, baseline)；right = true 時 x 是右端；超過 maxWidth 顯示「…」。
        // 起點和基線都取整數（不是 1080p 時乘上比例會是小數，字落在半個像素上會糊）
        static void DrawText(DrawingContext dc, string text, double size, FontWeight weight, Brush brush, double x, double baseline, double maxWidth, bool right)
        {
            if (string.IsNullOrEmpty(text)) return;
            var ft = Text(text, size, weight, brush);
            if (maxWidth < double.MaxValue) { ft.MaxTextWidth = maxWidth; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
            dc.DrawText(ft, new Point(Math.Round(right ? x - ft.Width : x), Math.Round(baseline) - ft.Baseline));
        }

        static FormattedText Text(string text, double size, FontWeight weight, Brush brush)
        {
            var typeface = new Typeface(CardFont, FontStyles.Normal, weight, FontStretches.Normal);
            return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, 1.0);
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
    }
}
