using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace VideoWallpaper
{
    // 畫面上方置中的時鐘（照 macOS 的鎖定畫面：上面日期和天氣、下面大的時間）。字都是液態玻璃（像 iOS 26 鎖定畫面的「玻璃」時鐘），
    // 由播放引擎照字形距離圖畫
    static partial class LockClock
    {
        // 時鐘的兩張圖（同樣大小、同樣位置）
        public class ClockImage
        {
            public int[] Solid;     // 實心的白字（畫不了玻璃時用，例如顯示卡太舊）
            public float[] Field;   // 字形距離圖，每個點四個值：離字的邊多遠（像素，字裡面是負的）、離字裡的洞（0、6、9、a、o 中間空的地方）多遠、
                                    // 字的大小（時間是 1，日期那一行比較小）、（不用）
            public int Width, Height, X, Y;
            public double Scale;    // 以 1080p 為 1
            // 換分鐘的動畫用（畫面座標）：時間的字串、每個字的左右邊界（字數 + 1 個）、數字頂端再往上一點（日期跟時間的分界）、數字的垂直中心和高度
            public string Time;
            public float[] CharX;
            public float TimeTop, DigitCenterY, DigitHeight;
        }

        // 換分鐘的動畫：舊時間 → 新時間，每個字一格（最多 10 格），每格八個值：
        //   種類（0 沒變的字，左右挪到新位置；1 新字長出來；2 舊字抹掉）、左右邊界、中心 x、舊字中心減新字中心（只有沒變的字用）、
        //   延遲（秒）、方向（1 由下往上、−1 由上往下）、速度倍數（換點時慢 1.4 倍）。
        // 照 iPhone 換分鐘、換點的錄影（兩個子代理量的）：
        //   字數一樣（12:50 → 12:51）：沒變的字整串滑到新位置，變了的字在原地抹掉、長出來；
        //   字數不同（12:59 → 1:00）：所有字都重做（舊字在舊位置抹掉、新字在新位置長出來，不滑動），慢 1.4 倍；
        //   分三組錯開：時、分鐘十位（含冒號）、分鐘個位，各晚 0、0.10、0.15 秒（只有一組變時不延遲）；
        //   新字是 0 或冒號由上往下長，其他由下往上（兩段錄影都符合；舊字照同一格的方向抹掉）
        public static float[] ClockSlots(ClockImage from, ClockImage to)
        {
            if (from == null || to == null || from.Time == to.Time || from.CharX == null || to.CharX == null) return null;
            string a = from.Time, b = to.Time;
            bool same = a.Length == b.Length;
            float slow = same ? 1f : 1.4f;
            var slots = new System.Collections.Generic.List<float>();
            for (int k = 0; k < b.Length; k++)
            {
                float x0 = to.CharX[k], x1 = to.CharX[k + 1], cx = (x0 + x1) / 2;
                float dir = b[k] == '0' || b[k] == ':' ? -1f : 1f;
                int j = k - (b.Length - a.Length);
                if (same && a[j] == b[k])
                {
                    float ocx = (from.CharX[j] + from.CharX[j + 1]) / 2;
                    slots.AddRange(new[] { 0f, x0, x1, cx, ocx - cx, 0f, 1f, 1f });
                    continue;
                }
                slots.AddRange(new[] { 1f, x0, x1, cx, 0f, GroupDelay(b.Length - 1 - k), dir, slow });
                if (same) slots.AddRange(new[] { 2f, from.CharX[j], from.CharX[j + 1], (from.CharX[j] + from.CharX[j + 1]) / 2, 0f, GroupDelay(b.Length - 1 - k), dir, slow });
            }
            if (!same)
                for (int j = 0; j < a.Length; j++)
                {
                    int k = j + (b.Length - a.Length);   // 新時間裡同一個位置（從右邊數）的字，決定方向
                    float dir = k >= 0 && k < b.Length && (b[k] == '0' || b[k] == ':') ? -1f : 1f;
                    slots.AddRange(new[] { 2f, from.CharX[j], from.CharX[j + 1], (from.CharX[j] + from.CharX[j + 1]) / 2, 0f, GroupDelay(a.Length - 1 - j), dir, slow });
                }
            // 只有一組在變：從 0 開始（最早的那組不延遲）
            float first = float.MaxValue;
            for (int i = 0; i < slots.Count; i += 8) if (slots[i] != 0) first = Math.Min(first, slots[i + 5]);
            for (int i = 0; i < slots.Count; i += 8) if (slots[i] != 0) slots[i + 5] -= first;
            return slots.Count / 8 <= 10 ? slots.ToArray() : null;
        }

        // 從右邊數第幾個字屬於哪一組：個位 0.15 秒、十位和冒號 0.10 秒、時 0
        static float GroupDelay(int fromRight) { return fromRight == 0 ? 0.15f : fromRight <= 2 ? 0.10f : 0f; }

        // 時間或天氣有變才需要重畫時鐘那張圖
        // now：同一個時間點（時間、日期都用它，跨過午夜的那一瞬間才不會兜不起來）
        public static string ClockKey(DateTime now)
        {
            return TimeText(now) + DateText(now) + (Weather.Current == null ? "" : Weather.Current.Fetched.Ticks.ToString());
        }

        // 時間下面的日期（英文）：「Friday, October 3」
        static string DateText(DateTime now)
        {
            return now.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
        }

        // 12 小時制：「9:02」（跟 iPhone 一樣不顯示 AM / PM）
        static string TimeText(DateTime now)
        {
            return now.ToString("h:mm", CultureInfo.InvariantCulture);
        }

        // 畫面上方置中（照 macOS 的鎖定畫面）：上面一行日期和天氣，下面是大的時間，兩行都是液態玻璃（now 跟算 ClockKey 時用同一個時間點）。
        // 同一個版面畫兩次：實心的字（畫不了玻璃時用，帶陰影）、字的形狀（不加陰影，拿來算字形距離圖）
        public static ClockImage RenderClock(DateTime now, int screenWidth, int screenHeight)
        {
            double s = screenHeight / 1080.0;
            // 時間用 Inter 粗體（玻璃要夠粗才看得出折射和亮邊），日期半粗體；日期約是時間的 27%（照 macOS）
            double timeSize = Math.Round(112 * s), dateSize = Math.Round(30 * s);
            double pad = Math.Ceiling(30 * s);   // 陰影需要的留白（也是玻璃邊緣取樣的空間）

            var date = new TextBlock { FontFamily = CardFont, FontSize = dateSize, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
            TextOptions.SetTextFormattingMode(date, TextFormattingMode.Ideal);
            date.Inlines.Add(new Run(DateText(now)));
            var weather = Weather.Current;
            if (weather != null)
            {
                double iconSize = Math.Round(dateSize * 1.15);
                var icon = WeatherIcon(weather.Code, weather.IsDay, iconSize);
                // 跟日期隔一段；圖示中心對齊文字的中間
                icon.Margin = new Thickness(Math.Round(dateSize * 0.7), 0, Math.Round(dateSize * 0.22), Math.Round(dateSize * 0.36 - iconSize / 2));
                date.Inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Baseline });
                date.Inlines.Add(new Run(Math.Round(weather.TempC).ToString("0", CultureInfo.InvariantCulture) + "°"));
            }
            // 時間：大數字的行高上面留很多空，往上拉近日期一點
            var time = new TextBlock
            {
                Text = TimeText(now), FontFamily = CardFont, FontSize = timeSize, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -Math.Round(12 * s), 0, 0),
            };
            TextOptions.SetTextFormattingMode(time, TextFormattingMode.Ideal);
            var lines = new StackPanel();
            lines.Children.Add(date);
            lines.Children.Add(time);

            var image = new ClockImage { Scale = s };
            Border root;
            image.Solid = ToPixels(lines, s, pad, out root, out image.Width, out image.Height);
            lines.Effect = null;                                                  // 字的形狀本身（不要陰影）
            // 日期那一行（上半部）的字小、筆畫細：邊緣的折射跟著字的大小縮小
            int split = (int)Math.Round(pad + date.ActualHeight + time.Margin.Top / 2);
            // 用兩倍的解析度畫字的形狀、算距離，再縮回來：一般解析度算的邊緣會微微抖動（彎的字最明顯，例如 5）
            root.UpdateLayout();
            image.Field = Shrink(DistanceField(Snapshot(root, image.Width, image.Height, 2), image.Width * 2, image.Height * 2, split * 2, (float)(dateSize / timeSize)),
                image.Width, image.Height);
            image.X = (int)Math.Round((screenWidth - image.Width) / 2.0);       // 左右置中
            image.Y = (int)Math.Round(screenHeight * 0.095 - pad);               // 日期的頂端約在畫面高度的 9.5%
            // 每個字的左右邊界、數字的位置（換分鐘的動畫用）：用同樣的字型量每段前綴的寬度
            var typeface = new Typeface(CardFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var origin = time.TranslatePoint(new Point(0, 0), root);
            image.Time = time.Text;
            image.CharX = new float[time.Text.Length + 1];
            for (int i = 0; i <= time.Text.Length; i++)
                image.CharX[i] = (float)(image.X + origin.X + (i == 0 ? 0 : new FormattedText(time.Text.Substring(0, i), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, timeSize, Brushes.White, 1.0).WidthIncludingTrailingWhitespace));
            var whole = new FormattedText(time.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, timeSize, Brushes.White, 1.0);
            image.DigitHeight = (float)(timeSize * 0.727);                       // Inter 的數字高度（跟大寫一樣高）
            image.DigitCenterY = (float)(image.Y + origin.Y + whole.Baseline - image.DigitHeight / 2);
            image.TimeTop = image.DigitCenterY - image.DigitHeight * 0.56f;      // 數字頂端再往上一點（日期的字底在這之上）
            return image;
        }

        // 字形距離圖，每個點四個值（交錯排）：
        //   離字的邊多遠（像素；字外面是正的、裡面是負的）：用 alpha 一半當邊界，兩邊各算一次到最近的另一邊的距離
        //   （Felzenszwalb 的距離轉換，跟圖的大小成正比）；
        //   離字裡的洞多遠（像素）：從圖的四邊往裡填，填不到的空白就是洞（0、6、9、a、o 中間），洞那一側的邊比較暗；
        //   字的大小：第 split 列以上（日期那一行）是 topScale，以下（時間）是 1；第四個值不用。
        // 沒有任何字（都是正的）回傳 null
        // 兩倍解析度的距離圖（每個點四個值）縮回 w × h：每 2 × 2 格平均，距離除以 2；沒有字（null）照樣回傳 null
        static float[] Shrink(float[] big, int w, int h)
        {
            if (big == null) return null;
            int bw = w * 2;
            var small = new float[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int a = ((2 * y) * bw + 2 * x) * 4, b = a + 4, c = a + bw * 4, d = c + 4, o = (y * w + x) * 4;
                    small[o] = (big[a] + big[b] + big[c] + big[d]) / 8;
                    small[o + 1] = (big[a + 1] + big[b + 1] + big[c + 1] + big[d + 1]) / 8;
                    small[o + 2] = (big[a + 2] + big[b + 2] + big[c + 2] + big[d + 2]) / 4;
                }
            return small;
        }

        static float[] DistanceField(int[] pixels, int w, int h, int split, float topScale)
        {
            int n = w * h;
            var inside = new bool[n];
            bool any = false;
            for (int i = 0; i < n; i++) { inside[i] = ((pixels[i] >> 24) & 255) >= 128; any |= inside[i]; }
            if (!any) return null;
            double[] toHole = SquaredDistance(Holes(inside, w, h), w, h, true);
            var field = EdgeDistance(pixels, inside, w, h);
            var all = new float[n * 4];
            for (int i = 0; i < n; i++)
            {
                all[i * 4] = field[i];
                all[i * 4 + 1] = (float)Math.Sqrt(toHole[i]);
                all[i * 4 + 2] = i / w < split ? topScale : 1f;
            }
            return all;
        }

        // 離字的邊多遠（像素，字裡面是負的），邊的位置精確到像素裡面：
        //   邊上的點（半透明的，或緊鄰另一邊的）用 WPF 抗鋸齒的覆蓋率和它的斜率，算出 alpha 一半的地方在這一格裡的確切位置；
        //   其他點記下「最近的邊上的點」，從四周往裡推兩遍（向量傳遞），距離就是到那個確切位置的長度。
        //   這樣斜的、彎的邊都是平滑的曲線，不會被像素格子綁住（只用整格判斷的話斜邊會一格一格地起伏，亮線、暗線看起來像虛線）
        static float[] EdgeDistance(int[] pixels, bool[] inside, int w, int h)
        {
            int n = w * h;
            var cover = new float[n];
            for (int i = 0; i < n; i++) cover[i] = ((pixels[i] >> 24) & 255) / 255f;
            const float Far = 1e6f;
            float[] ex = new float[n], ey = new float[n];   // 最近的邊上的點（像素座標）
            for (int i = 0; i < n; i++) { ex[i] = Far; ey[i] = Far; }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float c = cover[i];
                    bool edge = c > 0.02f && c < 0.98f;
                    if (!edge)
                        for (int k = 0; k < 4 && !edge; k++)
                        {
                            int nx = x + (k == 0 ? -1 : k == 1 ? 1 : 0), ny = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                            if (nx >= 0 && nx < w && ny >= 0 && ny < h && inside[ny * w + nx] != inside[i]) edge = true;
                        }
                    if (!edge) continue;
                    // 覆蓋率的斜率（指向字裡面）；alpha 一半的地方在 −(c − 0.5) / |斜率| 的方向上
                    float gx = (x < w - 1 ? cover[i + 1] : c) - (x > 0 ? cover[i - 1] : c);
                    float gy = (y < h - 1 ? cover[i + w] : c) - (y > 0 ? cover[i - w] : c);
                    gx *= 0.5f; gy *= 0.5f;
                    float len = (float)Math.Sqrt(gx * gx + gy * gy);
                    float off = len > 1e-3f ? Math.Max(-1f, Math.Min(1f, (c - 0.5f) / len)) : 0f;
                    float nxv = len > 1e-3f ? gx / len : 0, nyv = len > 1e-3f ? gy / len : 0;
                    ex[i] = x - nxv * off; ey[i] = y - nyv * off;
                }
            // 向量傳遞：往右下推一遍、往左上推一遍，各兩次
            for (int round = 0; round < 2; round++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++) { Take(ex, ey, w, h, x, y, -1, 0); Take(ex, ey, w, h, x, y, -1, -1); Take(ex, ey, w, h, x, y, 0, -1); Take(ex, ey, w, h, x, y, 1, -1); }
                    for (int x = w - 1; x >= 0; x--) Take(ex, ey, w, h, x, y, 1, 0);
                }
                for (int y = h - 1; y >= 0; y--)
                {
                    for (int x = w - 1; x >= 0; x--) { Take(ex, ey, w, h, x, y, 1, 0); Take(ex, ey, w, h, x, y, 1, 1); Take(ex, ey, w, h, x, y, 0, 1); Take(ex, ey, w, h, x, y, -1, 1); }
                    for (int x = 0; x < w; x++) Take(ex, ey, w, h, x, y, -1, 0);
                }
            }
            var field = new float[n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float dx = x - ex[i], dy = y - ey[i];
                    float d = ex[i] >= Far ? 1e4f : (float)Math.Sqrt(dx * dx + dy * dy);
                    field[i] = cover[i] >= 0.5f ? -d : d;
                }
            return field;
        }

        // 如果鄰居 (x + dx, y + dy) 記的邊上的點比自己記的近，就換成它的
        static void Take(float[] ex, float[] ey, int w, int h, int x, int y, int dx, int dy)
        {
            int nx = x + dx, ny = y + dy;
            if (nx < 0 || nx >= w || ny < 0 || ny >= h) return;
            int i = y * w + x, j = ny * w + nx;
            if (ex[j] >= 1e6f) return;
            float ax = x - ex[j], ay = y - ey[j], bx = x - ex[i], by = y - ey[i];
            if (ex[i] >= 1e6f || ax * ax + ay * ay < bx * bx + by * by) { ex[i] = ex[j]; ey[i] = ey[j]; }
        }

        // 數字裡的洞：字外面、但從圖的四邊走不到的空白（往八個方向走，斜的邊在格子上的階梯縫隙才不會卡住）；
        // 小於 minArea 個像素的空白不算（斜邊上偶爾還是會圍出一兩個像素，當成洞的話邊上會出現一段一段的暗點）
        static bool[] Holes(bool[] inside, int w, int h)
        {
            var reached = new bool[w * h];
            var stack = new System.Collections.Generic.Stack<int>();
            for (int x = 0; x < w; x++) { stack.Push(x); stack.Push((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { stack.Push(y * w); stack.Push(y * w + w - 1); }
            Fill(inside, reached, stack, w, h, null);
            var holes = new bool[w * h];
            int minArea = Math.Max(16, w * h / 2000);
            var part = new System.Collections.Generic.List<int>();
            for (int i = 0; i < holes.Length; i++)
            {
                if (inside[i] || reached[i]) continue;
                part.Clear();
                stack.Push(i);
                Fill(inside, reached, stack, w, h, part);
                if (part.Count >= minArea) foreach (int j in part) holes[j] = true;
            }
            return holes;
        }

        // 從 stack 裡的點往八個方向填字外面的空白（填到的記在 reached；part 不是 null 就順便記下這一塊有哪些點）
        static void Fill(bool[] inside, bool[] reached, System.Collections.Generic.Stack<int> stack, int w, int h, System.Collections.Generic.List<int> part)
        {
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (reached[i] || inside[i]) continue;
                reached[i] = true;
                if (part != null) part.Add(i);
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx != 0 || dy != 0) && nx >= 0 && nx < w && ny >= 0 && ny < h) stack.Push(ny * w + nx);
                    }
            }
        }

        // 每個點到最近的「seed == want」的點的距離平方（先每一欄、再每一列做一維的距離轉換）
        static double[] SquaredDistance(bool[] seed, int w, int h, bool want)
        {
            const double Far = 1e12;   // 「很遠」：比任何真正的距離平方都大，又不會大到把 q² 的差距吃掉
            var grid = new double[w * h];
            for (int i = 0; i < grid.Length; i++) grid[i] = seed[i] == want ? 0 : Far;
            int m = Math.Max(w, h);
            double[] f = new double[m], d = new double[m], z = new double[m + 1];
            var v = new int[m];
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++) f[y] = grid[y * w + x];
                Transform1D(f, h, d, v, z);
                for (int y = 0; y < h; y++) grid[y * w + x] = d[y];
            }
            for (int y = 0; y < h; y++)
            {
                Array.Copy(grid, y * w, f, 0, w);
                Transform1D(f, w, d, v, z);
                Array.Copy(d, 0, grid, y * w, w);
            }
            return grid;
        }

        // 一維的距離轉換：d[q] = 所有 p 裡 (q − p)² + f[p] 最小的（拋物線的下包絡）
        static void Transform1D(double[] f, int n, double[] d, int[] v, double[] z)
        {
            int k = 0;
            v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                double sx = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                while (sx <= z[k])
                {
                    k--;
                    sx = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                }
                k++;
                v[k] = q; z[k] = sx; z[k + 1] = double.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                double dq = q - v[k];
                d[q] = dq * dq + f[v[k]];
            }
        }

        // 天氣圖示（WMO 天氣代碼）
        static FrameworkElement WeatherIcon(int code, bool day, double size)
        {
            var canvas = new Canvas { Width = size, Height = size };
            var sun = Color.FromRgb(0xFD, 0xD6, 0x63);
            var moon = Color.FromRgb(0xFF, 0xE9, 0xA8);
            var cloud = Color.FromRgb(0xF1, 0xF3, 0xF4);
            var rain = Color.FromRgb(0x8A, 0xB4, 0xF8);

            if (code == 0)                       // 晴
            {
                if (day) AddSun(canvas, size / 2, size / 2, size * 0.34, sun); else AddMoon(canvas, size / 2, size / 2, size * 0.36, moon);
            }
            else if (code == 1 || code == 2)     // 晴時多雲
            {
                if (day) AddSun(canvas, size * 0.36, size * 0.34, size * 0.25, sun); else AddMoon(canvas, size * 0.36, size * 0.32, size * 0.27, moon);
                AddCloud(canvas, size * 0.24, size * 0.46, size * 0.76, cloud);
            }
            else if (code == 45 || code == 48)   // 霧
            {
                for (int i = 0; i < 3; i++)
                    AddBar(canvas, size * (0.1 + 0.08 * (i % 2)), size * (0.3 + 0.2 * i), size * 0.72, size * 0.09, cloud);
            }
            else
            {
                AddCloud(canvas, size * 0.08, size * 0.18, size * 0.84, cloud);
                if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82))            // 雨
                    for (int i = 0; i < 3; i++) AddBar(canvas, size * (0.27 + 0.2 * i), size * 0.74, size * 0.07, size * 0.2, rain);
                else if ((code >= 71 && code <= 77) || code == 85 || code == 86)          // 雪
                    for (int i = 0; i < 3; i++) AddDot(canvas, size * (0.3 + 0.2 * i), size * 0.84, size * 0.06, cloud);
                else if (code >= 95)                                                       // 雷雨
                    canvas.Children.Add(new System.Windows.Shapes.Path
                    {
                        Fill = new SolidColorBrush(sun),
                        Data = Geometry.Parse(string.Format(CultureInfo.InvariantCulture, "M{0},{1} L{2},{3} L{4},{3} L{5},{6} L{7},{8} L{9},{8} Z",
                            size * 0.52, size * 0.6, size * 0.4, size * 0.8, size * 0.5, size * 0.44, size, size * 0.62, size * 0.8, size * 0.52)),
                    });
            }
            return canvas;
        }

        static void AddSun(Canvas c, double cx, double cy, double r, Color color)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Fill = new SolidColorBrush(color) };
            dot.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = color, BlurRadius = r * 1.2, ShadowDepth = 0, Opacity = 0.55 };
            Canvas.SetLeft(dot, cx - r); Canvas.SetTop(dot, cy - r);
            c.Children.Add(dot);
        }

        static void AddMoon(Canvas c, double cx, double cy, double r, Color color)
        {
            var shape = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(cx, cy), r, r),
                new EllipseGeometry(new Point(cx + r * 0.55, cy - r * 0.35), r * 0.8, r * 0.8));
            c.Children.Add(new System.Windows.Shapes.Path { Data = shape, Fill = new SolidColorBrush(color) });
        }

        static void AddCloud(Canvas c, double x, double y, double w, Color color)
        {
            double h = w * 0.62;
            var shape = new GeometryGroup { FillRule = FillRule.Nonzero };
            shape.Children.Add(new RectangleGeometry(new Rect(x + w * 0.1, y + h * 0.5, w * 0.8, h * 0.42), h * 0.21, h * 0.21));
            shape.Children.Add(new EllipseGeometry(new Point(x + w * 0.3, y + h * 0.62), w * 0.2, w * 0.2));
            shape.Children.Add(new EllipseGeometry(new Point(x + w * 0.54, y + h * 0.45), w * 0.27, w * 0.27));
            shape.Children.Add(new EllipseGeometry(new Point(x + w * 0.74, y + h * 0.66), w * 0.17, w * 0.17));
            // 淡淡的深色邊，讓雲跟後面的太陽 / 月亮分得開
            c.Children.Add(new System.Windows.Shapes.Path
            {
                Data = shape.GetOutlinedPathGeometry(),   // 合成一個外框，邊線只畫在雲的外圍
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0x20, 0x24, 0x2A)), StrokeThickness = Math.Max(1, w * 0.045),
            });
        }

        static void AddBar(Canvas c, double x, double y, double w, double h, Color color)
        {
            var bar = new System.Windows.Shapes.Rectangle { Width = w, Height = h, RadiusX = Math.Min(w, h) / 2, RadiusY = Math.Min(w, h) / 2, Fill = new SolidColorBrush(color) };
            Canvas.SetLeft(bar, x); Canvas.SetTop(bar, y);
            c.Children.Add(bar);
        }

        static void AddDot(Canvas c, double cx, double cy, double r, Color color)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Fill = new SolidColorBrush(color) };
            Canvas.SetLeft(dot, cx - r); Canvas.SetTop(dot, cy - r);
            c.Children.Add(dot);
        }
    }
}
