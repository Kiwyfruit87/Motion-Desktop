using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;

namespace VideoWallpaper
{
    // 正在播放的歌（Spotify、瀏覽器裡的 YouTube Music 等）：透過 Windows 的「系統媒體控制」讀取（跟音量浮動視窗上的歌名是同一份資料）。
    // 這些是 WinRT 介面，這裡用的舊版 C# 編譯器沒辦法直接引用（要另外裝 Windows SDK），所以執行時才用反射呼叫。
    static class NowPlaying
    {
        public class Track
        {
            public string Title, Artist, Album, Key;
            public string App;       // 哪個程式在播（Windows 的應用程式識別碼，例如 Spotify、Chrome）
            public bool Playing;
            public bool IsSpotify { get { return App != null && App.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0; } }
            public byte[] Art;   // 專輯封面圖檔
            public TimeSpan Position, Duration;   // Spotify 回報的播放位置、歌曲長度
            public DateTime Updated;              // 回報位置的時間（UTC）
        }

        // 推算現在播到第幾秒：Spotify 只在播放、暫停、跳轉時回報位置，播放中就從回報的時間往後推
        public static double PositionSeconds(Track t)
        {
            double p = t.Position.TotalSeconds;
            double since = (DateTime.UtcNow - t.Updated).TotalSeconds;
            if (t.Playing && since > 0 && since < 86400) p += since;
            if (t.Duration.TotalSeconds > 0) p = Math.Min(p, t.Duration.TotalSeconds);
            return Math.Max(0, p);
        }

        static readonly object gate = new object();
        static Track current;
        public static Track Current { get { lock (gate) return current; } }

        // 換歌的那一刻（在背景執行緒上呼叫，要很快做完）
        public static Action SongChanged;

        static Assembly bridge;
        static Type managerType, sessionType, propsType, infoType, timelineType, streamRefType;
        static object manager, session;
        static System.Threading.Timer timer;
        static Action changed;
        static int polling, artTries;
        static string artKey, shownApp;
        static byte[] artCache;

        // 開始每秒讀一次；有變化時在 UI 執行緒呼叫 onChanged
        public static void Start(Action onChanged)
        {
            var ui = Dispatcher.CurrentDispatcher;
            changed = delegate { ui.BeginInvoke(onChanged); };
            if (timer == null) timer = new System.Threading.Timer(delegate { Poll(); }, null, 0, 1000);
            else timer.Change(0, 1000);
        }

