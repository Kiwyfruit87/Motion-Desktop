using System;
using System.Runtime.InteropServices;

namespace VideoWallpaper
{
    // 用 Desktop Duplication（DXGI）抓螢幕：Windows 合成好的整張畫面本來就在顯示卡上，在顯示卡上切出要的那一塊（控制面板後面）、
    // 縮成一半，只把它讀回來。GDI 抓螢幕要等顯示卡把畫面傳回主記憶體，實測一次約 16 毫秒（120Hz 兩格以上），
    // 這樣只要一兩毫秒。設成不會被擷取的視窗（面板自己）一樣不會被抓到。
    // 一個物件只抓一個螢幕、同一時間只在一條執行緒用；抓不到（安全桌面、螢幕換了模式、旋轉的螢幕…）呼叫的人丟掉它、退回 GDI
    sealed class DesktopCapture : IDisposable
    {
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
        [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            int[] levels, uint count, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Pt point, uint flags);

        static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        static readonly Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        const int WaitTimeout = unchecked((int)0x887A0027);   // DXGI_ERROR_WAIT_TIMEOUT：螢幕從上次到現在沒有更新
        const int AccessLost = unchecked((int)0x887A0026);    // DXGI_ERROR_ACCESS_LOST：換了顯示模式、切過全螢幕，要重建

