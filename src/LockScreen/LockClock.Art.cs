using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoWallpaper
{
    // 音樂卡片的專輯封面和顏色（模糊的封面、跟著封面色相的漸層、帶顏色的邊框和光暈）
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

        // 封面：跟卡片其他圖一樣大小、一樣位置的一張圖，封面放在 at（版面上留給它的那一格），size 見方、圓角。
        // 不加陰影：底下就是封面自己糊開的顏色（BlurredArt），看起來是嵌在卡片裡，不是浮在上面的照片
        static int[] RenderCover(BitmapSource art, int width, int height, Point at, double size, double s)
        {
            var image = new Border
            {
                Width = size, Height = size, Margin = new Thickness(at.X, at.Y, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(8 * s), Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill },
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var root = new Grid { Width = width, Height = height, UseLayoutRounding = true };
            root.Children.Add(image);
            return Paint(root, width, height);
        }

        // 卡片的底色：封面放大鋪滿、用很大的半徑糊成一片，只剩封面的顏色分布（封面左上藍、右下黃，卡片也是）。
        // 像 iPhone 的 Apple Music：再照封面的亮度調暗（亮的封面調暗多一點，白字才看得清楚），顏色加濃（糊掉之後會偏灰），
        // 幾乎不透明，只留一點點讓後面的毛玻璃透出來
        static int[] BlurredArt(BitmapSource art, int width, int height, double cardH)
        {
            double radius = Math.Round(cardH * 0.5);
            var image = new Border
            {
                Width = width + 2 * radius, Height = height + 2 * radius, Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill },
                Effect = new System.Windows.Media.Effects.BlurEffect
                {
                    Radius = radius, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality,
                },
            };
            Canvas.SetLeft(image, -radius);   // 四周多鋪一圈再裁掉，邊緣才不會糊成透明
            Canvas.SetTop(image, -radius);
            var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true, Background = Brushes.Black };
            canvas.Children.Add(image);
            var pixels = Paint(canvas, width, height);

            double total = 0;
            foreach (int p in pixels) total += 0.2126 * ((p >> 16) & 0xFF) + 0.7152 * ((p >> 8) & 0xFF) + 0.0722 * (p & 0xFF);
            double mean = total / pixels.Length / 255;
            const double Opacity = 0.94, Saturation = 1.6;
            double dim = Math.Max(0.2, Math.Min(0.8, 0.22 / Math.Max(0.01, mean))) * Opacity;   // 平均亮度調到大約 22%
            for (int i = 0; i < pixels.Length; i++)
            {
                int p = pixels[i];
                double r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
                double gray = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = Math.Max(0, Math.Min(255, gray + (r - gray) * Saturation)) * dim;
                g = Math.Max(0, Math.Min(255, gray + (g - gray) * Saturation)) * dim;
                b = Math.Max(0, Math.Min(255, gray + (b - gray) * Saturation)) * dim;
                pixels[i] = ((int)Math.Round(255 * Opacity) << 24) | ((int)Math.Round(r) << 16) | ((int)Math.Round(g) << 8) | (int)Math.Round(b);
            }
            return pixels;
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

        // 卡片的顏色：Texture 是比卡片大一圈（左右各多 MarginX、上下各多 MarginY）的漸層圖，
        // 播放引擎用它填滿卡片的圓角長方形、位置隨時間慢慢飄動；Edge 是帶顏色的邊框、Glow 是重拍時邊框泛出的光暈
        // （這兩張跟卡片其他圖一樣大小，不動）
        public class CardColor
        {
            public int[] Texture, Edge, Glow;
            public int TextureWidth, TextureHeight, MarginX, MarginY;
        }

        // 顏色都偏深、半透明：白字看得清楚，後面糊掉的影片也還透得出來。
        // 兩個色系：左上主色、右下另一個顏色，各佔一角，中間墊一層偏深的混色，柔和地接起來；
        // 一個色系：左上淺（相近色）→ 中間主色 → 右下深，左上角再一圈柔和的亮光。
        // 漸層的位置照「卡片停在正中間」的樣子安排，飄動時顏色就在卡片裡慢慢挪動。
        // 有封面的話不用這些漸層，直接用封面糊成一片的顏色（BlurredArt），封面和卡片才融在一起。
        // 邊框也帶一點旁邊卡片的顏色（淡很多、很亮，看得出是邊框；1.5 像素寬，所以透明度比 1 像素時低）
        static CardColor RenderCardColor(double[] hues, BitmapSource art, int width, int height, double pad, double cardW, double cardH, double s)
        {
            var color = new CardColor { MarginX = (int)Math.Round(cardW * 0.22), MarginY = (int)Math.Round(cardH * 0.4) };
            color.TextureWidth = (int)Math.Ceiling(cardW) + 2 * color.MarginX;
            color.TextureHeight = (int)Math.Ceiling(cardH) + 2 * color.MarginY;
            if (art != null) color.Texture = BlurredArt(art, color.TextureWidth, color.TextureHeight, cardH);
            double mx = color.MarginX, my = color.MarginY;
            var fill = art == null ? new Grid { Width = color.TextureWidth, Height = color.TextureHeight } : null;   // 沒有封面才需要畫漸層
            var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            double[] ends;   // 邊框左上、右下兩端的 {色相, 飽和度, 色相, 飽和度}（光暈也用）
            if (hues.Length > 4 && hues[4] > 0)
            {
                double sat1 = Math.Max(0.35, Math.Min(0.8, hues[1] * 0.9)), sat2 = Math.Max(0.35, Math.Min(0.8, hues[3] * 0.9));
                ends = new[] { hues[0], sat1, hues[2], sat2 };
                if (fill != null)
                {
                    Color first = Hsl(hues[0], sat1, CornerLight(hues[0]), 255), second = Hsl(hues[2], sat2, CornerLight(hues[2]), 255);
                    var mix = Color.FromArgb(0x80, (byte)((first.R + second.R) / 2 * 0.42), (byte)((first.G + second.G) / 2 * 0.42), (byte)((first.B + second.B) / 2 * 0.42));
                    fill.Children.Add(new Border { Background = new SolidColorBrush(mix) });
                    fill.Children.Add(new Border { Background = CornerGlow(first, mx, my, cardW, cardH) });
                    fill.Children.Add(new Border { Background = CornerGlow(second, mx + cardW, my + cardH, cardW, cardH) });
                }
                edge.GradientStops.Add(new GradientStop(Hsl(hues[0], sat1 * 0.75, 0.80, 0x60), 0));
                edge.GradientStops.Add(new GradientStop(Hsl(hues[2], sat2 * 0.75, 0.80, 0x60), 1));
            }
            else
            {
                double mainSat = Math.Max(0.3, Math.Min(0.75, hues[1] * 0.85)), nearSat = Math.Max(0.3, Math.Min(0.75, hues[3] * 0.85));
                ends = new[] { hues[2], nearSat, hues[0], mainSat };
                if (fill != null)
                {
                    double darkHue = hues[0] - 8 * Math.Sign(((hues[2] - hues[0] + 540) % 360) - 180);   // 深色往相近色的反方向偏一點
                    // 斜的漸層從卡片左上角到右下角；超出的地方延用兩端的顏色
                    var gradient = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(mx, my), EndPoint = new Point(mx + cardW, my + cardH) };
                    gradient.GradientStops.Add(new GradientStop(Hsl(hues[2], nearSat, 0.46, 0x9A), 0));
                    gradient.GradientStops.Add(new GradientStop(Hsl(hues[0], mainSat, 0.34, 0x8C), 0.55));
                    gradient.GradientStops.Add(new GradientStop(Hsl(darkHue, Math.Min(0.8, mainSat + 0.05), 0.18, 0xA8), 1));
                    var glowCenter = new Point(mx + 0.12 * cardW, my);
                    var glow = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = glowCenter, GradientOrigin = glowCenter, RadiusX = 0.75 * cardW, RadiusY = 1.4 * cardH };
                    glow.GradientStops.Add(new GradientStop(Hsl(hues[2], nearSat, 0.62, 0x40), 0));
                    glow.GradientStops.Add(new GradientStop(Hsl(hues[2], nearSat, 0.62, 0x00), 1));
                    fill.Children.Add(new Border { Background = gradient });
                    fill.Children.Add(new Border { Background = glow });
                }
                edge.GradientStops.Add(new GradientStop(Hsl(hues[2], nearSat * 0.7, 0.82, 0x5C), 0));
                edge.GradientStops.Add(new GradientStop(Hsl(hues[0], mainSat * 0.7, 0.72, 0x50), 1));
            }
            if (fill != null) color.Texture = Paint(fill, color.TextureWidth, color.TextureHeight);

            // 邊框：跟卡片其他圖一樣大小、一樣位置（再描一次玻璃邊緣，不然會被顏色蓋掉）
            var frame = new Border
            {
                Width = cardW, Height = cardH, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(CardRadius(s)), BorderBrush = edge, BorderThickness = new Thickness(EdgeWidth),
            };
            var root = new Border { Padding = new Thickness(pad), Child = frame, UseLayoutRounding = true };
            color.Edge = Paint(root, width, height);

            // 重拍時的光暈：邊框整圈泛出柔和的光（顏色跟邊框一樣，稍微亮一點、濃一點），往卡片裡外各散開一些，
            // 沒有銳利的亮線，看起來是一圈光慢慢暈開，不刺眼。平常看不到，播放引擎照拍子調亮度（見 Beat）
            Func<double, double, byte, Brush> tinted = (saturation, light, alpha) =>
            {
                var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                brush.GradientStops.Add(new GradientStop(Hsl(ends[0], Math.Max(saturation, ends[1]), light, alpha), 0));
                brush.GradientStops.Add(new GradientStop(Hsl(ends[2], Math.Max(saturation, ends[3]), light, alpha), 1));
                return brush;
            };
            var radius = new CornerRadius(CardRadius(s));
            var shine = new Grid { Width = cardW, Height = cardH, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            shine.Children.Add(new Border { CornerRadius = radius, BorderBrush = tinted(0.6, 0.62, 0xA0), BorderThickness = new Thickness(Math.Max(2, 3 * s)),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 16 * s } });   // 散得比較開的光
            shine.Children.Add(new Border { CornerRadius = radius, BorderBrush = tinted(0.6, 0.7, 0x70), BorderThickness = new Thickness(Math.Max(1.5, 2 * s)),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 * s } });    // 貼著邊框的光，也是糊開的
            var glowRoot = new Border { Padding = new Thickness(pad), Child = shine, UseLayoutRounding = true };
            color.Glow = Paint(glowRoot, width, height);
            return color;
        }

        // 兩個色系時每個角的亮度：黃、橘這類顏色太暗會變成土色，亮一點；藍紫色看起來比較暗，也亮一點
        static double CornerLight(double hue)
        {
            hue = ((hue % 360) + 360) % 360;
            return hue >= 35 && hue <= 80 ? 0.46 : hue > 200 && hue < 290 ? 0.42 : 0.38;
        }

        // 從 (cx, cy)（卡片的一個角）往內擴散、越來越淡的顏色；範圍各大約佔卡片的一半
        static Brush CornerGlow(Color color, double cx, double cy, double cardW, double cardH)
        {
            var center = new Point(cx, cy);
            var glow = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = center, GradientOrigin = center, RadiusX = 0.78 * cardW, RadiusY = 1.6 * cardH };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0xC8, color.R, color.G, color.B), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1));
            return glow;
        }
    }
}
