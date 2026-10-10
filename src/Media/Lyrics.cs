using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Threading;

namespace VideoWallpaper
{
    // 歌詞：向 LRCLIB（https://lrclib.net，免費公開的社群歌詞資料庫）查詢。
    // 有時間標記的「同步歌詞」會跟著播放進度一行一行捲動；只有一般歌詞的話，就照歌曲長度平均分配時間慢慢捲。
    static class Lyrics
    {
        public class Line { public double Time; public string Text; }

        public class Result
        {
            public List<Line> Lines;   // null = 沒有歌詞（純音樂、查不到）
            public bool Synced;        // 有時間標記
            public bool Failed;        // 網路錯誤：過一分鐘再查一次
            public DateTime When;
            // 整首歌畫好的長圖（查到歌詞後在背景先畫好，按下歌詞按鈕就能馬上淡入）；還沒畫好是 null
            public int[] Sheet;
            public int SheetWidth, SheetHeight, SheetFor;   // SheetFor：照多高的螢幕畫的
            public double[] Centers, Heights;   // 每一行在長圖上的中心高度、行高
            public double Spacing;
        }

        static readonly Dictionary<string, Result> cache = new Dictionary<string, Result>();
        static readonly Queue<string> cacheOrder = new Queue<string>();   // 查過的歌（先查的在前面），只留最近 50 首
        const int CacheLimit = 50;
        static readonly HashSet<string> loading = new HashSet<string>();
        static readonly List<Result> sheets = new List<Result>();   // 有長圖的歌（一張好幾 MB，只留最近畫的 3 首；同一首只會出現一次）
        static readonly object gate = new object();

        public static string SongKey(NowPlaying.Track track) { return track.Title + "\n" + track.Artist; }

        public static bool Has(Result r) { return r != null && r.Lines != null && r.Lines.Count > 0; }

        public static bool SheetReady(Result r, int screenHeight) { return Has(r) && r.Sheet != null && r.SheetFor == screenHeight; }

        // 回傳 null 代表還在查；查完（有歌詞的話連長圖也畫好）會在 UI 執行緒呼叫 loaded
        public static Result Get(NowPlaying.Track track, int screenHeight, Action loaded)
        {
            string key = SongKey(track);
            Result cached;
            lock (gate)
            {
                bool have = cache.TryGetValue(key, out cached) && !(cached.Failed && (DateTime.UtcNow - cached.When).TotalSeconds > 60);
                if (have && (!Has(cached) || SheetReady(cached, screenHeight))) return cached;
                if (loading.Contains(key)) return have ? cached : null;
                loading.Add(key);
                if (!have) cached = null;
            }
            var ui = Dispatcher.CurrentDispatcher;
            string title = track.Title, artist = track.Artist, album = track.Album ?? "";
            double duration = track.Duration.TotalSeconds;
            var known = cached;   // 已經查過、只是長圖被清掉或螢幕高度變了：只要重畫
            ThreadPool.QueueUserWorkItem(delegate
            {
                Result r = known;
                if (r == null)
                {
                    try { r = Fetch(title, artist, album, duration); }
                    catch { r = new Result { Failed = true }; }
                    r.When = DateTime.UtcNow;
                }
                if (Has(r)) DrawSheet(r, screenHeight);
                lock (gate)
                {
                    if (!cache.ContainsKey(key)) cacheOrder.Enqueue(key);
                    cache[key] = r;
                    loading.Remove(key);
                    // 查過的歌太多了：最早查的拿掉（程式開很久時記憶體才不會一直變大）
                    while (cache.Count > CacheLimit && cacheOrder.Count > 0)
                    {
                        Result old;
                        string oldest = cacheOrder.Dequeue();
                        if (oldest == key) { cacheOrder.Enqueue(key); continue; }   // 現在這首不拿掉，排回最後面
                        if (!cache.TryGetValue(oldest, out old)) continue;
                        cache.Remove(oldest);
                        if (sheets.Remove(old)) old.Sheet = null;
                    }
                }
                ui.BeginInvoke(loaded);
            });
            return cached;
        }

