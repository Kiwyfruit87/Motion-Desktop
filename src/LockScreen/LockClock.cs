using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoWallpaper
{
    // 鎖定畫面上的圖：用 WPF 畫成半透明圖，再交給播放引擎每一格貼到影片上。這個檔案是共用的部分（字型、大小、畫成像素）；
    // 時鐘和天氣在 LockClock.Clock.cs、音樂卡片在 LockClock.MusicCard.cs、封面和卡片顏色在 LockClock.Art.cs、歌詞長圖在 LockClock.Lyrics.cs
    static partial class LockClock
    {
        // 歌詞跟 ChromeOS 一樣用 Google Sans（fonts 資料夾裡的 Google Sans Flex，開源授權；拉丁字母含各種帶符號的字母都有）。
        // Google Sans 沒有的字依序找：俄文、希臘文用 Segoe UI（有真的半粗體，粗細才跟旁邊的字一致），
        // 中文和日文假名用微軟正黑體，韓文用 Malgun Gothic
        static readonly FontFamily SansFont = new FontFamily(
            new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts") + "\\"), "./#Google Sans Flex, Segoe UI, Microsoft JhengHei UI, Malgun Gothic");
        // 歌詞也可以換成自己電腦上的字型（跟時鐘一樣放在 fonts\local，不進 repo）：lyrics.ttf / .otf 給一般大小的歌詞（半粗體的字型檔）、
        // lyrics-big.ttf / .otf 給放大的歌詞（粗體的字型檔；沒有就用 lyrics 那個）。都沒有就用 Google Sans Flex
        const string LyricsFallback = ", Segoe UI, Microsoft JhengHei UI, Malgun Gothic";
        static readonly FontFamily LyricsFont = LocalFont("lyrics", SansFont, LyricsFallback);
        static readonly FontFamily BigLyricsFont = LocalFont("lyrics-big", LyricsFont, LyricsFallback);
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        // 時鐘和音樂卡片用 Inter（fonts 資料夾裡，開源授權；最接近 Apple 的 SF Pro）；中文用微軟正黑體，其他照歌詞的順序
        static readonly FontFamily CardFont = new FontFamily(
            new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts") + "\\"), "./#Inter, Microsoft JhengHei UI, Segoe UI, Malgun Gothic");

        // 時鐘可以換成自己電腦上的字型：fonts\local 資料夾（不進 repo）裡的 clock.ttf 給時間的大數字、date.ttf 給日期那一行，有就用、沒有就用 Inter。
        // 給有授權限制、不能跟著程式散佈的字型用；字型檔要是已經定好粗細、寬度的一般字型（可變字型請先切出來），粗細照字型檔本身
        static readonly FontFamily ClockFont = LocalFont("clock", CardFont, CardFallback), DateFont = LocalFont("date", CardFont, CardFallback);
        static readonly bool clockFontLocal = ClockFont != CardFont, dateFontLocal = DateFont != CardFont;
        const string CardFallback = ", Microsoft JhengHei UI, Segoe UI, Malgun Gothic";   // 字型裡沒有的字（中文等）照 CardFont 的順序找

        // fonts\local 裡的 name.ttf（或 .otf）；沒有、讀不了就用 fallback。字型裡沒有的字照 chain 的順序找
        static FontFamily LocalFont(string name, FontFamily fallback, string chain)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts", "local");
                foreach (string ext in new[] { ".ttf", ".otf" })
                {
                    string file = name + ext, path = Path.Combine(dir, file);
                    if (!File.Exists(path)) continue;
                    foreach (var family in new GlyphTypeface(new Uri(path)).FamilyNames.Values)   // 這個檔案自己的字型名稱（Fonts.GetFontFamilies 會列出整個資料夾的）
                        return new FontFamily(new Uri(dir + "\\"), "./" + file + "#" + family + chain);
                }
            }
            catch { }
            return fallback;
        }

        // 圖的四周留給陰影的空間
        public static int ShadowPad(int screenHeight) { return (int)Math.Ceiling(30 * screenHeight / 1080.0); }

        // 卡片內距；歌詞跟卡片裡的內容左右對齊，所以寬度＝卡片寬度扣掉兩邊內距。
        // 圓角 34（比封面的圓角 10 + 內距 16 = 26 的同心圓角大一點，看起來比較圓潤）
        public static double CardPadding(double s) { return Math.Round(16 * s); }
        public static double CardRadius(double s) { return Math.Round(34 * s); }

        // 卡片邊框線的寬度（見 DrawChrome 的玻璃邊緣）
        const double EdgeWidth = 1.5;
        public static double LyricsWidth(double s) { return Math.Round(368 * s); }
        // 放大歌詞的版面（照 Apple Music 的全螢幕歌詞：慢慢看歌詞，自己的桌布當底）。以下是 1080p 的像素：
        //   歌詞沒有框，直接排在輕糊的桌布上：靠左（左邊 BigLyricsLeft）、最寬 BigLyricsMaxW、字 20 × BigLyrics（約 72，粗體）；
        //   露出的範圍從 BigViewTop 開始、高 BigViewH（目前這行在中間，約畫面高度的 43%），上下柔和地淡出；
        //   左上角一小列封面、歌名、歌手（上緣 BigInfoTop）；
        //   進度條和播放鍵在底部中央的玻璃膠囊（BigCapsuleW × BigCapsuleH，以膠囊的縮放 BigControls 為準；離下緣 BigCapsuleBottom），
        //   滑鼠不動約 3 秒就淡出，一動又出來（見 LockScreen 的 ShowControls）
        public const double BigControls = 1.15, BigLyrics = 3.6;
        public const double BigLyricsLeft = 160, BigLyricsMaxW = 1600, BigViewTop = 160, BigViewH = 600, BigInfoTop = 64;
        public const double BigCapsuleW = 560, BigCapsuleH = 114, BigCapsuleDy = -84, BigCapsuleBottom = 56;

        // 放大時歌詞的長圖：字照多高的螢幕放大、一行多寬（像素）
        public static int BigLyricsHeight(int screenHeight) { return (int)Math.Round(screenHeight * BigLyrics); }
        public static int BigLyricsWidth(int screenWidth, int screenHeight)
        {
            double s = screenHeight / 1080.0;
            return (int)Math.Round(Math.Min(BigLyricsMaxW * s, screenWidth - 2 * BigLyricsLeft * s));
        }

        // 歌詞露出的高度（大約 5 行）、歌詞顯示時卡片的玻璃往上延伸多少（歌詞本身扣掉疊進卡片的 12、上面再留 16）
        // s：歌詞的字的縮放；cardScale：卡片的縮放
        public static int LyricsView(double s) { return (int)Math.Round(200 * s); }
        public static int LyricsExtension(double s, double cardScale) { return LyricsView(s) + (int)Math.Round(4 * cardScale); }

        // 把內容加上陰影畫成一張半透明圖（周圍留 pad 的空間給陰影）
        // shadow = false：不加陰影（歌詞在卡片的玻璃裡，跟卡片上的字一樣不加）
        static int[] ToPixels(FrameworkElement content, double s, double pad, out Border root, out int width, out int height, bool shadow = true)
        {
            Wrap(content, s, pad, out root, out width, out height, shadow);
            return Snapshot(root, width, height);
        }


        static void Wrap(FrameworkElement content, double s, double pad, out Border root, out int width, out int height, bool shadow = true)
        {
            if (shadow) content.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18 * s, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black };
            root = new Border { Padding = new Thickness(pad), Child = content, UseLayoutRounding = true };
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            width = (int)Math.Ceiling(root.DesiredSize.Width);
            height = (int)Math.Ceiling(root.DesiredSize.Height);
        }

        static int[] Snapshot(Visual root, int width, int height, int scale = 1)
        {
            // scale：放大幾倍畫（版面不變，只是像素變多；算字形距離圖時用，邊緣才準）
            width *= scale; height *= scale;
            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var pixels = new int[width * height];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        // 字和圖示很淡、很寬的暗色柔光（Apple 鎖定畫面的字一直帶著）：深色桌布上看不出來，白色、很亮的桌布上白字才看得清楚
        static System.Windows.Media.Effects.Effect Halo(double s)
        {
            return new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8 * s, ShadowDepth = 0, Opacity = 0.22, Color = Colors.Black };
        }

        // Apple 的連續曲率圓角（跟液態玻璃的 SdBox 同一條曲線：從離角 1.5 倍圓角的地方開始彎的四次方超橢圓）。
        // 圓角太大、放不下（1.5 倍超過短邊的一半）就用一般的圓角
        public static Geometry SmoothRect(Rect rect, double radius)
        {
            double rc = 1.5 * radius;
            if (radius <= 0 || rc > Math.Min(rect.Width, rect.Height) / 2 - 1) return new RectangleGeometry(rect, radius, radius);
            // 四個角（順時針：左上、右上、右下、左下）：角的中心、往外的方向、從哪一端走到哪一端
            var corners = new[]
            {
                new { X = rect.Left + rc, Y = rect.Top + rc, Sx = -1, Sy = -1, Reverse = false },
                new { X = rect.Right - rc, Y = rect.Top + rc, Sx = 1, Sy = -1, Reverse = true },
                new { X = rect.Right - rc, Y = rect.Bottom - rc, Sx = 1, Sy = 1, Reverse = false },
                new { X = rect.Left + rc, Y = rect.Bottom - rc, Sx = -1, Sy = 1, Reverse = true },
            };
            const int steps = 24;
            var points = new PointCollection();
            foreach (var c in corners)
                for (int i = 0; i <= steps; i++)
                {
                    double t = (c.Reverse ? steps - i : i) * Math.PI / 2 / steps;   // 超橢圓的參數式：(cos t)^½、(sin t)^½
                    points.Add(new Point(c.X + c.Sx * rc * Math.Sqrt(Math.Cos(t)), c.Y + c.Sy * rc * Math.Sqrt(Math.Sin(t))));
                }
            var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
            figure.Segments.Add(new PolyLineSegment(points, true));
            var geometry = new PathGeometry(new[] { figure });
            geometry.Freeze();
            return geometry;
        }

        // 把 element 排成 width × height 再畫成像素（預先乘好 alpha 的 BGRA）
        static int[] Paint(FrameworkElement element, int width, int height)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            return Snapshot(element, width, height);
        }
    }
}
