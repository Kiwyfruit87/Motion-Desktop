using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows.Threading;

namespace VideoWallpaper
{
    class WeatherInfo
    {
        public double TempC;
        public int Code;        // WMO 天氣代碼
        public bool IsDay;
        public DateTime Fetched;
    }

    // 目前的天氣（攝氏）：先用 IP 查大概的位置，再向 Open-Meteo 查目前天氣。兩個都是免費、不用帳號的服務。
    // 設定檔可以用 weatherlocation=緯度,經度 指定位置，就不會用 IP 查。
    static class Weather
    {
        static WeatherInfo current;
        static double lat = double.NaN, lon = double.NaN;
        static volatile bool fetching;
        static int failedTick;   // 上次查詢失敗的時間（0 = 沒失敗）：失敗後隔 2 分鐘才再試，斷網、服務限流時不要每秒一直查

        public static WeatherInfo Current { get { return current; } }

        public static void SetFixedLocation(string text)
        {
            var parts = (text ?? "").Split(',');
            double a, b;
            if (parts.Length == 2 && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b)) { lat = a; lon = b; }
        }

        // 超過 20 分鐘沒更新才重新查（上次失敗的話隔 2 分鐘）；查到後在 UI 執行緒呼叫 onUpdated
        public static void Refresh(Action onUpdated)
        {
            if (fetching || (current != null && (DateTime.Now - current.Fetched).TotalMinutes < 20)) return;
            if (failedTick != 0 && Environment.TickCount - failedTick < 120000) return;
            fetching = true;
            var ui = Dispatcher.CurrentDispatcher;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                try
                {
                    if (double.IsNaN(lat)) Locate();
                    if (double.IsNaN(lat)) return;
                    var inv = CultureInfo.InvariantCulture;
                    var data = Fetch("https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(inv) + "&longitude=" + lon.ToString(inv)
                        + "&current=temperature_2m,weather_code,is_day&timezone=auto");
                    var now = (Dictionary<string, object>)data["current"];
                    current = new WeatherInfo
                    {
                        TempC = Convert.ToDouble(now["temperature_2m"], inv),
                        Code = Convert.ToInt32(now["weather_code"], inv),
                        IsDay = Convert.ToInt32(now["is_day"], inv) == 1,
                        Fetched = DateTime.Now,
                    };
                    ok = true;
                    if (onUpdated != null) ui.BeginInvoke(onUpdated);
                }
                catch { }
                finally
                {
                    failedTick = ok ? 0 : Environment.TickCount | 1;   // | 1：剛好算出 0 時也要記得失敗過
                    fetching = false;
                }
            });
        }

        static void Locate()
        {
            foreach (var url in new[] { "https://ipapi.co/json/", "https://ipwho.is/" })
            {
                try
                {
                    var d = Fetch(url);
                    object a, b;
                    if (d.TryGetValue("latitude", out a) && d.TryGetValue("longitude", out b) && a != null && b != null)
                    {
                        lat = Convert.ToDouble(a, CultureInfo.InvariantCulture);
                        lon = Convert.ToDouble(b, CultureInfo.InvariantCulture);
                        return;
                    }
                }
                catch { }
            }
        }

        static Dictionary<string, object> Fetch(string url) { return (Dictionary<string, object>)Web.Json(Web.Get(url)); }
    }
}
