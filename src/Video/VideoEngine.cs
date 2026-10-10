using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoWallpaper
{
    // 播放引擎：自己一條執行緒，負責解碼並把畫面輸出到每個螢幕的 VideoWindow。
    // 外面（UI 執行緒）只透過下面幾個方法下指令，實際動作都排進佇列、在引擎執行緒上執行。
    partial class VideoEngine : IDisposable
    {
        [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            [In] int[] featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);
        [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] static extern int MFShutdown();
        [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
        [DllImport("mfplat.dll")] static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager manager);

        static readonly Guid MF_MEDIA_ENGINE_CALLBACK = new Guid("c60381b8-83a4-41f8-a3d0-de05076849a9");
        static readonly Guid MF_MEDIA_ENGINE_DXGI_MANAGER = new Guid("065702da-1094-486d-8617-ee7cc4ee4648");
        static readonly Guid MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT = new Guid("5066893c-8cf9-42bc-8b8a-472212e52726");
        static readonly Guid IID_IDXGIFactory2 = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
        const int DXGI_STATUS_OCCLUDED = 0x087A0001;   // 視窗目前看不到（例如鎖定中），不算錯誤

        class Target
        {
            public IntPtr Hwnd;
            public int Width, Height;
            public IDXGISwapChain1 SwapChain;
            public IntPtr BackBuffer;
            // 有疊圖的輸出畫面（鎖定畫面）：影片先畫在這張乾淨的畫布，每一格整張蓋到輸出畫面上再貼疊圖。
            // 就算某一格影片沒更新到，蓋上去的也是乾淨的影片，疊圖不會被貼兩次（那一格會突然變亮、閃一下）
            public IntPtr VideoTexture;
            // VideoTexture 裡已經是最新的一格（影片沒換格時直接拿來用，不用再叫解碼器輸出一次）；
            // FreshVideo：這次重畫的影片是剛換的新一格（毛玻璃要重新模糊）
            public bool VideoCurrent, FreshVideo;
            public bool Visible = true;
            // 已經畫上至少一格影片：剛建好的視窗是空的，就算被視窗蓋住（不用畫）也要先畫一格，
            // 不然工作列後面、視窗縮小的瞬間看到的會是原本的桌布
            public bool Presented;
            public int FirstFrameTries;
            // 疊在影片上的圖（鎖定畫面的時鐘、正在播放、歌詞）：每一格影片畫好後貼上去。
            // 優先用 Direct2D（D2D）；這台電腦不支援才退回 GDI（Surface）。Canvas 是畫疊圖用的（兩種其中一種）
            public D2DTarget D2D;
            public IDXGISurface1 Surface;
            public OverlayCanvas Canvas;
            public bool CanOverlay { get { return D2D != null || Surface != null; } }

            // 疊圖：名字 → 圖。照疊的順序排好的清單只在增減時重排（每一格都要照順序畫，不要每次都排序）
            readonly Dictionary<string, Overlay> overlays = new Dictionary<string, Overlay>();
            List<Overlay> ordered;
            public int OverlayCount { get { return overlays.Count; } }
            public bool TryGet(string name, out Overlay o) { return overlays.TryGetValue(name, out o); }
            public void Put(string name, Overlay o) { overlays[name] = o; ordered = null; }
            public void Remove(string name) { overlays.Remove(name); ordered = null; }
            public IEnumerable<KeyValuePair<string, Overlay>> Named { get { return overlays; } }
            public IEnumerable<Overlay> All { get { return overlays.Values; } }
            public List<Overlay> Ordered { get { return ordered ?? (ordered = overlays.Values.OrderBy(v => v.Z).ToList()); } }

            public void ReleaseOverlays()
            {
                foreach (var o in overlays.Values) o.Release();
                overlays.Clear();
                ordered = null;
            }
        }

        readonly string path;
        readonly bool initialMuted;
        readonly Dispatcher ui;
        readonly Action<string> onError;
        readonly Action onLost;
        readonly Thread thread;
        readonly ConcurrentQueue<Action> commands = new ConcurrentQueue<Action>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool redraw;
        // 每個畫面都要重畫（視窗要求重畫、剛建好、剛露出來、換縮放方式）；
        // 只有 redraw 的話是疊圖在動（光暈、歌詞捲動），只重畫有疊圖的畫面，其他螢幕、桌面不用跟著重新複製影片
        volatile bool redrawAll;
        volatile bool frameReady;   // 影片已經解碼出第一格（在這之前複製出來的是黑畫面，不能當作「畫好了」）

        // 健康狀態，給 UI 執行緒的監控用
        volatile int lastHealthyTick = Environment.TickCount;   // 上次輸出畫面成功（或正常地被遮住）的時間
        volatile int presentedFrames;                          // 成功輸出的新畫面數量
        public int MillisecondsSinceHealthy { get { return Environment.TickCount - lastHealthyTick; } }
        public int PresentedFrames { get { return presentedFrames; } }

        volatile int positionMs;   // 目前播放到哪裡（控制面板的預覽會對齊這個位置）
        public TimeSpan Position { get { return TimeSpan.FromMilliseconds(positionMs); } }

        // 光暈疊圖（Pulse）現在多亮（0～1）。鎖定畫面接上抓拍子（Beat.Glow）；沒接的話光暈不亮。
        // 亮度跟上次畫的差到看得出來（1/32）才重畫：剛亮起來時變化大，每次螢幕更新都畫（不會慢）；
        // 慢慢退的時候變化小，自動少畫幾次，省 CPU
        public volatile Func<long, double> PulseLevel;
        double drawnGlow;   // 上次畫上去的光暈亮度（只在引擎執行緒上用）

        // 以下只在引擎執行緒上使用
        readonly List<Target> targets = new List<Target>();
        IntPtr device, context;
        object deviceObject;
        int transferFailures, lastDeviceCheck;
        IMFDXGIDeviceManager manager;
        IDXGIFactory2 factory;
        IntPtr d2dFactory;   // Direct2D（畫疊圖用），建立失敗就是 0，改用 GDI
        IDXGIOutput output;
        IMFMediaEngine engine;
        EngineNotify notify;
        Stretch stretch;
        bool playing, stopping, mfStarted;

        // UI 執行緒這邊記住上次送出的狀態，沒變就不重複下指令
        bool uiPlaying;
        readonly Dictionary<int, bool> uiVisible = new Dictionary<int, bool>();

        public VideoEngine(string path, bool muted, Stretch stretch, Action<string> onError, Action onLost)
        {
            this.path = path;
            this.initialMuted = muted;
            this.stretch = stretch;
            this.onError = onError;
            this.onLost = onLost;
            ui = Dispatcher.CurrentDispatcher;
            thread = new Thread(Run) { IsBackground = true, Name = "VideoEngine" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        // ---------- 給 UI 執行緒用的指令 ----------

        public void AddTarget(IntPtr hwnd, int width, int height) { AddTarget(hwnd, width, height, false); }

        // withOverlay：之後要疊時鐘圖上去（輸出畫面要能用 GDI 畫）
        public void AddTarget(IntPtr hwnd, int width, int height, bool withOverlay)
        {
            Post(delegate { CreateTarget(hwnd, width, height, withOverlay); });
        }

        public void RemoveTarget(IntPtr hwnd)
        {
            Post(delegate
            {
                var t = targets.Find(x => x.Hwnd == hwnd);
                if (t == null) return;
                ReleaseTarget(t);
                targets.Remove(t);
            });
        }

        public void SetPlaying(bool play)
        {
            if (play == uiPlaying) return;
            uiPlaying = play;
            if (play) lastHealthyTick = Environment.TickCount;   // 監控從現在開始算
            Post(delegate
            {
                playing = play;
                if (play) engine.Play(); else engine.Pause();
            });
        }

        public void SetMuted(bool muted) { Post(delegate { engine.SetMuted(muted ? 1 : 0); }); }

        public void SetStretch(Stretch value) { Post(delegate { stretch = value; redraw = redrawAll = true; foreach (var t in targets) t.VideoCurrent = false; }); }

        public void SetTargetVisible(int index, bool visible)
        {
            bool old;
            if (uiVisible.TryGetValue(index, out old) && old == visible) return;
            uiVisible[index] = visible;
            if (visible) lastHealthyTick = Environment.TickCount;
            Post(delegate
            {
                if (index >= targets.Count) return;
                targets[index].Visible = visible;
                if (visible) redraw = redrawAll = true;
            });
        }

        public void RequestRedraw() { redraw = redrawAll = true; wake.Set(); }

        // 前面排的指令都做完之後，在 UI 執行緒執行 done
        public void WhenIdle(Action done) { Post(delegate { ui.BeginInvoke(done); }); }

        public void Dispose()
        {
            Post(delegate { stopping = true; });
            thread.Join(3000);
        }

        void Post(Action command) { commands.Enqueue(command); wake.Set(); }

        // ---------- 引擎執行緒 ----------

        void Run()
        {
            try
            {
                Init();
                while (true)
                {
                    Action command;
                    while (commands.TryDequeue(out command))
                    {
                        try { command(); }
                        catch (Exception ex) { ReportError(Lang.T("播放引擎發生錯誤：", "Playback engine error: ") + ex.Message); }
                    }
                    if (stopping) break;

                    long pts;
                    bool newFrame = engine.OnVideoStreamTick(out pts) == 0;   // S_OK = 有新的一格
                    if (newFrame) frameReady = true;
                    positionMs = (int)(engine.GetCurrentTime() * 1000);
                    // 邊框光暈的亮度變了：這次螢幕更新就畫，不等影片的下一格（只有在有疊圖的畫面才看，光暈就畫在那上面）
                    var pulse = PulseLevel;
                    bool glowChanged = pulse != null && targets.Exists(t => t.OverlayCount > 0) && Math.Abs(pulse(AnimationClock.Now) - drawnGlow) >= 1 / 32.0;
                    if (newFrame || redraw || glowChanged) Render(newFrame);
                    if (stopping) break;

                    // 每秒確認一次顯示卡裝置還在（驅動更新、顯示卡重設時會失效）
                    if (Environment.TickCount - lastDeviceCheck > 1000)
                    {
                        lastDeviceCheck = Environment.TickCount;
                        int reason = ((ID3D11Device)deviceObject).GetDeviceRemovedReason();
                        if (reason != 0) { Lost(Lang.T("顯示卡裝置失效 0x", "Graphics device lost 0x") + reason.ToString("X8")); break; }
                    }

                    if (playing)
                    {
                        // 跟著螢幕更新頻率走；拿不到螢幕（例如鎖定中）就退回用睡的
                        if (output == null || output.WaitForVBlank() < 0) Thread.Sleep(8);
                    }
                    else wake.WaitOne(redraw ? 15 : 250);
                }
            }
            catch (Exception ex)
            {
                ReportError(Lang.T("無法啟動播放引擎：", "Couldn't start the playback engine: ") + ex.Message);
            }
            finally
            {
                Cleanup();
            }
        }

        void Init()
        {
            Check(MFStartup(0x20070, 0), "MFStartup");
            mfStarted = true;

            int[] levels = { 0xb100, 0xb000, 0xa100, 0xa000, 0x9300 };
            int level;
            const uint BGRA = 0x20, VIDEO = 0x800;
            int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, BGRA | VIDEO, levels, (uint)levels.Length, 7, out device, out level, out context);
            if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, BGRA, levels, (uint)levels.Length, 7, out device, out level, out context);
            Check(hr, "D3D11CreateDevice");
            deviceObject = Marshal.GetObjectForIUnknown(device);
            ((ID3D10Multithread)deviceObject).SetMultithreadProtected(1);   // Media Engine 會從別的執行緒使用這個裝置
            d2dFactory = D2DTarget.CreateFactory();

            uint resetToken;
            Check(MFCreateDXGIDeviceManager(out resetToken, out manager), "MFCreateDXGIDeviceManager");
            Check(manager.ResetDevice(device, resetToken), "ResetDevice");

            IDXGIAdapter adapter;
            Check(((IDXGIDevice)deviceObject).GetAdapter(out adapter), "GetAdapter");
            object parent;
            var iidFactory = IID_IDXGIFactory2;
            Check(adapter.GetParent(ref iidFactory, out parent), "GetParent");
            factory = (IDXGIFactory2)parent;
            IDXGIOutput firstOutput;
            if (adapter.EnumOutputs(0, out firstOutput) == 0) output = firstOutput;
            Marshal.ReleaseComObject(adapter);

            IMFAttributes attributes;
            Check(MFCreateAttributes(out attributes, 3), "MFCreateAttributes");
            notify = new EngineNotify(this);
            var key = MF_MEDIA_ENGINE_DXGI_MANAGER; Check(attributes.SetUnknown(ref key, manager), "SetUnknown");
            key = MF_MEDIA_ENGINE_CALLBACK; Check(attributes.SetUnknown(ref key, notify), "SetUnknown");
            key = MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT; Check(attributes.SetUINT32(ref key, DXGI_FORMAT_B8G8R8A8_UNORM), "SetUINT32");
            var classFactory = (IMFMediaEngineClassFactory)new MFMediaEngineClassFactory();
            Check(classFactory.CreateInstance(0, attributes, out engine), "CreateInstance");
            Marshal.ReleaseComObject(classFactory);
            Marshal.ReleaseComObject(attributes);

            engine.SetLoop(1);       // 播完直接從頭接著播
            engine.SetAutoPlay(0);
            engine.SetPreload(4);    // MF_MEDIA_ENGINE_PRELOAD_AUTOMATIC：還沒播放也先解碼好第一格（桌面被蓋住、暫停中也有畫面可以先畫上去）
            engine.SetMuted(initialMuted ? 1 : 0);
            if (!SetSourceFromMemory()) Check(engine.SetSource(path), "SetSource");
        }

        // 整支影片先讀進記憶體，播放器之後都從記憶體讀，不會再碰硬碟：
        // 影片放在傳統硬碟上的話，桌面被蓋住（暫停）一陣子後硬碟會停轉、影片也可能被 Windows 從快取清掉，
        // 再開始播（例如打開鎖定畫面）時要等硬碟轉起來，影片會卡好幾秒。太大的影片（超過 256 MB）或失敗時照舊從檔案讀
        [DllImport("shlwapi.dll")] static extern IntPtr SHCreateMemStream(byte[] data, uint size);
        [DllImport("mfplat.dll")] static extern int MFCreateMFByteStreamOnStream(IntPtr stream, out IntPtr byteStream);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetSourceFromByteStreamFn(IntPtr self, IntPtr byteStream, [MarshalAs(UnmanagedType.BStr)] string url);
        static readonly Guid IID_IMFMediaEngineEx = new Guid("83015ead-b1e6-40d0-a98a-37145ffe1ad1");
        const long MemoryLimit = 256L * 1024 * 1024;

        bool SetSourceFromMemory()
        {
            IntPtr stream = IntPtr.Zero, byteStream = IntPtr.Zero, unknown = IntPtr.Zero, extended = IntPtr.Zero;
            try
            {
                if (new System.IO.FileInfo(path).Length > MemoryLimit) return false;
                byte[] data = System.IO.File.ReadAllBytes(path);
                stream = SHCreateMemStream(data, (uint)data.Length);   // 複製一份到它自己的記憶體
                if (stream == IntPtr.Zero || MFCreateMFByteStreamOnStream(stream, out byteStream) < 0) return false;
                unknown = Marshal.GetIUnknownForObject(engine);
                var iid = IID_IMFMediaEngineEx;
                if (Marshal.QueryInterface(unknown, ref iid, out extended) < 0) return false;
                // IMFMediaEngineEx::SetSourceFromByteStream：IUnknown 3 個 + IMFMediaEngine 42 個之後的第一個；網址只用來看副檔名判斷格式
                return ComVtable.Method<SetSourceFromByteStreamFn>(extended, 45)(extended, byteStream, path) >= 0;
            }
            catch { return false; }
            finally
            {
                foreach (var com in new[] { extended, unknown, byteStream, stream }) if (com != IntPtr.Zero) Marshal.Release(com);
            }
        }

        void CreateTarget(IntPtr hwnd, int width, int height, bool withOverlay)
        {
            // 要疊圖時先試 Direct2D；這台電腦不支援的話，改建一個 GDI 能畫的輸出畫面
            var target = CreateSwapChain(hwnd, width, height, false);
            if (withOverlay)
            {
                target.D2D = D2DTarget.Create(d2dFactory, target.BackBuffer);
                if (target.D2D != null) target.Canvas = new D2DCanvas(target.D2D);
                else
                {
                    ReleaseTarget(target);
                    target = CreateSwapChain(hwnd, width, height, true);
                    target.Surface = (IDXGISurface1)Marshal.GetObjectForIUnknown(target.BackBuffer);
                    target.Canvas = new GdiCanvas(target.Surface);
                }
                target.VideoTexture = CreateVideoTexture(width, height);
            }
            targets.Add(target);
            redraw = redrawAll = true;
        }

        static void ReleaseTarget(Target t)
        {
            t.ReleaseOverlays();
            t.Canvas = null;
            if (t.D2D != null) { t.D2D.Release(); t.D2D = null; }
            if (t.Surface != null) Marshal.ReleaseComObject(t.Surface);
            if (t.VideoTexture != IntPtr.Zero) { Marshal.Release(t.VideoTexture); t.VideoTexture = IntPtr.Zero; }
            if (t.BackBuffer != IntPtr.Zero) Marshal.Release(t.BackBuffer);
            if (t.SwapChain != null) Marshal.ReleaseComObject(t.SwapChain);
            t.Surface = null; t.BackBuffer = IntPtr.Zero; t.SwapChain = null;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Texture2DDesc { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality; public int Usage; public uint BindFlags, CpuAccessFlags, MiscFlags; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture2DFn(IntPtr self, ref Texture2DDesc desc, IntPtr initialData, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyResourceFn(IntPtr self, IntPtr destination, IntPtr source);
        CopyResourceFn copyResource;

        // 跟輸出畫面一樣大小、格式的畫布（影片解碼後畫在這裡）；建立失敗就是 0，影片直接畫在輸出畫面上
        IntPtr CreateVideoTexture(int width, int height)
        {
            try
            {
                var desc = new Texture2DDesc
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                    Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
                    BindFlags = 0x20 | 0x8,   // 可以當繪圖目標（影片畫進來）、也可以被讀取
                };
                IntPtr texture;
                if (ComVtable.Method<CreateTexture2DFn>(device, 5)(device, ref desc, IntPtr.Zero, out texture) < 0) return IntPtr.Zero;   // ID3D11Device::CreateTexture2D
                if (copyResource == null) copyResource = ComVtable.Method<CopyResourceFn>(context, 47);   // ID3D11DeviceContext::CopyResource
                return texture;
            }
            catch { return IntPtr.Zero; }
        }

        Target CreateSwapChain(IntPtr hwnd, int width, int height, bool gdi)
        {
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)width, Height = (uint)height,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1,
                BufferUsage = 0x20,   // DXGI_USAGE_RENDER_TARGET_OUTPUT
                BufferCount = 1,
                SwapEffect = 0,       // DXGI_SWAP_EFFECT_DISCARD：畫進視窗本身，外層 layered 容器才看得到
                Flags = gdi ? 4u : 0u,   // DXGI_SWAP_CHAIN_FLAG_GDI_COMPATIBLE：要用 GDI 貼疊圖
            };
            IDXGISwapChain1 swapChain;
            Check(factory.CreateSwapChainForHwnd(device, hwnd, ref desc, IntPtr.Zero, IntPtr.Zero, out swapChain), "CreateSwapChainForHwnd");
            factory.MakeWindowAssociation(hwnd, 3);   // 不要讓 DXGI 處理 Alt+Enter 全螢幕切換
            var target = new Target { Hwnd = hwnd, Width = width, Height = height, SwapChain = swapChain };
            var iidTexture = IID_ID3D11Texture2D;
            Check(swapChain.GetBuffer(0, ref iidTexture, out target.BackBuffer), "GetBuffer");
            return target;
        }

        void Render(bool newFrame)
        {
            bool all = newFrame || redrawAll;
            redraw = redrawAll = false;
            uint vw, vh;
            if (engine.GetNativeVideoSize(out vw, out vh) < 0 || vw == 0 || vh == 0) return;
            var border = new MFARGB { Alpha = 255 };   // 黑邊
            bool anyVisible = false;
            foreach (var t in targets)
            {
                // 被視窗蓋住的螢幕就不畫，省資源（還沒畫過的除外：等第一格解碼好，先畫一格上去）
                if (!t.Visible && (t.Presented || !frameReady)) continue;
                if (!all && t.OverlayCount == 0 && t.Presented) continue;   // 只是疊圖在動：沒有疊圖的畫面不用重畫
                if (t.Visible) anyVisible = true;
                MFVideoNormalizedRect src;
                D3DRect dst;
                Fit(vw, vh, t.Width, t.Height, stretch, out src, out dst);
                // 影片沒換格、而且留著的那份已經是最新的（只是疊圖在動，例如光暈、歌詞捲動）：直接拿來用
                t.FreshVideo = newFrame || !t.VideoCurrent || t.VideoTexture == IntPtr.Zero;
                int hr = t.FreshVideo ? engine.TransferVideoFrame(t.VideoTexture != IntPtr.Zero ? t.VideoTexture : t.BackBuffer, ref src, ref dst, ref border) : 0;
                t.VideoCurrent = hr >= 0 && t.VideoTexture != IntPtr.Zero;
                if (hr < 0)
                {
                    // 有新畫面卻一直複製失敗（約 2 秒）：多半是顯示卡資源失效，整個重建
                    if (newFrame && ++transferFailures > 120) { Lost(Lang.T("複製影片畫面一直失敗 0x", "Copying video frames keeps failing 0x") + hr.ToString("X8")); return; }
                    // 第一格還沒畫上去（影片可能還沒解碼好）：暫停中也要再試，最多試約 3 秒
                    if (!t.Presented && ++t.FirstFrameTries < 200) redraw = redrawAll = true;
                    continue;
                }
                transferFailures = 0;
                if (t.VideoTexture != IntPtr.Zero) copyResource(context, t.BackBuffer, t.VideoTexture);   // 乾淨的影片整張蓋上去，再貼疊圖
                if (DrawOverlay(t)) redraw = true;   // 歌詞捲動、淡入淡出中：下一次螢幕更新再畫一次
                hr = t.SwapChain.Present(0, 0);
                if (hr < 0) { Lost(Lang.T("輸出畫面失敗 0x", "Presenting the frame failed 0x") + hr.ToString("X8")); return; }
                if (hr == 0) t.Presented = true;   // DXGI_STATUS_OCCLUDED（例如鎖定中）時其實沒畫上去，不算
                lastHealthyTick = Environment.TickCount;
                if (hr == 0 && newFrame) presentedFrames++;
            }
            if (!anyVisible) lastHealthyTick = Environment.TickCount;   // 全部被蓋住時本來就不畫
        }

        // 引擎已經不能用了：停下來，請 UI 執行緒整個重建
        void Lost(string why)
        {
            stopping = true;
            ui.BeginInvoke(onLost);
        }

        // 依縮放方式算出要取影片的哪一塊（src，0～1）、畫到畫面的哪一塊（dst，像素）
        static void Fit(uint vw, uint vh, int tw, int th, Stretch mode, out MFVideoNormalizedRect src, out D3DRect dst)
        {
            src = new MFVideoNormalizedRect { Left = 0, Top = 0, Right = 1, Bottom = 1 };
            dst = new D3DRect { Left = 0, Top = 0, Right = tw, Bottom = th };
            if (mode == Stretch.Fill) return;
            double videoAspect = (double)vw / vh, targetAspect = (double)tw / th;
            if (mode == Stretch.Uniform)   // 完整顯示：縮小放在中間，旁邊留黑邊
            {
                if (videoAspect > targetAspect) { int h = (int)Math.Round(tw / videoAspect); dst.Top = (th - h) / 2; dst.Bottom = dst.Top + h; }
                else { int w = (int)Math.Round(th * videoAspect); dst.Left = (tw - w) / 2; dst.Right = dst.Left + w; }
            }
            else                           // 填滿：裁掉影片多出來的邊
            {
                if (videoAspect > targetAspect) { float keep = (float)(targetAspect / videoAspect); src.Left = (1 - keep) / 2; src.Right = src.Left + keep; }
                else { float keep = (float)(videoAspect / targetAspect); src.Top = (1 - keep) / 2; src.Bottom = src.Top + keep; }
            }
        }

        internal void OnEngineEvent(uint meEvent, uint param2)
        {
            switch (meEvent)
            {
                case 1009:  // FIRSTFRAMEREADY：第一格解碼好了，暫停中也要把它畫出來
                    frameReady = true;
                    RequestRedraw();
                    break;
                case 10:    // LOADEDMETADATA
                case 17:    // SEEKED
                    RequestRedraw();
                    break;
                case 5:     // ERROR
                    ReportError(Lang.T("無法播放這個影片（建議使用 H.264 編碼的 .mp4），錯誤碼 0x", "Can't play this video (an H.264 .mp4 is recommended), error code 0x") + param2.ToString("X8"));
                    break;
                case 1012:  // RESOURCELOST
                case 1014:  // STREAMRENDERINGERROR
                    ui.BeginInvoke(onLost);
                    break;
            }
        }

        void ReportError(string message)
        {
            ui.BeginInvoke(new Action(delegate { onError(message); }));
        }

        static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + Lang.T(" 失敗（0x", " failed (0x") + hr.ToString("X8") + Lang.T("）", ")"), hr);
        }

        void Cleanup()
        {
            try
            {
                if (engine != null) { engine.Shutdown(); Marshal.ReleaseComObject(engine); engine = null; }
                foreach (var t in targets) ReleaseTarget(t);
                targets.Clear();
                if (output != null) Marshal.ReleaseComObject(output);
                if (d2dFactory != IntPtr.Zero) { Marshal.Release(d2dFactory); d2dFactory = IntPtr.Zero; }
                if (factory != null) Marshal.ReleaseComObject(factory);
                if (manager != null) Marshal.ReleaseComObject(manager);
                if (deviceObject != null) Marshal.ReleaseComObject(deviceObject);
                if (context != IntPtr.Zero) Marshal.Release(context);
                if (device != IntPtr.Zero) Marshal.Release(device);
            }
            catch { }
            if (mfStarted) MFShutdown();
        }
    }
}
