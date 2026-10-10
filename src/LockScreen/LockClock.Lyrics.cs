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
        // 歌詞：整首歌一次畫成一張長圖（白字、靠左、跟音樂卡片一樣寬、太長自動折行，帶陰影），
        // 捲動和淡入淡出交給播放引擎，這樣每一格只是貼圖，不用重畫，捲動才會順。
        // centers：每一行中心在長圖上的高度；heights：每一行的高度（折成好幾列的行比較高）；
        // spacing：單行歌詞的行距（亮度遮罩、還沒開始唱時的位置用）
        public static int[] RenderLyricsSheet(int screenHeight, List<Lyrics.Line> lines, out int width, out int height, out double[] centers, out double[] heights, out double spacing)
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
                var block = LyricsBlock(lines[j].Text.Length == 0 ? "♪" : lines[j].Text, s, boxWidth);
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
            return ToPixels(canvas, s, pad, out root, out width, out height);
        }

        static TextBlock LyricsBlock(string text, double s, double width)
        {
            var block = new TextBlock
            {
                Text = text, FontFamily = ClockFont, FontSize = Math.Round(20 * s), FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left, Width = width,
            };
            TextOptions.SetTextFormattingMode(block, TextFormattingMode.Ideal);
            block.Measure(new Size(width, double.PositiveInfinity));
            return block;
        }

        // 整體的不透明度：目前這行也是半透明的 8 成，其他行照比例更淡
        const double LyricsOpacity = 0.8;

        // 歌詞露出那一段每一列的亮度：靠近上下邊緣淡出。
        // highlight = true（有時間標記）：哪一行亮由 LyricsShade 決定，這裡只管邊緣；
        // highlight = false：一般歌詞沒有時間標記，不強調哪一行，中間一段一樣亮、往上下淡出
        public static float[] LyricsMask(int viewHeight, double spacing, bool highlight)
        {
            var mask = new float[viewHeight];
            for (int row = 0; row < viewHeight; row++)
            {
                double lines = Math.Abs(row + 1 - viewHeight / 2.0) / spacing;          // 離中間幾行
                double edge = Math.Min(1, Math.Min(row, viewHeight - 1 - row) / (spacing * 0.8));   // 靠近邊緣再淡出
                double level = highlight ? 1 : 0.75 * Math.Max(0, Math.Min(1, (3 - lines) / 1.5));
                mask[row] = (float)(level * edge * LyricsOpacity);
            }
            return mask;
        }

        // 有時間標記的歌詞唱到第 line 行（-1 = 還沒開始唱）時，長圖每一列的亮度，跟著長圖一起捲動。
        // 以「行」為單位：目前這行不管折成幾列都一樣亮，其他行照離目前這行幾行越來越淡，行和行之間的空隙慢慢過渡
        public static float[] LyricsShade(int sheetHeight, double[] centers, double[] heights, double spacing, int line)
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
                shade[row] = (float)Fade(Math.Abs(at - line));
            }
            return shade;
        }

        // 離目前這行越遠越淡：0 行 100%、1 行 60%、2 行 38%、3 行 18%，再遠就看不到
        static double Fade(double distance)
        {
            double[] levels = { 1.0, 0.60, 0.38, 0.18, 0 };
            if (distance <= 0) return levels[0];
            if (distance >= levels.Length - 1) return 0;
            int i = (int)Math.Floor(distance);
            return levels[i] + (levels[i + 1] - levels[i]) * (distance - i);
        }
    }
}
