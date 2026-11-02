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
    // 另外一種是玻璃字（鎖定畫面的時鐘數字）：形狀不是圓角長方形，而是一張字形距離圖（TextGlass）
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
            public float Touch2X, Touch2Y, Touch2Glow, Touch2Radius;   // 上一次按的白光還在退（很快又按一下時，新的從手指重新擴散、舊的照常退）
            // 1：卡片的液態玻璃：中央輕輕糊（Scatter）、鮮一點（Sat）、一層很淡帶環境色調的光（TintAmt），不放大；
            // 外圍一圈（Bevel）像液體的彎月面往外取樣、把外面的東西壓進來（Refract；上下直邊和圓角打折），燈經過那圈會聚光變亮，最外緣一點色散（CA）；
            // 約 1 像素的細高光（上緣和左上最亮）、內側淡淡提亮、最外緣映著環境的細線（Spec 是強度）；左上的環境反光、很淡的玻璃厚度
            public float Clear;
            // 卡片的散射跟著背景調（見著色器卡片那一段）：Scatter 是背景平順時中央的量，ScatterBusy 是背景很雜（燈、輪廓、細碎的紋理）時，
            // ScatterRim 是靠邊那一圈（沒有字的地方，透明一點、折射看得清楚）。卡片的散射是四段：0 清楚、1/3 微糊、2/3 輕模糊、1 重模糊
            public float ScatterBusy, ScatterRim;

            // 第二層（卡片上的小玻璃圓鈕）：底下已經是玻璃了，不再糊、不加白霧，跟旁邊的也不黏在一起；
            // 折射輕一點（0.3）：圓鈕只有 14 像素，折射帶不到 2 像素寬，位移太大會把圓的抗鋸齒邊壓到不到半個像素，邊緣變成一格一格的
            public static Look OnTop(float scale)
            {
                return new Look { Scale = scale, Scatter = 0, Sat = 1.05f, TintAmt = 0, Legible = 0, Merge = 0, Refract = 0.3f };
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
    float4 E;          // 清澈的卡片玻璃（1）、背景很雜時中央的散射、靠邊的散射、（不用）
    float4 Touch2;     // 上一次按的白光還在退：位置 xy、亮度 z、半徑 w（跟 Touch 取亮的那個，不會疊得更亮）
};
cbuffer PassInfo : register(b1)
{
    float4 Src;        // 縮小：來源左上角 xy、來源大小 zw；模糊：zw = 1 / 小圖大小
    float4 Dir;        // 模糊的方向（小圖的 uv）
};
Texture2D SceneTex : register(t0);
Texture2D BlurATex : register(t1);
Texture2D BlurBTex : register(t2);
Texture2D FieldTex : register(t3);   // 玻璃字的字形距離圖（離字的邊多遠，像素，字裡面是負的）
Texture2D OldFieldTex : register(t4);   // 換分鐘的動畫：舊時間的距離圖（畫離開的字）
Texture2D BlurMTex : register(t5);   // 微糊（只有卡片用）：清楚和輕模糊之間的一級，散射才是一路慢慢變糊，不是清楚的圖疊在糊的圖上
cbuffer ClockAnim : register(b2)     // 換分鐘的動畫（沒有動畫時 Anim0.x 是 0）
{
    float4 Anim0;      // 幾格、數字頂端再往上一點（以上是日期那一行，照常畫；畫面座標）、舊距離圖的左上角 xy
    float4 Anim1;      // 舊距離圖的大小 xy
    float4 Slot[30];   // 每格三個：左右邊界、用哪張圖（0 新、1 舊）、（不用）；中心 xy、遮罩的邊（畫面 y）、漸層寬；
                       //   現在的左右位移、看得到哪一邊（1：邊以下；−1：邊以上）、（不用）
};
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

struct Hit { float d; float2 c; float appear, style, thick, mag, shadow, energy, focus, flat, corn; float4 tint, fill; };