        public static void Stop()
        {
            if (timer != null) timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // 最近一次按的是哪個按鈕、什麼時候按的（換歌動畫用來決定往哪邊滑）
        public static string LastCommand;
        public static int LastCommandTick;

        // "prev" 上一首 / "toggle" 播放暫停 / "next" 下一首
        public static void Command(string action)
        {
            LastCommand = action;
            LastCommandTick = Environment.TickCount;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    object s = session;
                    if (s == null) return;
                    string method = action == "prev" ? "TrySkipPreviousAsync" : action == "next" ? "TrySkipNextAsync" : "TryTogglePlayPauseAsync";
                    Type rt;
                    Await(Call(s, sessionType, method, out rt), rt);
                    Thread.Sleep(250);
                    Poll();   // 馬上更新畫面上的圖示
                }
                catch { }
            });
        }

        static void Poll()
        {
            if (Interlocked.Exchange(ref polling, 1) == 1) return;
            try
            {
                if (!Init()) return;
                Type rt;
                // 選要顯示哪個播放器（Spotify、瀏覽器裡的 YouTube Music…）：正在播放的優先；
                // 上次顯示的那個還在播就繼續顯示它（同時開好幾個時才不會跳來跳去）；都沒在播就用 Windows 認定的「目前」那個
                string currentApp = null;
                try
                {
                    object currentSession = Call(manager, managerType, "GetCurrentSession", out rt);
                    if (currentSession != null) currentApp = Get(currentSession, sessionType, "SourceAppUserModelId") as string;
                }
                catch { }
                object found = null;
                string foundApp = null;
                int best = -1;
                foreach (object s in (IEnumerable)Call(manager, managerType, "GetSessions", out rt))
                {
                    string app = Get(s, sessionType, "SourceAppUserModelId") as string ?? "";
                    bool playing = false;
                    try { playing = Get(Call(s, sessionType, "GetPlaybackInfo", out rt), infoType, "PlaybackStatus").ToString() == "Playing"; }
                    catch { }
                    int score = (playing ? 4 : 0) + (app == shownApp ? 2 : 0) + (app == currentApp ? 1 : 0);
                    if (score > best) { best = score; found = s; foundApp = app; }
                }
                session = found;
                shownApp = foundApp;

                Track track = null;
                if (found != null)
                {
                    object props = Await(Call(found, sessionType, "TryGetMediaPropertiesAsync", out rt), rt);
                    object info = Call(found, sessionType, "GetPlaybackInfo", out rt);
                    string title = Get(props, propsType, "Title") as string, artist = Get(props, propsType, "Artist") as string ?? "";
                    if (!string.IsNullOrEmpty(title))
                    {
                        // 換歌後封面有時會晚一點才更新，所以換歌後的前幾次都重新讀一次封面
                        string songKey = title + "\n" + artist;
                        if (songKey != artKey)   // 換歌了
                        {
                            artKey = songKey; artCache = null; artTries = 0;
                            var songChanged = SongChanged;
                            if (songChanged != null) songChanged();
                        }
                        // 換歌後前 3 次都重新讀封面（有時會晚一點才更新）；還沒讀到的話最多試 10 次
                        if (artTries < 3 || (artCache == null && artTries < 10))
                        {
                            artTries++;
                            var art = ReadArt(Get(props, propsType, "Thumbnail"));
                            if (art != null) artCache = art;
                        }
                        bool playing = Get(info, infoType, "PlaybackStatus").ToString() == "Playing";
                        track = new Track
                        {
                            Title = title, Artist = artist, Album = Get(props, propsType, "AlbumTitle") as string ?? "", Playing = playing, Art = artCache,
                            App = foundApp,
                            Key = songKey + "\n" + playing + "\n" + (artCache == null ? 0 : artCache.Length) + "\n" + foundApp,
                        };
                        try
                        {
                            object timeline = Call(found, sessionType, "GetTimelineProperties", out rt);
                            track.Position = (TimeSpan)Get(timeline, timelineType, "Position");
                            track.Duration = (TimeSpan)Get(timeline, timelineType, "EndTime");
                            track.Updated = ((DateTimeOffset)Get(timeline, timelineType, "LastUpdatedTime")).UtcDateTime;
                        }
                        catch { }
                    }
                }

                string oldKey;
                lock (gate) { oldKey = current == null ? null : current.Key; current = track; }
                if (oldKey != (track == null ? null : track.Key) && changed != null) changed();
            }
            catch { }
            finally { polling = 0; }
        }

        static bool Init()
        {
            if (manager != null) return true;
            try
            {
                bridge = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                managerType = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager", "Windows.Media");
                sessionType = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSession", "Windows.Media");
                propsType = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties", "Windows.Media");
                infoType = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackInfo", "Windows.Media");
                timelineType = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionTimelineProperties", "Windows.Media");
                streamRefType = WinRT("Windows.Storage.Streams.IRandomAccessStreamReference", "Windows.Storage");
                Type rt;
                manager = Await(Call(null, managerType, "RequestAsync", out rt), rt);
                return true;
            }
            catch { return false; }
        }

        static byte[] ReadArt(object thumbnail)
        {
            if (thumbnail == null) return null;
            try
            {
                Type rt;
                // 封面最多等 0.8 秒（Spotify 有時要從網路下載），還沒好就先不要，下一秒再試；歌名不用等封面
                object winStream = Await(Call(thumbnail, streamRefType, "OpenReadAsync", out rt), rt, 800);
                var asStream = Cached("AsStreamForRead", () => bridge.GetType("System.IO.WindowsRuntimeStreamExtensions").GetMethods()
                    .First(m => m.Name == "AsStreamForRead" && m.GetParameters().Length == 1));
                using (var stream = (Stream)asStream.Invoke(null, new[] { winStream }))
                using (var copy = new MemoryStream()) { stream.CopyTo(copy); return copy.ToArray(); }
            }
            catch { return null; }
        }

        static Type WinRT(string name, string contract) { return Type.GetType(name + ", " + contract + ", ContentType=WindowsRuntime", true); }

        // 等待 WinRT 的 IAsyncOperation<T>：用方法宣告的回傳型別找出 T，再呼叫 AsTask<T>
        // 最多等 timeout 毫秒；等不到就放棄（丟出例外），不然 Spotify 沒回應時會一直卡住，之後每秒的讀取都會被擋住
        static object Await(object operation, Type declared, int timeout = 5000)
        {
            var result = declared.GetGenericArguments()[0];
            var asTask = Cached("AsTask " + result.FullName, () => bridge.GetType("System.WindowsRuntimeSystemExtensions").GetMethods()
                .First(m => m.Name == "AsTask" && m.IsGenericMethod && m.GetParameters().Length == 1
                         && m.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1")
                .MakeGenericMethod(result));
            var task = (System.Threading.Tasks.Task)asTask.Invoke(null, new[] { operation });
            if (!task.Wait(timeout)) throw new TimeoutException();
            var taskType = task.GetType();
            return Cached("Result " + taskType.FullName, () => taskType.GetProperty("Result")).GetValue(task, null);
        }

        static object Call(object target, Type type, string method, out Type returnType)
        {
            var info = Cached(type.FullName + "." + method + "()", () => type.GetMethod(method, Type.EmptyTypes));
            returnType = info.ReturnType;
            return info.Invoke(target, null);
        }

        static object Get(object target, Type type, string property)
        {
            return Cached(type.FullName + "." + property, () => type.GetProperty(property)).GetValue(target, null);
        }

        // 反射找到的方法和屬性記起來：每秒要讀好幾次，不要每次都在幾百個方法裡找一遍。
        // 讀歌名和按播放鍵在不同的執行緒，查表時要鎖住
        static readonly Dictionary<string, MemberInfo> members = new Dictionary<string, MemberInfo>();
        static T Cached<T>(string key, Func<T> find) where T : MemberInfo
        {
            lock (members)
            {
                MemberInfo found;
                if (!members.TryGetValue(key, out found)) members[key] = found = find();
                return (T)found;
            }
        }
    }
}
