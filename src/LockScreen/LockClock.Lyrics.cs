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
        public static int[] RenderLyricsSheet(int screenHeight, List<Lyrics.Line> lines, out int width, out int height, out double[] centers, out double[] heights, out double spacing, out int[] blurred)
        {
            double s = screenHeight / 1080.0;
            double pad = ShadowPad(screenHeight);
            double boxWidth = LyricsWidth(s), gap = Math.Round(7 * s);
            var canvas = new Canvas { Width = boxWidth };
            centers = new double[lines.Count];
            heights = new double[lines.Count];
            double next = 0;
            for (int j = 0; j < lines.Count; j++)
            {
                var block = LyricsBlock(lines[j].Text.Length == 0 ? " " : lines[j].Text, s, boxWidth);
                double h = block.DesiredSize.Height;
                Canvas.SetTop(block, next);
                canvas.Children.Add(block);
                centers[j] = pad + next + h / 2;
                heights[j] = h;
                next += h + gap;
            }
            canvas.Height = Math.Max(1, next - gap);
            spacing = LyricsBlock("A", s, boxWidth).DesiredSize.Height + gap;
            Border root;
            canvas.Effect = Halo(s);   // 在卡片的玻璃裡：跟卡片上的字一樣只有很淡的柔光（不加陰影）
            var pixels = ToPixels(canvas, s, pad, out root, out width, out height, false);
            // 糊掉的版本：整張（連陰影）再糊一次。很長的歌、顯示卡的效果做不出來就不要（歌詞照樣顯示，只是不糊）
            blurred = null;
            try
            {
                root.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 7 * s, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
                var holder = new Border { Child = root };
                holder.Measure(new Size(width, height));
                holder.Arrange(new Rect(0, 0, width, height));
                blurred = Snapshot(holder, width, height);
            }
            catch { blurred = null; }
            return pixels;
        }

        static TextBlock LyricsBlock(string text, double s, double width)
        {
            var block = new TextBlock
            {
                Text = text, FontFamily = LyricsFont, FontSize = Math.Round(20 * s), FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
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
                // 靠近邊緣再淡出：上緣長一點、平順地淡出（像消失在玻璃裡）；下緣短短一段，才接得上卡片的內容
                double top = Math.Min(1, row / (spacing * 0.8)), bottom = Math.Min(1, (viewHeight - 1 - row) / (spacing * 0.45));
                double edge = Math.Min(top * top * (3 - 2 * top), bottom);
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

        // 離目前這行 0、1、2、3、4 行的亮度（再遠就看不到）和糊的程度：目前這行跟其他行主次分明（Apple Music 其他行大約 35 ～ 45%）；
        // 還沒唱到的行（下面）比唱過的（上面）亮一點（Apple Music 的樣子；下面一直有字，才跟卡片的內容接成一塊）。
        // 糊的程度很快就到底：清楚和糊掉兩張疊在一起（看起來有重影）的範圍短一點
        static readonly double[] ShadeLevels = { 1.0, 0.42, 0.28, 0.14, 0 }, UpcomingLevels = { 1.0, 0.5, 0.38, 0.26, 0.14 }, BlurLevels = { 0, 0.55, 0.9, 1, 1 };

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
