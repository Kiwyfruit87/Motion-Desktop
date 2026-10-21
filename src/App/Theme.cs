using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace VideoWallpaper
{
    // 跟隨 Windows 的深淺色模式與強調色
    static class Theme
    {
        public static bool IsDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("SystemUsesLightTheme");
                    if (v is int) return (int)v == 0;
                }
            }
            catch { }
            return false;
        }

        // 系統強調色：深色模式用較亮的變體、淺色模式用較深的變體（跟 Windows 11 一樣）
        static Color Accent(bool dark)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var palette = key == null ? null : key.GetValue("AccentPalette") as byte[];
                    if (palette != null && palette.Length >= 32)
                    {
                        int i = (dark ? 1 : 4) * 4;
                        return Color.FromRgb(palette[i], palette[i + 1], palette[i + 2]);
                    }
                }
            }
            catch { }
            return dark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0);
        }

        static SolidColorBrush B(long argb)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            return brush;
        }

        // 玻璃的填色：上面亮一點、下面淡一點
        static Brush Fill(long top, long bottom)
        {
            var brush = new LinearGradientBrush(C(top), C(bottom), 90);
            brush.Freeze();
            return brush;
        }

        // 玻璃的亮邊：光從左上來，左上最亮、右下反光次亮、中間幾乎看不到
        static Brush Rim(long topLeft, long middle1, long middle2, long bottomRight)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            brush.GradientStops.Add(new GradientStop(C(topLeft), 0));
            brush.GradientStops.Add(new GradientStop(C(middle1), 0.35));
            brush.GradientStops.Add(new GradientStop(C(middle2), 0.65));
            brush.GradientStops.Add(new GradientStop(C(bottomRight), 1));
            brush.Freeze();
            return brush;
        }

        // 模仿面板玻璃的亮邊（著色器算的那圈）：左上、右下兩個角最亮，直邊中等，右上、左下幾乎沒有。
        // 斜向漸層剛好是這樣：0 是左上角、0.5 是右上和左下角、1 是右下角，直邊在中間
        static Brush Gloss(byte corner, byte edge, byte side)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(corner, 255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(edge, 255, 255, 255), 0.25));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(side, 255, 255, 255), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(edge, 255, 255, 255), 0.75));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(corner, 255, 255, 255), 1));
            brush.Freeze();
            return brush;
        }

        static Color C(long argb) { return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb); }

        // 強調色的記號（變了才需要重新套顏色）
        public static string AccentKey() { return Accent(true).ToString() + Accent(false); }

        // 面板的顏色。glass：控制面板用液態玻璃（面板的底由玻璃畫，這裡只給字和小東西的顏色）；false 是整片毛玻璃的面板
        public static void Apply(ResourceDictionary r, bool dark, bool glass)
        {
            var accent = new SolidColorBrush(Accent(dark));
            accent.Freeze();
            r["Accent"] = accent;
            r["TextPrimary"]     = B(dark ? 0xFFFFFFFF : 0xE4000000);
            r["TextSecondary"]   = B(dark ? 0xC5FFFFFF : 0x9E000000);
            // 液態玻璃：卡片、按鈕都是半透明的白，加一圈亮邊
            r["CardFill"]        = dark ? Fill(0x1AFFFFFF, 0x0AFFFFFF) : Fill(0x99FFFFFF, 0x66FFFFFF);
            r["ControlFill"]     = dark ? Fill(0x24FFFFFF, 0x0FFFFFFF) : Fill(0xB3FFFFFF, 0x80FFFFFF);
            r["ControlStroke"]   = dark ? Rim(0x73FFFFFF, 0x18FFFFFF, 0x0CFFFFFF, 0x40FFFFFF) : Rim(0xFFFFFFFF, 0x80FFFFFF, 0x4DFFFFFF, 0xCCFFFFFF);
            r["PanelRim"]        = dark ? Rim(0x59FFFFFF, 0x14FFFFFF, 0x0AFFFFFF, 0x2EFFFFFF) : Rim(0xFFFFFFFF, 0x66FFFFFF, 0x33FFFFFF, 0xB3FFFFFF);
            r["HoverOverlay"]    = B(dark ? 0x12FFFFFF : 0x0A000000);
            r["SegmentTrack"]    = B(dark ? 0x1F000000 : 0x0F000000);
            r["SegmentSelected"] = dark ? Fill(0x40FFFFFF, 0x22FFFFFF) : Fill(0xFFFFFFFF, 0xF2FFFFFF);
            r["InnerFill"]       = B(dark ? 0x1FFFFFFF : 0x14000000);
            r["SectionFill"]     = B(dark ? 0x12FFFFFF : 0x80FFFFFF);   // 面板裡分區的底
            r["BarFill"]         = r["SectionFill"];                     // 工具列每一組的底
            // 開關：關的時候灰色、開的時候綠色（跟 iPhone 一樣）；按住時圓鈕變成透明的玻璃透鏡
            r["ToggleOffFill"]   = B(dark ? 0x2EFFFFFF : 0x1C000000);
            r["ToggleOn"]        = B(dark ? 0xFF30D158 : 0xFF34C759);
            r["KnobLens"]        = dark ? Fill(0x33FFFFFF, 0x10FFFFFF) : Fill(0x66FFFFFF, 0x26FFFFFF);
            r["KnobRim"]         = dark ? Rim(0xE6FFFFFF, 0x59FFFFFF, 0x33FFFFFF, 0xB3FFFFFF) : Rim(0xFFFFFFFF, 0x99FFFFFF, 0x66FFFFFF, 0xE6FFFFFF);
            r["TooltipFill"]     = B(dark ? 0xE82C2C2E : 0xEBFAFAFA);   // 提示字（和其他浮出來的小卡片）：幾乎不透明，稍微透一點後面
            // 提示字、確認卡的亮邊：一條淡淡的細線，模仿液態玻璃的光澤（左上、右下最亮，右上、左下幾乎沒有）；淺色模式白底上要再亮一點才看得到
            r["GlassRim"]        = dark ? Gloss(0x4C, 0x2E, 0x0A) : Gloss(0xFF, 0xD9, 0x80);
            r["WindowFallback"]  = B(dark ? 0xFF1E1E1E : 0xFFF3F3F3);
            if (!glass) return;
            // 液態玻璃（數值照原型）：卡片、按鈕的底和亮邊都由玻璃畫；開關關著是淡白（深色）/ 淡灰（淺色），開著是綠色
            var clear = Brushes.Transparent;
            r["CardFill"] = r["ControlFill"] = r["ControlStroke"] = r["PanelRim"] = clear;
            r["TextSecondary"]   = B(dark ? 0xB8FFFFFF : 0x99000000);
            r["HoverOverlay"] = clear;   // 跟官方一樣：滑鼠移上去不反白
            r["InnerFill"]       = B(dark ? 0x24FFFFFF : 0x17000000);   // ＋、− 這種小按鈕：淡淡的圓底
            // 分區只用淡淡的底色（玻璃不疊玻璃）；分段控制照 iOS：軌道是淡灰，選取平常是實心（淺色白、深色灰），按住才變玻璃透鏡
            r["SectionFill"]     = B(dark ? 0x14FFFFFF : 0x59FFFFFF);
            r["BarFill"]         = B(dark ? 0x0FFFFFFF : 0x40FFFFFF);   // 工具列：一塊清澈的玻璃（透鏡由第二層畫），底下只墊一點點白
            r["SegmentTrack"]    = B(dark ? 0x3D767680 : 0x1F767680);
            r["SegmentSelected"] = B(dark ? 0xFF636366 : 0xFFFFFFFF);
            r["KnobRim"]         = clear;
            r["ToggleOffFill"]   = B(dark ? 0x38FFFFFF : 0x6E787880);   // 淺色：白底上約 #C5C5C7（官方）
            r["ToggleOn"]        = B(dark ? 0xFF30D158 : 0xFF34C759);   // 深色 #30D158、淺色 #34C759
        }
    }
}