        // 在另一條執行緒畫長圖（WPF 要在 STA 執行緒上畫），不會卡住鎖定畫面
        static void DrawSheet(Result r, int screenHeight)
        {
            var thread = new Thread(delegate()
            {
                try
                {
                    int w, h;
                    double[] centers, heights;
                    double spacing;
                    var pixels = LockClock.RenderLyricsSheet(screenHeight, r.Lines, out w, out h, out centers, out heights, out spacing);
                    lock (gate)
                    {
                        r.SheetWidth = w; r.SheetHeight = h; r.Centers = centers; r.Heights = heights; r.Spacing = spacing; r.SheetFor = screenHeight;
                        r.Sheet = pixels;
                        // 移到最後面（重畫的歌原本就在清單裡的話先拿掉，才不會有兩筆、清掉舊的那筆時把剛畫好的長圖也清掉）
                        sheets.Remove(r);
                        sheets.Add(r);
                        while (sheets.Count > 3) { sheets[0].Sheet = null; sheets.RemoveAt(0); }
                    }
                }
                catch { }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
        }

        static Result Fetch(string title, string artist, string album, double duration)
        {
            // 有時間標記的歌詞優先：查到的如果只有一般歌詞，先記在這裡，繼續找有沒有別人上傳的、有時間標記的版本，都找不到才用它
            Result fallback = null;

            // 1. 用歌名、歌手、專輯、長度精確查詢（太長的通常是 Podcast，資料庫不收）
            if (duration > 0 && duration < 3600)
            {
                string exact = Download("https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title)
                    + "&artist_name=" + Uri.EscapeDataString(artist) + "&album_name=" + Uri.EscapeDataString(album)
                    + "&duration=" + Math.Round(duration).ToString(CultureInfo.InvariantCulture));
                var r = exact == null ? null : FromEntry(Web.Json(exact) as Dictionary<string, object>, duration);
                if (r != null && (r.Synced || !Has(r))) return r;   // 有時間標記、或資料庫標明是純音樂：直接用
                fallback = r;
            }

            // 2. 搜尋，挑長度接近、有同步歌詞的；歌名有「 - Remastered 2011」「(feat. …)」之類的，去掉再搜一次
            //    （用完整歌名只搜到一般歌詞的話，也會再用去掉後的歌名搜一次，看有沒有帶時間標記的）
            foreach (string name in new[] { title, Simplify(title) }.Distinct())
            {
                if (name.Length == 0) continue;
                string found = Download("https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(name) + "&artist_name=" + Uri.EscapeDataString(artist));
                var r = found == null ? null : FromEntry(Pick(Web.Json(found) as object[], duration), duration);
                if (r == null) continue;
                if (r.Synced) return r;
                if (fallback == null || (Has(r) && !Has(fallback))) fallback = r;   // 一般歌詞比「純音樂」的標記優先
            }
            return fallback ?? new Result();   // 都沒有時間標記：用一般歌詞；什麼都沒有：沒有歌詞
        }

        // 搜尋結果裡挑最適合的：同步歌詞優先，再來是一般歌詞，長度差太多（多半是別的版本）的不要
        static Dictionary<string, object> Pick(object[] list, double duration)
        {
            if (list == null) return null;
            Dictionary<string, object> best = null;
            double bestScore = double.MaxValue;
            foreach (object o in list)
            {
                var e = o as Dictionary<string, object>;
                if (e == null) continue;
                bool synced = Text(e, "syncedLyrics").Length > 0, plain = Text(e, "plainLyrics").Length > 0, instrumental = Flag(e, "instrumental");
                if (!synced && !plain && !instrumental) continue;
                double d = Number(e, "duration");
                double diff = duration > 0 && d > 0 ? Math.Abs(d - duration) : 0;
                if (diff > 10) continue;
                double score = diff + (synced ? 0 : plain ? 100 : 200);
                if (score < bestScore) { bestScore = score; best = e; }
            }
            return best;
        }

        static Result FromEntry(Dictionary<string, object> e, double duration)
        {
            if (e == null) return null;
            if (Flag(e, "instrumental")) return new Result();   // 純音樂
            var synced = ParseLrc(Text(e, "syncedLyrics"));
            if (synced.Count > 0) return new Result { Lines = synced, Synced = true };

            // 只有一般歌詞：平均分配到歌曲長度的 5%～95% 之間（不知道長度就每行 4 秒）
            var plain = Text(e, "plainLyrics").Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
            while (plain.Count > 0 && plain[plain.Count - 1].Length == 0) plain.RemoveAt(plain.Count - 1);
            if (plain.Count == 0) return null;
            var lines = new List<Line>();
            for (int i = 0; i < plain.Count; i++)
                lines.Add(new Line { Text = plain[i], Time = duration > 0 ? duration * (0.05 + 0.9 * i / plain.Count) : i * 4.0 });
            return new Result { Lines = lines, Synced = false };
        }

        // LRC 格式：「[分:秒.百分秒] 歌詞」，一行可以有好幾個時間；[ar:…] 之類的資訊標籤略過
        static List<Line> ParseLrc(string text)
        {
            var lines = new List<Line>();
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                var times = new List<double>();
                int i = 0;
                while (i < raw.Length && raw[i] == '[')
                {
                    int close = raw.IndexOf(']', i);
                    if (close < 0) break;
                    string[] parts = raw.Substring(i + 1, close - i - 1).Split(':');
                    int minutes; double seconds;
                    if (parts.Length == 2 && int.TryParse(parts[0], out minutes)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                        times.Add(minutes * 60 + seconds);
                    i = close + 1;
                }
                string content = raw.Substring(i).Trim();
                foreach (double t in times) lines.Add(new Line { Time = t, Text = content });
            }
            lines.Sort((a, b) => a.Time.CompareTo(b.Time));
            return lines;
        }

        static string Simplify(string title)
        {
            string t = title;
            int dash = t.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) t = t.Substring(0, dash);
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*[\(\[](feat\.?|ft\.?|with)\s[^\)\]]*[\)\]]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return t.Trim();
        }

        // 查不到（404 / 400）回傳 null；連不上之類的錯誤直接丟出去
        static string Download(string url) { return Web.Get(url, true); }

        static string Text(Dictionary<string, object> e, string key) { object v; return e.TryGetValue(key, out v) && v is string ? (string)v : ""; }
        static bool Flag(Dictionary<string, object> e, string key) { object v; return e.TryGetValue(key, out v) && v is bool && (bool)v; }
        static double Number(Dictionary<string, object> e, string key)
        {
            object v;
            if (!e.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
    }
}
