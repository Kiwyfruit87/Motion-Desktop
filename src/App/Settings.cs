using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace VideoWallpaper
{
    // 介面文字：跟著 Windows 的顯示語言，中文 Windows 用中文，其他語言用英文
    static class Lang
    {
        public static readonly bool Chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        public static string T(string chinese, string english) { return Chinese ? chinese : english; }
        public static string AppName { get { return T("動態桌布", "Motion Desktop"); } }
    }

    class Settings
    {
        public string VideoPath = "";
        public bool Muted = true;
        public bool AutoPause = true;
        public bool TaskbarFix = true;   // 工作列透明（搭配 TranslucentTB）
        public Stretch Stretch = Stretch.UniformToFill;
        public string WeatherLocation = "";   // 選填：「緯度,經度」，不填就用 IP 自動判斷位置

        // 設定檔放在 exe 旁邊（可攜式），不寫進 C 槽的 AppData
        static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    if (key == "video") s.VideoPath = value;
                    else if (key == "muted") s.Muted = value == "1";
                    else if (key == "autopause") s.AutoPause = value == "1";
                    else if (key == "taskbarfix") s.TaskbarFix = value == "1";
                    else if (key == "stretch") { Stretch st; if (Enum.TryParse(value, out st)) s.Stretch = st; }
                    else if (key == "weatherlocation") s.WeatherLocation = value;
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new[] {
                    "video=" + VideoPath,
                    "muted=" + (Muted ? "1" : "0"),
                    "autopause=" + (AutoPause ? "1" : "0"),
                    "taskbarfix=" + (TaskbarFix ? "1" : "0"),
                    "stretch=" + Stretch,
                    "weatherlocation=" + WeatherLocation,
                }, Encoding.UTF8);
            }
            catch { }
        }
    }
}
