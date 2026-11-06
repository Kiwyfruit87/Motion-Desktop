using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoWallpaper
{
    // 控制面板的第一層玻璃，直接用顯示卡畫（跟抓畫面同一個 Direct3D 裝置、同一條背景執行緒）：
    // Desktop Duplication 一拿到新的一張，馬上在顯示卡上縮小、模糊、畫玻璃、交給 Windows 合成，下一格就看得到。
    // 用 WPF 畫的話要先把畫面讀回主記憶體、等 WPF 下一次交畫面，再等 Windows 合成，玻璃比後面慢兩格左右；這樣只慢一格。
    // 玻璃畫在面板正下方的另一個視窗（DirectComposition，不經過 WPF）：滑鼠點得穿、不會被螢幕擷取（自己抓畫面時才不會抓到它）。
    // 著色器跟 WPF 那份（GlassBackdrop.Source）是同一份，只換掉取樣和常數的寫法
    sealed class GlassLayer : IDisposable
    {
        // ---------- 著色器：從 WPF 的那份轉過來，加上縮小、模糊（跟 GlassBackdrop 在主記憶體算的一樣：2 × 2 平均、方框模糊）----------
        const string Extra = @"
Texture2D Src : register(t0);
cbuffer PassInfo : register(b1) { float4 P; };   // 半徑、方向（1 左右、0 上下）、最大的 x、y

float4 VS(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
}

// 再縮一半：每 2 × 2 平均（來源是抓下來那塊的第二層，已經是一半大小）
float4 Down(float4 pos : SV_Position) : SV_Target
{
    int2 c = int2(pos.xy) * 2, m = int2(P.zw);
    float4 s = 0.0;
    [unroll] for (int j = 0; j < 2; j++)
        [unroll] for (int i = 0; i < 2; i++)
            s += Src.Load(int3(min(c + int2(i, j), m), 1));
    return s * 0.25;
}

// 方框模糊的一個方向（超出邊緣用邊緣的顏色）
float4 Box(float4 pos : SV_Position) : SV_Target
{
    int2 c = int2(pos.xy), m = int2(P.zw), d = P.y > 0.5 ? int2(1, 0) : int2(0, 1);
    int r = (int)P.x;
    float4 s = 0.0;
    [loop] for (int i = -r; i <= r; i++) s += Src.Load(int3(clamp(c + d * i, int2(0, 0), m), 0));
    return s / (2.0 * r + 1.0);
}
";

        // WPF 的取樣器、常數暫存器換成 Direct3D 11 的寫法（常數照原本的暫存器順序排進一個 cbuffer，最後多一個 Target）
        static string Port(string src)
        {
            src = src.Replace("sampler2D Input : register(s0);", "Texture2D InputT : register(t0);")
                     .Replace("sampler2D BlurA : register(s1);", "Texture2D BlurAT : register(t1);")
                     .Replace("sampler2D BlurB : register(s2);", "Texture2D BlurBT : register(t2);\nSamplerState Lin : register(s0);\ncbuffer Panel : register(b0)\n{");
            src = Regex.Replace(src, @"\s*:\s*register\(c\d+\);", ";");
            int mask = src.IndexOf("float4 View;");
            int end = src.IndexOf('\n', mask) + 1;
            src = src.Substring(0, end) + "float4 Target;   // 輸出的大小（像素）、清楚的那張用第幾層（1：一半大小，跟 WPF 一樣）\n};\n" +
                "float4 LI(float4 c) { return InputT.SampleLevel(Lin, c.xy, Target.z); }\n" +
                "float4 LA(float4 c) { return BlurAT.SampleLevel(Lin, c.xy, 0.0); }\n" +
                "float4 LB(float4 c) { return BlurBT.SampleLevel(Lin, c.xy, 0.0); }\n" + src.Substring(end);
            src = src.Replace("tex2Dlod(Input, ", "LI(").Replace("tex2Dlod(BlurA, ", "LA(").Replace("tex2Dlod(BlurB, ", "LB(");
            src = src.Replace("float4 main(float2 uv : TEXCOORD) : COLOR", "float4 main(float2 uv)");
            return src + "\nfloat4 PS(float4 pos : SV_Position) : SV_Target { return main(pos.xy / Target.xy); }\n";
        }

        static Task<byte[][]> compiled;

        // 跟 WPF 的著色器一起在背景編譯
        public static void Prepare(string wpfSource)
        {
            if (compiled != null) return;
            compiled = Task.Run(() =>
            {
                var extra = Encoding.UTF8.GetBytes(Extra);
                return new[]
                {
                    LiquidGlass.Compile(extra, "VS", "vs_4_0"),
                    LiquidGlass.Compile(extra, "Down", "ps_4_0"),
                    LiquidGlass.Compile(extra, "Box", "ps_4_0"),
                    LiquidGlass.Compile(Encoding.UTF8.GetBytes(Port(wpfSource)), "PS", "ps_4_0"),
                };
            });
        }

        // 還在編（或沒開始編）是 null；編好了 true；編譯失敗 false
        public static bool? Compiled
        {
            get
            {
                if (compiled == null || !compiled.IsCompleted) return null;
                return compiled.Status == TaskStatus.RanToCompletion;
            }
        }

        // 編好了（編譯失敗、還沒編好都是 null）
        static byte[][] Code
        {
            get
            {
                if (compiled == null || !compiled.IsCompleted || compiled.Status != TaskStatus.RanToCompletion) return null;
                return compiled.Result;
            }
        }

        public const int Registers = 94;   // WPF 那份的 93 個 + Target

        // ---------- 放玻璃的視窗（畫面執行緒建、一直留著）----------
        // 在面板正下方、一樣大；點得穿（WS_EX_LAYERED + WS_EX_TRANSPARENT）、不搶焦點、不在工作切換裡、沒有自己的畫面（內容全由 DirectComposition 給）
        public sealed class Window : NativeWindow
        {
            public Window()
            {
                CreateHandle(new CreateParams
                {
                    Caption = "",
                    Style = unchecked((int)0x80000000),   // WS_POPUP
                    ExStyle = 0x8 | 0x20 | 0x80 | 0x80000 | 0x200000 | 0x08000000,   // TOPMOST、TRANSPARENT、TOOLWINDOW、LAYERED、NOREDIRECTIONBITMAP、NOACTIVATE
                });
                Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
                // 一定要不會被擷取：不然面板抓後面的畫面時會抓到自己的玻璃，一層套一層
                Usable = GlassBackdrop.ExcludeFromCapture(Handle);
            }

            public readonly bool Usable;

            // 放到 panel 正下方、跟它一樣大，顯示出來
            public void ShowBelow(IntPtr panel, Native.RECT r)
            {
                Native.SetWindowPos(Handle, panel, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, Native.SWP_NOACTIVATE | 0x40 /* SHOWWINDOW */);
            }

            // 再放到 panel 正下方一次（位置、大小不動）
            public void Restack(IntPtr panel)
            {
                Native.SetWindowPos(Handle, panel, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }

            public void Hide() { Native.ShowWindow(Handle, 0); }
        }

        // ---------- Direct3D / DXGI / DirectComposition（照 SDK 標頭檔的順序）----------
        [DllImport("dcomp.dll")] static extern int DCompositionCreateDevice(IntPtr dxgiDevice, ref Guid iid, out IntPtr device);

        static readonly Guid IID_IDXGIDevice1 = new Guid("77db970f-6276-48ba-ba28-070143b4392c");
        static readonly Guid IID_IDXGIFactory2 = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        static readonly Guid IID_IDCompositionDevice = new Guid("c37ea93a-e7aa-450d-b16f-9746cb0407f3");
        static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [StructLayout(LayoutKind.Sequential)] struct BufferDesc { public uint ByteWidth; public int Usage; public uint BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }
        [StructLayout(LayoutKind.Sequential)] struct Texture2DDesc { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality; public int Usage; public uint BindFlags, CpuAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] struct SamplerDesc { public int Filter, AddressU, AddressV, AddressW; public float MipLodBias; public uint MaxAnisotropy; public int ComparisonFunc; public float Border0, Border1, Border2, Border3, MinLod, MaxLod; }
        [StructLayout(LayoutKind.Sequential)] struct Viewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }
        [StructLayout(LayoutKind.Sequential)] struct SwapDesc { public uint Width, Height; public int Format, Stereo; public uint SampleCount, SampleQuality, Usage, BufferCount; public int Scaling, SwapEffect, AlphaMode; public uint Flags; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBufferFn(IntPtr self, ref BufferDesc desc, IntPtr initialData, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture2DFn(IntPtr self, ref Texture2DDesc desc, IntPtr initialData, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateViewFn(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateShaderFn(IntPtr self, byte[] code, IntPtr length, IntPtr linkage, out IntPtr shader);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateSamplerFn(IntPtr self, ref SamplerDesc desc, out IntPtr sampler);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetResourcesFn(IntPtr self, uint start, uint count, IntPtr[] items);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetShaderFn(IntPtr self, IntPtr shader, IntPtr instances, uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DrawFn(IntPtr self, uint count, uint start);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetPointerFn(IntPtr self, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetTopologyFn(IntPtr self, int topology);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetTargetsFn(IntPtr self, uint count, IntPtr[] views, IntPtr depth);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetViewportsFn(IntPtr self, uint count, ref Viewport viewport);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UpdateFn(IntPtr self, IntPtr dst, uint sub, IntPtr box, float[] data, uint rowPitch, uint depthPitch);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ClearStateFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetFn(IntPtr self, out IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetParentFn(IntPtr self, ref Guid iid, out IntPtr parent);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetLatencyFn(IntPtr self, uint frames);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateSwapChainFn(IntPtr self, IntPtr device, ref SwapDesc desc, IntPtr restrictTo, out IntPtr swapChain);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBufferFn(IntPtr self, uint index, ref Guid iid, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResizeFn(IntPtr self, uint count, uint width, uint height, int format, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PresentFn(IntPtr self, uint sync, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int TargetFn(IntPtr self, IntPtr hwnd, int topmost, out IntPtr target);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetFn(IntPtr self, IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CommitFn(IntPtr self);

        const int Half16 = 10;   // DXGI_FORMAT_R16G16B16A16_FLOAT：模糊好幾次，8 位元的話暗的漸層會一階一階的
        const int Bgra = 87;

        readonly IntPtr device, context, window;
        CreateBufferFn createBuffer;
        CreateTexture2DFn createTexture;
        CreateViewFn createSrv, createRtv;
        SetResourcesFn psSetResources, psSetSamplers, psSetConstants;
        SetShaderFn vsSetShader, psSetShader;
        DrawFn draw;
        SetPointerFn iaSetLayout;
        SetTopologyFn iaSetTopology;
        SetTargetsFn omSetTargets;
        SetViewportsFn rsSetViewports;
        UpdateFn update;
        ClearStateFn clearState;
        PresentFn present;
        ResizeFn resize;

        IntPtr vs, downPs, boxPs, glassPs, sampler, panelBuffer, passBuffer;
        IntPtr dcomp, target, visual, swapChain, backRtv;
        int swapW, swapH;
        Native.RECT placed;   // 視窗上次放的位置
        readonly IntPtr[] one = new IntPtr[1], three = new IntPtr[3];
        readonly float[] pass = new float[4];
        readonly float[] constants = new float[Registers * 4];

        // 四分之一大小的模糊圖：縮小的、暫存、輕模糊、重模糊
        IntPtr[] blurTex = new IntPtr[4], blurRtv = new IntPtr[4], blurSrv = new IntPtr[4];
        int blurW, blurH;
        bool blurred;   // 模糊圖裡有東西了

        GlassLayer(IntPtr device, IntPtr context, IntPtr window)
        {
            this.device = device; this.context = context; this.window = window;
            Marshal.AddRef(device); Marshal.AddRef(context);
        }

        // 用抓畫面的那個裝置建；著色器還沒編好、顯示卡或 Windows 不支援就回傳 null（呼叫的人照舊用 WPF 畫）
        public static GlassLayer Create(IntPtr device, IntPtr context, IntPtr window)
        {
            if (Code == null || window == IntPtr.Zero) return null;
            var g = new GlassLayer(device, context, window);
            try { if (g.Init()) return g; }
            catch { }
            g.Dispose();
            return null;
        }

        bool Init()
        {
            createBuffer = ComVtable.Method<CreateBufferFn>(device, 3);            // ID3D11Device::CreateBuffer
            createTexture = ComVtable.Method<CreateTexture2DFn>(device, 5);        // ::CreateTexture2D
            createSrv = ComVtable.Method<CreateViewFn>(device, 7);                 // ::CreateShaderResourceView
            createRtv = ComVtable.Method<CreateViewFn>(device, 9);                 // ::CreateRenderTargetView
            psSetResources = ComVtable.Method<SetResourcesFn>(context, 8);         // ID3D11DeviceContext::PSSetShaderResources
            psSetShader = ComVtable.Method<SetShaderFn>(context, 9);               // ::PSSetShader
            psSetSamplers = ComVtable.Method<SetResourcesFn>(context, 10);         // ::PSSetSamplers
            vsSetShader = ComVtable.Method<SetShaderFn>(context, 11);              // ::VSSetShader
            draw = ComVtable.Method<DrawFn>(context, 13);                          // ::Draw
            psSetConstants = ComVtable.Method<SetResourcesFn>(context, 16);        // ::PSSetConstantBuffers
            iaSetLayout = ComVtable.Method<SetPointerFn>(context, 17);             // ::IASetInputLayout
            iaSetTopology = ComVtable.Method<SetTopologyFn>(context, 24);          // ::IASetPrimitiveTopology
            omSetTargets = ComVtable.Method<SetTargetsFn>(context, 33);            // ::OMSetRenderTargets
            rsSetViewports = ComVtable.Method<SetViewportsFn>(context, 44);        // ::RSSetViewports
            update = ComVtable.Method<UpdateFn>(context, 48);                      // ::UpdateSubresource
            clearState = ComVtable.Method<ClearStateFn>(context, 110);             // ::ClearState

            var code = Code;
            var createVs = ComVtable.Method<CreateShaderFn>(device, 12);   // ::CreateVertexShader
            var createPs = ComVtable.Method<CreateShaderFn>(device, 15);   // ::CreatePixelShader
            if (createVs(device, code[0], (IntPtr)code[0].Length, IntPtr.Zero, out vs) < 0) return false;
            if (createPs(device, code[1], (IntPtr)code[1].Length, IntPtr.Zero, out downPs) < 0) return false;
            if (createPs(device, code[2], (IntPtr)code[2].Length, IntPtr.Zero, out boxPs) < 0) return false;
            if (createPs(device, code[3], (IntPtr)code[3].Length, IntPtr.Zero, out glassPs) < 0) return false;
            var sd = new SamplerDesc { Filter = 0x15 /* 線性 */, AddressU = 3, AddressV = 3, AddressW = 3 /* 超出範圍用邊緣的顏色 */, ComparisonFunc = 1, MaxLod = float.MaxValue };
            if (ComVtable.Method<CreateSamplerFn>(device, 23)(device, ref sd, out sampler) < 0) return false;   // ::CreateSamplerState
            var bd = new BufferDesc { ByteWidth = Registers * 16, BindFlags = 0x4 /* 常數 */ };
            if (createBuffer(device, ref bd, IntPtr.Zero, out panelBuffer) < 0) return false;
            bd.ByteWidth = 16;
            if (createBuffer(device, ref bd, IntPtr.Zero, out passBuffer) < 0) return false;

            // DirectComposition：視窗 → 一個 visual → 交換鏈（大小等第一次畫的時候才知道）
            IntPtr dxgiDevice, adapter = IntPtr.Zero, factory = IntPtr.Zero;
            var iid = IID_IDXGIDevice1;
            if (Marshal.QueryInterface(device, ref iid, out dxgiDevice) < 0) return false;
            try
            {
                ComVtable.Method<SetLatencyFn>(dxgiDevice, 12)(dxgiDevice, 1);   // IDXGIDevice1::SetMaximumFrameLatency：顯示卡最多排一格，不多等
                if (ComVtable.Method<GetFn>(dxgiDevice, 7)(dxgiDevice, out adapter) < 0) { adapter = IntPtr.Zero; return false; }   // IDXGIDevice::GetAdapter
                iid = IID_IDXGIFactory2;
                if (ComVtable.Method<GetParentFn>(adapter, 6)(adapter, ref iid, out factory) < 0) { factory = IntPtr.Zero; return false; }   // IDXGIObject::GetParent
                // 三張：Windows 還拿著一張在合成時，畫下一張不用等（兩張的話背景執行緒可能被卡到下一次合成）
                var desc = new SwapDesc { Width = 16, Height = 16, Format = Bgra, SampleCount = 1, Usage = 0x20 /* 繪圖目標 */, BufferCount = 3,
                    Scaling = 0 /* STRETCH（合成用的只能這個）*/, SwapEffect = 3 /* FLIP_SEQUENTIAL */, AlphaMode = 1 /* PREMULTIPLIED：跟 WPF 的著色器輸出一樣 */ };
                if (ComVtable.Method<CreateSwapChainFn>(factory, 24)(factory, device, ref desc, IntPtr.Zero, out swapChain) < 0) { swapChain = IntPtr.Zero; return false; }   // IDXGIFactory2::CreateSwapChainForComposition
                swapW = swapH = 16;
                present = ComVtable.Method<PresentFn>(swapChain, 8);   // IDXGISwapChain::Present
                resize = ComVtable.Method<ResizeFn>(swapChain, 13);    // ::ResizeBuffers
                iid = IID_IDCompositionDevice;
                if (DCompositionCreateDevice(dxgiDevice, ref iid, out dcomp) < 0) { dcomp = IntPtr.Zero; return false; }
            }
            finally
            {
                if (factory != IntPtr.Zero) Marshal.Release(factory);
                if (adapter != IntPtr.Zero) Marshal.Release(adapter);
                Marshal.Release(dxgiDevice);
            }
            if (ComVtable.Method<TargetFn>(dcomp, 6)(dcomp, window, 1, out target) < 0) { target = IntPtr.Zero; return false; }   // IDCompositionDevice::CreateTargetForHwnd
            if (ComVtable.Method<GetFn>(dcomp, 7)(dcomp, out visual) < 0) { visual = IntPtr.Zero; return false; }               // ::CreateVisual
            if (ComVtable.Method<SetFn>(visual, 15)(visual, swapChain) < 0) return false;                                       // IDCompositionVisual::SetContent
            if (ComVtable.Method<SetFn>(target, 3)(target, visual) < 0) return false;                                           // IDCompositionTarget::SetRoot
            if (ComVtable.Method<CommitFn>(dcomp, 3)(dcomp) < 0) return false;                                                  // IDCompositionDevice::Commit
            return BackBuffer();
        }

        bool BackBuffer()
        {
            IntPtr buffer;
            var iid = IID_ID3D11Texture2D;
            if (ComVtable.Method<GetBufferFn>(swapChain, 9)(swapChain, 0, ref iid, out buffer) < 0) return false;   // IDXGISwapChain::GetBuffer
            try
            {
                if (createRtv(device, buffer, IntPtr.Zero, out backRtv) >= 0) return true;
                backRtv = IntPtr.Zero;
                return false;
            }
            finally { Marshal.Release(buffer); }
        }

        // 畫一次、交給 Windows 合成。region：抓下來的那塊（area 的大小，第二層是一半大小）；
        // data：WPF 那份的常數（GlassEffect 的 93 個暫存器）；fresh：region 換了新的畫面（要重新模糊）。
        // 畫不了回傳 false（呼叫的人丟掉這個物件、退回 WPF）
        public bool Render(IntPtr region, Native.RECT area, float[] data, bool fresh)
        {
            int fullW = area.Right - area.Left, fullH = area.Bottom - area.Top, w = fullW / 2, h = fullH / 2;
            if (w < 1 || h < 1) return true;
            if (fullW != swapW || fullH != swapH)
            {
                LiquidGlass.Free(ref backRtv);
                if (resize(swapChain, 0, (uint)fullW, (uint)fullH, 0, 0) < 0 || !BackBuffer()) return false;
                swapW = fullW; swapH = fullH;
            }
            if (area.Left != placed.Left || area.Top != placed.Top || area.Right != placed.Right || area.Bottom != placed.Bottom)
            {
                // 面板的大小、位置變了：視窗跟著（不等畫面執行緒）
                Native.SetWindowPos(window, IntPtr.Zero, area.Left, area.Top, fullW, fullH, Native.SWP_NOACTIVATE | 0x4 /* NOZORDER */ | 0x4000 /* ASYNCWINDOWPOS */);
                placed = area;
            }
            int qw = Math.Max(1, (w + 1) / 2), qh = Math.Max(1, (h + 1) / 2);
            if (qw != blurW || qh != blurH) { if (!CreateBlur(qw, qh)) return false; fresh = true; }
            if (!fresh && !blurred) return true;

            clearState(context);
            iaSetLayout(context, IntPtr.Zero);
            iaSetTopology(context, 4 /* 三角形清單 */);
            vsSetShader(context, vs, IntPtr.Zero, 0);
            one[0] = sampler; psSetSamplers(context, 0, 1, one);
            if (fresh)
            {
                // 縮成四分之一 → 輕模糊（半徑 1 兩次）→ 重模糊（再半徑 3 三次）
                psSetShader(context, downPs, IntPtr.Zero, 0);
                Pass(0, 0, w - 1, h - 1);
                SetTarget(blurRtv[0], qw, qh);
                one[0] = region; psSetResources(context, 0, 1, one);
                draw(context, 3, 0);
                psSetShader(context, boxPs, IntPtr.Zero, 0);
                BoxPass(0, 1, 1, 1); BoxPass(1, 2, 1, 0);
                BoxPass(2, 1, 1, 1); BoxPass(1, 2, 1, 0);
                BoxPass(2, 1, 3, 1); BoxPass(1, 3, 3, 0);
                BoxPass(3, 1, 3, 1); BoxPass(1, 3, 3, 0);
                BoxPass(3, 1, 3, 1); BoxPass(1, 3, 3, 0);
                one[0] = IntPtr.Zero; psSetResources(context, 0, 1, one);
                blurred = true;
            }
            Array.Copy(data, constants, Math.Min(data.Length, (Registers - 1) * 4));
            int t = (Registers - 1) * 4, v = (Registers - 2) * 4, size = 88 * 4;
            // 範圍一律是整個面板（WPF 那份在 layer 模式時只畫第二層附近）
            constants[v] = 0; constants[v + 1] = 0; constants[v + 2] = data[size]; constants[v + 3] = data[size + 1];
            constants[t] = fullW; constants[t + 1] = fullH; constants[t + 2] = 1; constants[t + 3] = 0;
            update(context, panelBuffer, 0, IntPtr.Zero, constants, 0, 0);
            one[0] = panelBuffer; psSetConstants(context, 0, 1, one);
            SetTarget(backRtv, fullW, fullH);
            psSetShader(context, glassPs, IntPtr.Zero, 0);
            three[0] = region; three[1] = blurSrv[2]; three[2] = blurSrv[3];
            psSetResources(context, 0, 3, three);
            draw(context, 3, 0);
            clearState(context);
            return present(swapChain, 0, 0) >= 0;   // 不等垂直同步，Windows 下一次合成就用最新的這張
        }



        void Pass(float a, float b, float c, float d)
        {
            pass[0] = a; pass[1] = b; pass[2] = c; pass[3] = d;
            update(context, passBuffer, 0, IntPtr.Zero, pass, 0, 0);
            one[0] = passBuffer; psSetConstants(context, 1, 1, one);
        }

        void SetTarget(IntPtr rtv, int w, int h)
        {
            one[0] = rtv;
            omSetTargets(context, 1, one, IntPtr.Zero);
            var vp = new Viewport { Width = w, Height = h, MaxDepth = 1 };
            rsSetViewports(context, 1, ref vp);
        }

        // 方框模糊的一個方向：from → to（先拿掉上一次的來源，它可能就是這次的目標）
        void BoxPass(int from, int to, int radius, int horizontal)
        {
            one[0] = IntPtr.Zero; psSetResources(context, 0, 1, one);
            SetTarget(blurRtv[to], blurW, blurH);
            Pass(radius, horizontal, blurW - 1, blurH - 1);
            one[0] = blurSrv[from]; psSetResources(context, 0, 1, one);
            draw(context, 3, 0);
        }

        bool CreateBlur(int w, int h)
        {
            FreeBlur();
            for (int i = 0; i < blurTex.Length; i++)
            {
                var desc = new Texture2DDesc { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Half16, SampleCount = 1, BindFlags = 0x20 | 0x8 };
                if (createTexture(device, ref desc, IntPtr.Zero, out blurTex[i]) < 0) { blurTex[i] = IntPtr.Zero; FreeBlur(); return false; }
                if (createRtv(device, blurTex[i], IntPtr.Zero, out blurRtv[i]) < 0) { blurRtv[i] = IntPtr.Zero; FreeBlur(); return false; }
                if (createSrv(device, blurTex[i], IntPtr.Zero, out blurSrv[i]) < 0) { blurSrv[i] = IntPtr.Zero; FreeBlur(); return false; }
            }
            blurW = w; blurH = h;
            return true;
        }

        void FreeBlur()
        {
            for (int i = 0; i < blurTex.Length; i++) { LiquidGlass.Free(ref blurSrv[i]); LiquidGlass.Free(ref blurRtv[i]); LiquidGlass.Free(ref blurTex[i]); }
            blurW = blurH = 0;
            blurred = false;
        }

        bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            FreeBlur();
            LiquidGlass.Free(ref backRtv);
            LiquidGlass.Free(ref visual);
            LiquidGlass.Free(ref target);   // 放掉了這個視窗才能再建新的（換了裝置時）
            LiquidGlass.Free(ref swapChain);
            if (dcomp != IntPtr.Zero) { ComVtable.Method<CommitFn>(dcomp, 3)(dcomp); LiquidGlass.Free(ref dcomp); }
            LiquidGlass.Free(ref panelBuffer); LiquidGlass.Free(ref passBuffer); LiquidGlass.Free(ref sampler);
            LiquidGlass.Free(ref glassPs); LiquidGlass.Free(ref boxPs); LiquidGlass.Free(ref downPs); LiquidGlass.Free(ref vs);
            var c = context; var d = device;
            Marshal.Release(c); Marshal.Release(d);
        }
    }
}
