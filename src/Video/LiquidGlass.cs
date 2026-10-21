using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VideoWallpaper
{
    // 液態玻璃（Apple 的 Liquid Glass）：用 Direct3D 11 的像素著色器直接畫在輸出畫面上，算法跟網頁原型同一套——
    //   邊緣：往外取樣的折射（像透鏡的邊，把玻璃外面的東西壓進來；越靠邊壓得越緊、越清楚，不會折返）＋一點色散；
    //   中間：整塊稍微放大（凸透鏡），在輕、重兩級模糊之間取（散射），再加飽和度、白霧、淡淡的有色玻璃；
    //   邊緣的環境反光、固定 45 度光源的細高光、上緣內側的內陰影、外面的影子。
    // 只在玻璃附近那一小塊執行（模糊也只做那一塊，而且是四分之一解析度），畫面其他地方不碰，顯示卡負擔很小。
    // 著色器在背景執行緒用 Windows 內建的 d3dcompiler_47.dll 編譯；編譯失敗或顯示卡太舊（Direct3D 10 以下）就不用，退回原本的毛玻璃
    class LiquidGlass
    {
        // 一塊玻璃（座標是畫面上的像素，y 往下）
        public struct Shape
        {
            public float X, Y, HalfW, HalfH, Radius;   // 中心、半寬半高、圓角
            public float Appear, Thick;                // 出現程度（0～1）、厚度倍數（邊緣折射帶的寬度跟著它）
            public float TintR, TintG, TintB, TintA;   // 有色玻璃的顏色和濃度
            public float Mag, Shadow, Energy;          // 放大倍率（不放大是 1；沒設的 0 也當 1）、影子濃度、按下時整塊亮起來的白光
            public float Focus;                        // 上下兩條直邊的折射打折多少：0 整圈一樣；0.45 是上下直邊只剩 55%（背景的字不會被拉長；左右邊、圓角不打折）
            public float D00, D11;                     // 變形的反矩陣（只有左右、上下的拉長壓扁）；不變形是 1, 1（沒設的 0 也當 1）
        }

        // 一層玻璃的質感（數值跟原型面板上調好的一樣）
        public class Look
        {
            public float Scale = 1;   // 以 1080p 為 1：裡面的像素數值（邊緣寬度、影子）都乘它
            public float Refract = 0.9f, Bevel = 20, CA = 0.06f, Scatter = 0.86f, Sat = 1.5f, TintAmt = 0.3f;
            public float Legible = 0.3f, Spec = 0.85f, Reflect = 0.3f, Merge = 6, InnerA = 0.06f, InnerR = 8, Inner = 6, Dim = 0;
            public float TouchX, TouchY, TouchGlow, TouchRadius;   // 按住時從手指下面亮起來（亮度 0 就沒有）

            // 第二層（卡片上的小玻璃圓鈕）：底下已經是玻璃了，不再糊、不加白霧，跟旁邊的也不黏在一起
            public static Look OnTop(float scale)
            {
                return new Look { Scale = scale, Scatter = 0, Sat = 1.05f, TintAmt = 0, Legible = 0, Merge = 0 };
            }
        }

        public const int MaxShapes = 4;

        // ---------- 著色器（HLSL）----------
        const string Source = @"
cbuffer GlassInfo : register(b0)
{
    float4 Shape[4];   // 中心 xy、半寬半高 zw（像素，y 往下）
    float4 Param[4];   // 圓角、出現程度、深淺（0 深色）、厚度倍數
    float4 Tint[4];    // 有色玻璃 rgb、濃度
    float4 Fill[4];    // 實心填色 rgb、多實心
    float4 Extra[4];   // 放大倍率、影子濃度、按下的白光、上下直邊的折射打折多少
    float4 Def[4];     // 變形的反矩陣
    float4 SceneRect;  // 玻璃後面那張圖在畫面上的範圍
    float4 BlurRect;   // 模糊圖在畫面上的範圍
    float4 A;          // 幾塊玻璃、縮放（1080p = 1）、折射強度、邊緣折射帶寬度
    float4 B;          // 色散、散射、飽和度、白霧
    float4 C;          // 易讀性、高光、環境反光、黏合距離
    float4 D;          // 內陰影濃度、內陰影寬度、往內反光的寬度、變暗
    float4 Touch;      // 按住：手指的位置 xy、亮度 z、擴散的半徑 w
};
cbuffer PassInfo : register(b1)
{
    float4 Src;        // 縮小：來源左上角 xy、來源大小 zw；模糊：zw = 1 / 小圖大小
    float4 Dir;        // 模糊的方向（小圖的 uv）
};
Texture2D SceneTex : register(t0);
Texture2D BlurATex : register(t1);
Texture2D BlurBTex : register(t2);
SamplerState Lin : register(s0);
static const float Weights[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };

float4 VS(uint id : SV_VertexID) : SV_Position
{
    return float4(id == 2 ? 3.0 : -1.0, id == 1 ? 3.0 : -1.0, 0.0, 1.0);   // 蓋滿整個 viewport 的三角形
}

float4 Down(float4 pos : SV_Position) : SV_Target   // 縮成四分之一：每個點是 4 × 4 個點的平均
{
    float2 t = 1.0 / Src.zw, uv = (Src.xy + pos.xy * 4.0) * t;
    return 0.25 * (SceneTex.SampleLevel(Lin, uv + float2(-t.x, -t.y), 0) + SceneTex.SampleLevel(Lin, uv + float2(t.x, -t.y), 0)
                 + SceneTex.SampleLevel(Lin, uv + float2(-t.x, t.y), 0) + SceneTex.SampleLevel(Lin, uv + t, 0));
}

float4 Blur(float4 pos : SV_Position) : SV_Target   // 9 點高斯（一個方向）
{
    float2 uv = pos.xy * Src.zw;
    float3 c = BlurATex.SampleLevel(Lin, uv, 0).rgb * Weights[0];
    [unroll] for (int i = 1; i < 5; i++)
    {
        c += BlurATex.SampleLevel(Lin, uv + Dir.xy * i, 0).rgb * Weights[i];
        c += BlurATex.SampleLevel(Lin, uv - Dir.xy * i, 0).rgb * Weights[i];
    }
    return float4(c, 1.0);
}

struct Hit { float d; float2 c; float appear, style, thick, mag, shadow, energy, focus, flat; float4 tint, fill; };

float SdBox(float2 p, float2 b, float r)
{
    r = min(r, min(b.x, b.y));
    float2 q = abs(p) - b + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// 這一點的折射要打折多少（0～1）：只有靠上下兩條直邊的地方打折（背景的字是橫的，上下邊緣最容易被拉長）；
// 靠左右邊、圓角裡保持全強度。用玻璃自己的座標判斷：離上下邊比離左右邊近就算「上下」，沿著 45° 那條線平順過渡（不會有折痕）
float FlatOf(int i, float2 p)
{
    float2 q = p - Shape[i].xy;
    float4 m = Def[i];
    q = float2(m.x * q.x + m.y * q.y, m.z * q.x + m.w * q.y);
    float2 b = Shape[i].zw;
    float r = min(Param[i].x, min(b.x, b.y));
    float2 over = abs(q) - (b - r);
    float corner = smoothstep(0.0, max(0.3 * r, 1e-3), min(over.x, over.y));   // 圓角裡（平順地接到直邊）
    float2 gap = b - abs(q);                                              // 離左右邊、上下邊多遠
    float k = 14.0 * A.y;
    return smoothstep(-k, k, gap.x - gap.y) * (1.0 - corner);
}

float ShapeD(int i, float2 p)
{
    float2 q = p - Shape[i].xy;
    float far = length(q) - length(Shape[i].zw) * 1.35;   // 離很遠：用外接圓估就好
    if (far > 80.0 * A.y) return far;
    float4 m = Def[i];
    q = float2(m.x * q.x + m.y * q.y, m.z * q.x + m.w * q.y);
    return SdBox(q, Shape[i].zw, Param[i].x);
}

Hit Scene(float2 p)   // 每塊玻璃平滑地黏在一起，屬性跟著混合
{
    Hit h;
    h.d = 1e5; h.c = float2(0.0, 0.0); h.appear = 0.0; h.style = 0.0; h.thick = 1.0; h.mag = 1.0; h.shadow = 0.0; h.energy = 0.0; h.focus = 0.0; h.flat = 0.0;
    h.tint = float4(0.0, 0.0, 0.0, 0.0); h.fill = float4(0.0, 0.0, 0.0, 0.0);
    float k = max(C.w * A.y, 1e-3);
    int n = (int)A.x;
    [loop] for (int i = 0; i < 4; i++)
    {
        if (i >= n) break;
        float di = ShapeD(i, p);
        float w = saturate(0.5 + 0.5 * (h.d - di) / k);
        h.d = lerp(h.d, di, w) - k * w * (1.0 - w);
        h.c = lerp(h.c, Shape[i].xy, w);
        h.appear = lerp(h.appear, Param[i].y, w); h.style = lerp(h.style, Param[i].z, w); h.thick = lerp(h.thick, Param[i].w, w);
        h.mag = lerp(h.mag, Extra[i].x, w); h.shadow = lerp(h.shadow, Extra[i].y, w); h.energy = lerp(h.energy, Extra[i].z, w); h.focus = lerp(h.focus, Extra[i].w, w);
        h.flat = lerp(h.flat, Extra[i].w > 0.0 ? FlatOf(i, p) : 0.0, w);   // 不用打折的（Focus 0）不用算
        h.tint = lerp(h.tint, Tint[i], w); h.fill = lerp(h.fill, Fill[i], w);
    }
    return h;
}

float Dist(float2 p)
{
    float d = 1e5, k = max(C.w * A.y, 1e-3);
    int n = (int)A.x;
    [loop] for (int i = 0; i < 4; i++)
    {
        if (i >= n) break;
        float di = ShapeD(i, p);
        float w = saturate(0.5 + 0.5 * (d - di) / k);
        d = lerp(d, di, w) - k * w * (1.0 - w);
    }
    return d;
}

float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

float3 Look(float2 p, float s)   // 在清楚、輕模糊、重模糊之間取
{
    float3 a = SceneTex.SampleLevel(Lin, (p - SceneRect.xy) / SceneRect.zw, 0).rgb;
    float2 bu = (p - BlurRect.xy) / BlurRect.zw;
    float3 b = BlurATex.SampleLevel(Lin, bu, 0).rgb, c = BlurBTex.SampleLevel(Lin, bu, 0).rgb;
    return s < 0.5 ? lerp(a, b, s * 2.0) : lerp(b, c, s * 2.0 - 1.0);
}

float4 Glass(float4 pos : SV_Position) : SV_Target
{
    float2 p = pos.xy;
    float s = A.y;
    float3 bg = SceneTex.SampleLevel(Lin, (p - SceneRect.xy) / SceneRect.zw, 0).rgb;
    float2 bu = (p - BlurRect.xy) / BlurRect.zw;

    float shOff = 9.0 * s, shR = 40.0 * s;
    float d0 = Dist(p), ds0 = Dist(p - float2(0.0, shOff));
    if (d0 > 1.0 && ds0 > shR) return float4(bg, 1.0);   // 離玻璃很遠：原樣

    // 影子：越厚越大，旁邊的顏色會滲進影子
    Hit hs = Scene(p - float2(0.0, shOff));
    float r = (12.0 + 20.0 * hs.thick) * s;
    float sh = hs.shadow * hs.appear * (1.0 - smoothstep(-0.4 * r, r, hs.d));
    float3 outside = bg * (1.0 - sh) + sh * 0.2 * BlurBTex.SampleLevel(Lin, bu, 0).rgb;
    if (d0 > 1.0) return float4(outside, 1.0);

    Hit h = Scene(p);
    // 邊緣的方向：用左右、上下各 14 的差算（圓角裡面 45° 那條線兩邊方向會突然轉，間距大一點才會平順地彎過去，不會有一條折線）。
    // 直邊上長度剛好是 2 × 14；圓角比 14 小的形狀（圓鈕、膠囊）在圓心附近左右上下互相抵消、長度趨近 0，這時讓它變短（折射自然淡掉），不硬拉回 1（會在圓心轉漩渦）
    float ne = 14.0 * s;
    float2 n = float2(Dist(p + float2(ne, 0.0)) - Dist(p - float2(ne, 0.0)), Dist(p + float2(0.0, ne)) - Dist(p - float2(0.0, ne)));
    n = n / max(length(n), 1.5 * ne);
    float a = h.appear;

    // 邊緣往外取樣（像透鏡的邊，把玻璃外面的東西壓進來）：位移 = 強度 × 0.5 × 邊寬 × e^(−深度 / (0.35 × 邊寬))；
    // 越靠邊壓得越緊，取樣位置一直往同一個方向走，不會折返、不會抹成一片
    float bev = max(A.w * s * h.thick, 1e-3);
    float u = max(-h.d, 0.0);
    float x = saturate(u / bev);
    float edge = 1.0 - x;
    float disp = A.z * 0.5 * bev * exp(-u / (0.35 * bev));
    // 上下兩條直邊的折射打折（背景的字不會被拉長）；左右邊、圓角保持全強度
    disp *= 1.0 - h.focus * h.flat;
    float2 off = n * disp * a;

    float2 base = h.c + (p - h.c) / lerp(1.0, h.mag, a);   // 整塊放大
    float ca = B.x * edge;
    // 越靠邊緣越清楚：邊緣折射進來的是看得出形狀的背景（液態玻璃最明顯的特徵），中間才是模糊的；
    // 跟位移用同一種衰減（位移大的地方才清楚），但至少留一點輕模糊（邊緣壓縮了好幾倍，完全不糊的話影片的細節會閃）
    float sc = saturate(B.y * lerp(0.8, 1.25, saturate(h.thick - 0.6))) * a * (1.0 - 0.75 * exp(-u / (0.5 * bev)));
    sc = max(sc, 0.1 * a * step(0.001, B.y));
    float3 col = float3(Look(base + off * (1.0 + ca), sc).r, Look(base + off, sc).g, Look(base + off * (1.0 - ca), sc).b);

    col *= 1.0 - D.w * a;
    col = lerp(Luma(col).xxx, col, lerp(1.0, B.z, a));
    float leg = C.x * a;
    col = lerp(col * (1.0 - 0.15 * leg), lerp(col, float3(1.0, 1.0, 1.0), 0.45 * leg), h.style);
    col = lerp(col, float3(1.0, 1.0, 1.0), B.w * a * lerp(0.5, 1.0, h.style));
    col = lerp(col, h.tint.rgb * (0.45 + 0.9 * Luma(col)), h.tint.a * a);
    col = lerp(col, h.fill.rgb, h.fill.a * a);

    col = lerp(col, float3(1.0, 1.0, 1.0), 0.24 * h.energy * a);   // 按下：整塊被白光填滿
    col *= 1.0 + 0.10 * edge * edge * a * (1.0 - h.fill.a);          // 光在彎曲的邊緣被集中
    float3 env = BlurBTex.SampleLevel(Lin, (p + n * 50.0 * s - BlurRect.xy) / BlurRect.zw, 0).rgb;   // 環境反光
    col = lerp(col, env * 1.2 + 0.04, saturate(pow(edge, 3.0) * C.z * 0.6 * a));
    // 高光：外緣一條細亮線（左上、右下最亮），往內一點有很淡的反光
    float dn = min(h.d, 0.0) / s;   // 跟控制面板一樣：最外緣半透明那一圈不再更亮
    float rim = exp(dn), inner = exp(dn / D.z);
    float ang = pow(abs(dot(n, float2(0.70710678, 0.70710678))), 1.5);
    float3 hl = lerp(float3(1.0, 1.0, 1.0), saturate(lerp(Luma(bg).xxx, bg, 2.0)), 0.25);
    col += C.y * a * hl * (rim * (0.12 + 0.88 * ang) * 0.6 + inner * ang * (D.z > 7.0 ? 0.3 : 0.1)) * lerp(1.0, 0.45, h.style);
    if (D.x > 0.0)   // 內陰影：上緣內側一條柔和的暗線
    {
        float io = D.y * s, du = Dist(p - float2(0.0, io));
        col *= 1.0 - D.x * smoothstep(-io, io * 0.3, du) * a * (1.0 - h.fill.a);
    }
    col = lerp(col, float3(0.42, 0.42, 0.42), exp(h.d / (0.8 * s)) * 0.3 * h.style * a);
    // 按住：從手指下面亮起來，白光往整片玻璃擴散
    float2 gp = p - Touch.xy;
    col += a * Touch.z * 0.13 * exp(-dot(gp, gp) / (2.0 * Touch.w * Touch.w + 1.0));
    return float4(lerp(outside, col, saturate(0.5 - h.d)), 1.0);
}
";

        // ---------- 編譯（背景執行緒，整個程式只做一次）----------
        [DllImport("d3dcompiler_47.dll")]
        static extern int D3DCompile(byte[] srcData, IntPtr srcDataSize, [MarshalAs(UnmanagedType.LPStr)] string sourceName, IntPtr defines, IntPtr include,
            [MarshalAs(UnmanagedType.LPStr)] string entryPoint, [MarshalAs(UnmanagedType.LPStr)] string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr BlobFn(IntPtr self);

        static byte[] vsCode, downCode, blurCode, glassCode;
        static int compileState;   // 0 還沒開始、1 編譯中、2 好了、3 失敗

        // 開始在背景編譯（已經在編、編好了就不重來）
        public static void Prepare()
        {
            if (Interlocked.CompareExchange(ref compileState, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var src = Encoding.UTF8.GetBytes(Source);
                    vsCode = Compile(src, "VS", "vs_4_0");
                    downCode = Compile(src, "Down", "ps_4_0");
                    blurCode = Compile(src, "Blur", "ps_4_0");
                    glassCode = Compile(src, "Glass", "ps_4_0");
                    Interlocked.Exchange(ref compileState, 2);
                }
                catch
                {
                    Interlocked.Exchange(ref compileState, 3);   // 編譯失敗：卡片退回毛玻璃
                }
            });
        }

        public static bool Ready { get { return Volatile.Read(ref compileState) == 2; } }

        internal static byte[] Compile(byte[] src, string entry, string target)   // 控制面板的玻璃（GlassBackdrop）也用
        {
            IntPtr code, errors;
            int hr = D3DCompile(src, (IntPtr)src.Length, "LiquidGlass", IntPtr.Zero, IntPtr.Zero, entry, target, 1 << 15 /* 最佳化等級 3 */, 0, out code, out errors);
            try
            {
                if (hr < 0) throw new Exception(entry + ": " + (errors != IntPtr.Zero ? BlobText(errors) : "0x" + hr.ToString("X8")));
                return BlobBytes(code);
            }
            finally
            {
                if (code != IntPtr.Zero) Marshal.Release(code);
                if (errors != IntPtr.Zero) Marshal.Release(errors);
            }
        }

        static byte[] BlobBytes(IntPtr blob)
        {
            IntPtr data = ComVtable.Method<BlobFn>(blob, 3)(blob);                // ID3DBlob::GetBufferPointer
            int size = (int)ComVtable.Method<BlobFn>(blob, 4)(blob);              // ::GetBufferSize
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, size);
            return bytes;
        }

        static string BlobText(IntPtr blob) { return Encoding.ASCII.GetString(BlobBytes(blob)).TrimEnd('\0'); }

        // ---------- Direct3D 11 的函式（照 d3d11.h 的順序）----------
        [StructLayout(LayoutKind.Sequential)] struct BufferDesc { public uint ByteWidth; public int Usage; public uint BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }
        [StructLayout(LayoutKind.Sequential)] struct Texture2DDesc { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality; public int Usage; public uint BindFlags, CpuAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] struct SamplerDesc { public int Filter, AddressU, AddressV, AddressW; public float MipLodBias; public uint MaxAnisotropy; public int ComparisonFunc; public float Border0, Border1, Border2, Border3, MinLod, MaxLod; }
        [StructLayout(LayoutKind.Sequential)] struct Viewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }
        [StructLayout(LayoutKind.Sequential)] struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

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
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyRegionFn(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref Box box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UpdateFn(IntPtr self, IntPtr dst, uint sub, IntPtr box, float[] data, uint rowPitch, uint depthPitch);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ClearStateFn(IntPtr self);

        const int Bgra = 87;   // DXGI_FORMAT_B8G8R8A8_UNORM（跟輸出畫面一樣）

        readonly IntPtr device, context;
        readonly CreateBufferFn createBuffer;
        readonly CreateTexture2DFn createTexture;
        readonly CreateViewFn createSrv, createRtv;
        readonly SetResourcesFn psSetResources, psSetSamplers, psSetConstants;
        readonly SetShaderFn vsSetShader, psSetShader;
        readonly DrawFn draw;
        readonly SetPointerFn iaSetLayout;
        readonly SetTopologyFn iaSetTopology;
        readonly SetTargetsFn omSetTargets;
        readonly SetViewportsFn rsSetViewports;
        readonly CopyRegionFn copyRegion;
        readonly UpdateFn update;
        readonly ClearStateFn clearState;

        IntPtr vs, downPs, blurPs, glassPs, sampler, glassBuffer, passBuffer;
        readonly float[] glassData = new float[31 * 4];   // 跟 cbuffer GlassInfo 一樣的排法（31 個 float4）
        readonly float[] passData = new float[8];
        readonly IntPtr[] one = new IntPtr[1], two = new IntPtr[2], three = new IntPtr[3];

        // 模糊用的四張小圖（四分之一解析度）
        class Small { public IntPtr Texture, Rtv, Srv; }
        readonly Small[] blur = new Small[4];   // q1、q2（暫存）、qA（輕模糊）、qB（重模糊）
        int blurW, blurH;
        // 第二層：先把畫好的那一塊複製下來，再拿它來折射
        IntPtr copyTexture, copySrv;
        int copyW, copyH;

        LiquidGlass(IntPtr device, IntPtr context)
        {
            this.device = device;
            this.context = context;
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
            copyRegion = ComVtable.Method<CopyRegionFn>(context, 46);              // ::CopySubresourceRegion
            update = ComVtable.Method<UpdateFn>(context, 48);                      // ::UpdateSubresource
            clearState = ComVtable.Method<ClearStateFn>(context, 110);             // ::ClearState
        }

        // 著色器編好了、顯示卡支援（Direct3D 10 以上）才建立；不行回傳 null（呼叫的人退回毛玻璃）
        public static LiquidGlass Create(IntPtr device, IntPtr context, int featureLevel)
        {
            if (!Ready || featureLevel < 0xa000) return null;
            var g = new LiquidGlass(device, context);
            try
            {
                if (g.Init()) return g;
            }
            catch { }
            g.Release();
            return null;
        }

        bool Init()
        {
            var createVs = ComVtable.Method<CreateShaderFn>(device, 12);   // ID3D11Device::CreateVertexShader
            var createPs = ComVtable.Method<CreateShaderFn>(device, 15);   // ::CreatePixelShader
            if (createVs(device, vsCode, (IntPtr)vsCode.Length, IntPtr.Zero, out vs) < 0) return false;
            if (createPs(device, downCode, (IntPtr)downCode.Length, IntPtr.Zero, out downPs) < 0) return false;
            if (createPs(device, blurCode, (IntPtr)blurCode.Length, IntPtr.Zero, out blurPs) < 0) return false;
            if (createPs(device, glassCode, (IntPtr)glassCode.Length, IntPtr.Zero, out glassPs) < 0) return false;
            var sd = new SamplerDesc { Filter = 0x15 /* 線性 */, AddressU = 3, AddressV = 3, AddressW = 3 /* 超出範圍用邊緣的顏色 */, ComparisonFunc = 1, MaxLod = float.MaxValue };
            if (ComVtable.Method<CreateSamplerFn>(device, 23)(device, ref sd, out sampler) < 0) return false;   // ::CreateSamplerState
            var bd = new BufferDesc { ByteWidth = (uint)(glassData.Length * 4), BindFlags = 0x4 /* 常數 */ };
            if (createBuffer(device, ref bd, IntPtr.Zero, out glassBuffer) < 0) return false;
            bd.ByteWidth = (uint)(passData.Length * 4);
            if (createBuffer(device, ref bd, IntPtr.Zero, out passBuffer) < 0) return false;
            return true;
        }

        // 輸出畫面（或影片畫布）用的繪圖目標 / 讀取介面；失敗回傳 0
        public IntPtr CreateTargetView(IntPtr texture) { IntPtr v; return createRtv(device, texture, IntPtr.Zero, out v) < 0 ? IntPtr.Zero : v; }
        public IntPtr CreateReadView(IntPtr texture) { IntPtr v; return createSrv(device, texture, IntPtr.Zero, out v) < 0 ? IntPtr.Zero : v; }

        public static void Free(ref IntPtr com) { if (com != IntPtr.Zero) { Marshal.Release(com); com = IntPtr.Zero; } }

        // ---------- 第一層：影片上的玻璃 ----------
        // scene：玻璃後面的影片（整個畫面大小）；target：輸出畫面。
        // (bx, by, bw, bh)：要模糊的範圍（玻璃加上四周，玻璃移動時也蓋得到）；(dx, dy, dw, dh)：這次要畫的範圍（玻璃加上影子）。
        // reblur = false：沿用上次的模糊圖（影片沒換格、範圍沒變）
        // 畫不了（模糊圖建不起來，例如顯示卡記憶體不夠）回傳 false
        public bool DrawLayer(IntPtr scene, int sceneW, int sceneH, IntPtr target, Shape[] shapes, int count, Look look,
            int bx, int by, int bw, int bh, int dx, int dy, int dw, int dh, bool reblur)
        {
            int qw = Math.Max(1, (bw + 3) / 4), qh = Math.Max(1, (bh + 3) / 4);
            clearState(context);
            Common();
            if (qw != blurW || qh != blurH) { if (!CreateBlur(qw, qh)) { clearState(context); return false; } reblur = true; }
            if (reblur)
            {
                // 縮成四分之一 → 輕模糊（A）→ 再糊兩次（B）；半徑以畫面上的大小為準。
                // 9 點高斯的取樣點相隔超過約 1.5 格時，中間的細節會被拆成一排分開的重影（4K 時最明顯），
                // 所以輕模糊先用小一半、再小一半的間距糊過（細節先抹平），最後才用原本的間距
                SetTarget(blur[0].Rtv, 0, 0, qw, qh);
                psSetShader(context, downPs, IntPtr.Zero, 0);
                one[0] = scene; psSetResources(context, 0, 1, one);
                SetPass(bx, by, sceneW, sceneH, 0, 0);
                draw(context, 3, 0);
                one[0] = IntPtr.Zero; psSetResources(context, 0, 1, one);
                psSetShader(context, blurPs, IntPtr.Zero, 0);
                float r = 2.6f * look.Scale, r2 = r * 1.7f;
                int levels = 1;
                while (levels < 4 && r / (1 << (levels - 1)) > 1.5f) levels++;   // 1080p：1.3、2.6；4K：1.3、2.6、5.2
                for (int k = levels - 1; k >= 0; k--)
                {
                    float step = r / (1 << k);
                    BlurPass(blur[0], blur[1], step, 0);
                    BlurPass(blur[1], k == 0 ? blur[2] : blur[0], 0, step);
                }
                BlurPass(blur[2], blur[1], r2, 0); BlurPass(blur[1], blur[0], 0, r2); BlurPass(blur[0], blur[1], r2, 0); BlurPass(blur[1], blur[3], 0, r2);
                one[0] = IntPtr.Zero; psSetResources(context, 1, 1, one);
            }
            if (dw <= 0 || dh <= 0) { clearState(context); return true; }
            SetTarget(target, dx, dy, dw, dh);
            psSetShader(context, glassPs, IntPtr.Zero, 0);
            three[0] = scene; three[1] = blur[2].Srv; three[2] = blur[3].Srv;
            psSetResources(context, 0, 3, three);
            Pack(shapes, count, look, 0, 0, sceneW, sceneH, bx, by, qw * 4, qh * 4);
            draw(context, 3, 0);
            clearState(context);   // 狀態還原成預設，不影響影片解碼器和 Direct2D
            return true;
        }

        // ---------- 第二層：卡片內容上面的小玻璃（歌詞圓鈕）----------
        // 先把 (dx, dy, dw, dh) 這塊已經畫好的畫面複製下來，再拿它來折射、畫回同一個位置
        public void DrawOnTop(IntPtr backBuffer, IntPtr target, Shape[] shapes, int count, Look look, int dx, int dy, int dw, int dh)
        {
            if (dw <= 0 || dh <= 0) return;
            if ((dw > copyW || dh > copyH) && !CreateCopy(Math.Max(dw, copyW), Math.Max(dh, copyH))) return;
            var box = new Box { Left = (uint)dx, Top = (uint)dy, Right = (uint)(dx + dw), Bottom = (uint)(dy + dh), Back = 1 };
            copyRegion(context, copyTexture, 0, 0, 0, 0, backBuffer, 0, ref box);
            clearState(context);
            Common();
            SetTarget(target, dx, dy, dw, dh);
            psSetShader(context, glassPs, IntPtr.Zero, 0);
            three[0] = three[1] = three[2] = copySrv;
            psSetResources(context, 0, 3, three);
            Pack(shapes, count, look, dx, dy, copyW, copyH, dx, dy, copyW, copyH);
            draw(context, 3, 0);
            clearState(context);
        }

        void Common()
        {
            iaSetLayout(context, IntPtr.Zero);
            iaSetTopology(context, 4 /* 三角形清單 */);
            vsSetShader(context, vs, IntPtr.Zero, 0);
            one[0] = sampler; psSetSamplers(context, 0, 1, one);
            two[0] = glassBuffer; two[1] = passBuffer;
            psSetConstants(context, 0, 2, two);
        }

        void SetTarget(IntPtr rtv, int x, int y, int w, int h)
        {
            one[0] = rtv;
            omSetTargets(context, 1, one, IntPtr.Zero);
            var vp = new Viewport { X = x, Y = y, Width = w, Height = h, MaxDepth = 1 };
            rsSetViewports(context, 1, ref vp);
        }

        void SetPass(float a, float b, float c, float d, float e, float f)
        {
            passData[0] = a; passData[1] = b; passData[2] = c; passData[3] = d; passData[4] = e; passData[5] = f; passData[6] = passData[7] = 0;
            update(context, passBuffer, 0, IntPtr.Zero, passData, 0, 0);
        }

        // 一個方向的高斯模糊：from → to。先把上一次的來源拿掉（它可能就是這次的目標），再換繪圖目標、接上這次的來源，
        // 同一張圖不會同時是讀取和繪圖目標
        void BlurPass(Small from, Small to, float dirX, float dirY)
        {
            one[0] = IntPtr.Zero; psSetResources(context, 1, 1, one);
            SetTarget(to.Rtv, 0, 0, blurW, blurH);
            one[0] = from.Srv; psSetResources(context, 1, 1, one);
            SetPass(0, 0, 1f / blurW, 1f / blurH, dirX / blurW, dirY / blurH);
            draw(context, 3, 0);
        }

        // 把每塊玻璃和質感填進常數（排法跟 cbuffer Glass 一樣）
        void Pack(Shape[] shapes, int count, Look look, float sx, float sy, float sw, float sh, float bx, float by, float bw, float bh)
        {
            var g = glassData;
            Array.Clear(g, 0, g.Length);
            count = Math.Min(count, MaxShapes);
            for (int i = 0; i < count; i++)
            {
                var s = shapes[i];
                Put(g, i, s.X, s.Y, s.HalfW, s.HalfH);
                Put(g, 4 + i, s.Radius, s.Appear, 0 /* 深色玻璃 */, s.Thick);
                Put(g, 8 + i, s.TintR, s.TintG, s.TintB, s.TintA);
                // 12 + i：實心填色（現在的形狀都不用，保持 0）
                Put(g, 16 + i, s.Mag > 0 ? s.Mag : 1, s.Shadow, s.Energy, s.Focus);   // 沒設（0）的放大倍率、變形當作 1：不然著色器會除以 0
                Put(g, 20 + i, s.D00 != 0 ? s.D00 : 1, 0, 0, s.D11 != 0 ? s.D11 : 1);
            }
            Put(g, 24, sx, sy, sw, sh);
            Put(g, 25, bx, by, bw, bh);
            Put(g, 26, count, look.Scale, look.Refract, look.Bevel);
            Put(g, 27, look.CA, look.Scatter, look.Sat, look.TintAmt);
            Put(g, 28, look.Legible, look.Spec, look.Reflect, look.Merge);
            Put(g, 29, look.InnerA, look.InnerR, look.Inner, look.Dim);
            Put(g, 30, look.TouchX, look.TouchY, look.TouchGlow, look.TouchRadius);
            update(context, glassBuffer, 0, IntPtr.Zero, g, 0, 0);
        }

        static void Put(float[] g, int register, float x, float y, float z, float w)
        {
            g[register * 4] = x; g[register * 4 + 1] = y; g[register * 4 + 2] = z; g[register * 4 + 3] = w;
        }

        bool CreateBlur(int w, int h)
        {
            ReleaseBlur();
            for (int i = 0; i < blur.Length; i++)
            {
                var s = blur[i] = new Small();
                var desc = new Texture2DDesc { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Bgra, SampleCount = 1, BindFlags = 0x20 | 0x8 };
                if (createTexture(device, ref desc, IntPtr.Zero, out s.Texture) < 0) { ReleaseBlur(); return false; }
                s.Rtv = CreateTargetView(s.Texture);
                s.Srv = CreateReadView(s.Texture);
                if (s.Rtv == IntPtr.Zero || s.Srv == IntPtr.Zero) { ReleaseBlur(); return false; }
            }
            blurW = w; blurH = h;
            return true;
        }

        void ReleaseBlur()
        {
            for (int i = 0; i < blur.Length; i++)
            {
                var s = blur[i];
                if (s == null) continue;
                Free(ref s.Srv); Free(ref s.Rtv); Free(ref s.Texture);
                blur[i] = null;
            }
            blurW = blurH = 0;
        }

        bool CreateCopy(int w, int h)
        {
            Free(ref copySrv); Free(ref copyTexture);
            copyW = copyH = 0;
            var desc = new Texture2DDesc { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Bgra, SampleCount = 1, BindFlags = 0x8 };
            if (createTexture(device, ref desc, IntPtr.Zero, out copyTexture) < 0) { copyTexture = IntPtr.Zero; return false; }
            copySrv = CreateReadView(copyTexture);
            if (copySrv == IntPtr.Zero) { Free(ref copyTexture); return false; }
            copyW = w; copyH = h;
            return true;
        }

        // 模糊圖、複本圖還回去（鎖定畫面收起來時；下次用到會再建立）
        public void ReleaseScratch()
        {
            ReleaseBlur();
            Free(ref copySrv); Free(ref copyTexture);
            copyW = copyH = 0;
        }

        public void Release()
        {
            ReleaseScratch();
            Free(ref glassBuffer); Free(ref passBuffer); Free(ref sampler);
            Free(ref glassPs); Free(ref blurPs); Free(ref downPs); Free(ref vs);
        }
    }
}
