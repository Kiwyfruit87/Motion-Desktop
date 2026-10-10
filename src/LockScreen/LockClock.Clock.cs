using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace VideoWallpaper
{
    // 左下角的時鐘和天氣（含天氣圖示）
    static partial class LockClock
    {
        // 時間或天氣有變才需要重畫時鐘那張圖
        // now：同一個時間點（時間、AM / PM、日期都用它，跨過整點或午夜的那一瞬間才不會兜不起來）
        public static string ClockKey(DateTime now)
        {
            return TimeText(now) + AmPmText(now) + DateText(now) + (Weather.Current == null ? "" : Weather.Current.Fetched.Ticks.ToString());
        }

        // 時間下面的日期（英文）：「Friday, October 3」
        static string DateText(DateTime now)
        {
            return now.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
        }

        // 12 小時制：「9:02」＋ 後面小一號的「AM / PM」
        static string TimeText(DateTime now)
        {
            return now.ToString("h:mm", CultureInfo.InvariantCulture);
        }

        static string AmPmText(DateTime now)
        {
            return now.Hour < 12 ? "AM" : "PM";
        }

        // 左下角：時鐘和天氣，下面一行日期（now 跟算 ClockKey 時用同一個時間點）
        public static int[] RenderClock(DateTime now, int screenHeight, out int width, out int height, out int x, out int y)
        {
            double s = screenHeight / 1080.0;
            double timeSize = Math.Round(92 * s), ampmSize = Math.Round(34 * s), tempSize = Math.Round(40 * s);
            double pad = Math.Ceiling(30 * s);   // 陰影需要的留白

            var text = new TextBlock { FontFamily = ClockFont, Foreground = Brushes.White };
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);
            text.Inlines.Add(new Run(TimeText(now)) { FontSize = timeSize });
            // AM / PM 跟時間數字的底部對齊。字小，用中等粗細才不會比大數字顯得單薄；
            // 前面的空格用較大的字級，讓它跟數字之間多留一點距離
            text.Inlines.Add(new Run(" ") { FontSize = Math.Round(48 * s) });
            text.Inlines.Add(new Run(AmPmText(now)) { FontSize = ampmSize, FontWeight = FontWeights.Medium });
            var weather = Weather.Current;
            if (weather != null)
            {
                double iconSize = Math.Round(tempSize * 1.1);
                var icon = WeatherIcon(weather.Code, weather.IsDay, iconSize);
                // 圖示中心對齊溫度文字的中間
                icon.Margin = new Thickness(Math.Round(timeSize * 0.3), 0, Math.Round(tempSize * 0.2), Math.Round(tempSize * 0.36 - iconSize / 2));
                text.Inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Baseline });
                text.Inlines.Add(new Run(Math.Round(weather.TempC).ToString("0", CultureInfo.InvariantCulture) + "°") { FontSize = tempSize });
            }
            // 時間下面一行小字的日期（大數字的行高下面留很多空，往上拉近一點）
            var date = new TextBlock
            {
                Text = DateText(now), FontFamily = ClockFont, FontSize = Math.Round(26 * s), FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)),
                Margin = new Thickness(Math.Round(4 * s), -Math.Round(12 * s), 0, 0),
            };
            TextOptions.SetTextFormattingMode(date, TextFormattingMode.Ideal);
            var lines = new StackPanel();
            lines.Children.Add(text);
            lines.Children.Add(date);
            Border root;
            var pixels = ToPixels(lines, s, pad, out root, out width, out height);
            x = (int)Math.Round(64 * s - pad);                       // 左下角
            y = (int)Math.Round(screenHeight - 52 * s - height + pad);
            return pixels;
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
