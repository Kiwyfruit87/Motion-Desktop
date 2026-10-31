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
    // 正在播放的歌（只看音樂串流平台：Spotify、Apple Music、YouTube Music 等，見 MusicKind）：透過 Windows 的「系統媒體控制」讀取（跟音量浮動視窗上的歌名是同一份資料）。
    // 這些是 WinRT 介面，這裡用的舊版 C# 編譯器沒辦法直接引用（要另外裝 Windows SDK），所以執行時才用反射呼叫。
    static class NowPlaying
    {
        public class Track
        {
            public string Title, Artist, Album, Key;
            public string CardKey;   // 卡片上的樣子（歌、專輯、封面、哪個程式），不含播放 / 暫停：只是暫停的話卡片不用整張重畫
            public string App;       // 哪個程式在播（Windows 的應用程式識別碼，例如 Spotify、Chrome）
            public bool Playing;
            public byte[] Art;   // 專輯封面圖檔
            public TimeSpan Position, Duration;   // Spotify 回報的播放位置、歌曲長度
            public bool? Shuffle;                 // 隨機播放開著嗎（null：這個播放器不支援）
            public int? Repeat;                   // 循環：0 關、1 單曲、2 全部（null：不支援；跟 Windows 的 MediaPlaybackAutoRepeatMode 一樣）
            public bool CanSeek = true;           // 播放器接受跳到別的位置嗎（不接受的話進度條不能拖）
            public bool CanPrev = true, CanNext = true;   // 播放器現在能不能跳上一首 / 下一首（不能的話卡片上的鍵很淡）
            public DateTime Updated;              // 回報位置的時間（UTC）
        }

        // 推算現在播到第幾秒：Spotify 只在播放、暫停、跳轉時回報位置，播放中就從回報的時間往後推
        public static double PositionSeconds(Track t)
        {
            double p = t.Position.TotalSeconds;
            double since = (DateTime.UtcNow - t.Updated).TotalSeconds;
            if (t.Playing && since > 0 && since < 86400) p += since;
            // 單曲循環：播到底就自己從頭算（有些播放器重播同一首時不會回報新的位置）
            double d = t.Duration.TotalSeconds;
            if (d > 0) p = t.Repeat == 1 && t.Playing && p >= d ? p % d : Math.Min(p, d);
            return Math.Max(0, p);
        }

        static readonly object gate = new object();
        static bool stopped;   // 停止讀取了（gate 鎖著時讀寫）
        static Track current;
        public static Track Current { get { lock (gate) return current; } }

        // 換歌的那一刻（在背景執行緒上呼叫，要很快做完）
        public static Action SongChanged;

        static Assembly bridge;
        static Type managerType, sessionType, propsType, infoType, timelineType, streamRefType, controlsType;
        static object manager, session;
        static System.Threading.Timer timer;
        static Action changed;
        static int polling, artTries;
        static string artKey, shownApp;
        static byte[] artCache;

        // 音樂串流平台的程式（應用程式識別碼裡有這些字就算，不分大小寫）：Spotify、Apple Music、iTunes、
        // YouTube Music（Chrome / Edge 安裝成 App 的、非官方的電腦版）、KKBOX、Amazon Music、Tidal、Deezer、QQ 音樂、SoundCloud、Pandora
        static readonly string[] MusicApps = { "spotify", "applemusic", "itunes", "cinhimbnkkaeohfgghhklpknlkffjgod", "youtube-music", "youtubemusic",
            "kkbox", "amazonmusic", "tidal", "deezer", "qqmusic", "soundcloud", "pandora" };
        // 瀏覽器（Firefox 的識別碼是一串固定的編號）：分不出是哪個網站，所以有專輯的才算——
        // YouTube Music、Spotify、Apple Music 的網頁版都會給專輯，一般的影片（YouTube、其他網站）沒有
        static readonly string[] Browsers = { "chrome", "msedge", "308046b0af4a39cb", "firefox", "brave", "opera", "vivaldi", "thebrowsercompany" };

        // 2：音樂串流平台的程式、1：瀏覽器（要再看有沒有專輯）、0：其他（影片播放器、遊戲…不顯示）
        static int MusicKind(string app)
        {
            app = (app ?? "").ToLowerInvariant();
            if (MusicApps.Any(m => app.Contains(m))) return 2;   // 先看這個：安裝成 App 的 YouTube Music 的識別碼也有「chrome」
            return Browsers.Any(b => app.Contains(b)) ? 1 : 0;
        }

        // Spotify 有時候換歌了卻沒告訴 Windows：Windows 那邊一直是上一首的歌名、封面（只有時間軸是新的），重開 Spotify 才會好。
        // Spotify 自己的視窗標題（播放中是「歌手 - 歌名」，暫停時是「Spotify Premium」之類）一直是對的：
        // 兩邊對不上就回傳 true，從標題拆出歌名、歌手（歌名可能有「 - 」，所以從第一個拆）
        static string staleWindow;
        static DateTime staleSince;
        static bool StaleSpotify(string app, string title, string artist, out string realTitle, out string realArtist)
        {
            realTitle = title; realArtist = artist;
            if ((app ?? "").IndexOf("spotify", StringComparison.OrdinalIgnoreCase) < 0) return false;
            string window = null;
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("Spotify"))
                    using (p)
                        if (window == null && !string.IsNullOrEmpty(p.MainWindowTitle)) window = p.MainWindowTitle;
            }
            catch { }
            if (window == null) return false;
            int dash = window.IndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0 || window.EndsWith(" - " + title, StringComparison.Ordinal)) { staleWindow = null; return false; }   // 暫停中（看不出是哪首），或是同一首
            // 平常換歌時兩邊會差一下下才一起更新：對不上超過 2 秒才算
            if (window != staleWindow) { staleWindow = window; staleSince = DateTime.UtcNow; }
            if (DateTime.UtcNow - staleSince < TimeSpan.FromSeconds(2)) return false;
            realArtist = window.Substring(0, dash);
            realTitle = window.Substring(dash + 3);
            return true;
        }

        class Candidate { public object Session; public string App; public int Kind, Score; }

        // 開始每秒讀一次；有變化時在 UI 執行緒呼叫 onChanged
        public static void Start(Action onChanged)
        {
            lock (gate) stopped = false;
            var ui = Dispatcher.CurrentDispatcher;
            changed = delegate { ui.BeginInvoke(onChanged); };
            if (timer == null) timer = new System.Threading.Timer(delegate { Poll(); }, null, 0, 1000);
            else timer.Change(0, 1000);
        }

        // 鎖定畫面收起來：停止讀取，也忘掉上一首（下次打開時才不會先畫舊的歌、再翻頁換成現在的）
        public static void Stop()
        {
            if (timer != null) timer.Change(Timeout.Infinite, Timeout.Infinite);
            lock (gate) { stopped = true; current = null; }
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

        // 隨機播放開 / 關
        public static void SetShuffle(bool on)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    object s = session;
                    if (s == null) return;
                    var method = Cached(sessionType.FullName + ".TryChangeShuffleActiveAsync(bool)", () => sessionType.GetMethod("TryChangeShuffleActiveAsync", new[] { typeof(bool) }));
                    Await(method.Invoke(s, new object[] { on }), method.ReturnType);
                    Thread.Sleep(250);
                    Poll();
                }
                catch { }
            });
        }

        // 循環：0 關、1 單曲、2 全部
        public static void SetRepeat(int mode)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    object s = session;
                    if (s == null) return;
                    var method = Cached(sessionType.FullName + ".TryChangeAutoRepeatModeAsync", () => sessionType.GetMethod("TryChangeAutoRepeatModeAsync"));
                    var modeType = method.GetParameters()[0].ParameterType;   // Windows.Media.MediaPlaybackAutoRepeatMode
                    Await(method.Invoke(s, new object[] { Enum.ToObject(modeType, mode) }), method.ReturnType);
                    Thread.Sleep(250);
                    Poll();
                }
                catch { }
            });
        }

        // 跳到第 seconds 秒（鎖定畫面拖曳進度條放開時）
        public static void Seek(double seconds)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    object s = session;
                    if (s == null) return;
                    var method = Cached(sessionType.FullName + ".TryChangePlaybackPositionAsync(long)", () => sessionType.GetMethod("TryChangePlaybackPositionAsync", new[] { typeof(long) }));
                    Await(method.Invoke(s, new object[] { (long)(Math.Max(0, seconds) * TimeSpan.TicksPerSecond) }), method.ReturnType);
                    Thread.Sleep(250);
                    Poll();   // 馬上讀新的位置
                }
                catch { }
            });
        }

        // 播放器的狀態一變（鍵盤的播放暫停鍵、耳機上的按鍵、在播放器裡按的、換歌），Windows 會通知：馬上讀一次，不用等下一秒的輪詢。
        // 通知是 WinRT 的事件，這裡的舊版編譯器沒辦法直接寫 +=，用 WindowsRuntimeMarshal 接上（接不上就算了，還有每秒的輪詢）
        static readonly HashSet<object> watched = new HashSet<object>();   // 已經在聽的（同一個播放器只聽一次）
        static int pollAgain;   // 讀的途中又有變化：讀完再讀一次

        static void Watch(object target, Type type, params string[] events)
        {
            lock (watched) { if (!watched.Add(target)) return; }
            var notified = typeof(NowPlaying).GetMethod("OnNotified", BindingFlags.NonPublic | BindingFlags.Static);
            var add = typeof(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeMarshal).GetMethod("AddEventHandler");
            var token = typeof(System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken);
            foreach (string name in events)
            {
                try
                {
                    var ev = type.GetEvent(name);
                    var handler = Delegate.CreateDelegate(ev.EventHandlerType, notified);
                    var adder = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(ev.EventHandlerType, token), target, ev.GetAddMethod());
                    var remover = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(token), target, ev.GetRemoveMethod());
                    add.MakeGenericMethod(ev.EventHandlerType).Invoke(null, new object[] { adder, remover, handler });
                }
                catch { }
            }
        }

        static void OnNotified(object sender, object args)
        {
            lock (gate) if (stopped) return;   // 鎖定畫面收起來了：不用讀
            Interlocked.Exchange(ref pollAgain, 1);
            ThreadPool.QueueUserWorkItem(delegate { Poll(); });
        }

        static void Poll()
        {
            if (Interlocked.Exchange(ref polling, 1) == 1) return;
            Interlocked.Exchange(ref pollAgain, 0);
            try
            {
                if (!Init()) return;
                Type rt;
                // 選要顯示哪個播放器（只看音樂串流平台，見 MusicKind）：正在播放的優先；
                // 上次顯示的那個還在就繼續顯示它（同時開好幾個時才不會跳來跳去，都沒在播時也一樣）；都沒有才用 Windows 認定的「目前」那個
                string currentApp = null;
                try
                {
                    object currentSession = Call(manager, managerType, "GetCurrentSession", out rt);
                    if (currentSession != null) currentApp = Get(currentSession, sessionType, "SourceAppUserModelId") as string;
                }
                catch { }
                var candidates = new List<Candidate>();
                foreach (object s in (IEnumerable)Call(manager, managerType, "GetSessions", out rt))
                {
                    string app = Get(s, sessionType, "SourceAppUserModelId") as string ?? "";
                    Watch(s, sessionType, "PlaybackInfoChanged", "MediaPropertiesChanged", "TimelinePropertiesChanged");   // 播放 / 暫停、換歌、跳轉
                    int kind = MusicKind(app);
                    if (kind == 0) continue;
                    bool playing = false;
                    try { playing = Get(Call(s, sessionType, "GetPlaybackInfo", out rt), infoType, "PlaybackStatus").ToString() == "Playing"; }
                    catch { }
                    candidates.Add(new Candidate { Session = s, App = app, Kind = kind, Score = (playing ? 4 : 0) + (app == shownApp ? 2 : 0) + (app == currentApp ? 1 : 0) });
                }
                // 分數由高到低一個個看，第一個符合的就用；瀏覽器要讀到歌的資料、有專輯才算（讀到的資料下面直接用）
                object found = null, props = null;
                string foundApp = null;
                foreach (var c in candidates.OrderByDescending(c => c.Score))
                {
                    object read = null;
                    if (c.Kind == 1)
                    {
                        try { read = Await(Call(c.Session, sessionType, "TryGetMediaPropertiesAsync", out rt), rt); }
                        catch { continue; }
                        if (string.IsNullOrEmpty(Get(read, propsType, "AlbumTitle") as string)) continue;
                    }
                    found = c.Session; foundApp = c.App; props = read;
                    break;
                }
                session = found;
                shownApp = foundApp;

                Track track = null;
                if (found != null)
                {
                    if (props == null) props = Await(Call(found, sessionType, "TryGetMediaPropertiesAsync", out rt), rt);
                    object info = Call(found, sessionType, "GetPlaybackInfo", out rt);
                    string title = Get(props, propsType, "Title") as string, artist = Get(props, propsType, "Artist") as string ?? "";
                    string album = Get(props, propsType, "AlbumTitle") as string ?? "";
                    // Spotify 給 Windows 的還是上一首：歌名、歌手照它的視窗標題，專輯和封面是舊的就不用
                    bool stale = !string.IsNullOrEmpty(title) && StaleSpotify(foundApp, title, artist, out title, out artist);
                    if (stale) album = "";
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
                        if (!stale && (artTries < 3 || (artCache == null && artTries < 10)))
                        {
                            artTries++;
                            var art = ReadArt(Get(props, propsType, "Thumbnail"));
                            if (art != null) artCache = art;
                        }
                        bool playing = Get(info, infoType, "PlaybackStatus").ToString() == "Playing";
                        // 隨機播放、循環：播放器有提供這個功能（Controls）才算，沒有就是 null（卡片上的按鈕變淡、按了沒反應）
                        bool? shuffle = null;
                        int? repeat = null;
                        bool canSeek = true, canPrev = true, canNext = true;
                        try
                        {
                            object controls = Get(info, infoType, "Controls");
                            canSeek = (bool)Get(controls, controlsType, "IsPlaybackPositionEnabled");
                            try   // 自己一層：讀不到也不影響下面的隨機播放、循環
                            {
                                canPrev = (bool)Get(controls, controlsType, "IsPreviousEnabled");
                                canNext = (bool)Get(controls, controlsType, "IsNextEnabled");
                            }
                            catch { canPrev = canNext = true; }
                            if ((bool)Get(controls, controlsType, "IsShuffleEnabled"))
                            {
                                object v = Get(info, infoType, "IsShuffleActive");
                                shuffle = v != null && Convert.ToBoolean(v);
                            }
                            if ((bool)Get(controls, controlsType, "IsRepeatEnabled"))
                            {
                                object v = Get(info, infoType, "AutoRepeatMode");
                                repeat = v == null ? 0 : Convert.ToInt32(v);
                            }
                        }
                        catch { }
                        track = new Track
                        {
                            Title = title, Artist = artist, Album = album, Playing = playing, Art = artCache,
                            App = foundApp,
                            Shuffle = shuffle, Repeat = repeat, CanSeek = canSeek, CanPrev = canPrev, CanNext = canNext,
                            Key = songKey + "\n" + playing + "\n" + shuffle + "\n" + repeat + "\n" + (artCache == null ? 0 : artCache.Length) + "\n" + foundApp
                                + "\n" + canSeek + canPrev + canNext,
                            CardKey = songKey + "\n" + album + "\n" + (artCache == null ? 0 : artCache.Length) + "\n" + foundApp,
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
                lock (gate)
                {
                    if (stopped) return;   // 讀的途中鎖定畫面收起來了：不要把這首寫回去（下次打開才不會先畫舊的）
                    oldKey = current == null ? null : current.Key;
                    current = track;
                }
                if (oldKey != (track == null ? null : track.Key) && changed != null) changed();
            }
            catch { }
            finally
            {
                polling = 0;
                if (Interlocked.Exchange(ref pollAgain, 0) == 1) ThreadPool.QueueUserWorkItem(delegate { Poll(); });   // 讀的途中又有變化
            }
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
                controlsType = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackControls", "Windows.Media");
                streamRefType = WinRT("Windows.Storage.Streams.IRandomAccessStreamReference", "Windows.Storage");
                Type rt;
                manager = Await(Call(null, managerType, "RequestAsync", out rt), rt);
                Watch(manager, managerType, "SessionsChanged", "CurrentSessionChanged");   // 開了、關了播放器
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