        [StructLayout(LayoutKind.Sequential)] struct Pt { public int X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OutputDesc { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; public Native.RECT Desktop; public int Attached, Rotation; public IntPtr Monitor; }
        [StructLayout(LayoutKind.Sequential)]
        struct FrameInfo { public long LastPresentTime, LastMouseUpdateTime; public uint AccumulatedFrames; public int RectsCoalesced, ProtectedContentMaskedOut, PointerX, PointerY, PointerVisible; public uint MetadataSize, PointerShapeSize; }
        [StructLayout(LayoutKind.Sequential)] struct Texture2DDesc { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality; public int Usage; public uint BindFlags, CpuAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
        [StructLayout(LayoutKind.Sequential)] struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumFn(IntPtr self, uint index, out IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OutputDescFn(IntPtr self, out OutputDesc desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DuplicateFn(IntPtr self, IntPtr device, out IntPtr duplication);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AcquireFn(IntPtr self, uint timeout, out FrameInfo info, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFrameFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void TextureDescFn(IntPtr self, out Texture2DDesc desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTextureFn(IntPtr self, ref Texture2DDesc desc, IntPtr initialData, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateViewFn(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr resource, uint sub, int type, uint flags, out Mapped mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr resource, uint sub);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyRegionFn(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref Box box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ClearFn(IntPtr self, IntPtr view, float[] color);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void MipsFn(IntPtr self, IntPtr view);

        const int Bgra = 87;   // DXGI_FORMAT_B8G8R8A8_UNORM（桌面的格式）

        IntPtr device, context, duplication;
        readonly Native.RECT screen;   // 這個螢幕在桌面上的範圍（實體像素，跟 GetWindowRect 一樣；程式是 Per-Monitor V2）
        readonly IntPtr monitor;
        AcquireFn acquire;
        ReleaseFrameFn releaseFrame;
        CreateTextureFn createTexture;
        CreateViewFn createSrv, createRtv;
        MapFn map;
        UnmapFn unmap;
        CopyRegionFn copyRegion;
        ClearFn clear;
        MipsFn mips;

        IntPtr region, regionSrv, regionRtv;   // 要的那一塊，原本大小 + 縮成一半的第二層（顯示卡自己算，每 2 × 2 平均）
        bool haveRegion;                       // region 裡是 last 那個範圍最新的畫面（螢幕沒更新、範圍沒變時重讀它就好）
        IntPtr staging;                        // 一半大小、讀得回來的那張
        int regionW, regionH;
        Native.RECT last;
        readonly float[] black = new float[4];

        DesktopCapture(Native.RECT screen, IntPtr monitor) { this.screen = screen; this.monitor = monitor; }

        // 上一次 Grab 回傳 -1 是因為 Windows 收回了（換了顯示模式、切過全螢幕）：可以馬上重建
        public bool Lost { get; private set; }

        // 抓 area（桌面座標）中心所在的那個螢幕；不行（Windows 8 以前、那個螢幕是旋轉的、顯示卡不支援、遠端桌面…）回傳 null
        public static DesktopCapture TryCreate(Native.RECT area)
        {
            int cx = (area.Left + area.Right) / 2, cy = (area.Top + area.Bottom) / 2;
            IntPtr factory = IntPtr.Zero;
            try
            {
                var iid = IID_IDXGIFactory1;
                if (CreateDXGIFactory1(ref iid, out factory) < 0) return null;
                var enumAdapters = ComVtable.Method<EnumFn>(factory, 12);   // IDXGIFactory1::EnumAdapters1
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    if (enumAdapters(factory, a, out adapter) < 0) return null;   // 都找過了
                    try
                    {
                        var enumOutputs = ComVtable.Method<EnumFn>(adapter, 7);   // IDXGIAdapter::EnumOutputs
                        for (uint o = 0; ; o++)
                        {
                            IntPtr output;
                            if (enumOutputs(adapter, o, out output) < 0) break;
                            try
                            {
                                OutputDesc desc;
                                if (ComVtable.Method<OutputDescFn>(output, 7)(output, out desc) < 0) continue;   // IDXGIOutput::GetDesc
                                var d = desc.Desktop;
                                if (cx < d.Left || cx >= d.Right || cy < d.Top || cy >= d.Bottom) continue;
                                if (desc.Rotation > 1) return null;   // 旋轉的螢幕：畫面是沒轉過的方向，座標對不上（用 GDI）
                                var c = new DesktopCapture(d, desc.Monitor);
                                bool ok = false;
                                try { ok = c.Init(adapter, output); }
                                finally { if (!ok) c.Dispose(); }
                                return ok ? c : null;
                            }
                            finally { Marshal.Release(output); }
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            catch { return null; }
            finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
        }

        bool Init(IntPtr adapter, IntPtr output)
        {
            int level;
            var levels = new[] { 0xb000, 0xa100, 0xa000 };
            if (D3D11CreateDevice(adapter, 0 /* 指定了顯示卡就是 UNKNOWN */, IntPtr.Zero, 0x20 /* BGRA */, levels, (uint)levels.Length, 7, out device, out level, out context) < 0)
            { device = context = IntPtr.Zero; return false; }
            IntPtr output1;
            var iid = IID_IDXGIOutput1;
            if (Marshal.QueryInterface(output, ref iid, out output1) < 0) return false;
            try
            {
                if (ComVtable.Method<DuplicateFn>(output1, 22)(output1, device, out duplication) < 0) { duplication = IntPtr.Zero; return false; }   // IDXGIOutput1::DuplicateOutput
            }
            finally { Marshal.Release(output1); }
            acquire = ComVtable.Method<AcquireFn>(duplication, 8);             // IDXGIOutputDuplication::AcquireNextFrame
            releaseFrame = ComVtable.Method<ReleaseFrameFn>(duplication, 14);  // ::ReleaseFrame
            createTexture = ComVtable.Method<CreateTextureFn>(device, 5);      // ID3D11Device::CreateTexture2D
            createSrv = ComVtable.Method<CreateViewFn>(device, 7);             // ::CreateShaderResourceView
            createRtv = ComVtable.Method<CreateViewFn>(device, 9);             // ::CreateRenderTargetView
            map = ComVtable.Method<MapFn>(context, 14);                        // ID3D11DeviceContext::Map
            unmap = ComVtable.Method<UnmapFn>(context, 15);                    // ::Unmap
            copyRegion = ComVtable.Method<CopyRegionFn>(context, 46);          // ::CopySubresourceRegion
            clear = ComVtable.Method<ClearFn>(context, 50);                    // ::ClearRenderTargetView
            mips = ComVtable.Method<MipsFn>(context, 54);                      // ::GenerateMips
            return true;
        }

        // area 的中心還在這個螢幕上（面板搬到別的螢幕就要換一個）
        public bool Covers(Native.RECT area)
        {
            int cx = (area.Left + area.Right) / 2, cy = (area.Top + area.Bottom) / 2;
            return cx >= screen.Left && cx < screen.Right && cy >= screen.Top && cy < screen.Bottom;
        }

        // 用來畫玻璃（GlassLayer 跟抓畫面用同一個裝置，抓下來的那塊不用換裝置）
        public IntPtr Device { get { return device; } }
        public IntPtr Context { get { return context; } }
        // 抓下來的那塊：area 的大小，第二層是一半大小（Acquire 回傳 1 之後才有東西）
        public IntPtr Region { get { return regionSrv; } }

        // 把螢幕上 area 這塊切下來放進 Region（再用 Read 讀回一半大小的）。force：沒變也要（剛打開面板）。
        // wait：螢幕還沒有新的一張時最多等幾毫秒（一更新就抓，不用等下一次被叫；範圍變了、force 的時候不等，手上那張就能用）。
        // 回傳 1：Region 是最新的畫面；0：螢幕從上次到現在完全沒更新、範圍也沒變；
        // -2：這次抓不了、物件還能用（剛建好第一張還沒來、範圍有一部分在別的螢幕上：這次用 GDI）；
        // -1：這個物件不能用了（螢幕換了模式、切到安全桌面…；Lost 說是不是 Windows 收回的）
        public int Acquire(Native.RECT area, bool force, int wait)
        {
            int fullW = area.Right - area.Left, fullH = area.Bottom - area.Top, w = fullW / 2, h = fullH / 2;
            if (w < 1 || h < 1 || Spans(area)) return -2;
            bool moved = !haveRegion || area.Left != last.Left || area.Top != last.Top || area.Right != last.Right || area.Bottom != last.Bottom;
            FrameInfo info;
            IntPtr resource;
            int hr = acquire(duplication, 0, out info, out resource);
            if (hr == WaitTimeout && !moved && !force && wait > 0) hr = acquire(duplication, (uint)wait, out info, out resource);
            bool updated = false;
            if (hr != WaitTimeout)
            {
                if (hr < 0) { Lost = hr == AccessLost; return -1; }
                try
                {
                    // 畫面真的有更新才用（LastPresentTime 是 0：只有滑鼠動了；剛建好時有些電腦的第一張也是 0，內容還是黑的）。
                    // 拿著這張的時候直接切出要的那塊（不複製整個螢幕：每張省一次整個螢幕大小的複製，4K 時差很多）
                    if (info.LastPresentTime != 0)
                    {
                        IntPtr texture;
                        var iid = IID_ID3D11Texture2D;
                        if (Marshal.QueryInterface(resource, ref iid, out texture) < 0) return -1;
                        try
                        {
                            // 格式、大小跟這個螢幕對不上（理論上不會：Desktop Duplication 一律給 8 位元 BGRA）：座標不能用，退回 GDI
                            Texture2DDesc d;
                            ComVtable.Method<TextureDescFn>(texture, 10)(texture, out d);   // ID3D11Texture2D::GetDesc
                            if (d.Format != Bgra || d.Width != screen.Right - screen.Left || d.Height != screen.Bottom - screen.Top) return -1;
                            if ((fullW != regionW || fullH != regionH) && !CreateRegion(fullW, fullH)) return -1;
                            // 範圍超出這個螢幕的部分（面板貼著螢幕邊緣、外面沒有別的螢幕）：先填黑，跟 GDI 抓螢幕外面一樣
                            int x0 = Math.Max(area.Left, screen.Left), y0 = Math.Max(area.Top, screen.Top);
                            int x1 = Math.Min(area.Right, screen.Right), y1 = Math.Min(area.Bottom, screen.Bottom);
                            if (x0 != area.Left || y0 != area.Top || x1 != area.Right || y1 != area.Bottom) clear(context, regionRtv, black);
                            if (x1 > x0 && y1 > y0)
                            {
                                var box = new Box { Left = (uint)(x0 - screen.Left), Top = (uint)(y0 - screen.Top), Right = (uint)(x1 - screen.Left), Bottom = (uint)(y1 - screen.Top), Back = 1 };
                                copyRegion(context, region, 0, (uint)(x0 - area.Left), (uint)(y0 - area.Top), 0, texture, 0, ref box);
                            }
                        }
                        finally { Marshal.Release(texture); }
                        last = area;
                        haveRegion = updated = true;
                    }
                }
                finally
                {
                    Marshal.Release(resource);
                    releaseFrame(duplication);   // 馬上還回去：Windows 才能繼續更新下一張
                }
            }
            if (!updated)
            {
                if (moved) return -2;      // 範圍變了（或剛建好）、這時又沒有新的一張：手上沒有新範圍的畫面，這次用 GDI
                if (!force) return 0;      // 螢幕沒更新、範圍也沒變
            }
            // 縮成一半（顯示卡自己算第二層；force 又沒有新的一張時，region 裡是上次切下來的那塊）
            if (updated) mips(context, regionSrv);
            return 1;
        }

        // 把 Region 縮成一半的那層讀回 dst（(寬 / 2) × (高 / 2) × 4，BGRA，跟 GDI 抓的一樣）；讀不了回傳 false（這個物件不能用了）
        public bool Read(byte[] dst)
        {
            int w = regionW / 2, h = regionH / 2;
            if (!haveRegion || w < 1 || h < 1) return false;
            var half = new Box { Right = (uint)w, Bottom = (uint)h, Back = 1 };
            copyRegion(context, staging, 0, 0, 0, 0, region, 1, ref half);
            Mapped m;
            if (map(context, staging, 0, 1 /* READ */, 0, out m) < 0) return false;
            try
            {
                for (int y = 0; y < h; y++) Marshal.Copy(m.Data + (int)(y * m.RowPitch), dst, y * w * 4, w * 4);
            }
            finally { unmap(context, staging, 0); }
            return true;
        }

        // 範圍超出這個螢幕、超出去的地方是另一個螢幕（面板貼著兩個螢幕的交界）：這裡只抓得到一個螢幕，那部分會變黑，這次用 GDI。
        // 超出去的地方沒有螢幕（貼著最外側的邊）：跟 GDI 一樣是黑的，照樣用
        bool Spans(Native.RECT area)
        {
            if (area.Left >= screen.Left && area.Top >= screen.Top && area.Right <= screen.Right && area.Bottom <= screen.Bottom) return false;
            int mx = (area.Left + area.Right) / 2, my = (area.Top + area.Bottom) / 2;
            int[] xs = { area.Left, mx, area.Right - 1 }, ys = { area.Top, my, area.Bottom - 1 };
            foreach (int x in xs)
                foreach (int y in ys)
                {
                    if (x >= screen.Left && x < screen.Right && y >= screen.Top && y < screen.Bottom) continue;
                    IntPtr m = MonitorFromPoint(new Pt { X = x, Y = y }, 0 /* 不在任何螢幕上回傳 0 */);
                    if (m != IntPtr.Zero && m != monitor) return true;
                }
            return false;
        }

        bool CreateRegion(int fullW, int fullH)
        {
            FreeRegion();
            var rd = new Texture2DDesc { Width = (uint)fullW, Height = (uint)fullH, MipLevels = 2, ArraySize = 1, Format = Bgra, SampleCount = 1,
                BindFlags = 0x8 | 0x20 /* 讀取 + 繪圖目標：GenerateMips 要 */, MiscFlags = 0x1 /* GENERATE_MIPS */ };
            if (createTexture(device, ref rd, IntPtr.Zero, out region) < 0) { region = IntPtr.Zero; return false; }
            if (createSrv(device, region, IntPtr.Zero, out regionSrv) < 0) { regionSrv = IntPtr.Zero; return false; }
            if (createRtv(device, region, IntPtr.Zero, out regionRtv) < 0) { regionRtv = IntPtr.Zero; return false; }
            var sd = new Texture2DDesc { Width = (uint)(fullW / 2), Height = (uint)(fullH / 2), MipLevels = 1, ArraySize = 1, Format = Bgra, SampleCount = 1,
                Usage = 3 /* STAGING */, CpuAccessFlags = 0x20000 /* READ */ };
            if (createTexture(device, ref sd, IntPtr.Zero, out staging) < 0) { staging = IntPtr.Zero; return false; }
            regionW = fullW; regionH = fullH;
            return true;
        }

        void FreeRegion()
        {
            Release(ref regionRtv); Release(ref regionSrv); Release(ref region); Release(ref staging);
            regionW = regionH = 0;
            haveRegion = false;
        }

        static void Release(ref IntPtr com) { if (com != IntPtr.Zero) { Marshal.Release(com); com = IntPtr.Zero; } }

        public void Dispose()
        {
            FreeRegion();
            Release(ref duplication);
            Release(ref context);
            Release(ref device);
        }
    }
}
