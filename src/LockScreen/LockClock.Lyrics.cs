using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VideoWallpaper
{
    // 音樂卡片上方的歌詞長圖和亮度遮罩
    static partial class LockClock
    {
        // 歌詞：整首歌一次畫成一張長圖（白字、靠左、跟音樂卡片一樣寬、太長自動折行，不加陰影），
        // 捲動和淡入淡出交給播放引擎，這樣每一格只是貼圖，不用重畫，捲動才會順。
        // centers：每一行中心在長圖上的高度；heights：每一行的高度（折成好幾列的行比較高）；
        // spacing：單行歌詞的行距（亮度遮罩、還沒開始唱時的位置用）；blurred：同一張長圖糊掉的版本（畫不出來是 null）。
        // 空的行（間奏）畫成空白：唱到那裡時由播放引擎在那一行畫三個點的動畫（見 VideoEngine.SetLyricsDots）
        // screenHeight：字照多高的螢幕放大；boxWidth：一行多寬（像素；0 = 跟一般大小的卡片一樣寬）
        // glyphs：放大歌詞（boxWidth > 0）每一行的字在長圖上的位置（逐字亮用）；一般大小的是 null
        public static int[] RenderLyricsSheet(int screenHeight, int boxWidth, List<Lyrics.Line> lines, out int width, out int height, out double[] centers, out double[] heights, out double spacing, out int[] blurred, out LyricsGlyphs[] glyphs)
        {
            double s = screenHeight / 1080.0;
            double pad = ShadowPad(screenHeight);
            bool stage = boxWidth > 0;   // 有指定寬度的是放大歌詞（Apple Music 全螢幕歌詞是粗體）
            if (boxWidth <= 0) boxWidth = (int)LyricsWidth(s);
            var weight = stage ? FontWeights.Bold : FontWeights.SemiBold;
            double gap = Math.Round(7 * s);
            var canvas = new Canvas { Width = boxWidth };
            centers = new double[lines.Count];
            heights = new double[lines.Count];
            glyphs = stage ? new LyricsGlyphs[lines.Count] : null;
            double next = 0;
            for (int j = 0; j < lines.Count; j++)
            {
                string text = lines[j].Text.Length == 0 ? " " : lines[j].Text;
                // 放大歌詞：一行放不下的，字稍微縮小到剛好一行（最多縮到 72%），再長的才換行
                double k = 1;
                if (stage)
                {
                    double natural = LyricsNaturalWidth(text, s, weight);
                    if (natural > boxWidth) k = Math.Max(0.72, (boxWidth - 1) / natural);
                }
                var block = LyricsBlock(text, s * k, boxWidth, weight);
                double h = block.DesiredSize.Height;
                if (stage && glyphs != null)
                {
                    try { glyphs[j] = Glyphs(lines[j].Text, s * k, boxWidth, weight, pad, pad + next); }
                    catch { glyphs[j] = null; }   // 量不出來（怪字元）：這行沒有逐字亮，歌詞照常顯示
                }
                Canvas.SetTop(block, next);
                canvas.Children.Add(block);
                centers[j] = pad + next + h / 2;
                heights[j] = h;
                next += h + gap;
            }
            canvas.Height = Math.Max(1, next - gap);
            spacing = LyricsBlock("A", s, boxWidth, weight).DesiredSize.Height + gap;
            Border root;
            // 字先不加效果畫出來；柔光和糊掉的版本自己算（WPF 的效果畫成圖時是一格一格用 CPU 算的，
            // 放大歌詞的長圖有一千多萬個點，加效果要畫十秒左右；自己用多執行緒的方框模糊算不到一秒）
            var pixels = ToPixels(canvas, s, pad, out root, out width, out height, false);
            // 柔光：跟卡片上的字一樣很淡（Halo：黑、半徑 8、22%），墊在字的下面
            AddHalo(pixels, width, height, Math.Max(1, (int)Math.Round(8 * s / 3)), 0.22);
            // 糊掉的版本：整張（連柔光）再糊一次（半徑 7）。記憶體不夠之類的就不要（歌詞照樣顯示，只是不糊）
            blurred = null;
            try { blurred = Blurred(pixels, width, height, Math.Max(1, (int)Math.Round(7 * s / 3))); }
            catch { blurred = null; }
            return pixels;
        }

        // 方框模糊連做三次（很接近高斯模糊；r 大約是高斯半徑的三分之一）：a 是一個通道（0～255），tmp 是一樣大的暫存。
        // 一列一列、一行一行各自獨立，分給多個執行緒做
        static void BoxBlur3(byte[] a, byte[] tmp, int w, int h, int r)
        {
            int win = 2 * r + 1;
            for (int pass = 0; pass < 3; pass++)
            {
                System.Threading.Tasks.Parallel.For(0, h, y =>
                {
                    int row = y * w, sum = 0;
                    for (int i = -r; i <= r; i++) sum += a[row + Math.Max(0, Math.Min(w - 1, i))];
                    for (int x = 0; x < w; x++)
                    {
                        tmp[row + x] = (byte)(sum / win);
                        sum += a[row + Math.Min(w - 1, x + r + 1)] - a[row + Math.Max(0, x - r)];
                    }
                });
                System.Threading.Tasks.Parallel.For(0, w, x =>
                {
                    int sum = 0;
                    for (int i = -r; i <= r; i++) sum += tmp[Math.Max(0, Math.Min(h - 1, i)) * w + x];
                    for (int y = 0; y < h; y++)
                    {
                        a[y * w + x] = (byte)(sum / win);
                        sum += tmp[Math.Min(h - 1, y + r + 1) * w + x] - tmp[Math.Max(0, y - r) * w + x];
                    }
                });
            }
        }

        // 黑色的柔光墊在字的下面（圖是預乘透明度的 BGRA）：柔光的透明度 = 字的透明度糊開 × opacity
        static void AddHalo(int[] px, int w, int h, int r, double opacity)
        {
            int n = px.Length;
            var a = new byte[n];
            for (int i = 0; i < n; i++) a[i] = (byte)((uint)px[i] >> 24);
            BoxBlur3(a, new byte[n], w, h, r);
            int k = (int)Math.Round(opacity * 256);
            for (int i = 0; i < n; i++)
            {
                int ta = (int)((uint)px[i] >> 24), sa = a[i] * k >> 8;
                int oa = ta + (255 - ta) * sa / 255;   // 黑色：顏色不用動（預乘），只有透明度變
                px[i] = (px[i] & 0x00FFFFFF) | (oa << 24);
            }
        }

        // 整張圖糊掉的版本（四個通道各自糊，預乘透明度的圖直接糊就對了）
        static int[] Blurred(int[] px, int w, int h, int r)
        {
            int n = px.Length;
            var result = new int[n];
            var ch = new byte[n];
            var tmp = new byte[n];
            for (int c = 0; c < 4; c++)
            {
                int shift = 8 * c;
                for (int i = 0; i < n; i++) ch[i] = (byte)((uint)px[i] >> shift);
                BoxBlur3(ch, tmp, w, h, r);
                for (int i = 0; i < n; i++) result[i] |= ch[i] << shift;
            }
            return result;
        }

        // 放大歌詞一行裡每個字在長圖上的位置（逐字亮用）：Rows 是這行每一列（折行）的上下緣（長圖的列，兩個一組）；
        // Text 的每個 UTF-16 字元在第幾列（CharRow；-1 = 沒有寬度：代理對的後半、換行處的空白）、左右（X0、X1，長圖的 x）
        public class LyricsGlyphs
        {
            public float[] Rows;
            public int[] CharRow;
            public float[] X0, X1;
        }

        // 照跟 LyricsBlock 一樣的字型、大小、寬度排一次版，量每個字的位置；(left, top) 是這行在長圖上的左上角
        static LyricsGlyphs Glyphs(string text, double s, double width, FontWeight weight, double left, double top)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var family = weight == FontWeights.Bold ? BigLyricsFont : LyricsFont;
            var ft = new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("en-us"), FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), Math.Round(20 * s), Brushes.White);
            ft.MaxTextWidth = width;
            ft.TextAlignment = TextAlignment.Left;
            var g = new LyricsGlyphs { CharRow = new int[text.Length], X0 = new float[text.Length], X1 = new float[text.Length] };
            var tops = new List<double>();
            var bottoms = new List<double>();
            for (int i = 0; i < text.Length; i++)
            {
                g.CharRow[i] = -1;
                if (char.IsLowSurrogate(text[i])) continue;
                int count = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                var geo = ft.BuildHighlightGeometry(new Point(0, 0), i, count);
                if (geo == null) continue;
                var b = geo.Bounds;
                if (b.IsEmpty || b.Width <= 0) continue;
                // 第幾列：上下範圍跟哪一列重疊就是那一列（中英混排時備援字型的字高度可能不一樣，不能只比上緣）
                int row = -1;
                for (int r = 0; r < tops.Count; r++) if (b.Top < bottoms[r] - 1 && b.Bottom > tops[r] + 1) { row = r; break; }
                if (row < 0) { tops.Add(b.Top); bottoms.Add(b.Bottom); row = tops.Count - 1; }
                else { tops[row] = Math.Min(tops[row], b.Top); bottoms[row] = Math.Max(bottoms[row], b.Bottom); }
                g.CharRow[i] = row;
                g.X0[i] = (float)(left + b.Left);
                g.X1[i] = (float)(left + b.Right);
            }
            if (tops.Count == 0) return null;
            g.Rows = new float[tops.Count * 2];
            for (int r = 0; r < tops.Count; r++) { g.Rows[2 * r] = (float)(top + tops[r]); g.Rows[2 * r + 1] = (float)(top + bottoms[r]); }
            return g;
        }

        // 這行歌詞不換行時有多寬
        static double LyricsNaturalWidth(string text, double s, FontWeight weight)
        {
            var block = LyricsBlock(text, s, 100000, weight);
            block.TextWrapping = TextWrapping.NoWrap;
            block.Width = double.NaN;
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return block.DesiredSize.Width;
        }

        static TextBlock LyricsBlock(string text, double s, double width, FontWeight weight)
        {
            var block = new TextBlock
            {
                Text = text, FontFamily = weight == FontWeights.Bold ? BigLyricsFont : LyricsFont, FontSize = Math.Round(20 * s), FontWeight = weight, Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left, Width = width,
            };
            TextOptions.SetTextFormattingMode(block, TextFormattingMode.Ideal);
            block.Measure(new Size(width, double.PositiveInfinity));
            return block;
        }

        // 整體的不透明度：目前這行幾乎全白（95%，Apple Music 是全白），其他行照比例更淡
        const double LyricsOpacity = 0.95;

        // 歌詞露出那一段每一列的亮度：靠近上下邊緣淡出。
        // highlight = true（有時間標記）：哪一行亮由 LyricsShade 決定，這裡只管邊緣；
        // highlight = false：一般歌詞沒有時間標記，不強調哪一行，中間一段一樣亮、往上下淡出
        public static float[] LyricsMask(int viewHeight, double spacing, bool highlight)
        {
            var mask = new float[viewHeight];
            for (int row = 0; row < viewHeight; row++)
            {
                double lines = Math.Abs(row + 1 - viewHeight / 2.0) / spacing;          // 離中間幾行
                // 靠近邊緣再淡出，兩邊都是平順的曲線、都超過一行：換行往上捲的那一下（剛好捲一行），
                // 最上面那行是一路慢慢消失在玻璃上緣、最下面那行從封面上方一路慢慢浮現，不會在某個位置突然變淡、突然冒出來。
                // 上緣長一點（像消失在玻璃裡）；下緣短一點，才接得上卡片的內容
                double top = Math.Min(1, row / (spacing * 1.2)), bottom = Math.Min(1, (viewHeight - 1 - row) / (spacing * 1.0));
                double edge = Math.Min(top * top * (3 - 2 * top), bottom * bottom * (3 - 2 * bottom));
                double level = highlight ? 1 : 0.75 * Math.Max(0, Math.Min(1, (3 - lines) / 1.5));
                mask[row] = (float)(level * edge * LyricsOpacity);
            }
            return mask;
        }

        // 有時間標記的歌詞唱到第 line 行（-1 = 還沒開始唱）時，長圖每一列的亮度，跟著長圖一起捲動。
        // 以「行」為單位：目前這行不管折成幾列都一樣亮，其他行照離目前這行幾行越來越淡，行和行之間的空隙慢慢過渡
        public static float[] LyricsShade(int sheetHeight, double[] centers, double[] heights, double spacing, int line)
        {
            return PerLine(sheetHeight, centers, heights, spacing, line, ShadeLevels, UpcomingLevels);
        }

        // 同樣以「行」為單位：每一列要糊多少（0 清楚、1 全糊）。Apple Music 的樣子：目前這行清楚，離越遠越糊
        public static float[] LyricsBlur(int sheetHeight, double[] centers, double[] heights, double spacing, int line)
        {
            return PerLine(sheetHeight, centers, heights, spacing, line, BlurLevels, BlurLevels);
        }

        // 長圖每一列屬於哪一行、那行的中心（長圖的列）：行的範圍內就是那行；行跟行之間的空白從正中間分給上下兩行
        public static float[] LyricsAnchors(int sheetHeight, double[] centers, double[] heights)
        {
            var anchors = new float[sheetHeight];
            int n = centers.Length, j = 0;
            for (int row = 0; row < sheetHeight; row++)
            {
                double y = row + 0.5;
                while (j + 1 < n && y > (centers[j] + heights[j] / 2 + centers[j + 1] - heights[j + 1] / 2) / 2) j++;
                anchors[row] = (float)(n == 0 ? y : centers[j]);
            }
            return anchors;
        }

        // 唱到第 line 行時長圖每一列的大小（Apple Music：唱到的那行正常大小，其他行小一點；換行時跟亮度一起慢慢變過去）
        public static float[] LyricsZoom(int sheetHeight, double[] centers, double[] heights, int line)
        {
            var zoom = new float[sheetHeight];
            int n = centers.Length, j = 0;
            for (int row = 0; row < sheetHeight; row++)
            {
                double y = row + 0.5;
                while (j + 1 < n && y > (centers[j] + heights[j] / 2 + centers[j + 1] - heights[j + 1] / 2) / 2) j++;
                zoom[row] = (float)(j == line ? 1 : OtherLineZoom);
            }
            return zoom;
        }

        const double OtherLineZoom = 0.94;

        // 離目前這行 0、1、2、3、4 行的亮度（再遠就看不到）和糊的程度：目前這行跟其他行主次分明（Apple Music 其他行大約 35 ～ 45%）；
        // 還沒唱到的行（下面）比唱過的（上面）亮一點（Apple Music 的樣子；下面一直有字，才跟卡片的內容接成一塊）。
        // 糊的程度很快就到底：清楚和糊掉兩張疊在一起（看起來有重影）的範圍短一點
        static readonly double[] ShadeLevels = { 1.0, 0.42, 0.28, 0.14, 0 }, UpcomingLevels = { 1.0, 0.5, 0.38, 0.26, 0.14 }, BlurLevels = { 0, 0.55, 0.9, 1, 1 };
        // 放大歌詞時自己捲（挑要跳到哪一行）：每一行都看得清楚，正在唱的那行最亮
        static readonly double[] BrowseLevels = { 1.0, 0.55, 0.5 };

        public static float[] LyricsBrowseShade(int sheetHeight, double[] centers, double[] heights, double spacing, int line)
        {
            return PerLine(sheetHeight, centers, heights, spacing, line, BrowseLevels, BrowseLevels);
        }

        // 長圖每一列照「離目前這行幾行」查 levels（行和行之間的空隙慢慢過渡）
        // （levels 是目前這行上面、唱過的行；upcoming 是下面還沒唱到的）
        static float[] PerLine(int sheetHeight, double[] centers, double[] heights, double spacing, int line, double[] levels, double[] upcoming)
        {
            var shade = new float[sheetHeight];
            int n = centers.Length, j = 0;
            for (int row = 0; row < sheetHeight; row++)
            {
                double y = row + 0.5;
                while (j + 1 < n && y >= centers[j + 1] - heights[j + 1] / 2) j++;   // 第 j 行是開頭在這一列上面的最後一行
                double top = centers[j] - heights[j] / 2, bottom = centers[j] + heights[j] / 2, at;
                if (y < top) at = j - (top - y) / spacing;                                                  // 第一行上面
                else if (y <= bottom) at = j;                                                               // 第 j 行的字裡面
                else if (j + 1 < n) at = j + (y - bottom) / (centers[j + 1] - heights[j + 1] / 2 - bottom);   // 和下一行之間的空隙
                else at = j + (y - bottom) / spacing;                                                       // 最後一行下面
                shade[row] = (float)Level(at > line ? upcoming : levels, Math.Abs(at - line));
            }
            return shade;
        }

        // 離目前這行 distance 行時的值（levels 之間線性內插，超過最後一個就用最後一個）
        static double Level(double[] levels, double distance)
        {
            if (distance <= 0) return levels[0];
            if (distance >= levels.Length - 1) return levels[levels.Length - 1];
            int i = (int)Math.Floor(distance);
            return levels[i] + (levels[i + 1] - levels[i]) * (distance - i);
        }
    }
}