// 圓角長方形到邊的距離。圓角是 Apple 的連續曲率圓角：不是直邊接一段四分之一圓（曲率在接縫突然跳變，看起來有點硬），
// 而是從離角 1.5 倍圓角的地方就慢慢開始彎的四次方超橢圓（x⁴ + y⁴ = 1；45° 那一點跟一般圓角差不多，轉角更柔）。
// 超橢圓的值不是真正的距離，除以它的梯度長度修正回像素（邊緣的折射、亮線寬度才準）。
// 圓、膠囊（圓角佔滿短邊，1.5 倍放不下）照原本的四分之一圓
float SdBox(float2 p, float2 b, float r)
{
    r = min(r, min(b.x, b.y));
    float rc = 1.5 * r;
    if (rc <= min(b.x, b.y) - 1.0)
    {
        float2 q = abs(p) - b + rc;
        float2 m = max(q, 0.0), m2 = m * m;
        float l4 = sqrt(sqrt(dot(m2, m2)));
        float gl = l4 > 1e-3 ? length(m2 * m) / (l4 * l4 * l4) : 1.0;
        return (l4 - rc) / gl + min(max(q.x, q.y), 0.0);
    }
    float2 q = abs(p) - b + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// x：這一點的折射要打折多少（0～1）：只有靠上下兩條直邊的地方打折（背景的字是橫的，上下邊緣最容易被拉長）；
// 靠左右邊、圓角裡保持全強度。用玻璃自己的座標判斷：離上下邊比離左右邊近就算「上下」，沿著 45° 那條線平順過渡（不會有折痕）
// y：在圓角裡多深（0～1）。圓角外面一大片要壓進一小段弧裡（往外、沿著弧兩個方向都被壓縮），背景會被擠成雜亂的小點
float2 FlatOf(int i, float2 p)
{
    float2 q = p - Shape[i].xy;
    float4 m = Def[i];
    q = float2(m.x * q.x + m.y * q.y, m.z * q.x + m.w * q.y);
    float2 b = Shape[i].zw;
    float r = min(Param[i].x, min(b.x, b.y));
    float2 over = abs(q) - (b - r);
    float corner = smoothstep(0.0, max(0.6 * r, 1e-3), min(over.x, over.y));   // 圓角裡（慢慢地接到直邊，圓角裡不會多出一道弧）
    float2 gap = b - abs(q);                                              // 離左右邊、上下邊多遠
    float k = 14.0 * A.y;
    return float2(smoothstep(-k, k, gap.x - gap.y) * (1.0 - corner), corner);
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
    h.d = 1e5; h.c = float2(0.0, 0.0); h.appear = 0.0; h.style = 0.0; h.thick = 1.0; h.mag = 1.0; h.shadow = 0.0; h.energy = 0.0; h.focus = 0.0; h.flat = 0.0; h.corn = 0.0;
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
        float2 fc = Extra[i].w > 0.0 ? FlatOf(i, p) : float2(0.0, 0.0);   // 不用打折的（Focus 0）不用算
        h.flat = lerp(h.flat, fc.x, w); h.corn = lerp(h.corn, fc.y, w);
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

// 只留色相：除以最亮的那個通道（除以亮度的話，藍色這種亮度很低的顏色會被放大到綠色通道也滿，偏成青色）
float3 Hue(float3 c) { return c / max(max(c.r, max(c.g, c.b)), 0.02); }

float3 Look(float2 p, float s)   // 在清楚、輕模糊、重模糊之間取
{
    float3 a = SceneTex.SampleLevel(Lin, (p - SceneRect.xy) / SceneRect.zw, 0).rgb;
    float2 bu = (p - BlurRect.xy) / BlurRect.zw;
    float3 b = BlurATex.SampleLevel(Lin, bu, 0).rgb, c = BlurBTex.SampleLevel(Lin, bu, 0).rgb;
    return s < 0.5 ? lerp(a, b, s * 2.0) : lerp(b, c, s * 2.0 - 1.0);
}

// 卡片的散射：清楚 → 微糊（1080p 約 6 像素）→ 輕模糊（約 21）→ 重模糊（約 50），s 是 0、1/3、2/3、1，中間連續地混
float3 LookCard(float2 p, float s)
{
    float3 a = SceneTex.SampleLevel(Lin, (p - SceneRect.xy) / SceneRect.zw, 0).rgb;
    float2 bu = (p - BlurRect.xy) / BlurRect.zw;
    float3 m = BlurMTex.SampleLevel(Lin, bu, 0).rgb, b = BlurATex.SampleLevel(Lin, bu, 0).rgb, c = BlurBTex.SampleLevel(Lin, bu, 0).rgb;
    float t = saturate(s) * 3.0;
    return t < 1.0 ? lerp(a, m, t) : t < 2.0 ? lerp(m, b, t - 1.0) : lerp(b, c, t - 2.0);
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

    if (E.x > 0.5)   // 卡片的液態玻璃（見 Look.Clear）
    {
        // 邊：外圍一圈（bev，1080p 約 45 像素）像液體的彎月面往外取樣，把外面的東西壓進來——
        // 位移 = Refract × bev × t²（t：1 在邊上、0 在折射帶的內緣），內緣斜率 0、平順地接到中央，最外緣約 40 像素；上下直邊打折（Focus，背景的字不會被拉長），
        // 圓角加強約 30%（卡片的 Focus 0.45 × 0.7；曲面在圓角裡轉得最快，厚玻璃的圓角本來就折得最明顯；方向跟著邊的法線連續地轉，整個圓角是一片連續的曲面）
        float cu = max(-h.d, 0.0);
        float2 nn = n / max(length(n), 1e-4);
        float bev = max(A.w * s * h.thick, 1e-3);
        float ct = 1.0 - saturate(cu / bev);
        float cdisp = A.z * bev * ct * ct * (1.0 - h.focus * (h.flat - 0.7 * h.corn));
        float2 coff = n * cdisp * a;
        // 色差只在最外緣；上下直邊（背景的字、燈是橫的，會拉成彩色的條紋）少一半，圓角裡壓得最緊，幾乎拿掉（不然會有彩色的雜點）
        float cca = B.x * ct * ct * ct * (1.0 - 0.5 * h.flat) * (1.0 - 0.95 * h.corn);
        // 散射（以文字好讀為先，但不是整張一樣糊）：
        //   背景有多雜：輕模糊和重模糊差多少（中等尺度的東西：燈、物件的輪廓、紋理——會干擾字的就是這些；更細的在輕模糊裡已經抹掉了），
        //   在周圍 3 × 3 點（間隔 40）平均，在卡片上是很大範圍慢慢變的，不會一塊一塊；
        //   中央（有字的地方）照背景多雜在 Scatter 和 ScatterBusy 之間；靠邊那一圈（沒有字）透明一點（ScatterRim，微糊附近），
        //   約 17 像素平順地接到中央（兩側的時間離邊 16，已經在中央的糊度裡）。最外緣壓得很緊的那幾像素、圓角裡還是糊一點（壓縮的背景才不會變成鋸齒、碎點）
        float cbusy = 0.0;
        [unroll] for (int bi = -1; bi <= 1; bi++)
            [unroll] for (int bj = -1; bj <= 1; bj++)
            {
                float2 bq = (p + float2(bi, bj) * 40.0 * s - BlurRect.xy) / BlurRect.zw;
                float3 bd = BlurATex.SampleLevel(Lin, bq, 0).rgb - BlurBTex.SampleLevel(Lin, bq, 0).rgb;
                cbusy += abs(Luma(bd)) + 0.35 * length(bd - Luma(bd));
            }
        cbusy = smoothstep(0.012, 0.07, cbusy / 9.0);
        float ccore = lerp(B.y, E.y, cbusy);
        float csc = lerp(E.z, ccore, smoothstep(1.5 * s, 0.38 * bev, cu)) * a;
        csc = max(csc, 0.4 * h.corn * sqrt(h.corn) * ct * a);           // 圓角裡糊一點（慢慢進來；壓進圓角的東西不會留下一塊直邊的楔形；太糊就看不出折射）
        csc = max(csc, 0.6 * a * exp(-cu / (0.22 * bev)));              // 最靠邊壓得很緊（約 10 像素）
        // 紅藍分開不到 0.12 像素就看不出來：只取一次（卡片大部分的地方），省下兩次 LookCard（各 4 次取樣）
        float3 ccol;
        [branch] if (cca * length(coff) > 0.12)
            ccol = float3(LookCard(p + coff * (1.0 + cca), csc).r, LookCard(p + coff, csc).g, LookCard(p + coff * (1.0 - cca), csc).b);
        else
            ccol = LookCard(p + coff, csc);
        // 背景裡零星的小亮點（雪粒、燈珠）透過玻璃會變成刺刺的顆粒：亮度上限跟著輕糊的那張走，大片的亮東西不受影響、中央也不會變霧
        float3 clo = BlurATex.SampleLevel(Lin, (p + coff - BlurRect.xy) / BlurRect.zw, 0).rgb;
        ccol = min(ccol, clo * 1.12 + 0.06);
        // 很亮的地方（玻璃後面的燈、亮的招牌）：亮度柔和地壓下來（0.5 以上壓成 d / (1 + 2.2d)，一路單調，燈團裡不會有一圈平平的環），
        // 只壓亮度、保留顏色——燈還是一團有顏色的光，不是往周圍的平均色混（那樣整張會糊成一片棕色）。
        // 外圍折射那圈從 0.35 就開始壓：外面一大片的燈被壓進那一小圈，亮度會集中成一塊（暖色的燈在角落變成一團橘光），壓早一點才會散開
        float cl0 = Luma(ccol), cover0 = max(cl0 - (0.5 - 0.15 * ct * ct), 0.0);
        ccol *= lerp(1.0, (cl0 - cover0 + cover0 / (1.0 + 2.2 * cover0)) / max(cl0, 1e-3), a);
        // 外圍那圈裡比周圍（最糊的那張）亮出一截的光點：只在那圈裡（t，往內約 45 像素慢慢放掉）把多出來的亮度柔和地收一點（d / (1 + 6d)，只動亮度），
        // 燈經過邊上是一段往兩側、往內慢慢淡掉的光，不是一團集中的亮斑；中央不受影響
        float3 cwide = BlurBTex.SampleLevel(Lin, (p + coff - BlurRect.xy) / BlurRect.zw, 0).rgb;
        float cl1 = Luma(ccol), cpk = max(cl1 - Luma(cwide) - 0.03, 0.0);
        ccol *= lerp(1.0, (cl1 - cpk + cpk / (1.0 + 6.0 * cpk)) / max(cl1, 1e-3), ct * a);
        // 透過玻璃的顏色鮮一點（Sat；靠飽和度通透，不靠白霧）。本來就很鮮豔的顏色（霓虹、純紅、純藍）少加一點，
        // 不然通道會被截在 0 或 1，漸層變成一塊塊平的色塊、色相也會偏（Apple 的 vibrancy 也是主要提亮灰淡的內容）。
        // 暗的地方不加、反而收一點：燈和暗部散射在一起是暗的橘色（就是棕色），暗部偏中性，看起來才是暗的玻璃、不是一層棕色的膜
        float cchroma = max(ccol.r, max(ccol.g, ccol.b)) - min(ccol.r, min(ccol.g, ccol.b));
        float csat = lerp(B.z, 1.0, smoothstep(0.2, 0.6, cchroma));
        csat = lerp(0.82, csat, smoothstep(0.1, 0.42, Luma(ccol)));
        ccol = saturate(lerp(Luma(ccol).xxx, ccol, lerp(1.0, csat, a)));
        // 霧（TintAmt）：一小部分是均勻的淡光（帶一點附近背景的色調）；大部分是周圍的光在玻璃裡散開——最糊的那張用 screen 疊，
        // 亮的地方多、暗的地方少、顏色跟著底下的環境，所以霧本身就有冷暖、明暗的層次，不是一整片一樣的乳白（暗的桌布上霧比較少，深色更有深度）
        float3 cenv = BlurBTex.SampleLevel(Lin, (p - BlurRect.xy) / BlurRect.zw, 0).rgb;
        float cL = Luma(cenv);
        float3 ctone = lerp(float3(1.0, 1.0, 1.0), saturate(Hue(cenv) * 0.85 + 0.15), 0.15);
        ccol = lerp(ccol, ctone, 0.55 * B.w * a);
        ccol = 1.0 - (1.0 - ccol) * (1.0 - saturate(lerp(cL.xxx, cenv, 0.3) * 1.5 + 0.02) * B.w * a);   // 顏色只帶三成：霧不會把整片染得更鮮
        float2 ctop = Shape[0].xy - Shape[0].zw;
        float2 cq = (p - ctop) / max(2.0 * Shape[0].zw, 1.0);   // 卡片裡的位置（左上 0、右下 1；卡片是第一塊）
        // 環境的色溫：卡片外面左、右、上、下四點（最糊的那張，邊外 36 像素）照卡片裡的位置平順地內插，
        // 像白平衡一樣乘上去（只偏色溫、亮度不變，最多 9%）——暖的燈那一側微暖、冷的夜景那一側微冷；
        // 用乘的，玻璃後面本來的明暗、顏色變化都保留（往同一個顏色混的話會把中央抹成一片）；不會變亮
        float2 cmid = Shape[0].xy, chalf = Shape[0].zw + 36.0 * s;
        float3 caL = BlurBTex.SampleLevel(Lin, (float2(cmid.x - chalf.x, cmid.y) - BlurRect.xy) / BlurRect.zw, 0).rgb;
        float3 caR = BlurBTex.SampleLevel(Lin, (float2(cmid.x + chalf.x, cmid.y) - BlurRect.xy) / BlurRect.zw, 0).rgb;
        float3 caT = BlurBTex.SampleLevel(Lin, (float2(cmid.x, cmid.y - chalf.y) - BlurRect.xy) / BlurRect.zw, 0).rgb;
        float3 caB = BlurBTex.SampleLevel(Lin, (float2(cmid.x, cmid.y + chalf.y) - BlurRect.xy) / BlurRect.zw, 0).rgb;
        float3 camb = 0.5 * (lerp(caL, caR, cq.x) + lerp(caT, caB, cq.y)), cavg = 0.25 * (caL + caR + caT + caB);
        // 跟四點的平均比（只看這一側比整體偏暖還是偏冷）：整片都是暖色的桌布不會再往暖色推（那就是一層濾鏡），
        // 一側是暖燈、一側是冷色的夜景才會分出冷暖
        // （每個通道先加一點底再相除：純色、全黑的環境比值才會是 1，不會把某個通道放大好幾倍；倍率再夾在 0.9～1.15）
        float3 crel = (camb + 0.03) / (cavg + 0.03);
        ccol = saturate(ccol * clamp(lerp(float3(1.0, 1.0, 1.0), crel / max(Luma(crel), 0.05), 0.09 * a), 0.9, 1.15));
        // 中央的環境色：玻璃後面這一帶（最糊的那張，約 50 像素的範圍）比整張卡片後面的平均色（卡片裡五點）偏哪個顏色，就在玻璃裡再偏一點
        // （燈那邊微暖、冷色的背景那邊微冷，過渡跟著最糊的那張一樣平順）。大家共有的那層色調不動（只在中央收的話，單色的桌布上中央會比外圈灰、看得出一個框）。
        // 只動色度、亮度不變（色度的亮度是 0），整體飽和度也不提高——同樣是深色霧面，只是顏色有空間上的深淺，不是一層均勻的棕。
        // 只在中央（外圍那圈有自己的折射、反光）；色度照這一點跟後面那一帶的亮度比例縮（暗的灰色上不會憑空推出互補色），很暗的地方再少一點
        float2 cio = Shape[0].zw * float2(0.6, 0.5);
        float3 cin = 0.2 * (BlurBTex.SampleLevel(Lin, (cmid - BlurRect.xy) / BlurRect.zw, 0).rgb
            + BlurBTex.SampleLevel(Lin, (cmid + cio - BlurRect.xy) / BlurRect.zw, 0).rgb + BlurBTex.SampleLevel(Lin, (cmid - cio - BlurRect.xy) / BlurRect.zw, 0).rgb
            + BlurBTex.SampleLevel(Lin, (cmid + float2(cio.x, -cio.y) - BlurRect.xy) / BlurRect.zw, 0).rgb + BlurBTex.SampleLevel(Lin, (cmid + float2(-cio.x, cio.y) - BlurRect.xy) / BlurRect.zw, 0).rgb);
        float3 cdev = (cwide - Luma(cwide)) - (cin - Luma(cin));
        float cmidK = smoothstep(0.3 * bev, bev, cu) * (0.4 + 0.6 * smoothstep(0.03, 0.25, Luma(ccol))) * saturate(Luma(ccol) / max(Luma(cwide), 0.03)) * a;
        // （差得越多放大得越少：本來就很明顯的冷暖、顏色不會被推成色塊，主要是把細微的變化帶出來）。
        // 加完以後色度的大小最多比原本多 3%：顏色只是往附近環境的方向轉（燈旁邊暖一點、冷的那邊棕色退一點），不會整片變鮮
        float3 cc0 = ccol - Luma(ccol), cc1 = cc0 + 0.3 * cdev / (1.0 + 3.0 * length(cdev)) * cmidK;
        ccol = saturate(Luma(ccol) + cc1 * min(1.0, 1.03 * length(cc0) / max(length(cc1), 1e-4)));
        // 玻璃的深度：越往中央越厚、吸收得越多——中央比外圍深約 5.5%，從邊往內約 90 像素平順地變深（不是一層均勻的膜；白字也更清楚一點）
        ccol *= 1.0 - 0.055 * smoothstep(0.0, 0.9 * min(Shape[0].z, Shape[0].w), cu) * a;
        // 亮的背景上壓暗一點（白字才清楚）：上面少、下面多，不是一整片均勻的灰；接近純白（雪地、白牆）再多壓一點，白字才不會幾乎看不見
        float cbright = smoothstep(0.45, 0.85, cL);
        ccol *= 1.0 - (0.06 * smoothstep(0.3, 0.8, cL) + 0.1 * smoothstep(0.75, 1.0, cL)) * (0.85 + 0.2 * cq.y) * a;
        // 表面的反射：卡片上方的景色（最糊的那張）倒過來淡淡映在玻璃上，從左上斜下來慢慢淡掉。
        // 暖色的燈映出來就是淡淡的暖色、夜景就是冷色——用環境本身的明暗和色調，不是疊一層白色漸層；用 screen 疊，不會爆白；
        // 映出來的景色亮（燈）的地方反光多、暗的地方少
        float2 crp = float2(p.x - 0.12 * (p.y - ctop.y), ctop.y - 6.0 * s - 0.5 * (p.y - ctop.y));
        float3 craw = BlurBTex.SampleLevel(Lin, (crp - BlurRect.xy) / BlurRect.zw, 0).rgb;
        float3 crefl = saturate(craw * 1.6 + 0.06);
        // 範圍照原本卡片的高度（1080p 198 像素）算，不照現在的高度：歌詞展開、卡片變高時，反光還是只在上緣那一段，不會蓋住整個歌詞區
        float csy = (p.y - ctop.y) / (198.0 * s);
        float csheen = (0.13 * smoothstep(0.85, 0.0, csy + 0.45 * cq.x) + 0.035 * (1.0 - saturate(csy))) * (0.5 + smoothstep(0.02, 0.4, Luma(craw)));
        csheen *= (1.0 - 0.35 * cbright) * a;
        ccol = 1.0 - (1.0 - ccol) * (1.0 - crefl * csheen);
        // 聚光：外圍那圈像透鏡，把後面的燈聚起來——亮的東西更亮（暗的不動）；整片都亮的背景不加，不然會多一圈亮框
        ccol += ccol * 0.1 * ct * ct * smoothstep(0.35, 0.9, Luma(ccol)) * (1.0 - 0.8 * cbright) * a;   // （輕輕的，燈經過邊上不會集中成一塊）
        // 玻璃的厚度：貼著邊一圈很淡的暗，下緣、右側比上緣、左側暗一點（亮的背景上淡一點：已經壓暗過一次，再疊下緣會髒髒灰灰的）
        ccol *= 1.0 - 0.5 * a * exp(-cu / (22.0 * s)) * (0.03 + 0.035 * smoothstep(-0.3, 1.0, nn.y) + 0.015 * smoothstep(-0.3, 1.0, nn.x)) * (1.0 - 0.4 * cbright);
        // 下緣內反射：光從上面進到玻璃裡，走到下緣再從裡面反射回來——下緣往內約 10 像素一道很軟、很淡的亮帶，
        // 照邊的方向連續地繞進兩個下角、再沿著右側往上淡淡地延伸一段（到卡片高度的三成左右淡掉；右下稍強，跟左上的光源一致）；貼著最外緣那 2～3 像素留給上面那層厚度（暗），亮帶在它裡面。
        // 顏色是卡片上方的環境（光是從那裡進來的）：上面有暖色的燈下緣就微微暖、冷色的夜景就微微冷；用 screen 疊，不會爆白；
        // 上方越亮反光越多（暗的桌布上幾乎看不出來），整片都亮的背景減半（白字才清楚）
        float cbot = max(smoothstep(0.15, 0.95, nn.y), 0.45 * smoothstep(0.3, 1.0, nn.x) * smoothstep(0.3, 1.0, cq.y)) * (0.85 + 0.25 * saturate(nn.x + 0.2));
        float3 cabove = BlurBTex.SampleLevel(Lin, (float2(p.x, ctop.y - 10.0 * s) - BlurRect.xy) / BlurRect.zw, 0).rgb;
        float cband = exp(-cu / (13.0 * s)) * smoothstep(1.5 * s, 6.5 * s, cu);   // 慢慢亮起、慢慢淡掉（很軟，不是一條線）
        float cbk = 0.07 * cbot * cband * (0.35 + 0.65 * smoothstep(0.05, 0.5, Luma(cabove))) * (1.0 - 0.5 * cbright) * a;
        ccol = 1.0 - (1.0 - ccol) * (1.0 - saturate(cabove * 1.4 + 0.05) * cbk);
        // 高光的亮度沿著邊變化（連續地繞過圓角）：約三分之一照「上緣最亮、兩側中等、下緣最淡」，
        // 約三分之二照光從左上來——對角兩道光（Apple 的招牌）：左上最亮、右下是光從玻璃裡面反射出來的那道（約一半多）、
        // 右上和左下暗下來；兩道光比較集中（四次方：受光面明確，往兩側收得快一點，整圈不會平均地亮）
        // 光源在左上角附近（不是無限遠）：同一條直邊上離它越近越亮——上緣從左往右、左側從上往下慢慢淡下去（直邊上的法線都一樣，
        // 只看方向的話整條一樣亮）；右下那道反射照鏡像的位置，從右下角往兩邊淡。照卡片裡的位置算，連續地繞過圓角
        float2 lq0 = (cq - float2(0.1, 0.0)) * float2(1.0, 1.4), lq1 = (cq - float2(0.9, 1.0)) * float2(1.0, 1.4);
        float near0 = 0.15 + 0.85 * exp(-dot(lq0, lq0) / 0.55), near1 = 0.6 + 0.4 * exp(-dot(lq1, lq1) / 0.55);
        float face = dot(nn, float2(-0.6, -0.8));
        float mw = 0.26 + 0.5 * smoothstep(-0.2, 1.0, -nn.y) + 0.12 * smoothstep(-0.2, 1.0, -nn.x);
        float lit = 0.68 * (1.05 * pow(saturate(face), 4.0) * near0 + 0.62 * pow(saturate(-face), 4.0) * near1 + 0.05) + 0.32 * mw;
        // 高光映著卡片外面的環境（最糊的那張，邊外 14 和 40 像素，取亮的那個：燈的光沿著側邊、圓角延伸一段，不只亮一小點）：
        // 顏色帶外面的色調（暖色的燈旁邊偏暖金、夜景偏冷），亮度跟著外面多亮（燈旁邊亮一點、背光的地方暗一點）——
        // 整圈自然有明暗、冷暖變化，不是一圈一樣亮的白線
        float3 cnear = BlurBTex.SampleLevel(Lin, (p + nn * 14.0 * s - BlurRect.xy) / BlurRect.zw, 0).rgb;
        float3 cfar = BlurBTex.SampleLevel(Lin, (p + nn * 40.0 * s - BlurRect.xy) / BlurRect.zw, 0).rgb * 0.8;
        float3 cout = lerp(cnear, cfar, saturate((Luma(cfar) - Luma(cnear)) * 8.0 + 0.5));
        float oL = Luma(cout);
        // 燈光滲進玻璃：外面亮（燈）的那段邊，往內約 26 像素淡淡地帶進外面的顏色（越亮越多），像光從玻璃的邊透進來、跟玻璃融在一起
        // （峰值低、散得遠：從邊往內慢慢淡掉，不會在燈旁邊集中成一團橘光）；
        // 先把顏色鮮一點再加（暖色的燈疊在偏冷的玻璃上才不會被中和成灰灰的卡其色；灰色的環境還是灰色）
        float3 cseep = saturate(lerp(oL.xxx, cout, 1.4));
        ccol += a * cseep * 0.09 * smoothstep(0.2, 0.7, oL) * exp(-cu / (26.0 * s)) * (1.0 - 0.7 * cbright);
        // 高光的顏色：外面越亮（旁邊有燈）越帶外面的顏色（暖燈旁邊偏金、冷的夜景偏冷）；外面暗的時候偏白（暗處的色相不可靠，帶進來會髒）
        float3 renv = lerp(float3(1.0, 1.0, 1.0), saturate(Hue(cout) * 0.8 + 0.2), 0.25 + 0.2 * smoothstep(0.08, 0.4, oL));
        // 整片都亮的背景上別讓整圈一起變亮（會像貼紙的描邊）
        float envK = 0.65 + 0.7 * smoothstep(0.04, 0.45, oL) * (1.0 - 0.5 * cbright);
        ccol += C.y * 0.03 * mw * envK * renv * exp(-cu / (1.8 * s)) * a;       // 內側淡淡提亮
        // 面向亮光的那段邊自己亮起來（不管朝哪個方向：右邊緊貼著燈，邊線也會亮）。
        // 看的是外面比卡片底下亮多少：只有旁邊的燈會點亮那段邊；整片都亮的背景四邊一樣亮，保留左上強、右下弱的方向感
        lit = max(lit, 0.85 * smoothstep(0.06, 0.25, oL - cL));
        // 玻璃的厚度（亮的那一半）：往內約 8 像素慢慢淡掉的一層很淡的光，受光的一側多一點——軟的漸層，不是第二條線
        ccol += C.y * 0.022 * lit * envK * renv * exp(-cu / (8.0 * s)) * (1.0 - 0.6 * cbright) * a;
        // 外圍那圈很淡的菲涅耳（越靠邊反射越多），往環境的顏色混，不會爆白
        ccol = lerp(ccol, renv, saturate(0.055 * ct * ct * ct * (0.4 + lit) * envK * a));
        // 約 1 像素的細高光（只有一條：離外緣 0.5 像素、寬 1.1，比 1 像素寬一點，沿著圓角的弧線才不會一格一格的）。
        // 受光的那段亮、背光的那段只剩很淡的輪廓（黑底上整圈的形狀也看得出來，外面越暗輪廓多一點），不是一圈一樣亮的描邊
        float fr = (cu - 0.5 * s) / (1.1 * s);
        float fline = 0.34 * lit * envK + 0.06 * (0.55 + 0.6 * lit) * (0.8 + 0.6 * (1.0 - smoothstep(0.0, 0.12, oL)));
        ccol = lerp(ccol, renv, saturate(C.y * fline * exp(-fr * fr) * a));
        ccol = lerp(ccol, float3(1.0, 1.0, 1.0), 0.24 * h.energy * a);         // 按下：整塊被白光填滿
        float2 cgp = p - Touch.xy, cgq = p - Touch2.xy;
        ccol += a * 0.13 * max(Touch.z * exp(-dot(cgp, cgp) / (2.0 * Touch.w * Touch.w + 1.0)), Touch2.z * exp(-dot(cgq, cgq) / (2.0 * Touch2.w * Touch2.w + 1.0)));
        // 很淡的固定抖動（±半階，交錯梯度雜訊，不會動、不會閃）：暗的天空、漸層透過玻璃壓暗、加飽和後不會一階一階的
        ccol += (frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715)))) - 0.5) / 255.0;
        return float4(lerp(outside, ccol, saturate(0.5 - h.d / 1.4)), 1.0);   // 外緣的抗鋸齒 1.4 像素寬（圓角的弧線才圓順）
    }

    // 邊緣往外取樣（像透鏡的邊，把玻璃外面的東西壓進來）：位移 = 強度 × 0.5 × 邊寬 × e^(−深度 / (0.35 × 邊寬))；
    // 越靠邊壓得越緊，取樣位置一直往同一個方向走，不會折返、不會抹成一片
    float bev = max(A.w * s * h.thick, 1e-3);
    float u = max(-h.d, 0.0);
    float x = saturate(u / bev);
    float edge = 1.0 - x;
    float disp = A.z * 0.5 * bev * exp(-u / (0.35 * bev));
    // 上下兩條直邊的折射打折（背景的字不會被拉長）；左右邊、圓角保持全強度
    disp *= 1.0 - h.focus * (h.flat + 0.5 * h.corn);   // 圓角也稍微收一點（壓縮最嚴重的地方）
    float2 off = n * disp * a;

    float2 base = h.c + (p - h.c) / lerp(1.0, h.mag, a);   // 整塊放大
    float ca = B.x * edge * (1.0 - 0.7 * h.corn);   // 圓角裡色差少一點（壓得很緊時色差會變成彩色雜點）
    // 越靠邊緣越清楚：邊緣折射進來的是看得出形狀的背景（液態玻璃最明顯的特徵），中間才是模糊的；
    // 跟位移用同一種衰減（位移大的地方才清楚），但至少留一點輕模糊（邊緣壓縮了好幾倍，完全不糊的話影片的細節會閃）
    float sc = saturate(B.y * lerp(0.8, 1.25, saturate(h.thick - 0.6))) * a * (1.0 - 0.75 * exp(-u / (0.5 * bev)));
    sc = max(sc, 0.1 * a * step(0.001, B.y));
    sc = max(sc, 0.3 * h.corn * edge * a);   // 圓角靠邊的地方糊一點：壓縮好幾十倍的背景不清楚反而好看，不會一閃一閃
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
    float2 gp = p - Touch.xy, gq = p - Touch2.xy;
    col += a * 0.13 * max(Touch.z * exp(-dot(gp, gp) / (2.0 * Touch.w * Touch.w + 1.0)), Touch2.z * exp(-dot(gq, gq) / (2.0 * Touch2.w * Touch2.w + 1.0)));
    return float4(lerp(outside, col, saturate(0.5 - h.d)), 1.0);
}

