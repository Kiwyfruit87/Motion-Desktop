using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VideoWallpaper
{
    // 控制面板的液態玻璃：跟網頁原型、鎖定畫面的音樂卡片同一套算法（見 LiquidGlass），改寫成 WPF 的像素著色器。
    // 面板是透明的視窗，裡面只有一塊塊浮著的玻璃：
    //   第一層：整塊面板（畫在面板內容的最底下，折射面板後面的畫面）；
    //   第二層：工具列的三塊玻璃，以及開關、分段控制按住時浮起的透鏡（疊在最上面，折射第一層的玻璃和上面的字）。
    // 玻璃後面的畫面在面板開著時跟著畫面的每一格從螢幕抓（後面一陣子都沒變就隔一格抓；面板自己設成不會被抓到），縮成四分之一、模糊成兩級。
    // 面板收起來就不抓了。深淺照 Windows；玻璃後面哪裡太亮（深色玻璃）就在那裡壓暗一點、哪裡太暗（淺色玻璃）就多一點白霧，字才看得清楚
    class GlassBackdrop
    {
        public const int MaxShapes = 12;

        const string Source = @"
sampler2D Input : register(s0);   // 玻璃後面的畫面
sampler2D BlurA : register(s1);   // 輕模糊（四分之一大小）
sampler2D BlurB : register(s2);   // 重模糊
float4 Shape[12] : register(c0);  // 中心 xy、半寬半高 zw（跟面板一樣的單位，y 往下）
float4 Param[12] : register(c12); // 圓角、出現程度、按下的白光、厚度倍數
float4 Tint[12] : register(c24);  // 有色玻璃 rgb、濃度
float4 Fill[12] : register(c36);  // 實心填色 rgb、多實心（旋鈕平常是白色實心，按住才變透明的透鏡）
float4 Def[12] : register(c48);   // 果凍變形的反矩陣
float4 Extra[12] : register(c60); // 放大倍率、影子濃度
float4 Pull[12] : register(c72);  // 被手指拉出來：手指 xy、拉出多少、範圍
float4 A : register(c84);         // 幾塊、（沒用）、折射強度、邊緣折射帶寬度
float4 B : register(c85);         // 色散、散射、飽和度、白霧
float4 C : register(c86);         // 易讀性、高光、環境反光、黏合距離
float4 D : register(c87);         // 內陰影濃度、內陰影寬度、往內反光的寬度、變暗
float4 Size : register(c88);      // 面板寬高、深淺（0 深色 1 淺色）、白光的亮度
float4 Touch : register(c89);     // 按住：手指的位置 xy、亮度 z、擴散的半徑 w
float4 Mask : register(c90);      // 白光只在被按的那個元件裡：它的中心 xy、半寬半高 zw
float4 MaskR : register(c91);     // 那個元件的圓角、背景亮的地方多壓暗多少（深色）、背景暗的地方多加多少白霧（淺色）

float SdBox(float2 p, float2 b, float r)
{
    r = min(r, min(b.x, b.y));
    float2 q = abs(p) - b + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

float ShapeD(int i, float2 p)
{
    float2 q = p - Shape[i].xy;
    float4 m = Def[i];
    q = float2(m.x * q.x + m.y * q.y, m.z * q.x + m.w * q.y);
    float2 pp = p - Pull[i].xy;
    return SdBox(q, Shape[i].zw, Param[i].x) - Pull[i].z * exp(-dot(pp, pp) / max(Pull[i].w * Pull[i].w, 1.0));
}

float Dist(float2 p)
{
    float d = 1e5, k = max(C.w, 1e-3);
    int n = (int)A.x;
    [loop] for (int i = 0; i < 12; i++)
    {
        if (i >= n) break;
        float di = ShapeD(i, p);
        float w = saturate(0.5 + 0.5 * (d - di) / k);
        d = lerp(d, di, w) - k * w * (1.0 - w);
    }
    return d;
}

struct Hit { float d; float2 c; float2 hs; float appear, energy, thick, mag, shadow; float4 tint, fill; };

Hit Scene(float2 p)   // 每塊玻璃平滑地黏在一起，屬性跟著混合
{
    Hit h;
    h.d = 1e5; h.c = float2(0.0, 0.0); h.hs = float2(1.0, 1.0); h.appear = 0.0; h.energy = 0.0; h.thick = 1.0; h.mag = 1.0; h.shadow = 0.0;
    h.tint = float4(0.0, 0.0, 0.0, 0.0); h.fill = float4(0.0, 0.0, 0.0, 0.0);
    float k = max(C.w, 1e-3);
    int n = (int)A.x;
    [loop] for (int i = 0; i < 12; i++)
    {
        if (i >= n) break;
        float di = ShapeD(i, p);
        float w = saturate(0.5 + 0.5 * (h.d - di) / k);
        h.d = lerp(h.d, di, w) - k * w * (1.0 - w);
        h.c = lerp(h.c, Shape[i].xy, w); h.hs = lerp(h.hs, Shape[i].zw, w);
        h.appear = lerp(h.appear, Param[i].y, w); h.energy = lerp(h.energy, Param[i].z, w); h.thick = lerp(h.thick, Param[i].w, w);
        h.mag = lerp(h.mag, Extra[i].x, w); h.shadow = lerp(h.shadow, Extra[i].y, w);
        h.tint = lerp(h.tint, Tint[i], w); h.fill = lerp(h.fill, Fill[i], w);
    }
    return h;
}

float2 UV(float2 p) { return saturate(p / Size.xy); }
float3 Sharp(float2 p) { return tex2Dlod(Input, float4(UV(p), 0.0, 0.0)).rgb; }

// 透鏡放大用：Catmull-Rom 取樣（9 次雙線性取樣湊出 4×4），放大後字的邊緣比一般的內插銳利、不會糊成一片
float3 SharpFine(float2 p)
{
    float2 size = Size.xy * max(MaskR.w, 1.0);   // 實體像素（MaskR.w 是螢幕縮放倍率）
    float2 pos = saturate(p / Size.xy) * size;
    float2 t1 = floor(pos - 0.5) + 0.5, f = pos - t1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 t0 = (t1 - 1.0) / size, t3 = (t1 + 2.0) / size, t12 = (t1 + w2 / w12) / size;
    float3 c = 0.0;
    c += tex2Dlod(Input, float4(t0.x, t0.y, 0, 0)).rgb * w0.x * w0.y;
    c += tex2Dlod(Input, float4(t12.x, t0.y, 0, 0)).rgb * w12.x * w0.y;
    c += tex2Dlod(Input, float4(t3.x, t0.y, 0, 0)).rgb * w3.x * w0.y;
    c += tex2Dlod(Input, float4(t0.x, t12.y, 0, 0)).rgb * w0.x * w12.y;
    c += tex2Dlod(Input, float4(t12.x, t12.y, 0, 0)).rgb * w12.x * w12.y;
    c += tex2Dlod(Input, float4(t3.x, t12.y, 0, 0)).rgb * w3.x * w12.y;
    c += tex2Dlod(Input, float4(t0.x, t3.y, 0, 0)).rgb * w0.x * w3.y;
    c += tex2Dlod(Input, float4(t12.x, t3.y, 0, 0)).rgb * w12.x * w3.y;
    c += tex2Dlod(Input, float4(t3.x, t3.y, 0, 0)).rgb * w3.x * w3.y;
    return saturate(c);
}
float3 Soft(float2 p) { return tex2Dlod(BlurA, float4(UV(p), 0.0, 0.0)).rgb; }
float3 Softer(float2 p) { return tex2Dlod(BlurB, float4(UV(p), 0.0, 0.0)).rgb; }
float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

float3 Look(float2 p, float s)   // 在清楚、輕模糊、重模糊之間取
{
    float3 a = Sharp(p);
    if (s <= 0.0) return a;
    float3 b = Soft(p), c = Softer(p);
    return s < 0.5 ? lerp(a, b, s * 2.0) : lerp(b, c, s * 2.0 - 1.0);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv * Size.xy;
    float style = Size.z;

    float d0 = Dist(p);
    [branch] if (d0 > 56.0) return float4(0.0, 0.0, 0.0, 0.0);   // 離玻璃很遠（影子最遠約 51）：只算距離就結束，不用把整個場景算完
    // 影子：越厚越大，背景的顏色會滲進影子
    Hit hs = Scene(p - float2(0.0, 9.0));
    // 分支要真的跳過（[branch]）；編譯器還是可能把後面攤平一起算，所以下面的 exp 都夾在 0 以下，
    // 不然離玻璃很遠的地方會算出無限大，乘上 0 變成無效數字，把整片污染成玻璃
    [branch] if (d0 > 1.0 && hs.d > 40.0) return float4(0.0, 0.0, 0.0, 0.0);   // 離玻璃很遠：透明，看得到後面
    float r = 12.0 + 20.0 * hs.thick;
    float sh = hs.shadow * hs.appear * (1.0 - smoothstep(-0.4 * r, r, hs.d));
    float3 shade = sh * 0.2 * Softer(p);
    float4 outside = float4(shade, sh);
    [branch] if (d0 > 1.0) return outside;

    Hit h = Scene(p);
    float dn = min(h.d, 0.0);   // 給 exp 用：玻璃裡面才有意義
    float2 n = float2(Dist(p + float2(1.0, 0.0)) - d0, Dist(p + float2(0.0, 1.0)) - d0);
    n = n / max(length(n), 1e-4);
    float a = h.appear;

    float edge, sc = saturate(B.y * lerp(0.8, 1.25, saturate(h.thick - 0.6))) * a;
    float3 col;
    [branch] if (A.z < 0.0)
    {
        // 透鏡（開關、分段控制按住時；照 iPhone 實機逐格量的）：中間清澈、放大約 1.2 倍；
        // 邊緣一條往「外」取樣的折射帶，把透鏡外面的東西拉進邊緣（帶寬約透鏡高的 1/4，越往裡越小，不會折返成鏡像）；
        // 兩端圓頭只彎一半；色散沿著邊緣的切線方向把 RGB 錯開（上緣左暖右冷、下緣相反）
        float u = max(-h.d, 0.0), R = min(h.hs.x, h.hs.y);
        float D = -A.z * 0.28 * R * exp(-u / (0.24 * R)) * (1.0 - smoothstep(0.5 * R, 0.9 * R, u));
        D *= lerp(0.5, 1.0, abs(n.y)) * a;
        float2 base = h.c + (p - h.c) / lerp(1.0, h.mag, smoothstep(0.15 * R, 0.6 * R, u) * a);   // 只放大中間
        float2 off = n * D, cs = float2(n.y, -n.x) * D * B.x;
        col = SharpFine(base + off);
        [branch] if (dot(cs, cs) > 0.0025) { col.r = SharpFine(base + off + cs).r; col.b = SharpFine(base + off - cs).b; }   // 邊緣才有色散
        edge = 0.0;   // 透鏡的邊緣不變亮、沒有環境反光（只有一圈細亮邊）
    }
    else
    {
        // 一般玻璃（面板；跟鎖定畫面的卡片一樣）：邊緣往外取樣，像透鏡的邊把玻璃外面的東西壓進來：
        // 位移 = 強度 × 0.5 × 邊寬 × e^(−深度 / (0.35 × 邊寬))，越靠邊壓得越緊，不會折返、不會抹成一片。
        // 邊緣的方向用左右、上下各 14 的差算：圓角裡 45° 那條線兩邊會平順地彎過去（不會有折線），圓角的圓心附近折射自然淡掉
        float bev = max(A.w * h.thick, 1e-3);
        float u = max(-h.d, 0.0);
        float ne = 14.0;
        float2 m = float2(0.0, 0.0);
        [branch] if (u < 2.1 * bev)   // 再往裡面位移已經不到 0.3%（e^−6）：不用算方向
        {
            m = float2(Dist(p + float2(ne, 0.0)) - Dist(p - float2(ne, 0.0)), Dist(p + float2(0.0, ne)) - Dist(p - float2(0.0, ne)));
            m = m / max(length(m), 1.5 * ne);
        }
        edge = 1.0 - saturate(u / bev);
        float disp = A.z * 0.5 * bev * exp(-u / (0.35 * bev));
        float2 off = m * disp * a;
        // 越靠邊緣越清楚（折射進來的東西看得出形狀），中間才是模糊的；至少留一點輕模糊，邊緣的細節才不會閃
        sc *= 1.0 - 0.75 * exp(-u / (0.5 * bev));
        sc = max(sc, 0.1 * a * step(0.001, B.y));
        float2 base = h.c + (p - h.c) / lerp(1.0, h.mag, a);   // 整塊放大（凸透鏡）
        float ca = B.x * edge;
        col = float3(Look(base + off * (1.0 + ca), sc).r, Look(base + off, sc).g, Look(base + off * (1.0 - ca), sc).b);
    }

    float lb = Luma(Softer(p));   // 這一點後面有多亮：太亮就多壓暗（深色玻璃）、太暗就多加白霧（淺色玻璃）
    col *= 1.0 - saturate(D.w + MaskR.y * saturate((lb - 0.45) / 0.4)) * a;
    col = lerp(Luma(col).xxx, col, lerp(1.0, B.z, a));
    float leg = C.x * a;
    col = lerp(col * (1.0 - 0.15 * leg), lerp(col, float3(1.0, 1.0, 1.0), 0.45 * leg), style);
    col = lerp(col, float3(1.0, 1.0, 1.0), (B.w + MaskR.z * saturate((0.55 - lb) / 0.4)) * a * lerp(0.5, 1.0, style));
    col = lerp(col, h.tint.rgb * (0.45 + 0.9 * Luma(col)), h.tint.a * a);
    col = lerp(col, h.fill.rgb, h.fill.a * a);

    col = lerp(col, float3(1.0, 1.0, 1.0), 0.24 * h.energy * a);   // 按下：整塊被白光填滿
    col *= 1.0 + 0.10 * edge * edge * a * (1.0 - h.fill.a);         // 光在彎曲的邊緣被集中
    float3 env = Softer(p + n * 50.0);                              // 環境反光：越靠外緣越像鏡子
    col = lerp(col, env * 1.2 + 0.04, saturate(pow(edge, 3.0) * C.z * 0.6 * a));
    // 高光：外緣一條細亮線（左上、右下最亮），往內一點有很淡的反光
    float rim = exp(dn), inner = exp(dn / D.z);
    float ang = pow(abs(dot(n, float2(0.70710678, 0.70710678))), 1.5);
    float3 bg = Sharp(p);
    float3 hl = lerp(float3(1.0, 1.0, 1.0), saturate(lerp(Luma(bg).xxx, bg, 2.0)), 0.25);
    col += C.y * a * hl * (rim * (0.12 + 0.88 * ang) * 0.6 + inner * ang * 0.1) * lerp(1.0, 0.45, style);
    // 內陰影：上緣內側一條柔和的暗線（白色實心的旋鈕不畫）
    float io = D.y, du = Dist(p - float2(0.0, io));
    col *= 1.0 - D.x * smoothstep(-io, io * 0.3, du) * a * (1.0 - h.fill.a);
    // 淺色玻璃的外框是一圈細灰線（白線在淺色背景上看不出來）
    col = lerp(col, float3(0.42, 0.42, 0.42), exp(dn / 0.8) * 0.3 * style * a);
    // 按住：從手指下面亮起來，白光擴散到被按的那個元件（官方：不透明度約 0.24 的白光）
    float2 gp = p - Touch.xy;
    float glow = a * Touch.z * Size.w * exp(-dot(gp, gp) / (2.0 * Touch.w * Touch.w + 1.0)) * saturate(0.5 - SdBox(p - Mask.xy, Mask.zw, MaskR.x));
    col = lerp(col, float3(1.0, 1.0, 1.0), glow);
    // 出現 / 收起時玻璃本身跟著透明（看到的是真的背景）；不然會先露出抓來的那張半解析度背景，像素化
    float cover = saturate(0.5 - h.d) * a;
    return cover > 0.0 ? lerp(outside, float4(saturate(col), 1.0), cover) : outside;
}
";

        static Task<byte[]> compiled;

        // 這台電腦能不能用：Windows 10 2004 以後（面板才能不被自己抓到）、顯示卡支援 ps_3_0
        static bool Supported { get { return Environment.OSVersion.Version.Build >= 19041 && RenderCapability.IsPixelShaderVersionSupported(3, 0); } }

        // 程式啟動時先在背景編譯（打開面板時通常已經好了）；用不了的電腦不編譯
        public static void Prepare()
        {
            if (compiled == null && Supported) compiled = Task.Run(() => LiquidGlass.Compile(Encoding.UTF8.GetBytes(Source), "main", "ps_3_0"));
        }

        // 編譯好的著色器；用不了、編譯失敗是 null（面板退回毛玻璃）
        public static byte[] Shader
        {
            get
            {
                Prepare();
                if (compiled == null) return null;
                try { return compiled.Result; }
                catch { return null; }
            }
        }

        // 讓面板不會被螢幕截圖抓到（自己抓後面的畫面時才不會抓到自己）；不支援就回傳 false
        public static bool ExcludeFromCapture(IntPtr window)
        {
            return Native.SetWindowDisplayAffinity(window, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
        }

        readonly Grid content;
        readonly Canvas holder = new Canvas { IsHitTestVisible = false };   // 不佔版面（面板的大小由內容決定）
        readonly Image image = new Image { Stretch = Stretch.Fill };
        readonly Rectangle overlay = new Rectangle { IsHitTestVisible = false };
        readonly Canvas overlayHolder = new Canvas { IsHitTestVisible = false };   // 不佔版面
        readonly VisualBrush contentBrush;
        readonly GlassEffect bottom, top;

        // 抓畫面：在背景執行緒做（抓螢幕、縮小、模糊都不佔畫面執行緒，動畫才不會卡），做好了交給畫面執行緒貼上。
        // 抓的時候就縮成一半：玻璃裡面看到的幾乎都是模糊過的背景，清楚的那張只用在邊緣的高光，一半就夠了（要處理的量少四倍）
        IntPtr hwnd, memDC, dib, bits, oldBitmap;   // 以下到 temp 只在背景執行緒用
        int width, height, smallW, smallH;
        byte[] raw;
        float[] small, soft, softer, temp;
        long lastSum = -1;     // 上一次抓到的畫面（取樣的總和）：一樣就不用重算
        volatile int unchanged;   // 連續幾次抓到的都一樣
        int kicks;
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread worker;
        volatile bool capturing, pending, fresh;
        volatile int session;   // 第幾次打開（收起來又馬上打開時，上一次還沒貼上的舊畫面不要貼）
        int outSession;
        Dispatcher dispatcher;
        // 交給畫面執行緒的結果（pending 的時候背景執行緒不會動它）
        byte[] outSharp, outSoft, outSofter;
        int outW, outH, outSW, outSH;
        // 畫面執行緒用
        WriteableBitmap sharp, softBitmap, softerBitmap;

        // 深淺照 Windows（官方：選單、側欄這類大元件不會跟著背景在深淺之間翻轉）；背景哪裡太亮、太暗由著色器逐點處理
        public double Style;   // 0 深色、1 淺色
        int warm;              // 剛打開的幾格：第二層就算沒東西也畫一下（讓顯示卡先準備好，第一次按住開關、分段時才不會卡）

        // content：面板的內容（第一層玻璃畫在它最底下）；outer：包住 content 的那一層（第二層疊在 content 上面）
        public GlassBackdrop(Grid content, Grid outer, byte[] shaderCode)
        {
            this.content = content;
            var shader = new PixelShader();
            shader.SetStreamSource(new MemoryStream(shaderCode));
            bottom = new GlassEffect(shader);
            top = new GlassEffect(shader);
            image.Effect = bottom;
            holder.Children.Add(image);
            content.Children.Insert(0, holder);

            contentBrush = new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            overlay.Fill = contentBrush;
            overlay.Effect = top;
            overlay.HorizontalAlignment = HorizontalAlignment.Left;
            overlay.VerticalAlignment = VerticalAlignment.Top;
            overlayHolder.Children.Add(overlay);
            outer.Children.Add(overlayHolder);
            content.SizeChanged += delegate
            {
                double w = content.ActualWidth, h = content.ActualHeight;
                image.Width = w; image.Height = h;
                overlay.Width = w; overlay.Height = h;
                contentBrush.Viewbox = new Rect(0, 0, w, h);
                contentBrush.ViewboxUnits = BrushMappingMode.Absolute;
            };
        }

        public void Attach(IntPtr window) { hwnd = window; }

        // 螢幕縮放倍率（透鏡放大取樣要知道一個像素多大）
        double DeviceScale
        {
            get
            {
                var source = PresentationSource.FromVisual(content);
                return source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
            }
        }

        // 面板打開：開始在背景抓後面的畫面
        public void Start()
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            lock (gate)
            {
                capturing = true;
                session++;
                fresh = true;   // 第一張一定要貼上（就算跟收起來前的一樣）
                warm = 3;
                if (worker != null) return;   // 還在跑（包括正要結束的）：它看到 capturing 又變回 true 就會繼續
                worker = new Thread(Run) { IsBackground = true, Name = "Glass" };
                worker.Start();
            }
        }

        // 每一格開始時叫背景執行緒馬上抓一次（上一張還沒貼上就跳過）
        public void Kick()
        {
            if (unchanged > 10 && (++kicks & 1) == 1) return;   // 後面一陣子都沒變（靜止的視窗）：隔一格抓就好
            if (capturing && !pending) wake.Set();
        }

        // 面板收起來：停止抓畫面、把記憶體還回去
        public void Stop()
        {
            capturing = false;
            wake.Set();
            image.Source = null;
            sharp = softBitmap = softerBitmap = null;
            // 下次打開時，抓到第一張背景之前兩層玻璃都先藏起來（不然第一格沒有背景，玻璃會先閃一片黑）
            holder.Visibility = Visibility.Hidden;
            overlayHolder.Visibility = Visibility.Hidden;
        }

        // 不用了：兩層玻璃都拿掉
        public void Remove()
        {
            Stop();
            var a = holder.Parent as Panel;
            if (a != null) a.Children.Remove(holder);
            var b = overlayHolder.Parent as Panel;
            if (b != null) b.Children.Remove(overlayHolder);
        }

        // ---------- 畫一層玻璃 ----------
        // mask：白光只亮在這一塊裡（被按的元件；空的就不亮），maskRadius 是它的圓角
        public void Draw(bool upper, List<GlassShape> shapes, GlassLook look, Point touch, double glow, double glowRadius, Rect mask, double maskRadius, double glowStrength)
        {
            var e = upper ? top : bottom;
            int n = Math.Min(MaxShapes, shapes.Count);
            if (upper)
            {
                // 第二層沒有東西就整層不畫（不用把整個面板再畫一次；現在工具列一直在，平常都有；剛打開的幾格先暖機）
                bool warming = warm > 0;
                if (warming) warm--;
                var v = n > 0 || warming ? Visibility.Visible : Visibility.Hidden;
                if (overlay.Visibility != v) overlay.Visibility = v;
                if (n == 0 && !warming) return;
            }
            for (int i = 0; i < n; i++)
            {
                var s = shapes[i];
                e.Set(i, s.X, s.Y, s.HalfW, s.HalfH);
                e.Set(12 + i, s.Radius, s.Appear, s.Energy, s.Thick);
                e.Set(24 + i, s.TintR, s.TintG, s.TintB, s.TintA);
                e.Set(36 + i, s.FillR, s.FillG, s.FillB, s.FillA);
                e.Set(48 + i, s.D00, s.D01, s.D01, s.D11);
                e.Set(60 + i, s.Mag, s.Shadow, 0, 0);
                e.Set(72 + i, s.PullX, s.PullY, s.PullAmount, s.PullRadius);
            }
            // 沒用到的放到很遠的地方：編譯出來的迴圈不一定會在第 n 塊停下（實測會跑滿 12 塊），
            // 全 0 的變形矩陣會讓距離處處是 0，整片變成玻璃
            for (int i = n; i < MaxShapes; i++)
            {
                e.Set(i, -1e4, -1e4, 0, 0);
                e.Set(12 + i, 0, 0, 0, 1);
                e.Set(48 + i, 1, 0, 0, 1);
                e.Set(60 + i, 1, 0, 0, 0);
                e.Set(72 + i, 0, 0, 0, 0);
            }
            e.Set(84, n, 0, look.Refract, look.Bevel);
            e.Set(85, look.CA, look.Scatter, look.Sat, look.Fog);
            e.Set(86, look.Legible, look.Spec, look.Reflect, look.Merge);
            e.Set(87, look.InnerA, look.InnerR, look.Inner, look.Dim);
            e.Set(88, content.ActualWidth, content.ActualHeight, Style, glowStrength);
            e.Set(89, touch.X, touch.Y, glow, glowRadius);
            if (mask.IsEmpty) e.Set(90, -1e4, -1e4, 0, 0);
            else e.Set(90, mask.X + mask.Width / 2, mask.Y + mask.Height / 2, mask.Width / 2, mask.Height / 2);
            e.Set(91, mask.IsEmpty ? 0 : maskRadius, look.DimAdapt, look.FogAdapt, DeviceScale);
        }

        // ---------- 抓玻璃後面的畫面（背景執行緒）----------
        void Run()
        {
            while (true)
            {
                while (capturing)
                {
                    if (!pending && hwnd != IntPtr.Zero) { try { Capture(); } catch { } }
                    // 跟著畫面的每一格抓（面板每一格都會叫 Kick）：後面一動，下一格玻璃就跟上，不會慢一拍。
                    // 萬一沒人叫（例如畫面暫停更新），最多也每 0.1 秒抓一次
                    wake.WaitOne(100);
                }
                lock (gate)
                {
                    if (capturing) continue;
                    worker = null;
                    Free();
                    raw = null; small = soft = softer = temp = null; outSharp = outSoft = outSofter = null;
                    return;
                }
            }
        }

        void Capture()
        {
            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return;
            int fullW = r.Right - r.Left, fullH = r.Bottom - r.Top;
            if (fullW <= 1 || fullH <= 1) return;
            int w = fullW / 2, h = fullH / 2;
            int gen = session;
            if (w != width || h != height || raw == null) Allocate(w, h);

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            Native.SetStretchBltMode(memDC, 3 /* COLORONCOLOR：直接取點，最快 */);
            Native.StretchBlt(memDC, 0, 0, w, h, screen, r.Left, r.Top, fullW, fullH, 0x00CC0020 /* SRCCOPY */ | 0x40000000 /* CAPTUREBLT */);
            Native.ReleaseDC(IntPtr.Zero, screen);
            Native.GdiFlush();
            Marshal.Copy(bits, raw, 0, raw.Length);
            // 後面有沒有變：整張平均每 4 × 4 取一點比（以前沿著斜線跳著取，太稀疏，
            // 像系統匣的隱藏圖示框縮回去那種只變一小塊的會漏掉，玻璃裡就一直留著舊的畫面）
            long check = 17;
            for (int y = 1; y < h; y += 4)
                for (int x = 1, k = (y * w + 1) * 4; x < w; x += 4, k += 16)
                    check = check * 31 + raw[k] + (raw[k + 1] << 8) + (raw[k + 2] << 16);
            if (check == lastSum && !fresh) { unchanged++; return; }   // 後面沒變：不用重算

            // 再縮一半（原本的四分之一，每 2 × 2 平均）
            for (int y = 0; y < smallH; y++)
                for (int x = 0; x < smallW; x++)
                {
                    float b = 0, g = 0, rr = 0;
                    for (int j = 0; j < 2; j++)
                        for (int i = 0; i < 2; i++)
                        {
                            int sx = Math.Min(w - 1, x * 2 + i), sy = Math.Min(h - 1, y * 2 + j), k = (sy * w + sx) * 4;
                            b += raw[k]; g += raw[k + 1]; rr += raw[k + 2];
                        }
                    int o = (y * smallW + x) * 3;
                    small[o] = b / 4; small[o + 1] = g / 4; small[o + 2] = rr / 4;
                }
            Box(small, soft, 1); Box(soft, soft, 1);                                  // 輕模糊
            Box(soft, softer, 3); Box(softer, softer, 3); Box(softer, softer, 3);   // 重模糊

            Buffer.BlockCopy(raw, 0, outSharp, 0, raw.Length);
            Pack(soft, outSoft);
            Pack(softer, outSofter);
            outW = w; outH = h; outSW = smallW; outSH = smallH; outSession = gen;
            pending = true;
            // 交出去了才記下這張（中途出錯的話下一次還會重做，不會卡在「沒變」）
            fresh = false;
            lastSum = check;
            unchanged = 0;
            var d = dispatcher;
            if (d != null) d.BeginInvoke(DispatcherPriority.Render, new Action(Apply));   // 用最高的畫面優先權貼上，趕上下一格
        }

        // 畫面執行緒：把背景執行緒做好的圖貼上，更新深淺
        void Apply()
        {
            if (!pending) return;
            try { Paste(); }
            finally { pending = false; wake.Set(); }   // 貼的時候出錯也要放背景執行緒繼續抓（不然它會一直等）
        }

        void Paste()
        {
            if (outSharp == null || outSoft == null || outSofter == null) return;   // 背景執行緒已經收工、記憶體還回去了
            if (capturing && outSession != session) { fresh = true; lastSum = -1; return; }   // 上一次打開時抓的（收起來又馬上打開）：不貼，下一張重抓
            if (capturing)
            {
                if (sharp == null || sharp.PixelWidth != outW || sharp.PixelHeight != outH)
                {
                    sharp = new WriteableBitmap(outW, outH, 96, 96, PixelFormats.Bgr32, null);
                    image.Source = sharp;
                }
                if (softBitmap == null || softBitmap.PixelWidth != outSW || softBitmap.PixelHeight != outSH)
                {
                    softBitmap = new WriteableBitmap(outSW, outSH, 96, 96, PixelFormats.Bgr32, null);
                    softerBitmap = new WriteableBitmap(outSW, outSH, 96, 96, PixelFormats.Bgr32, null);
                    bottom.BlurA = top.BlurA = new ImageBrush(softBitmap);
                    bottom.BlurB = top.BlurB = new ImageBrush(softerBitmap);
                }
                sharp.WritePixels(new Int32Rect(0, 0, outW, outH), outSharp, outW * 4, 0);
                if (holder.Visibility != Visibility.Visible) { holder.Visibility = Visibility.Visible; overlayHolder.Visibility = Visibility.Visible; }   // 第一張背景到了
                softBitmap.WritePixels(new Int32Rect(0, 0, outSW, outSH), outSoft, outSW * 4, 0);
                softerBitmap.WritePixels(new Int32Rect(0, 0, outSW, outSH), outSofter, outSW * 4, 0);
            }
        }

        void Allocate(int w, int h)
        {
            Free();
            width = w; height = h;
            memDC = Native.CreateCompatibleDC(IntPtr.Zero);
            var info = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            dib = Native.CreateDIBSection(IntPtr.Zero, ref info, 0, out bits, IntPtr.Zero, 0);
            oldBitmap = Native.SelectObject(memDC, dib);
            raw = new byte[w * h * 4];
            outSharp = new byte[raw.Length];
            smallW = Math.Max(1, (w + 1) / 2); smallH = Math.Max(1, (h + 1) / 2);
            small = new float[smallW * smallH * 3];
            soft = new float[small.Length];
            softer = new float[small.Length];
            temp = new float[small.Length];
            outSoft = new byte[smallW * smallH * 4];
            outSofter = new byte[outSoft.Length];
        }

        void Free()
        {
            if (memDC != IntPtr.Zero)
            {
                Native.SelectObject(memDC, oldBitmap);
                Native.DeleteDC(memDC);
                memDC = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; }
            width = height = 0;
        }

        static void Pack(float[] src, byte[] dst)
        {
            for (int i = 0, j = 0; i < src.Length; i += 3, j += 4)
            {
                dst[j] = (byte)src[i]; dst[j + 1] = (byte)src[i + 1]; dst[j + 2] = (byte)src[i + 2]; dst[j + 3] = 255;
            }
        }

        // 方框模糊（左右一次、上下一次；做兩三次就接近高斯）。src 和 dst 可以是同一個
        void Box(float[] src, float[] dst, int radius)
        {
            int w = smallW, h = smallH;
            float scale = 1f / (radius * 2 + 1);
            for (int y = 0; y < h; y++)
                for (int c = 0; c < 3; c++)
                {
                    float acc = 0;
                    for (int i = -radius; i <= radius; i++) acc += src[(y * w + Math.Max(0, Math.Min(w - 1, i))) * 3 + c];
                    for (int x = 0; x < w; x++)
                    {
                        temp[(y * w + x) * 3 + c] = acc * scale;
                        acc += src[(y * w + Math.Min(w - 1, x + radius + 1)) * 3 + c] - src[(y * w + Math.Max(0, x - radius)) * 3 + c];
                    }
                }
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    float acc = 0;
                    for (int i = -radius; i <= radius; i++) acc += temp[(Math.Max(0, Math.Min(h - 1, i)) * w + x) * 3 + c];
                    for (int y = 0; y < h; y++)
                    {
                        dst[(y * w + x) * 3 + c] = acc * scale;
                        acc += temp[(Math.Min(h - 1, y + radius + 1) * w + x) * 3 + c] - temp[(Math.Max(0, y - radius) * w + x) * 3 + c];
                    }
                }
        }
    }

    // 一塊玻璃（面板的單位，y 往下）
    class GlassShape
    {
        public double X, Y, HalfW, HalfH, Radius, Appear = 1, Energy, Thick = 1;
        public double TintR, TintG, TintB, TintA;
        public double FillR = 1, FillG = 1, FillB = 1, FillA;   // 實心填色（目前都用 WPF 畫實心，玻璃只有透鏡）
        public double D00 = 1, D01, D11 = 1;   // 果凍變形的反矩陣（對稱）
        public double Mag = 1, Shadow;
        public double PullX, PullY, PullAmount, PullRadius;
    }

    // 一層玻璃的質感（數值跟原型面板上的「官方 Regular」一樣）
    class GlassLook
    {
        public double Refract = 0.9, Bevel = 20, CA = 0.06, Scatter = 0.86, Sat = 1.5, Fog = 0.3;
        public double Legible = 0.3, Spec = 0.85, Reflect = 0.3, Merge = 6, InnerA = 0.06, InnerR = 8, Inner = 6, Dim = 0;
        public double DimAdapt, FogAdapt;   // 後面太亮的地方多壓暗多少、太暗的地方多加多少白霧

        // 面板：邊緣折射跟鎖定畫面的卡片一樣（往外把面板外面的東西壓進來）。面板的厚度是 0.7（GlassMotion），
        // 最多位移約 1.4 × 0.5 × 50 × 0.7 ≈ 25：大過整塊放大往內縮的量（左右約 19），才拉得到外面的東西；也還在視窗四周留給玻璃取樣的 36 以內
        public static readonly GlassLook Base = new GlassLook { Refract = 1.4, Bevel = 50, CA = 0.022 };

        // 第二層（開關、分段控制按住時的透鏡；照 iPhone 實機）：Refract 負的 = 透鏡模式（邊緣往外取樣）；
        // 中間完全清澈：不糊、沒有霧、沒有填色、沒有內陰影、沒有環境反光，外面也沒有光暈；只有一圈細亮邊和沿邊緣的色散
        public static readonly GlassLook Top = new GlassLook { Refract = -1, CA = 0.35, Scatter = 0, Sat = 1.0, Fog = 0, Legible = 0, Reflect = 0, InnerA = 0, Inner = 0.6, Dim = 0 };

        public void CopyFrom(GlassLook o)
        {
            Refract = o.Refract; Bevel = o.Bevel; CA = o.CA; Scatter = o.Scatter; Sat = o.Sat; Fog = o.Fog;
            Legible = o.Legible; Spec = o.Spec; Reflect = o.Reflect; Merge = o.Merge; InnerA = o.InnerA; InnerR = o.InnerR; Inner = o.Inner; Dim = o.Dim;
            DimAdapt = o.DimAdapt; FogAdapt = o.FogAdapt;
        }
    }

    // 玻璃的像素著色器：常數暫存器 c0～c91 都是 float4（照 Source 裡的排法）
    class GlassEffect : ShaderEffect
    {
        const int Registers = 92;
        static readonly DependencyProperty[] Constants = new DependencyProperty[Registers];
        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(GlassEffect), 0);
        public static readonly DependencyProperty BlurAProperty = RegisterPixelShaderSamplerProperty("BlurA", typeof(GlassEffect), 1);
        public static readonly DependencyProperty BlurBProperty = RegisterPixelShaderSamplerProperty("BlurB", typeof(GlassEffect), 2);
        readonly Point4D[] values = new Point4D[Registers];

        static GlassEffect()
        {
            for (int i = 0; i < Registers; i++)
                Constants[i] = DependencyProperty.Register("C" + i, typeof(Point4D), typeof(GlassEffect),
                    new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(i)));
        }

        public GlassEffect(PixelShader shader)
        {
            PixelShader = shader;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(BlurAProperty);
            UpdateShaderValue(BlurBProperty);
            for (int i = 0; i < Registers; i++) UpdateShaderValue(Constants[i]);
        }

        public Brush BlurA { get { return (Brush)GetValue(BlurAProperty); } set { SetValue(BlurAProperty, value); } }
        public Brush BlurB { get { return (Brush)GetValue(BlurBProperty); } set { SetValue(BlurBProperty, value); } }

        // 變了才設（每一格都會叫；沒變就不用讓 WPF 重畫）
        public void Set(int register, double x, double y, double z, double w)
        {
            var v = new Point4D(x, y, z, w);
            if (values[register] == v) return;
            values[register] = v;
            SetValue(Constants[register], v);
        }
    }
}
