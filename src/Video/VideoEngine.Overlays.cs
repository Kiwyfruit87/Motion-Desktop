using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VideoWallpaper
{
    // 疊圖的順序（數字小的先畫）：
    // 卡片的毛玻璃（左下角的時鐘也在這層）→ 重拍時邊框的光暈 → 封面、文字、按鈕 → 歌詞
    static class OverlayLayer
    {
        public const int Glass = 0, CardEdge = 1, CardContent = 2, Lyrics = 3;
    }

    // 播放引擎的疊圖：鎖定畫面的時鐘、正在播放、歌詞，每一格影片畫好之後貼上去。
    // 外面（UI 執行緒）用下面的 Set* / *Overlay 指令放圖、做動畫，實際動作排進佇列、在引擎執行緒上做
    partial class VideoEngine
    {
        class Overlay
        {
            public IntPtr D2DBitmap;              // Direct2D 的圖
            public IntPtr DC, Bitmap, OldBitmap;  // GDI 的圖（退回 GDI 時）
            public int X, Y, W, H;
            public int Z;                         // 疊的順序（見 OverlayLayer）
            public int Follow;                    // 鎖定畫面滑動時跟著卡片動：0 不動（時鐘）、1 只跟著移動、2 跟著移動和變形（卡片本身和玻璃裡的歌詞）
            public bool ClockDigits;              // 時鐘的實心數字：這一格畫出了玻璃數字就不畫（名字是 "clockDigits"）
            public Tween Fade = new Tween(1);     // 整張圖的不透明度（淡入淡出）
            public bool RemoveWhenFaded;          // 動畫做完就拿掉（淡出、滑出）
            // 捲動的疊圖（歌詞）：整張長圖只露出 ViewH 高的一段（從第 Scroll 列開始），Mask 是露出那段每一列的亮度（0～1）
            public int ViewH;
            public float[] Mask;
            public Tween Scroll;
            // Shade：長圖每一列的亮度（跟著長圖一起捲動，null = 都一樣亮）；換的時候從 ShadeFrom 慢慢變過去
            public float[] Shade, ShadeFrom;
            public Tween ShadeMix = new Tween(1);
            // 糊掉的長圖（只有 Direct2D）和每一列要糊多少（0～1，跟著長圖捲動；跟 Shade 一起慢慢換）
            public IntPtr D2DBlur;
            public float[] Blur, BlurFrom;
            // 間奏的三個點（畫在露出那段的正中間、文字的左邊對齊 DotsLeft）：DotsStart 是間奏開始的時間（引擎的時鐘），
            // 暫停時停在 DotsAt 毫秒；DotsLen 是間奏多長（秒）；DotsSize、DotsGap 是點的直徑和間距
            public bool Dots, DotsPaused;
            public long DotsStart;
            public double DotsAt, DotsLen, DotsLeft, DotsSize, DotsGap;
            // 畫面上三個點現在的亮度和整組的大小（平順地追到該有的樣子：拖進度條跳到別的位置時才不會一下子跳過去）
            public double[] DotsShown;
            public long DotsDrawnAt;
            public bool DotsEasing;   // 還在追（暫停中也要繼續畫到追上）
            // 跟著重拍閃的光暈（卡片邊框的光）：平常看不到，亮度跟著 PulseLevel 亮起來再暗回去
            public bool Pulse;
            // 左右滑動（換歌時封面和文字的翻頁動畫），只在 Clip 範圍內看得到
            public Tween Move;
            public bool Clip;
            public int ClipX, ClipY, ClipW, ClipH;
            // 毛玻璃：圖底下這塊圓角長方形 (GlassX, GlassY, GlassW, GlassH) 的影片先糊掉再貼圖
            public bool Glass;
            public int GlassX, GlassY, GlassW, GlassH;
            public double GlassRadius;
            public bool GlassReady;
            public int BlurX, BlurY, BlurW, BlurH;
            // 縮放（暫停時封面縮小、播放鍵換圖）：以畫面上的 (ZoomCX, ZoomCY) 為中心，彈簧追 ZoomTo（null = 不縮放）
            public Spring Zoom;
            public double ZoomTo = 1, ZoomCX, ZoomCY, ZoomResponse = 0.5, ZoomDamping = 0.8;
            public long ZoomAt;
            // 跑馬燈（太長的歌名）：圖裡的字寬 MarqueeW，兩份一前一後相隔 MarqueeGap 往左捲，每次停 MarqueePause 毫秒再捲；
            // 只在畫面上 (MarqueeViewX, 寬 MarqueeViewW) 看得到，兩端 MarqueeFade 的距離淡出（MarqueeW = 0：不捲）
            public double MarqueeW, MarqueeGap, MarqueeSpeed, MarqueeViewX, MarqueeViewW, MarqueeFade;
            public int MarqueePause;
            public long MarqueeStart;
            // 歌詞每一行的大小（Apple Music：唱到的那行正常大小、其他行小一點）：LineZoom 是長圖每一列的縮放（跟 Shade 一起換），
            // LineAnchor 是那一列屬於的那行的中心（長圖的列），LineLeft 是字的左邊（圖裡的 x）：照這一點縮放，字的左邊不會跑
            public float[] LineZoom, LineZoomFrom, LineAnchor;
            public double LineLeft;

            public bool Running(long now)
            {
                return Scroll.Running(now) || ShadeMix.Running(now) || Fade.Running(now) || Move.Running(now) || Dots && (!DotsPaused || DotsEasing)
                    || Zoom != null && Zoom.Moving(ZoomTo, 0.0005) || MarqueeW > 0;
            }

            public void Release()
            {
                if (D2DBitmap != IntPtr.Zero) { Marshal.Release(D2DBitmap); D2DBitmap = IntPtr.Zero; }
                if (D2DBlur != IntPtr.Zero) { Marshal.Release(D2DBlur); D2DBlur = IntPtr.Zero; }
                if (DC != IntPtr.Zero)
                {
                    Native.SelectObject(DC, OldBitmap);
                    Native.DeleteObject(Bitmap);
                    Native.DeleteDC(DC);
                    DC = IntPtr.Zero;
                }
            }
        }

        // ---------- 給 UI 執行緒用的指令 ----------

        // 設定一張疊圖（name 不同就是不同張，例如 "clock"、"music"）；pixels 為 null 代表拿掉這張。
        // pixels：預先乘好 alpha 的 BGRA（WPF 的 Pbgra32），貼在畫面的 (x, y)
        public void SetOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                Replace(t, name, pixels == null ? null : CreateOverlay(t, pixels, width, height, x, y));
            });
        }

        // 淡入換圖（音樂卡片的顏色）：新的從看不到慢慢變清楚，舊的同時淡出、淡完拿掉；fadeMs = 0 直接換。pixels 是 null 就拿掉。
        // pulse：光暈（平常看不到，跟著 PulseLevel 亮）
        public void SetFadeOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y, int z, int fadeMs, bool pulse = false)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                ReplaceFading(t, now, name, pixels, width, height, x, y, z, fadeMs, delegate(Overlay o) { o.Pulse = pulse; });
            });
        }

        // 底下帶毛玻璃的疊圖（音樂卡片）：(glassX, glassY, glassW, glassH) 這塊圓角長方形裡的影片會先糊掉
        public void SetGlassOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y,
            int glassX, int glassY, int glassW, int glassH, double radius)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                Overlay o = null;
                if (pixels != null)
                {
                    o = CreateOverlay(t, pixels, width, height, x, y);
                    o.Z = OverlayLayer.Glass;
                    o.Glass = true;
                    o.GlassX = glassX; o.GlassY = glassY; o.GlassW = glassW; o.GlassH = glassH; o.GlassRadius = radius;
                }
                Replace(t, name, o);
            });
        }

        // 捲動的疊圖（歌詞）：pixels 是整張長圖，畫面上從 (x, y) 開始只露出 viewH 高的一段，
        // 露出的是長圖從第 scroll 列開始的部分；mask 是露出那段每一列的亮度（0～1，固定在畫面上），
        // shade 是長圖每一列的亮度（跟著長圖捲動，null = 都一樣亮），兩個相乘。出現時用 fadeMs 淡入。
        // blurred 是糊掉的同一張長圖、blur 是每一列要往它混多少（都可以是 null：不糊；退回 GDI 時也不糊）
        // zoom、anchor、left：每一行的大小（見 Overlay.LineZoom；null = 都一樣大）
        public void SetScrollOverlay(IntPtr hwnd, string name, int[] pixels, int[] blurred, int width, int height, int x, int y, int viewH, float[] mask, float[] shade, float[] blur,
            float[] zoom, float[] anchor, double left, double scroll, int fadeMs)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                var o = CreateOverlay(t, pixels, width, height, x, y);
                if (t.D2D != null && blurred != null && blur != null) o.D2DBlur = t.D2D.CreateBitmap(blurred, width, height);
                o.Z = OverlayLayer.Lyrics;
                o.ViewH = viewH;
                o.Mask = mask;
                o.Shade = shade;
                o.Blur = blur;
                if (zoom != null && anchor != null) { o.LineZoom = zoom; o.LineAnchor = anchor; o.LineLeft = left; }
                o.Scroll = new Tween(scroll);
                o.Fade = new Tween(0, 1, now, fadeMs);
                Replace(t, name, o);
            });
        }

        // 卡片上的疊圖（封面、文字、按鈕的圖示），只在 (clipX, clipY, clipW, clipH) 範圍內看得到。
        // shift ≠ 0：換歌的動畫——舊的往左滑一小段（shift）同時淡出、新的從右邊 shift 處滑進來同時淡入（shift 是負的就反過來），
        //            舊的淡完就拿掉。
        // shift = 0：只是換圖（例如封面晚一點才讀到、按了播放暫停），如果正在滑動就接著滑，不會跳。
        // opacity：這張圖的亮度（0～1）。只是換圖（shift = 0）時，從原本的亮度花 fadeMs 慢慢變到新的亮度
        public void SetSlideOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y, int z,
            int clipX, int clipY, int clipW, int clipH, int shift, int ms, double opacity, int fadeMs)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                Overlay old;
                t.TryGet(name, out old);
                if (old != null) t.Remove(name);
                if (pixels == null)
                {
                    if (old == null) return;
                    if (shift == 0) { old.Release(); return; }
                    // 換歌、新的這首沒有這張（例如新歌名放得下、不用跑馬燈）：舊的照樣滑出去、淡掉
                    old.Move.Continue(old.Move.Value(now) - shift, ms, now);
                    old.Fade.Retarget(0, ms, now);
                    old.RemoveWhenFaded = true;
                    t.Put(name + "#" + now, old);
                    return;
                }

                var o = CreateOverlay(t, pixels, width, height, x, y);
                o.Z = z;
                o.Clip = true; o.ClipX = clipX; o.ClipY = clipY; o.ClipW = clipW; o.ClipH = clipH;
                o.Fade = new Tween(opacity);
                if (shift != 0)
                {
                    if (old != null)
                    {
                        old.Move.Continue(old.Move.Value(now) - shift, ms, now);   // 接著原本的速度（連按兩次下一首時不會先停一下）
                        old.Fade.Retarget(0, ms, now);   // 一邊滑一邊淡出
                        old.RemoveWhenFaded = true;      // 淡完就拿掉
                        t.Put(name + "#" + now, old);
                    }
                    o.Move = new Tween(shift, 0, now, ms);
                    o.Fade = new Tween(0, opacity, now, ms);   // 新的一邊滑進來一邊淡入
                    if (old != null && old.Zoom != null)
                    {
                        // 新的一張從舊的現在的大小接著（換歌時正暫停著：新封面也是縮小的樣子）
                        CopyZoom(old, o);
                        o.Zoom = new Spring();
                        o.Zoom.Jump(old.Zoom.X);
                    }
                }
                else if (old != null)
                {
                    o.Move = old.Move;
                    CopyZoom(old, o);
                    o.Zoom = old.Zoom;
                    o.MarqueeW = old.MarqueeW; o.MarqueeGap = old.MarqueeGap; o.MarqueeSpeed = old.MarqueeSpeed; o.MarqueePause = old.MarqueePause;
                    o.MarqueeViewX = old.MarqueeViewX; o.MarqueeViewW = old.MarqueeViewW; o.MarqueeFade = old.MarqueeFade; o.MarqueeStart = old.MarqueeStart;
                    double current = old.Fade.Value(now);
                    if (Math.Abs(current - opacity) > 0.001) o.Fade = new Tween(current, opacity, now, fadeMs);
                    old.Release();
                }
                t.Put(name, o);
            });
        }

        static void CopyZoom(Overlay from, Overlay to)
        {
            to.ZoomTo = from.ZoomTo; to.ZoomCX = from.ZoomCX; to.ZoomCY = from.ZoomCY;
            to.ZoomResponse = from.ZoomResponse; to.ZoomDamping = from.ZoomDamping; to.ZoomAt = from.ZoomAt;
        }

        // 這張圖以畫面上的 (cx, cy) 為中心縮放到 to 倍（彈簧：response 秒、阻尼 damping）；from ≥ 0 時先跳到 from 再開始
        public void ZoomOverlay(IntPtr hwnd, string name, double to, double cx, double cy, double response, double damping, double from = -1)
        {
            WithOverlay(hwnd, name, delegate(Overlay o, long now)
            {
                if (o.Zoom == null) o.Zoom = new Spring();
                if (from >= 0) o.Zoom.Jump(from);
                o.ZoomTo = to; o.ZoomCX = cx; o.ZoomCY = cy; o.ZoomResponse = response; o.ZoomDamping = damping;
            });
        }

        // 跑馬燈：圖裡的字寬 width（字從圖的左邊開始），畫面上只在 (viewX, 寬 viewW) 看得到；width = 0 停掉。
        // 設定一樣（同一首歌重畫）就不重新開始
        public void SetMarquee(IntPtr hwnd, string name, double width, double gap, double viewX, double viewW, double fade, double speed, int pauseMs)
        {
            WithOverlay(hwnd, name, delegate(Overlay o, long now)
            {
                if (o.MarqueeW != width || o.MarqueeViewX != viewX || o.MarqueeViewW != viewW) o.MarqueeStart = now;
                o.MarqueeW = width; o.MarqueeGap = gap; o.MarqueeViewX = viewX; o.MarqueeViewW = viewW;
                o.MarqueeFade = fade; o.MarqueeSpeed = speed; o.MarqueePause = pauseMs;
            });
        }

        // 跑馬燈現在捲了多遠：停 MarqueePause 毫秒，再捲過一份（字寬 + 間隔）；捲完後面那份剛好在原本的位置，接著重來
        static double MarqueeShift(Overlay o, long now)
        {
            double period = o.MarqueeW + o.MarqueeGap, travel = period / Math.Max(1, o.MarqueeSpeed) * 1000;
            double t = (now - o.MarqueeStart) % (o.MarqueePause + travel);
            if (t < o.MarqueePause) return 0;
            double u = (t - o.MarqueePause) / travel;
            // 開頭 0.4 秒慢慢加速、結尾 0.4 秒慢慢減速（一樣的曲線）：出發和停下的樣子一致，不會一下子衝出去、也不會撞到定點才停
            double ramp = Math.Min(0.5, 400 / travel), full = 2 * ramp * (1 - ramp);
            return period * (u < ramp ? u * u / full : u > 1 - ramp ? 1 - (1 - u) * (1 - u) / full : (u - ramp / 2) / (1 - ramp));
        }

        // 捲動到長圖的第 to 列（從目前畫面上的位置接著捲，花 ms 毫秒）；
        // shade 不是 null 時，長圖每一列的亮度也在這段時間裡從現在的樣子慢慢變成 shade（blur 也一樣：每一列糊多少）
        public void ScrollOverlay(IntPtr hwnd, string name, double to, int ms, float[] shade = null, float[] blur = null, float[] zoom = null)
        {
            WithOverlay(hwnd, name, delegate(Overlay o, long now)
            {
                o.Scroll.Continue(to, ms, now);   // 接著原本的速度捲（歌詞很快換行、拖進度條時不會停一下）
                if (shade == null) return;
                o.ShadeFrom = MixAt(o.ShadeFrom, o.Shade, o.ShadeMix, now);
                o.BlurFrom = MixAt(o.BlurFrom, o.Blur, o.ShadeMix, now);
                o.LineZoomFrom = MixAt(o.LineZoomFrom, o.LineZoom, o.ShadeMix, now);
                o.Shade = shade;
                if (blur != null) o.Blur = blur;
                if (zoom != null) o.LineZoom = zoom;
                o.ShadeMix = new Tween(0, 1, now, ms);
            });
        }

        // 長圖每一列現在的值（換的過程中是新舊兩份混在一起的樣子）
        static float[] MixAt(float[] from, float[] to, Tween mixing, long now)
        {
            if (from == null || to == null || !mixing.Running(now)) return to;
            double mix = mixing.Value(now);
            var current = new float[to.Length];
            for (int i = 0; i < current.Length; i++) current[i] = (float)(from[i] + (to[i] - from[i]) * mix);
            return current;
        }

        // 歌詞唱到間奏（Apple Music 的三個點）：on = false 拿掉。elapsed：間奏已經過了幾秒；length：間奏多長（秒）；
        // playing = false 時停在 elapsed 那一刻。left：點從長圖的第幾欄開始（跟文字的左邊對齊）；size、gap：點的直徑和間距
        public void SetLyricsDots(IntPtr hwnd, string name, bool on, double elapsed, double length, bool playing, double left, double size, double gap)
        {
            WithOverlay(hwnd, name, delegate(Overlay o, long now)
            {
                if (!on || !o.Dots) o.DotsShown = null;   // 新的一段間奏：從該有的樣子開始（不用追）
                o.Dots = on;
                o.DotsPaused = !playing;
                o.DotsAt = elapsed * 1000;
                o.DotsStart = now - (long)(elapsed * 1000);
                o.DotsLen = length;
                o.DotsLeft = left; o.DotsSize = size; o.DotsGap = gap;
            });
        }

        // 三個點：間奏開始時從左邊長出來（0.45 秒），三個點依序從淡變亮（各佔間奏的三分之一），整組慢慢地呼吸（大小 ±6%、1.8 秒一次），
        // 快結束時縮回去（下一句前 0.2～0.65 秒）；暫停時停在那一刻。
        // 拖進度條跳到間奏的別的地方：亮度和大小在約 0.2 秒內平順地變過去（例如從三個都亮退回只亮第一個）
        static void DrawDots(OverlayCanvas canvas, Overlay o, long now, double fade)
        {
            double t = (o.DotsPaused ? o.DotsAt : now - o.DotsStart) / 1000.0, len = o.DotsLen;
            double fillLen = Math.Max(0.5, len - 0.65);
            var goal = new double[4];
            for (int i = 0; i < 3; i++) goal[i] = 0.35 + 0.65 * Ease(t / fillLen * 3 - i);
            goal[3] = Ease(t / 0.45) * Ease((len - 0.2 - t) / 0.45) * (1 + 0.06 * Math.Sin(2 * Math.PI * t / 1.8));   // 長出來、縮回去、呼吸
            if (o.DotsShown == null) o.DotsShown = goal;
            else
            {
                double follow = 1 - Math.Exp(-Math.Max(0, now - o.DotsDrawnAt) / 70.0);   // 時間常數 0.07 秒（約 0.2 秒到位）
                for (int i = 0; i < 4; i++) o.DotsShown[i] += (goal[i] - o.DotsShown[i]) * follow;
            }
            o.DotsDrawnAt = now;
            o.DotsEasing = false;
            for (int i = 0; i < 4; i++) if (Math.Abs(goal[i] - o.DotsShown[i]) > 0.004) o.DotsEasing = true;
            double k = o.DotsShown[3];
            if (k <= 0.01) return;
            double size = o.DotsSize * k, gap = o.DotsGap * k, cy = o.Y + o.ViewH / 2.0;
            double alpha = fade * o.Mask[o.ViewH / 2];
            for (int i = 0; i < 3; i++)
            {
                double x = o.X + o.DotsLeft + i * (size + gap);
                canvas.FillSolid(x, cy - size / 2, size, size, size / 2, 1, 1, 1, Math.Min(1, alpha * o.DotsShown[i]));
            }
        }

        // 畫面上第 y 列在玻璃裡多少（玻璃上緣 top 以上 0；往下 fade 的距離內平順地變到 1）
        // 歌詞離玻璃上緣多近：上緣往下 0.57 × fade（1080p 8 像素）以內完全看不到，再往下 fade 慢慢出現（字跟玻璃的邊至少留 8）
        static double InGlass(double y, double top, double fade) { return Ease((y - top - 0.57 * fade) / fade); }

        // 0～1 之間平順地進出（超出範圍夾住）
        static double Ease(double x) { x = Math.Max(0, Math.Min(1, x)); return x * x * (3 - 2 * x); }

        // 淡入 / 淡出到 to（0～1）；remove = true 時淡出完就拿掉
        public void FadeOverlay(IntPtr hwnd, string name, double to, int ms, bool remove)
        {
            WithOverlay(hwnd, name, delegate(Overlay o, long now)
            {
                o.Fade.Retarget(to, ms, now);
                o.RemoveWhenFaded = remove;
            });
        }

        // ---------- 引擎執行緒 ----------

        // 在引擎執行緒上對 hwnd 這個畫面做 action（找不到、或這個畫面不能疊圖就不做）；做完下一次會重畫
        void WithTarget(IntPtr hwnd, Action<Target, long> action)
        {
            Post(delegate
            {
                var t = targets.Find(x => x.Hwnd == hwnd);
                if (t == null || !t.CanOverlay) return;
                action(t, AnimationClock.Now);
                redraw = true;
            });
        }

        // 在引擎執行緒上對名字是 name 的疊圖做 action（沒有這張就不做）；做完下一次會重畫
        void WithOverlay(IntPtr hwnd, string name, Action<Overlay, long> action)
        {
            Post(delegate
            {
                var t = targets.Find(x => x.Hwnd == hwnd);
                Overlay o;
                if (t == null || !t.TryGet(name, out o)) return;
                action(o, AnimationClock.Now);
                redraw = true;
            });
        }

        // 換上新的疊圖，舊的直接拿掉；o 是 null 就只拿掉
        static void Replace(Target t, string name, Overlay o)
        {
            Overlay old;
            if (t.TryGet(name, out old)) { old.Release(); t.Remove(name); }
            if (o != null) t.Put(name, o);
        }

        // 換上新的疊圖：fadeMs > 0 時新的慢慢淡入、舊的同時淡出（淡完拿掉）；setup 設定新圖的其他屬性
        int fadeSerial;
        void ReplaceFading(Target t, long now, string name, int[] pixels, int width, int height, int x, int y, int z, int fadeMs, Action<Overlay> setup)
        {
            Overlay old;
            if (t.TryGet(name, out old))
            {
                t.Remove(name);
                if (fadeMs > 0)
                {
                    old.Fade.Retarget(0, fadeMs, now);
                    old.RemoveWhenFaded = true;
                    t.Put(name + "#" + (++fadeSerial), old);
                }
                else old.Release();
            }
            if (pixels == null) return;
            var o = CreateOverlay(t, pixels, width, height, x, y);
            o.Z = z;
            if (fadeMs > 0) o.Fade = new Tween(0, 1, now, fadeMs);
            if (setup != null) setup(o);
            t.Put(name, o);
        }

        static Overlay CreateOverlay(Target t, int[] pixels, int width, int height, int x, int y)
        {
            var o = new Overlay { X = x, Y = y, W = width, H = height };
            if (t.D2D != null)
            {
                o.D2DBitmap = t.D2D.CreateBitmap(pixels, width, height);   // 太大（超過顯示卡上限）會是 0，就不畫
                return o;
            }
            var header = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            o.Bitmap = Native.CreateDIBSection(IntPtr.Zero, ref header, 0, out bits, IntPtr.Zero, 0);
            Marshal.Copy(pixels, 0, bits, width * height);
            o.DC = Native.CreateCompatibleDC(IntPtr.Zero);
            o.OldBitmap = Native.SelectObject(o.DC, o.Bitmap);
            return o;
        }

        // 把疊圖用 alpha 混合貼到剛畫好的影片畫面上（Direct2D 或 GDI，見 OverlayCanvas）。
        // 回傳 true 代表還有疊圖在動畫中（捲動、淡入淡出、光暈），下一次螢幕更新要再畫一次
        bool DrawOverlay(Target t)
        {
            if (t.OverlayCount == 0 || !t.CanOverlay) return false;
            long now = AnimationClock.Now;
            var level = PulseLevel;
            double glow = level != null ? level(now) : 0;   // 光暈疊圖現在多亮
            drawnGlow = glow;

            // 淡出完、要拿掉的先拿掉
            List<string> finished = null;
            foreach (var pair in t.Named)
                if (pair.Value.RemoveWhenFaded && !pair.Value.Running(now))
                {
                    if (finished == null) finished = new List<string>();
                    finished.Add(pair.Key);
                }
            if (finished != null)
                foreach (var name in finished) { Overlay o; t.TryGet(name, out o); o.Release(); t.Remove(name); }
            if (t.OverlayCount == 0) return false;

            var canvas = t.Canvas;
            if (!canvas.Begin(t)) return false;
            bool animating = false;
            // 液態玻璃卡片：卡片內容（封面、文字、圖示）貼上去之前，引擎先畫進度條、圓鈕的底；
            // 卡片內容都貼完之後（下一張是歌詞、或全部畫完），再疊上第二層的小玻璃（歌詞圓鈕）
            bool card = t.Liquid != null && t.Liquid.Spec != null, controlsPending = card, topPending = card;
            int follow = -1;   // canvas 現在的變形（-1：還沒設定）
            foreach (var o in t.Ordered)
            {
                if (controlsPending && o.Z >= OverlayLayer.CardContent) { DrawControls(t, canvas, ref follow); controlsPending = false; }
                if (topPending && o.Z > OverlayLayer.CardContent) { DrawTop(t, canvas, ref follow); topPending = false; }
                if (o.Running(now)) animating = true;   // 光暈要不要重畫由引擎看亮度變了多少決定（見 PulseLevel）
                if (!canvas.HasImage(o)) continue;
                if (o.Glass && t.LiquidActive) continue;   // 液態玻璃取代原本的毛玻璃（底和邊框都不畫）
                if (o.ClockDigits && t.ClockGlassOn) continue;   // 玻璃數字取代實心數字
                double fade = o.Fade.Value(now);
                if (fade <= 0.004) continue;
                Follow(t, canvas, o.Follow, ref follow);
                if (o.Pulse) fade *= 0.7 * glow;   // 邊框的光暈：只在重拍時亮，最亮 70%（像呼吸，不是閃）
                if (fade <= 0.004) continue;
                if (o.Pulse && t.Liquid != null && (Math.Abs(t.Liquid.Extended) > 0.5 || Math.Abs(t.Liquid.Wobbled) > 0.0005 || Math.Abs(t.Liquid.Bulged) > 0.05))
                {
                    // 歌詞顯示時卡片的玻璃往上延伸、左右彈：光暈跟著玻璃的形狀（上緣往上挪 Extended、左右各挪寬度的一半 × Wobbled；
                    // 往一側鼓出去時整張挪 Bulged 的一半、兩邊各再寬一半）
                    double bulge = t.Liquid.Bulged;
                    canvas.ImageGrown(o, t.Liquid.Spec.W / 2 * t.Liquid.Wobbled + Math.Abs(bulge) / 2, t.Liquid.Extended, Math.Min(1, fade), bulge / 2);
                    continue;
                }
                if (o.ViewH == 0)
                {
                    canvas.Glass(o, Math.Min(1, fade));
                    double offset = canvas.Snap(o.Move.Value(now));
                    int clip = o.Clip ? canvas.PushClip(o) : 0;
                    double zoom = 1;
                    if (o.Zoom != null)
                    {
                        double dt = o.ZoomAt == 0 ? 0 : Math.Min(0.05, (now - o.ZoomAt) / 1000.0);   // 第一次直接是目標的大小
                        o.ZoomAt = now;
                        zoom = o.Zoom.Step(o.ZoomTo, o.ZoomResponse, o.ZoomDamping, dt);
                    }
                    if (o.MarqueeW > 0) canvas.Marquee(o, offset, MarqueeShift(o, now), Math.Min(1, fade));
                    else if (Math.Abs(zoom - 1) > 0.0005) canvas.ImageZoomed(o, offset, Math.Min(1, fade), zoom, o.ZoomCX + offset, o.ZoomCY);
                    else canvas.Image(o, offset, Math.Min(1, fade));
                    if (o.Clip) canvas.PopClip(clip);
                    continue;
                }
                // 捲動的疊圖：每 2 列一條，各自用遮罩（畫面上的位置）和長圖那幾列的亮度貼上去；
                // 有糊掉的長圖時，照那幾列要糊多少，清楚的和糊掉的兩張交叉淡入淡出（Apple Music：離目前這行越遠越糊）
                // 歌詞在卡片的玻璃裡：只畫到玻璃現在長到的地方（玻璃往上長、縮回去的途中，上緣以上的字不會先跑出來），
                // 靠近上緣的那段柔和地淡出。玻璃還在長 / 縮的時候，字跟著玻璃從下面浮上來、沉下去（玻璃還差多少的 0.35），
                // 上緣淡出的那段也拉長：字是慢慢浮現，不是被移動的邊一刀切進來
                var lc = o.Follow == 2 ? t.Liquid : null;
                double glassTop = lc != null && lc.Spec != null ? lc.Spec.Y - lc.Extended : double.MinValue;
                double glassFade = lc != null && lc.Spec != null ? 14 * lc.Spec.Scale : 1;
                double lift = 0;
                if (lc != null && lc.Spec != null && lc.ExtendFull > 0)
                {
                    double gap = lc.ExtendFull - lc.Extended;   // 縮回去時越來越大；往上彈過頭時是負的（字也跟著往上一點）
                    lift = 0.35 * gap;
                    glassFade *= 1 + 1.5 * Math.Min(1, Math.Abs(gap) / (0.5 * lc.ExtendFull));
                }
                double top = canvas.Snap(o.Scroll.Value(now) - lift);
                bool mixing = o.Shade != null && o.ShadeFrom != null && o.ShadeMix.Running(now);
                bool blurMixing = o.Blur != null && o.BlurFrom != null && o.ShadeMix.Running(now);
                double mix = mixing || blurMixing ? o.ShadeMix.Value(now) : 1;
                bool blurred = o.D2DBlur != IntPtr.Zero && o.Blur != null;
                for (int row = 0; row < o.ViewH; row += 2)
                {
                    int h = Math.Min(2, o.ViewH - row);
                    double source = top + row;
                    if (source < 0 || source + h > o.H) continue;
                    double alpha = fade * o.Mask[row] * InGlass(o.Y + row + h / 2.0, glassTop, glassFade);
                    int at = Math.Min(o.H - 1, (int)(source + h / 2.0));
                    if (o.Shade != null) alpha *= mixing ? o.ShadeFrom[at] + (o.Shade[at] - o.ShadeFrom[at]) * mix : o.Shade[at];
                    if (alpha <= 0.004) continue;
                    double b = !blurred ? 0 : blurMixing ? o.BlurFrom[at] + (o.Blur[at] - o.BlurFrom[at]) * mix : o.Blur[at];
                    // 這一行的大小：照那行的中心、字的左邊縮放（同一行的每一條接在一起，行跟行之間的空白跟著變寬變窄）
                    double k = o.LineZoom == null ? 1 : mixing && o.LineZoomFrom != null ? o.LineZoomFrom[at] + (o.LineZoom[at] - o.LineZoomFrom[at]) * mix : o.LineZoom[at];
                    double anchor = o.LineAnchor == null ? 0 : o.LineAnchor[at] - top;
                    // 先畫糊的、再把清楚的疊上去。一般的疊法下面那張會被上面那張蓋掉一部分，兩張交叉淡換的途中字會變暗：
                    // 清楚的那張照糊的那張平均蓋掉的比例補回來（糊掉的字筆畫散開，透明度大約只剩 0.3）
                    if (b > 0.004) canvas.StripZoomed(o, true, row, h, source, Math.Min(1, alpha * b), k, anchor);
                    if (b < 0.996) canvas.StripZoomed(o, false, row, h, source, Math.Min(1, alpha * (1 - b) / (1 - 0.3 * Math.Min(1, alpha * b))), k, anchor);
                }
                if (o.Dots) DrawDots(canvas, o, now, fade * InGlass(o.Y + o.ViewH / 2.0, glassTop, glassFade));
            }
            if (controlsPending) DrawControls(t, canvas, ref follow);
            if (topPending) DrawTop(t, canvas, ref follow);
            Follow(t, canvas, 0, ref follow);   // 還原（下一格從不變形開始）
            canvas.End();
            return animating;
        }

        // 讓 canvas 照這張圖跟著卡片的方式變形（跟現在一樣就不重設）
        void Follow(Target t, OverlayCanvas canvas, int how, ref int current)
        {
            if (how == current) return;
            current = how;
            double sx = 1, sy = 1, dx = 0, dy = 0;
            if (how == 2) CardTransform(t, out sx, out sy, out dx, out dy);
            else if (how == 1 && t.Motion != null) dy = t.Motion.D;
            canvas.SetTransform(sx, sy, dx, dy);
        }

        // 進度條、圓鈕的底（跟著卡片變形）
        void DrawControls(Target t, OverlayCanvas canvas, ref int follow)
        {
            Follow(t, canvas, 2, ref follow);
            DrawLiquidControls(t, canvas);
        }

        // 卡片的圖都貼完了：先停下 Direct2D，用 Direct3D 疊上第二層的小玻璃，再接著畫
        void DrawTop(Target t, OverlayCanvas canvas, ref int follow)
        {
            if (!t.LiquidActive) return;
            Follow(t, canvas, 0, ref follow);
            canvas.End();
            DrawLiquidTop(t);
            canvas.Resume();
            follow = -1;
        }
    }
}