// ---------- 玻璃字（時鐘的數字，照 iOS 26 鎖定畫面「玻璃」時鐘、透明度最低的照片量出來的）----------
//   裡面：一扇清澈的窗，不放大、不染色、不改飽和度，只輕輕糊一點（細小的斑點抹掉、大的形狀還清楚），
//         再加一層平平的、無色的白（暗的背景約 5%、亮的約 3%）；
//   邊緣：只有最外緣一兩個像素往字裡面取樣（穿過邊的線條被彎成沿著邊的細鉤，筆畫下緣看起來像往下滴），邊上一點點色散；
//         緊貼邊緣內側一段柔和的提亮；數字裡的洞（0、4、6、8、9 中間）那一側的邊有一條細暗帶；
//   輪廓：約一像素的細線，顏色是背景提亮（不是純白），左上、右下各一段亮弧、兩端漸細，其他地方只剩很淡的暗線；
//   沒有影子、沒有內線、沒有立體浮雕。
// 字形距離圖貼在 Shape[0]（左上角 xy、大小 zw）：r 是離字的邊多遠（字裡面是負的）、g 是離洞多遠（像素）、b 是字的大小（時間 1，日期比較小）；
// A.z、A.w 是邊緣往裡取樣的強度和衰減（1080p 的像素）；B.x 色散（像素）、B.y 模糊；C.x 白的濃度、C.y 輪廓線
float3 FieldAt(float2 p) { return FieldTex.SampleLevel(Lin, (p - Shape[0].xy) / Shape[0].zw, 0).rgb; }

