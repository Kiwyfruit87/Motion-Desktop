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
        // 跟 ChromeOS 一樣用 Google Sans（fonts 資料夾裡的 Google Sans Flex，開源授權；拉丁字母含各種帶符號的字母都有）。
        // Google Sans 沒有的字依序找：俄文、希臘文用 Segoe UI（有真的半粗體，粗細才跟旁邊的字一致），
        // 中文和日文假名用微軟正黑體，韓文用 Malgun Gothic
        static readonly FontFamily ClockFont = new FontFamily(
            new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts") + "\\"), "./#Google Sans Flex, Segoe UI, Microsoft JhengHei UI, Malgun Gothic");
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        // 圖的四周留給陰影的空間
        public static int ShadowPad(int screenHeight) { return (int)Math.Ceiling(30 * screenHeight / 1080.0); }

        // 卡片內距；歌詞跟卡片裡的內容左右對齊，所以寬度＝卡片寬度扣掉兩邊內距
        public static double CardPadding(double s) { return Math.Round(12 * s); }
        public static double CardRadius(double s) { return Math.Round(14 * s); }

        // 卡片邊框線的寬度（見 BuildCard 的玻璃邊緣）
        const double EdgeWidth = 1.5;
        public static double LyricsWidth(double s) { return Math.Round(284 * s); }

        // 把內容加上陰影畫成一張半透明圖（周圍留 pad 的空間給陰影）
        static int[] ToPixels(FrameworkElement content, double s, double pad, out Border root, out int width, out int height)
        {
            Wrap(content, s, pad, out root, out width, out height);
            return Snapshot(root, width, height);
        }

        static void Wrap(FrameworkElement content, double s, double pad, out Border root, out int width, out int height)
        {
            content.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18 * s, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black };
            root = new Border { Padding = new Thickness(pad), Child = content, UseLayoutRounding = true };
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            width = (int)Math.Ceiling(root.DesiredSize.Width);
            height = (int)Math.Ceiling(root.DesiredSize.Height);
        }

        static int[] Snapshot(Visual root, int width, int height)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var pixels = new int[width * height];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels;
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
