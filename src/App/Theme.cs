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

        public static void Apply(ResourceDictionary r, bool dark)
        {
            var accent = new SolidColorBrush(Accent(dark));
            accent.Freeze();
            r["Accent"] = accent;
            r["TextOnAccent"]    = B(dark ? 0xFF000000 : 0xFFFFFFFF);
            r["TextPrimary"]     = B(dark ? 0xFFFFFFFF : 0xE4000000);
            r["TextSecondary"]   = B(dark ? 0xC5FFFFFF : 0x9E000000);
            r["CardFill"]        = B(dark ? 0x0DFFFFFF : 0xB3FFFFFF);
            r["CardStroke"]      = B(dark ? 0x12FFFFFF : 0x0F000000);
            r["ControlFill"]     = B(dark ? 0x0FFFFFFF : 0xB3FFFFFF);
            r["ControlStroke"]   = B(dark ? 0x12FFFFFF : 0x0F000000);
            r["HoverOverlay"]    = B(dark ? 0x0FFFFFFF : 0x09000000);
            r["AccentOverlay"]   = B(dark ? 0x1A000000 : 0x1AFFFFFF);
            r["SegmentTrack"]    = B(dark ? 0x19000000 : 0x06000000);
            r["SegmentSelected"] = B(dark ? 0x15FFFFFF : 0xFFFFFFFF);
            r["Divider"]         = B(dark ? 0x15FFFFFF : 0x0F000000);
            r["FooterFill"]      = B(dark ? 0x33000000 : 0x0C000000);
            r["ToggleOffFill"]   = B(dark ? 0x19000000 : 0x06000000);
            r["ToggleOffStroke"] = B(dark ? 0x8BFFFFFF : 0x72000000);
            r["TooltipFill"]     = B(dark ? 0xFF2C2C2C : 0xFFF9F9F9);
            r["WindowFallback"]  = B(dark ? 0xFF202020 : 0xFFF3F3F3);
        }
    }
}