// 換分鐘的動畫：在新 / 舊距離圖的 (x0, x1) 這一格裡取 q 的距離；超出這一格的地方用格子邊上的值加上超出的距離（距離才是連續的）
float3 SlotField(float2 q, float x0, float x1, bool old)
{
    float4 r = old ? float4(Anim0.zw, Anim0.zw + Anim1.xy) : float4(Shape[0].xy, Shape[0].xy + Shape[0].zw);
    float2 qc = float2(clamp(q.x, x0, x1), clamp(q.y, Anim0.y, r.w - 1.0));
    float3 f = old ? OldFieldTex.SampleLevel(Lin, (qc - Anim0.zw) / Anim1.xy, 0).rgb : FieldAt(qc);
    f.x += length(q - qc);
    return f;
}

// 這一點的距離圖。換分鐘的動畫中，時間那一行由每一格拼起來：每個字照自己的位置取距離，再乘上一道上下的漸層遮罩（字本身不變形）；
// alpha 是遮罩在這一點的值（字看得到多少，外面那圈暗暈也跟著）；blur 留給以後用（現在是 0）
float3 FieldDH(float2 p, out float alpha, out float blur)
{
    alpha = 1.0; blur = 0.0;
    int n = (int)Anim0.x;
    if (n == 0) return FieldAt(p);
    float3 best = float3(1e4, 1e4, 1.0);
    if (p.y < Anim0.y) best = FieldAt(p);                      // 時間上面（日期那一行）照常
    float pick = best.x;                                       // 選最近的字；遮罩淡的（快抹掉、剛長出來）算遠一點，不搶旁邊的字的暗暈
    [loop] for (int i = 0; i < 10; i++)
    {
        if (i >= n) break;
        float4 a = Slot[i * 3], b = Slot[i * 3 + 1], c = Slot[i * 3 + 2];
        float al = saturate(c.y * (p.y - b.z) / max(b.w, 1e-3));
        if (al < 0.003) continue;
        float3 f = SlotField(p - float2(c.x, 0.0), a.x, a.y, a.z > 0.5);
        float fp = f.x + (1.0 - al) * 40.0 * A.y;
        if (fp < pick) { pick = fp; best = f; alpha = al; }
    }
    return best;
}

