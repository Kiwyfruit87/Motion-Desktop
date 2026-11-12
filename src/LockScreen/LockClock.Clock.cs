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
            public int[] Shape;     // 字的形狀（兩倍解析度，不加陰影；算完距離圖就丟掉）
            public int Split;       // Shape 裡日期那一行和時間的分界（列）
            public float TopScale;  // 日期的字跟時間的大小比例
            public float[] Field;   // 字形距離圖，每個點四個值：離字的邊多遠（像素，字裡面是負的）、離字裡的洞（0、6、9、a、o 中間空的地方）多遠、
                                    // 字的大小（時間是 1，日期那一行比較小）、（不用）
            public int Width, Height, X, Y;
            public double Scale;    // 以 1080p 為 1
            // 換分鐘的動畫用（畫面座標）：時間的字串、每個字的左右邊界（字數 + 1 個）、數字頂端再往上一點（日期跟時間的分界）、數字的垂直中心和高度
            public string Time;
            public float[] CharX;
            public float TimeTop, DigitCenterY, DigitHeight;
            // 日期那一行的動畫用：一段一段（星期、月、日、天氣圖示、溫度）的內容和左右邊界（每段兩個），這一行的垂直中心和高度
            public string[] DateParts;
            public float[] DateX;
            public float DateCenterY, DateHeight;
        }

        // 日期那一行的動畫：只有變了的那幾段動（舊的抹掉、新的長出來，跟換分鐘同一種動作），沒變的滑到新位置（整行置中，寬度變了位置會跟著變）。
        // 每格八個值，排法跟 ClockSlots 一樣，種類加 10（引擎用它分辨是哪一行）。沒變、或是沒有上一次的資料回傳 null
        public static float[] DateSlots(ClockImage from, ClockImage to)
        {
            if (from == null || to == null || from.DateParts == null || to.DateParts == null || from.DateX == null || to.DateX == null) return null;
            string[] a = from.DateParts, b = to.DateParts;
            bool changed = a.Length != b.Length;
            for (int i = 0; !changed && i < b.Length; i++) changed = a[i] != b[i];
            if (!changed) return null;
            var slots = new System.Collections.Generic.List<float>();
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                bool hasNew = i < b.Length, hasOld = i < a.Length;
                float nx0 = hasNew ? to.DateX[i * 2] - 2 : 0, nx1 = hasNew ? to.DateX[i * 2 + 1] + 2 : 0;
                float ox0 = hasOld ? from.DateX[i * 2] - 2 : 0, ox1 = hasOld ? from.DateX[i * 2 + 1] + 2 : 0;
                if (hasNew && hasOld && a[i] == b[i]) { slots.AddRange(new[] { 10f, nx0, nx1, (nx0 + nx1) / 2, (ox0 + ox1) / 2 - (nx0 + nx1) / 2, 0f, 1f, 1f }); continue; }
                if (hasNew) slots.AddRange(new[] { 11f, nx0, nx1, (nx0 + nx1) / 2, 0f, 0f, 1f, 1f });
                if (hasOld) slots.AddRange(new[] { 12f, ox0, ox1, (ox0 + ox1) / 2, 0f, 0f, 1f, 1f });
            }
            return slots.ToArray();
        }

        // 時間和日期兩行的動畫合在一起（引擎最多 16 格；放不下時日期那一行不做動畫，直接換）
        public static float[] AllSlots(ClockImage from, ClockImage to)
        {
            float[] time = ClockSlots(from, to), date = DateSlots(from, to);
            if (time == null) return date != null && date.Length / 8 <= 16 ? date : null;
            if (date == null || (time.Length + date.Length) / 8 > 16) return time;
            var all = new float[time.Length + date.Length];
            time.CopyTo(all, 0); date.CopyTo(all, time.Length);
            return all;
        }

        // 換分鐘的動畫：舊時間 → 新時間，每個字一格（最多 10 格），每格八個值：
        //   種類（0 沒變的字，左右挪到新位置；1 新字長出來；2 舊字抹掉）、左右邊界、中心 x、舊字中心減新字中心（只有沒變的字用）、
        //   延遲（秒）、方向（1 由下往上、−1 由上往下）、速度倍數（換點時慢 1.4 倍）。
        // 照 iPhone 換分鐘、換點的錄影（兩個子代理量的）：
        //   字數一樣（12:50 → 12:51）：沒變的字整串滑到新位置，變了的字在原地抹掉、長出來；
        //   字數不同（12:59 → 1:00）：所有字都重做（舊字在舊位置抹掉、新字在新位置長出來，不滑動），慢 1.4 倍；
        //   分組錯開：時、冒號、分鐘（十位和個位同時），各晚 0、0.10、0.15 秒（只有一組變時不延遲）；
        //   新字是 0 或冒號由上往下長，其他由下往上（兩段錄影都符合；舊字照同一格的方向抹掉）；
        //   分鐘的十位跟著個位的方向（1:09 → 1:10 兩個字一起由上往下，不會一上一下）
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
                float dir = Direction(b, k);
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
                    float dir = k >= 0 && k < b.Length ? Direction(b, k) : 1f;
                    slots.AddRange(new[] { 2f, from.CharX[j], from.CharX[j + 1], (from.CharX[j] + from.CharX[j + 1]) / 2, 0f, GroupDelay(a.Length - 1 - j), dir, slow });
                }
            // 只有一組在變：從 0 開始（最早的那組不延遲）
            float first = float.MaxValue;
            for (int i = 0; i < slots.Count; i += 8) if (slots[i] != 0) first = Math.Min(first, slots[i + 5]);
            for (int i = 0; i < slots.Count; i += 8) if (slots[i] != 0) slots[i + 5] -= first;
            return slots.Count / 8 <= 10 ? slots.ToArray() : null;
        }

        // 新時間 b 的第 k 個字往哪個方向長（1 由下往上、−1 由上往下）：0 和冒號由上往下，其他由下往上；分鐘的十位照個位的
        static float Direction(string b, int k)
        {
            char c = b.Length - 1 - k == 1 ? b[b.Length - 1] : b[k];
            return c == '0' || c == ':' ? -1f : 1f;
        }

        // 從右邊數第幾個字屬於哪一組：分鐘的個位和十位 0.15 秒（同時換）、冒號 0.10 秒、時 0
        static float GroupDelay(int fromRight) { return fromRight <= 1 ? 0.15f : fromRight == 2 ? 0.10f : 0f; }

        // 時間或天氣有變才需要重畫時鐘那張圖
        // now：同一個時間點（時間、日期都用它，跨過午夜的那一瞬間才不會兜不起來）
        public static string ClockKey(DateTime now)
        {
            return TimeText(now) + DateText(now) + (Weather.Current == null ? "" : Weather.Current.Fetched.Ticks.ToString());
        }

        // 時間上面的日期（英文，照 iPhone / iPad 的鎖定畫面用縮寫、沒有逗號）：「Fri Oct 3」
        static string DateText(DateTime now)
        {
            return now.ToString("ddd MMM d", CultureInfo.InvariantCulture);
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
            // 時間用 Inter 粗體（玻璃要夠粗才看得出折射和亮邊），日期半粗體；日期約是時間的 27%（照 macOS）。
            // fonts\local 裡有字型的話時間改用它（見 ClockFont；粗細照字型檔本身）
            var timeWeight = clockFontLocal ? FontWeights.Normal : FontWeights.Bold;
            // 換上的字型比 Inter 窄的話放大：整串時間比原本寬三成（照 iPhone 的鎖定畫面，時間跟日期那一行差不多寬；
            // 用固定的「00:00」量，倍數才不會每分鐘跟著數字變）
            double fit = 1;
            if (clockFontLocal)
            {
                double inter = new FormattedText("00:00", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(CardFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 100, Brushes.White, 1.0).Width;
                double local = new FormattedText("00:00", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(ClockFont, FontStyles.Normal, timeWeight, FontStretches.Normal), 100, Brushes.White, 1.0).Width;
                if (local > 1) fit = Math.Max(1, Math.Min(3.5, 1.3 * inter / local));
            }
            double timeSize = Math.Round(112 * s * fit), dateSize = Math.Round(30 * s);
            double pad = Math.Ceiling(30 * s);   // 陰影需要的留白（也是玻璃邊緣取樣的空間）

            var date = new TextBlock { FontFamily = DateFont, FontSize = dateSize, FontWeight = dateFontLocal ? FontWeights.Normal : FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
            TextOptions.SetTextFormattingMode(date, TextFormattingMode.Ideal);
            string dateText = DateText(now);
            date.Inlines.Add(new Run(dateText));
            var weather = Weather.Current;
            // 日期那一行分成幾段（動畫用）：日期的每個詞、天氣圖示、溫度；每段的左右邊界（從這一行的左邊算，下面再換成畫面座標）
            var dateTypeface = new Typeface(DateFont, FontStyles.Normal, dateFontLocal ? FontWeights.Normal : FontWeights.SemiBold, FontStretches.Normal);
            var parts = new System.Collections.Generic.List<string>();
            var partX = new System.Collections.Generic.List<double>();
            for (int at = 0; at < dateText.Length; )
            {
                int end = dateText.IndexOf(' ', at);
                if (end < 0) end = dateText.Length;
                parts.Add(dateText.Substring(at, end - at));
                partX.Add(TextWidth(dateText.Substring(0, at), dateTypeface, dateSize));
                partX.Add(TextWidth(dateText.Substring(0, end), dateTypeface, dateSize));
                at = end + 1;
            }
            if (weather != null)
            {
                // 圖示照它實際的形狀縮放：跟大寫字差不多高（月亮、太陽）、寬的（雲）不超過大寫字高的 1.5 倍；形狀的中心對齊大寫字的中間
                double cap = dateTypeface.CapsHeight * dateSize;
                var icon = WeatherIcon(weather.Code, weather.IsDay, cap * 1.15, cap * 1.5);
                string tempText = (Math.Round(weather.TempC) + 0.0).ToString("0", CultureInfo.InvariantCulture) + "\u00B0";
                double partAt = TextWidth(dateText, dateTypeface, dateSize) + Math.Round(dateSize * 0.32);
                parts.Add("icon" + weather.Code + (weather.IsDay ? "d" : "n"));
                partX.Add(partAt); partX.Add(partAt + icon.Width);
                partAt += icon.Width + Math.Round(dateSize * 0.2);
                parts.Add(tempText);
                partX.Add(partAt); partX.Add(partAt + TextWidth(tempText, dateTypeface, dateSize));
                // 跟兩邊的字各隔一點；放在基線上再往上抬，中心對到大寫字的一半高
                icon.Margin = new Thickness(Math.Round(dateSize * 0.32), 0, Math.Round(dateSize * 0.2), cap / 2 - icon.Height / 2);
                date.Inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Baseline });
                date.Inlines.Add(new Run((Math.Round(weather.TempC) + 0.0).ToString("0", CultureInfo.InvariantCulture) + "°"));   // + 0.0：−0 變成 0（不然會顯示「-0°」）
            }
            // 時間：大數字的行高上面留很多空，往上拉近日期一點
            var time = new TextBlock
            {
                Text = TimeText(now), FontFamily = ClockFont, FontSize = timeSize, FontWeight = timeWeight, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -Math.Round(12 * s * fit), 0, 0),
            };
            TextOptions.SetTextFormattingMode(time, TextFormattingMode.Ideal);
            var lines = new StackPanel();
            lines.Children.Add(date);
            lines.Children.Add(time);

            var image = new ClockImage { Scale = s };
            Border root;
            image.Solid = ToPixels(lines, s, pad, out root, out image.Width, out image.Height);
            lines.Effect = null;                                                  // 字的形狀本身（不要陰影，天氣圖示的光暈也拿掉）
            ClearEffects(lines);
            // 日期那一行（上半部）的字小、筆畫細：邊緣的折射跟著字的大小縮小
            int split = (int)Math.Round(pad + date.ActualHeight + time.Margin.Top / 2);
            // 用兩倍的解析度畫字的形狀（算距離圖用，見 ComputeField；一般解析度算的邊緣會微微抖動，彎的字最明顯，例如 5）
            root.UpdateLayout();
            image.Shape = Snapshot(root, image.Width, image.Height, 2);
            image.Split = split * 2;
            image.TopScale = (float)(dateSize / timeSize);
            image.X = (int)Math.Round((screenWidth - image.Width) / 2.0);       // 左右置中
            image.Y = (int)Math.Round(screenHeight * 0.095 - pad);               // 日期的頂端約在畫面高度的 9.5%
            // 每個字的左右邊界、數字的位置（換分鐘的動畫用）：用同樣的字型量每段前綴的寬度
            var typeface = new Typeface(ClockFont, FontStyles.Normal, timeWeight, FontStretches.Normal);
            var origin = time.TranslatePoint(new Point(0, 0), root);
            image.Time = time.Text;
            image.CharX = new float[time.Text.Length + 1];
            for (int i = 0; i <= time.Text.Length; i++)
                image.CharX[i] = (float)(image.X + origin.X + (i == 0 ? 0 : new FormattedText(time.Text.Substring(0, i), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, timeSize, Brushes.White, 1.0).WidthIncludingTrailingWhitespace));
            var whole = new FormattedText(time.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, timeSize, Brushes.White, 1.0);
            image.DigitHeight = (float)(timeSize * (clockFontLocal ? typeface.CapsHeight : 0.727));   // 數字的高度（跟大寫一樣高；Inter 是 0.727）
            image.DigitCenterY = (float)(image.Y + origin.Y + whole.Baseline - image.DigitHeight / 2);
            image.TimeTop = image.DigitCenterY - image.DigitHeight * 0.56f;      // 數字頂端再往上一點（日期的字底在這之上）
            var dateOrigin = date.TranslatePoint(new Point(0, 0), root);
            image.DateParts = parts.ToArray();
            image.DateX = new float[partX.Count];
            for (int i = 0; i < partX.Count; i++) image.DateX[i] = (float)(image.X + dateOrigin.X + partX[i]);
            image.DateHeight = (float)date.ActualHeight;
            image.DateCenterY = (float)(image.Y + dateOrigin.Y + date.ActualHeight / 2);
            return image;
        }

        static double TextWidth(string text, Typeface typeface, double size)
        {
            if (text.Length == 0) return 0;
            return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.White, 1.0).WidthIncludingTrailingWhitespace;
        }

        // 字形距離圖，每個點四個值（交錯排）：
        //   離字的邊多遠（像素；字外面是正的、裡面是負的）：用 alpha 一半當邊界，兩邊各算一次到最近的另一邊的距離
        //   （Felzenszwalb 的距離轉換，跟圖的大小成正比）；
        //   離字裡的洞多遠（像素）：從圖的四邊往裡填，填不到的空白就是洞（0、6、9、a、o 中間），洞那一側的邊比較暗；
        //   字的大小：第 split 列以上（日期那一行）是 topScale，以下（時間）是 1（ComputeField 再換成筆畫寬度的倍數，見 StrokeScale）；第四個值不用。
        // 沒有任何字（都是正的）回傳 null
        // 從 RenderClock 畫好的形狀算出字形距離圖（Field），再把形狀丟掉。只用到陣列，可以在背景執行緒做（4K 時要零點幾秒）
        public static void ComputeField(ClockImage c)
        {
            if (c.Shape == null) return;
            c.Field = Shrink(DistanceField(c.Shape, c.Width * 2, c.Height * 2, c.Split, c.TopScale, (int)Math.Max(16, 12 * 4 * c.Scale * c.Scale),
                (float)(2 * TimeRound * c.Scale), (float)(2 * DateRound * c.Scale)), c.Width, c.Height);
            c.Shape = null;
            StrokeScale(c);
        }

        // 時間那一行的筆畫寬度是 Inter 粗體（1080p 約 16.5 像素）的幾倍，寫進距離圖時間那幾列的「字的大小」：
        // 換了比較粗（或細）的字型時，玻璃貼邊的斜面、聚光、折射跟著筆畫寬度縮放（見 TextGlass 的 ks），一像素的亮線不縮。
        // 量法：數字中段幾列裡每一段連續的「字裡面」的寬度取中位數（直的筆畫最多，橫的長段被中位數排掉）
        static void StrokeScale(ClockImage c)
        {
            var f = c.Field;
            if (f == null) return;
            int w = c.Width, top = -1, bottom = -1;
            for (int y = 0; y < c.Height; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    if (f[o + 2] > 0.99f && f[o] < 0) { if (top < 0) top = y; bottom = y; break; }
                }
            if (top < 0 || bottom - top < 10) return;
            var runs = new System.Collections.Generic.List<int>();
            for (int y = top + (bottom - top) * 2 / 5; y <= top + (bottom - top) * 13 / 20; y += 2)
                for (int x = 0, start = -1; x <= w; x++)
                {
                    bool inside = x < w && f[(y * w + x) * 4 + 2] > 0.99f && f[(y * w + x) * 4] < 0;
                    if (inside && start < 0) start = x;
                    else if (!inside && start >= 0) { runs.Add(x - start); start = -1; }
                }
            if (runs.Count == 0) return;
            runs.Sort();
            float scale = (float)Math.Max(0.9, Math.Min(2.5, runs[runs.Count / 2] / (16.5 * c.Scale)));
            for (int i = 0; i < f.Length; i += 4)
                if (f[i + 2] > 0.99f) f[i + 2] = scale;
        }

        // 拿掉 d 底下所有的效果（天氣圖示的光暈之類；算字的形狀時不要）
        static void ClearEffects(DependencyObject d)
        {
            var ui = d as UIElement;
            if (ui != null) ui.Effect = null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) ClearEffects(VisualTreeHelper.GetChild(d, i));
        }

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

        // 筆畫凸角磨圓的半徑（1080p 的像素）：時間 3、日期那一行 0.6（筆畫才約 4 像素寬，太大會把細的地方吃掉）。
        // Inter 的轉角比 Apple 的字硬，字型不用換，距離圖上把轉角磨圓一點點
        const double TimeRound = 3, DateRound = 0.6;

        // 把字的凸角磨圓（先往內縮 r、再往外長 r：筆畫粗細不變，只有凸出去的角變成半徑 r 的圓弧）。
        // 往內縮後的形狀照距離圖重新做成覆蓋率，再量一次到邊的距離（精確到像素裡面，弧線才不會一格一格的）
        // 日期和時間的半徑不同，兩行分開算再合併（取近的）：同一張算的話，時間那一行靠近分界的地方會照時間的半徑外長，日期底下多出一條假的字
        static float[] RoundCorners(float[] field, int w, int h, int split, float round, float topRound)
        {
            float[] top = RoundRows(field, w, h, 0, split, topRound), bottom = RoundRows(field, w, h, split, h, round);
            for (int i = 0; i < top.Length; i++) top[i] = Math.Min(top[i], bottom[i]);
            return top;
        }

        // 只算第 from ～ to − 1 列的字
        static float[] RoundRows(float[] field, int w, int h, int from, int to, float r)
        {
            int n = w * h;
            var shrunk = new int[n];
            var inside = new bool[n];
            for (int y = from; y < to; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, alpha = (int)Math.Round(Math.Max(0f, Math.Min(1f, 0.5f - (field[i] + r))) * 255);
                    shrunk[i] = alpha << 24;
                    inside[i] = alpha >= 128;
                }
            var again = EdgeDistance(shrunk, inside, w, h);
            for (int i = 0; i < n; i++) again[i] -= r;
            return again;
        }

        static float[] DistanceField(int[] pixels, int w, int h, int split, float topScale, int minHole, float round = 0, float topRound = 0)
        {
            int n = w * h;
            var inside = new bool[n];
            bool any = false;
            for (int i = 0; i < n; i++) { inside[i] = ((pixels[i] >> 24) & 255) >= 128; any |= inside[i]; }
            if (!any) return null;
            // 日期有往下伸的筆畫（y 的尾巴、逗號）會超過原本的分界：往下找到第一列空白的才是兩行真正的分界
            for (int y = split, cut = -1; y < h && cut < 0; y++)
            {
                bool empty = true;
                for (int x = 0; x < w && empty; x++) empty = !inside[y * w + x];
                if (empty) { cut = y; split = y; }
            }
            double[] toHole = SquaredDistance(Holes(inside, w, h, minHole), w, h, true);
            var field = EdgeDistance(pixels, inside, w, h);
            if (round > 0) field = RoundCorners(field, w, h, split, round, topRound);
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
        // 小於 minArea 個像素的空白不算（斜邊上偶爾還是會圍出一兩個像素，當成洞的話邊上會出現一段一段的暗點；跟字的大小走）
        static bool[] Holes(bool[] inside, int w, int h, int minArea)
        {
            var reached = new bool[w * h];
            var stack = new System.Collections.Generic.Stack<int>();
            for (int x = 0; x < w; x++) { Seed(inside, reached, stack, x); Seed(inside, reached, stack, (h - 1) * w + x); }
            for (int y = 0; y < h; y++) { Seed(inside, reached, stack, y * w); Seed(inside, reached, stack, y * w + w - 1); }
            Fill(inside, reached, stack, w, h, null);
            var holes = new bool[w * h];
            var part = new System.Collections.Generic.List<int>();
            for (int i = 0; i < holes.Length; i++)
            {
                if (inside[i] || reached[i]) continue;
                part.Clear();
                Seed(inside, reached, stack, i);
                Fill(inside, reached, stack, w, h, part);
                if (part.Count >= minArea) foreach (int j in part) holes[j] = true;
            }
            return holes;
        }

        // 還沒走過的空白：標記、放進堆疊（放進去時就標記，堆疊才不會比像素數多）
        static void Seed(bool[] inside, bool[] reached, System.Collections.Generic.Stack<int> stack, int i)
        {
            if (reached[i] || inside[i]) return;
            reached[i] = true;
            stack.Push(i);
        }

        // 從 stack 裡的點往八個方向填字外面的空白（填到的記在 reached；part 不是 null 就順便記下這一塊有哪些點）
        static void Fill(bool[] inside, bool[] reached, System.Collections.Generic.Stack<int> stack, int w, int h, System.Collections.Generic.List<int> part)
        {
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (part != null) part.Add(i);
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx != 0 || dy != 0) && nx >= 0 && nx < w && ny >= 0 && ny < h) Seed(inside, reached, stack, ny * w + nx);
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

        // 天氣圖示（WMO 天氣代碼）：照 iPhone / iPad 鎖定畫面的樣子，白色單色、實心（跟日期的字同一個顏色，玻璃畫的時候也跟著日期變色）；
        // 疊在一起的部分（雲前面的太陽、月亮、閃電）中間留一道細縫分開，不用顏色區分。形狀是自己畫的，沒用 Apple 的圖示
        // maxH、maxW：縮放後最高、最寬多少（回傳的圖剛好包住形狀，沒有留白）
        static FrameworkElement WeatherIcon(int code, bool day, double maxH, double maxW)
        {
            double s = 100, gap = Math.Max(6, 130 / maxH);   // 先在 100 × 100 裡畫，最後再縮；疊在一起的形狀中間的縫縮完後至少約 1.3 像素
            Geometry g;
            if (code == 0)                                                           // 晴
                g = day ? SunShape(s * 0.5, s * 0.5, s * 0.48) : MoonStars(s * 0.5, s * 0.52, s * 0.46);
            else if (code == 1 || code == 2)                                         // 晴時多雲：雲前面露出太陽 / 月亮
            {
                var cloud = CloudShape(s * 0.2, s * 0.4, s * 0.8);
                var back = day ? SunShape(s * 0.36, s * 0.36, s * 0.32) : (Geometry)Crescent(s * 0.38, s * 0.36, s * 0.27);
                g = Union(Cut(back, cloud, gap), cloud);
            }
            else if (code == 45 || code == 48)                                       // 霧：雲下面兩條橫線
            {
                var lines = new GeometryGroup();
                lines.Children.Add(Capsule(s * 0.12, s * 0.66, s * 0.76, s * 0.09));
                lines.Children.Add(Capsule(s * 0.12, s * 0.82, s * 0.6, s * 0.09));
                g = Union(Cut(CloudShape(s * 0.08, s * 0.08, s * 0.84), lines, gap), lines);
            }
            else
            {
                var cloud = CloudShape(s * 0.06, s * 0.08, s * 0.88);
                var below = new GeometryGroup();
                if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82))      // 雨：三條斜的雨滴
                    for (int i = 0; i < 3; i++)
                    {
                        var drop = Capsule(s * (0.3 + 0.2 * i) - s * 0.035, s * 0.66, s * 0.07, s * 0.24);
                        drop.Transform = new RotateTransform(20, s * (0.3 + 0.2 * i), s * 0.78);
                        below.Children.Add(drop);
                    }
                else if ((code >= 71 && code <= 77) || code == 85 || code == 86)    // 雪：雲下面的點
                {
                    below.Children.Add(new EllipseGeometry(new Point(s * 0.3, s * 0.74), s * 0.06, s * 0.06));
                    below.Children.Add(new EllipseGeometry(new Point(s * 0.5, s * 0.86), s * 0.06, s * 0.06));
                    below.Children.Add(new EllipseGeometry(new Point(s * 0.7, s * 0.74), s * 0.06, s * 0.06));
                }
                else if (code >= 95)                                                 // 雷雨：閃電穿過雲的下緣
                    below.Children.Add(Geometry.Parse(string.Format(CultureInfo.InvariantCulture, "M{0},{1} L{2},{1} L{3},{4} L{5},{4} L{6},{7} L{8},{9} L{10},{9} Z",
                        s * 0.62, s * 0.42, s * 0.45, s * 0.35, s * 0.72, s * 0.49, s * 0.41, s * 0.98, s * 0.69, s * 0.62, s * 0.54)));
                g = below.Children.Count == 0 ? cloud : Union(Cut(cloud, below, gap), below);   // 陰天：只有雲
            }
            var box = g.Bounds;
            double k = Math.Min(maxH / box.Height, maxW / box.Width);
            var t = new TransformGroup();
            t.Children.Add(new TranslateTransform(-box.X, -box.Y));
            t.Children.Add(new ScaleTransform(k, k));
            var canvas = new Canvas { Width = box.Width * k, Height = box.Height * k };
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = g, Fill = Brushes.White, RenderTransform = t });
            return canvas;
        }

        // 太陽：圓心一個實心圓，外面八道圓頭的光芒；r 是光芒外緣的半徑
        static Geometry SunShape(double cx, double cy, double r)
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            group.Children.Add(new EllipseGeometry(new Point(cx, cy), r * 0.46, r * 0.46));
            double t = r * 0.16;
            for (int i = 0; i < 8; i++)
            {
                var ray = new RectangleGeometry(new Rect(cx - t / 2, cy - r, t, r * 0.3), t / 2, t / 2);
                ray.Transform = new RotateTransform(45 * i, cx, cy);
                group.Children.Add(ray);
            }
            return group;
        }

        // 新月：大圓扣掉往右上偏的圓
        static Geometry Crescent(double cx, double cy, double r)
        {
            return new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(cx, cy), r, r),
                new EllipseGeometry(new Point(cx + r * 0.58, cy - r * 0.38), r * 0.8, r * 0.8));
        }

        // 晴朗的夜晚：新月（偏左下）加右上兩顆四角星
        static Geometry MoonStars(double cx, double cy, double r)
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            group.Children.Add(Crescent(cx - r * 0.12, cy + r * 0.1, r * 0.8));
            group.Children.Add(Star(cx + r * 0.55, cy - r * 0.6, r * 0.3));
            group.Children.Add(Star(cx + r * 0.82, cy - r * 0.08, r * 0.2));
            return group;
        }

        // 四角星（四邊往裡彎）
        static Geometry Star(double cx, double cy, double r)
        {
            return Geometry.Parse(string.Format(CultureInfo.InvariantCulture, "M{0},{1} Q{2},{1} {2},{3} Q{2},{1} {4},{1} Q{2},{1} {2},{5} Q{2},{1} {0},{1} Z",
                cx - r, cy, cx, cy - r, cx + r, cy + r));
        }

        // 雲：三個圓疊在一條圓頭的底上，三個圓的底都剛好碰到底邊（底是平的）；從 (x, y) 開始、寬 w，高 0.6 w
        static Geometry CloudShape(double x, double y, double w)
        {
            var shape = new GeometryGroup { FillRule = FillRule.Nonzero };
            shape.Children.Add(new RectangleGeometry(new Rect(x + w * 0.02, y + w * 0.36, w * 0.96, w * 0.24), w * 0.12, w * 0.12));
            shape.Children.Add(new EllipseGeometry(new Point(x + w * 0.27, y + w * 0.4), w * 0.2, w * 0.2));
            shape.Children.Add(new EllipseGeometry(new Point(x + w * 0.53, y + w * 0.3), w * 0.28, w * 0.28));
            shape.Children.Add(new EllipseGeometry(new Point(x + w * 0.78, y + w * 0.42), w * 0.18, w * 0.18));
            return shape.GetOutlinedPathGeometry();
        }

        // 圓頭的長條（左上角 (x, y)、寬 w、高 h）
        static Geometry Capsule(double x, double y, double w, double h)
        {
            double r = Math.Min(w, h) / 2;
            return new RectangleGeometry(new Rect(x, y, w, h), r, r);
        }

        static Geometry Union(Geometry a, Geometry b) { return Geometry.Combine(a, b, GeometryCombineMode.Union, null); }

        // a 扣掉 b（再往外多扣 gap，兩個形狀中間留一道細縫）
        static Geometry Cut(Geometry a, Geometry b, double gap)
        {
            var grown = Geometry.Combine(b, b.GetWidenedPathGeometry(new Pen(Brushes.Black, gap * 2)), GeometryCombineMode.Union, null);
            return Geometry.Combine(a, grown, GeometryCombineMode.Exclude, null);
        }
    }
}
