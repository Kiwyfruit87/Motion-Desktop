using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoWallpaper
{
    // 音樂卡片的專輯封面和顏色（封面、帶顏色的邊框和光暈）
    static partial class LockClock
    {
        // ---------- 專輯封面 ----------
        // 讀封面圖檔（width：解碼成多寬）；沒有或讀不出來回傳 null
        static BitmapImage DecodeArt(byte[] data, int width)
        {
            if (data == null) return null;
            try
            {
                var art = new BitmapImage();
                art.BeginInit();
                art.StreamSource = new MemoryStream(data);
                art.CacheOption = BitmapCacheOption.OnLoad;
                art.DecodePixelWidth = width;
                art.EndInit();
                art.Freeze();
                return art;
            }
            catch { return null; }
        }

        // 封面：跟卡片其他圖一樣大小、一樣位置的一張圖，72 見方、圓角 10，底下淡淡的影子（往下 2、很柔）。
        // 沒有封面：半透明方塊和音符
        static int[] RenderCover(MusicCard card, BitmapSource art)
        {
            double s = card.Scale, size = Math.Round(ArtSize * s), radius = ArtRadius * s;
            var image = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(radius),
                Margin = new Thickness(card.InnerX - card.X + CardPad * s, card.InnerY - card.Y + CardPad * s, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            if (art != null)
            {
                image.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
                image.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12 * s, ShadowDepth = 2 * s, Direction = 270, Opacity = 0.35, Color = Colors.Black };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            }
            else
            {
                image.Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
                image.Child = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 30 * s, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            var root = new Grid { Width = card.Width, Height = card.Height, UseLayoutRounding = true };
            root.Children.Add(image);
            return Paint(root, card.Width, card.Height);
        }

        // ---------- 卡片顏色（跟著專輯封面）----------
        // 封面縮小後依色相統計（越鮮豔、越亮的點越算數，接近黑、白、灰的不算），找出最多的色相，
        // 再找第二個顏色：有明顯的另一個色系（差 60° 以上）就用它，不然用旁邊 20°～50° 的相近色相。
        // 回傳 {主色相, 主飽和度, 第二色相, 第二飽和度, 是不是兩個色系（1 / 0）}；封面幾乎是黑白的就回傳 null
        static byte[] huesArt;
        static double[] huesCache;
        static double[] AlbumHues(byte[] art)
        {
            if (art == null) return null;
            if (art != huesArt)
            {
                huesArt = art;
                var image = DecodeArt(art, 48);
                try { huesCache = image == null ? null : HuesOf(image); }
                catch { huesCache = null; }
            }
            return huesCache;
        }

        static double[] HuesOf(BitmapSource image)
        {
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new int[w * h];
            bgra.CopyPixels(pixels, w * 4, 0);

            const int bins = 36;   // 每 10° 一格
            var weight = new double[bins];
            var cosSum = new double[bins];
            var sinSum = new double[bins];
            var satSum = new double[bins];
            double total = 0;
            foreach (int p in pixels)
            {
                double r = ((p >> 16) & 255) / 255.0, g = ((p >> 8) & 255) / 255.0, b = (p & 255) / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
                double sat = max <= 0 ? 0 : d / max;
                if (sat < 0.18 || max < 0.18) continue;
                double hue = max == r ? 60 * ((g - b) / d) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
                if (hue < 0) hue += 360;
                double wgt = sat * max;
                int bin = Math.Min(bins - 1, (int)(hue / 10));
                weight[bin] += wgt;
                cosSum[bin] += wgt * Math.Cos(hue * Math.PI / 180);
                sinSum[bin] += wgt * Math.Sin(hue * Math.PI / 180);
                satSum[bin] += wgt * sat;
                total += wgt;
            }
            if (total / pixels.Length >= 0.05)
            {
                // 相鄰的格子一起算，找最多的那一段
                Func<int, double> around = i => weight[(i + bins - 1) % bins] * 0.5 + weight[i] + weight[(i + 1) % bins] * 0.5;
                int peak = 0;
                for (int i = 1; i < bins; i++) if (around(i) > around(peak)) peak = i;
                // 另一個色系：離主色相 60° 以上、份量至少有主色的四分之一（例如藍色封面上的黃字）
                int other = -1;
                for (int i = 0; i < bins; i++)
                {
                    int distance = Math.Min(Math.Abs(i - peak), bins - Math.Abs(i - peak));
                    if (distance >= 6 && (other < 0 || around(i) > around(other))) other = i;
                }
                bool two = other >= 0 && around(other) >= 0.25 * around(peak);
                // 沒有另一個色系的話，找第二多的相近色相：離主色相 2～5 格（20°～50°）
                int near = -1;
                for (int k = 2; k <= 5; k++)
                    foreach (int i in new[] { (peak + k) % bins, (peak - k + bins) % bins })
                        if (near < 0 || around(i) > around(near)) near = i;
                Func<int, double[]> mean = c =>
                {
                    double cx = 0, sy = 0, ss = 0, ww = 0;
                    for (int j = -1; j <= 1; j++) { int i = (c + j + bins) % bins; cx += cosSum[i]; sy += sinSum[i]; ss += satSum[i]; ww += weight[i]; }
                    double hue = Math.Atan2(sy, cx) * 180 / Math.PI;
                    return new[] { hue < 0 ? hue + 360 : hue, ww > 0 ? ss / ww : 0.5 };
                };
                var main = mean(peak);
                // 封面上沒有明顯的第二個顏色：自己往旁邊偏 28° 配一個
                var second = two ? mean(other) : around(near) >= 0.2 * around(peak) ? mean(near) : new[] { (main[0] + 28) % 360, main[1] };
                return new[] { main[0], main[1], second[0], second[1], two ? 1.0 : 0.0 };
            }
            return null;
        }

        // 色相、飽和度、亮度（HSL）→ 顏色
        static Color Hsl(double hue, double sat, double light, byte alpha)
        {
            hue = ((hue % 360) + 360) % 360;
            double c = (1 - Math.Abs(2 * light - 1)) * sat, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = light - c / 2;
            double r = 0, g = 0, b = 0;
            if (hue < 60) { r = c; g = x; } else if (hue < 120) { r = x; g = c; } else if (hue < 180) { g = c; b = x; }
            else if (hue < 240) { g = x; b = c; } else if (hue < 300) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromArgb(alpha, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        // 封面沒有顏色（黑白的、沒有封面、封面還沒讀到）：用歌名算出一個固定的顏色，同一首歌每次都一樣
        static double[] SongHues(string songKey)
        {
            uint hash = 2166136261;   // FNV-1a（不用 GetHashCode，那個每次執行可能不一樣）
            foreach (char ch in songKey) { hash ^= ch; hash *= 16777619; }
            double hue = hash % 360;
            return new[] { hue, 0.55, (hue + 32) % 360, 0.5, 0.0 };
        }

        // 卡片的顏色：重拍時邊框泛出的光暈（跟卡片其他圖一樣大小，不動；亮度由播放引擎照拍子調，見 Beat）
        public class CardColor
        {
            public int[] Glow;
        }

        // 光暈：邊框整圈泛出柔和的光，顏色從左上到右下跟著封面的兩個顏色（兩個色系就各佔一角，一個色系就是相近的兩個色相），
        // 往卡片裡外各散開一些，沒有銳利的亮線，看起來是一圈光慢慢暈開，不刺眼
        static CardColor RenderCardColor(double[] hues, int width, int height, double pad, double cardW, double cardH, double radius, double s)
        {
            double sat1 = Math.Max(0.35, Math.Min(0.8, hues[1] * 0.9)), sat2 = Math.Max(0.35, Math.Min(0.8, hues[3] * 0.9));
            bool two = hues.Length > 4 && hues[4] > 0;
            double[] ends = two ? new[] { hues[0], sat1, hues[2], sat2 } : new[] { hues[2], sat2, hues[0], sat1 };   // 左上、右下兩端的 {色相, 飽和度}
            Func<double, double, byte, Brush> tinted = (saturation, light, alpha) =>
            {
                var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                brush.GradientStops.Add(new GradientStop(Hsl(ends[0], Math.Max(saturation, ends[1]), light, alpha), 0));
                brush.GradientStops.Add(new GradientStop(Hsl(ends[2], Math.Max(saturation, ends[3]), light, alpha), 1));
                return brush;
            };
            var corner = new CornerRadius(radius);
            var shine = new Grid { Width = cardW, Height = cardH, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            shine.Children.Add(new Border { CornerRadius = corner, BorderBrush = tinted(0.6, 0.62, 0xA0), BorderThickness = new Thickness(Math.Max(2, 3 * s)),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 16 * s } });   // 散得比較開的光
            shine.Children.Add(new Border { CornerRadius = corner, BorderBrush = tinted(0.6, 0.7, 0x70), BorderThickness = new Thickness(Math.Max(1.5, 2 * s)),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 * s } });    // 貼著邊框的光，也是糊開的
            var glowRoot = new Border { Padding = new Thickness(pad), Child = shine, UseLayoutRounding = true };
            return new CardColor { Glow = Paint(glowRoot, width, height) };
        }
    }
}