float FieldD(float2 p) { float a = 1.0, b = 0.0; return FieldDH(p, a, b).r; }

float4 TextGlass(float4 pos : SV_Position) : SV_Target
{
    float2 p = pos.xy;
    float s = A.y;
    float3 bg = SceneTex.SampleLevel(Lin, (p - SceneRect.xy) / SceneRect.zw, 0).rgb;
    float alpha, blur;
    float3 dh = FieldDH(p, alpha, blur);
    float d = dh.x;
    float clear = 1.0 - saturate(blur / (8.0 * s));           // 換分鐘時糊掉的字：邊緣的折射、亮邊跟著變淡
    float k = max(dh.z, 0.2);                                // 字的大小：小字的折射和模糊跟著縮小
    // 背景很亮時（例如白天的天空）玻璃幾乎看不見：字外面加一圈很淡、很柔的暗暈，把字跟背景分開（暗的背景不加）
    float bright = smoothstep(0.5, 0.85, Luma(BlurBTex.SampleLevel(Lin, (p - BlurRect.xy) / BlurRect.zw, 0).rgb));
    float3 outside = bg * (1.0 - 0.07 * bright * alpha * exp(-max(d, 0.0) / (4.0 * s)));
    float soft = max(1.0, blur);
    if (d > soft) return float4(outside, 1.0);
    // 1：日期那一行的小字。筆畫只有幾像素寬，邊緣的細鉤、洞的暗帶、內側提亮、暗線都會讓每一筆變成兩條線夾著（像空心字），
    // 小字不加這些，改成字本身的玻璃濃一點（一整塊）
    float small = 1.0 - smoothstep(0.4, 0.9, dh.z);

    // 邊緣的方向（字形距離圖的斜率，往字外面）、離邊多深（1080p 的像素）
    // 斜率取左右、上下各 2.5 像素（只差 1 像素的話，斜邊上方向會一格一格地跳，亮線、暗線跟著變成虛線）
    float e = 2.5 * s;
    float2 g = float2(FieldD(p + float2(e, 0.0)) - FieldD(p - float2(e, 0.0)), FieldD(p + float2(0.0, e)) - FieldD(p - float2(0.0, e)));
    g = g / max(length(g), 1e-4);
    float u = max(-d, 0.0) / s;

    // 最外緣往字裡面取樣（細鉤）；邊上一點點色散（紅往外、藍往內）
    float hook = A.z * k * (1.0 - small) * clear * exp(-u / A.w) * (1.0 - smoothstep(3.0, 4.5, u));
    float2 q = p - g * (hook * s);
    float cab = B.x * exp(-u / 1.2) * s;
    float3 col = float3(Look(q + g * cab, B.y * k).r, Look(q, B.y * k).g, Look(q - g * cab, B.y * k).b);

    // 一層平平的、無色的白（暗的背景多一點）
    float Lb = Luma(BlurBTex.SampleLevel(Lin, (p - BlurRect.xy) / BlurRect.zw, 0).rgb);
    col += C.x * lerp(0.05, 0.028, smoothstep(0.08, 0.75, Lb));

    // 方向：左上、右下各一段亮弧（約 70°），兩端漸細
    float2 L = float2(-0.70710678, -0.70710678);
    float D = smoothstep(0.5, 0.92, max(dot(g, L), 0.9 * dot(g, -L)));
    // 緊貼邊緣內側一段柔和的提亮；洞那一側的邊一條細暗帶
    col += C.x * 0.12 * (1.0 - small) * exp(-u / 1.3) * (0.4 + 0.6 * D);
    col += C.x * 0.05 * small * (1.0 - bright);              // 小字：暗的背景上多一點乳白
    float hole = exp(-max(dh.y / s - 0.5, 0.0) / 1.5);
    col *= 1.0 - 0.3 * (1.0 - 0.7 * bright) * (1.0 - small) * hole * exp(-u / 1.3);
    col *= 1.0 - (0.14 + 0.14 * small) * bright;              // 亮的背景上變成淡淡的煙燻玻璃（還是透明的；小字濃一點）
    // 輪廓：約一像素多的細線（中心在邊內 0.7 像素），顏色是背景提亮；亮弧的地方亮，其他地方只剩很淡的暗線
    float rp = exp(-(u - 0.7) * (u - 0.7) / (2.0 * 0.7 * 0.7));
    float ra = C.y * rp * (0.12 + 0.75 * D) * lerp(0.45, 0.85, smoothstep(0.05, 0.5, Lb)) * (1.0 - 0.4 * bright) * (1.0 - 0.5 * small);
    col = lerp(col, lerp(bg, float3(1.0, 1.0, 1.0), 0.55), saturate(ra * clear));
    // 很淡的暗線；亮的背景上整圈加深，輪廓才看得出來。比亮線往內、寬一點（中心在邊內 1.2 像素、寬約 2.6 像素）：
    // 線太細的話，斜的邊（例如 5 上面那段）每一列落在像素上的位置不同、深淺跟著跳，看起來像虛線、一格一格的階梯
    float rd = exp(-(u - 1.2) * (u - 1.2) / (2.0 * 1.3 * 1.3));
    col *= 1.0 - (0.08 + 0.1 * bright) * alpha * (1.0 - small) * rd * (1.0 - 0.5 * D);
    return float4(lerp(outside, col, saturate(0.5 - d / soft) * alpha), 1.0);
}
";

        // ---------- 編譯（背景執行緒，整個程式只做一次）----------
        [DllImport("d3dcompiler_47.dll")]
        static extern int D3DCompile(byte[] srcData, IntPtr srcDataSize, [MarshalAs(UnmanagedType.LPStr)] string sourceName, IntPtr defines, IntPtr include,
            [MarshalAs(UnmanagedType.LPStr)] string entryPoint, [MarshalAs(UnmanagedType.LPStr)] string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr BlobFn(IntPtr self);

        static byte[] vsCode, downCode, blurCode, glassCode, textCode;
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
                    try { textCode = Compile(src, "TextGlass", "ps_4_0"); } catch { textCode = null; }   // 失敗只有時鐘退回實心字
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
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UpdateHalfFn(IntPtr self, IntPtr dst, uint sub, IntPtr box, ushort[] data, uint rowPitch, uint depthPitch);
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
        readonly UpdateHalfFn updateHalf;
        readonly ClearStateFn clearState;

        IntPtr vs, downPs, blurPs, glassPs, textPs, sampler, glassBuffer, passBuffer, animBuffer;
        public const int ClockAnimFloats = (2 + 30) * 4;   // 跟 cbuffer ClockAnim 一樣的排法
        static readonly float[] noAnim = new float[ClockAnimFloats];
        readonly float[] glassData = new float[33 * 4];   // 跟 cbuffer GlassInfo 一樣的排法（33 個 float4）
        readonly float[] passData = new float[8];
        readonly IntPtr[] one = new IntPtr[1], two = new IntPtr[2], three = new IntPtr[3], six = new IntPtr[6];

        // 模糊用的五張小圖（四分之一解析度）
        class Small { public IntPtr Texture, Rtv, Srv; }
        readonly Small[] blur = new Small[5];   // q1、q2（暫存）、qA（輕模糊）、qB（重模糊）、qM（微糊，卡片用）
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
            updateHalf = ComVtable.Method<UpdateHalfFn>(context, 48);              // 同一個函式（資料是 16 位元浮點數）
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
            if (textCode == null || createPs(device, textCode, (IntPtr)textCode.Length, IntPtr.Zero, out textPs) < 0) textPs = IntPtr.Zero;   // 只有時鐘退回實心字
            var sd = new SamplerDesc { Filter = 0x15 /* 線性 */, AddressU = 3, AddressV = 3, AddressW = 3 /* 超出範圍用邊緣的顏色 */, ComparisonFunc = 1, MaxLod = float.MaxValue };
            if (ComVtable.Method<CreateSamplerFn>(device, 23)(device, ref sd, out sampler) < 0) return false;   // ::CreateSamplerState
            var bd = new BufferDesc { ByteWidth = (uint)(glassData.Length * 4), BindFlags = 0x4 /* 常數 */ };
            if (createBuffer(device, ref bd, IntPtr.Zero, out glassBuffer) < 0) return false;
            bd.ByteWidth = (uint)(passData.Length * 4);
            if (createBuffer(device, ref bd, IntPtr.Zero, out passBuffer) < 0) return false;
            bd.ByteWidth = ClockAnimFloats * 4;
            if (createBuffer(device, ref bd, IntPtr.Zero, out animBuffer) < 0) return false;
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
            return Draw(glassPs, IntPtr.Zero, IntPtr.Zero, null, scene, sceneW, sceneH, target, shapes, count, look, bx, by, bw, bh, dx, dy, dw, dh, reblur);
        }

        // ---------- 玻璃字：時鐘的數字 ----------
        // field 是 CreateField 建的字形距離圖，貼在畫面的 (fx, fy)、大小 fw × fh；換分鐘的動畫中 oldField 是舊時間的距離圖、
        // anim 是這一格的動畫（ClockAnimFloats 個，排法跟 cbuffer ClockAnim 一樣；沒有動畫是 null）。其他參數跟 DrawLayer 一樣
        public bool DrawText(IntPtr scene, int sceneW, int sceneH, IntPtr target, IntPtr field, float fx, float fy, float fw, float fh,
            IntPtr oldField, float[] anim, Look look, int bx, int by, int bw, int bh, int dx, int dy, int dw, int dh, bool reblur)
        {
            textShape[0] = new Shape { X = fx, Y = fy, HalfW = fw, HalfH = fh };
            return Draw(textPs, field, oldField, anim, scene, sceneW, sceneH, target, textShape, 1, look, bx, by, bw, bh, dx, dy, dw, dh, reblur);
        }

        readonly Shape[] textShape = new Shape[1];

        // 32 位元浮點數換成 16 位元（超過範圍的夾到 ±65504；距離圖的值不會小到需要次正規數，直接當 0）
        static ushort Half(int bits)
        {
            int sign = (bits >> 16) & 0x8000, exp = ((bits >> 23) & 0xff) - 127 + 15, mant = bits & 0x7fffff;
            if (exp <= 0) return (ushort)sign;
            if (exp >= 31) return (ushort)(sign | 0x7bff);
            return (ushort)(sign | (exp << 10) | (mant >> 13));
        }

        // 字形距離圖放上顯示卡（每個點四個 16 位元浮點數：Direct3D 10.0 的顯示卡就一定能平滑取樣，32 位元的要 10.1 才保證；
        // 精度在幾十像素內約 0.03，夠用）。data 是交錯排的四個值。玻璃字的著色器沒編好、或建不起來回傳 false
        public bool CreateField(float[] data, int w, int h, out IntPtr texture, out IntPtr view)
        {
            texture = view = IntPtr.Zero;
            if (textPs == IntPtr.Zero || data == null || data.Length < w * h * 4) return false;
            var bits = new int[data.Length];   // 一次把浮點數的位元複製出來（逐個用 BitConverter 會配置幾百萬個小陣列）
            Buffer.BlockCopy(data, 0, bits, 0, data.Length * 4);
            var half = new ushort[data.Length];
            for (int i = 0; i < bits.Length; i++) half[i] = Half(bits[i]);
            var desc = new Texture2DDesc { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = 10 /* R16G16B16A16_FLOAT */, SampleCount = 1, BindFlags = 0x8 };
            if (createTexture(device, ref desc, IntPtr.Zero, out texture) < 0) { texture = IntPtr.Zero; return false; }
            updateHalf(context, texture, 0, IntPtr.Zero, half, (uint)(w * 8), 0);
            view = CreateReadView(texture);
            if (view == IntPtr.Zero) { Free(ref texture); return false; }
            return true;
        }

        // 模糊後面的影片（需要的話），再用 ps 把玻璃畫到 target 上；field 是玻璃字的字形距離圖（圓角長方形的玻璃不用，0）
        bool Draw(IntPtr ps, IntPtr field, IntPtr oldField, float[] anim, IntPtr scene, int sceneW, int sceneH, IntPtr target, Shape[] shapes, int count, Look look,
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
                // 微糊（qM，只有卡片用）：縮小的圖糊一次小的（1080p 約 6 像素），q0 留著給下面的輕模糊接著用；
                // 間距超過 1.5 格（4K）先用小一半的間距糊過，跟輕模糊一樣不會拆成重影
                if (look.Clear > 0.5f)
                {
                    float rm = 0.85f * look.Scale;
                    if (rm > 1.5f) { BlurPass(blur[0], blur[1], rm / 2, 0); BlurPass(blur[1], blur[4], 0, rm / 2); BlurPass(blur[4], blur[1], rm, 0); }
                    else BlurPass(blur[0], blur[1], rm, 0);
                    BlurPass(blur[1], blur[4], 0, rm);
                }
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
            psSetShader(context, ps, IntPtr.Zero, 0);
            six[0] = scene; six[1] = blur[2].Srv; six[2] = blur[3].Srv; six[3] = field; six[4] = oldField; six[5] = blur[4].Srv;
            psSetResources(context, 0, 6, six);
            update(context, animBuffer, 0, IntPtr.Zero, anim ?? noAnim, 0, 0);
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
            three[0] = glassBuffer; three[1] = passBuffer; three[2] = animBuffer;
            psSetConstants(context, 0, 3, three);
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
            Put(g, 31, look.Clear, look.ScatterBusy, look.ScatterRim, 0);
            Put(g, 32, look.Touch2X, look.Touch2Y, look.Touch2Glow, look.Touch2Radius);
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
                // 16 位元浮點：模糊好幾次每次都捨入到 8 位元的話，暗的漸層會一階一階的（四分之一解析度，多用的記憶體很少）
                var desc = new Texture2DDesc { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = 10 /* R16G16B16A16_FLOAT */, SampleCount = 1, BindFlags = 0x20 | 0x8 };
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
            Free(ref glassBuffer); Free(ref passBuffer); Free(ref animBuffer); Free(ref sampler);
            Free(ref textPs); Free(ref glassPs); Free(ref blurPs); Free(ref downPs); Free(ref vs);
        }
    }
}
