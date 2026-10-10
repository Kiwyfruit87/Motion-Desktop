using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VideoWallpaper
{
    // 疊圖的順序（數字小的先畫）：
    // 卡片的毛玻璃（左下角的時鐘也在這層）→ 卡片的顏色 → 帶顏色的邊框和光暈 → 封面、文字、按鈕 → 歌詞
    static class OverlayLayer
    {
        public const int Glass = 0, CardColor = 1, CardEdge = 2, CardContent = 3, Lyrics = 4;
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
            public Tween Fade = new Tween(1);     // 整張圖的不透明度（淡入淡出）
            public bool RemoveWhenFaded;          // 動畫做完就拿掉（淡出、滑出）
            // 捲動的疊圖（歌詞）：整張長圖只露出 ViewH 高的一段（從第 Scroll 列開始），Mask 是露出那段每一列的亮度（0～1）
            public int ViewH;
            public float[] Mask;
            public Tween Scroll;
            // Shade：長圖每一列的亮度（跟著長圖一起捲動，null = 都一樣亮）；換的時候從 ShadeFrom 慢慢變過去
            public float[] Shade, ShadeFrom;
            public Tween ShadeMix = new Tween(1);
            // 慢慢飄動的漸層（音樂卡片的顏色）：這張圖比卡片大一圈，用它填滿卡片的圓角長方形 (DriftX, DriftY, DriftW, DriftH)，
            // 圖的位置隨時間在 ±DriftAmpX / ±DriftAmpY 的範圍內慢慢移動
            public bool Drift;
            public int DriftX, DriftY, DriftW, DriftH;
            public double DriftRadius, DriftAmpX, DriftAmpY;
            public IntPtr DriftBrush;
            // 跟著重拍閃的光暈（卡片邊框的光）：平常看不到，亮度跟著 PulseLevel 亮起來再暗回去
            public bool Pulse;
            // 左右滑動（換歌時封面和文字的翻頁動畫），只在 Clip 範圍內看得到
            public Tween Move;
            public bool Clip;
            public int ClipX, ClipY, ClipW, ClipH;
            // 展開 / 收起（音量條）：圖上 RevealX 開始、RevealW 寬的這段，展開到一半時從左邊長出來——
            // 切成「左邊滑桿｜把手（RevealPivot 左右 RevealPivotW）｜右邊滑桿」三塊，兩邊照展開程度拉長、把手保持原樣。RevealW = 0 就是不用
            public int RevealX, RevealW;
            public double RevealPivot, RevealPivotW;
            public Tween Reveal = new Tween(1);
            // 毛玻璃：圖底下這塊圓角長方形 (GlassX, GlassY, GlassW, GlassH) 的影片先糊掉再貼圖
            public bool Glass;
            public int GlassX, GlassY, GlassW, GlassH;
            public double GlassRadius;
            public bool GlassReady;
            public int BlurX, BlurY, BlurW, BlurH;

            public bool Running(long now) { return Scroll.Running(now) || ShadeMix.Running(now) || Fade.Running(now) || Move.Running(now) || Reveal.Running(now); }

            public void Release()
            {
                if (DriftBrush != IntPtr.Zero) { Marshal.Release(DriftBrush); DriftBrush = IntPtr.Zero; }
                if (D2DBitmap != IntPtr.Zero) { Marshal.Release(D2DBitmap); D2DBitmap = IntPtr.Zero; }
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

        // 慢慢飄動的漸層（音樂卡片的顏色）：pixels 是比卡片大一圈的漸層圖，平常左上角放在 (x, y)；
        // 用它填滿卡片的圓角長方形 (cardX, cardY, cardW, cardH)，位置在 ±ampX / ±ampY 的範圍內慢慢移動。換顏色時跟 SetFadeOverlay 一樣淡入淡出
        public void SetDriftOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y, int z,
            int cardX, int cardY, int cardW, int cardH, double radius, double ampX, double ampY, int fadeMs)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                ReplaceFading(t, now, name, pixels, width, height, x, y, z, fadeMs, delegate(Overlay o)
                {
                    o.Drift = true;
                    o.DriftX = cardX; o.DriftY = cardY; o.DriftW = cardW; o.DriftH = cardH;
                    o.DriftRadius = radius; o.DriftAmpX = ampX; o.DriftAmpY = ampY;
                });
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
        // shade 是長圖每一列的亮度（跟著長圖捲動，null = 都一樣亮），兩個相乘。出現時用 fadeMs 淡入
        public void SetScrollOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y, int viewH, float[] mask, float[] shade, double scroll, int fadeMs)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                var o = CreateOverlay(t, pixels, width, height, x, y);
                o.Z = OverlayLayer.Lyrics;
                o.ViewH = viewH;
                o.Mask = mask;
                o.Shade = shade;
                o.Scroll = new Tween(scroll);
                o.Fade = new Tween(0, 1, now, fadeMs);
                Replace(t, name, o);
            });
        }

        // 可以左右滑動的疊圖（音樂卡片的封面和文字），只在 (clipX, clipY, clipW, clipH) 範圍內看得到。
        // shift ≠ 0：換歌的翻頁動畫——舊的往左滑出 shift、新的從右邊 shift 處滑進來（shift 是負的就反過來），
        //            兩張一起移動、間距不變，舊的完全離開卡片才拿掉。
        // shift = 0：只是換圖（例如封面晚一點才讀到），如果正在滑動就接著滑，不會跳。
        // opacity：這張圖的亮度（0～1）。只是換圖（shift = 0）時，從原本的亮度花 fadeMs 慢慢變到新的亮度
        // revealW > 0：可以展開 / 收起的音量條（範圍 revealX 開始 revealW 寬，把手在 pivot、左右各 pivotW；
        // reveal 是展開程度，之後用 RevealOverlay 慢慢展開 / 收起）；換圖時接著原本的展開程度
        public void SetSlideOverlay(IntPtr hwnd, string name, int[] pixels, int width, int height, int x, int y, int z,
            int clipX, int clipY, int clipW, int clipH, int shift, int ms, double opacity, int fadeMs,
            int revealX = 0, int revealW = 0, double reveal = 1, double pivot = 0, double pivotW = 0)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                Overlay old;
                t.TryGet(name, out old);
                if (old != null) t.Remove(name);
                if (pixels == null) { if (old != null) old.Release(); return; }

                var o = CreateOverlay(t, pixels, width, height, x, y);
                o.Z = z;
                o.Clip = true; o.ClipX = clipX; o.ClipY = clipY; o.ClipW = clipW; o.ClipH = clipH;
                o.Fade = new Tween(opacity);
                o.RevealX = revealX; o.RevealW = revealW; o.RevealPivot = pivot; o.RevealPivotW = pivotW;
                o.Reveal = revealW > 0 && old != null && old.RevealW > 0 ? old.Reveal : new Tween(reveal);
                if (shift != 0)
                {
                    if (old != null)
                    {
                        double from = old.Move.Value(now);
                        old.Move = new Tween(from, from - shift, now, ms);
                        old.Fade.Freeze(now);         // 亮度停在現在的樣子
                        old.RemoveWhenFaded = true;   // 滑完就拿掉
                        t.Put(name + "#" + now, old);
                    }
                    o.Move = new Tween(shift, 0, now, ms);
                }
                else if (old != null)
                {
                    o.Move = old.Move;
                    double current = old.Fade.Value(now);
                    if (Math.Abs(current - opacity) > 0.001) o.Fade = new Tween(current, opacity, now, fadeMs);
                    old.Release();
                }
                t.Put(name, o);
            });
        }

        // 捲動到長圖的第 to 列（從目前畫面上的位置接著捲，花 ms 毫秒）；
        // shade 不是 null 時，長圖每一列的亮度也在這段時間裡從現在的樣子慢慢變成 shade
        public void ScrollOverlay(IntPtr hwnd, string name, double to, int ms, float[] shade = null)
        {
            WithOverlay(hwnd, name, delegate(Overlay o, long now)
            {
                o.Scroll.Retarget(to, ms, now);
                if (shade == null) return;
                o.ShadeFrom = ShadeAt(o, now);
                o.Shade = shade;
                o.ShadeMix = new Tween(0, 1, now, ms);
            });
        }

        // 長圖每一列現在的亮度（換的過程中是新舊兩份混在一起的樣子）
        static float[] ShadeAt(Overlay o, long now)
        {
            if (o.ShadeFrom == null || o.Shade == null || !o.ShadeMix.Running(now)) return o.Shade;
            double mix = o.ShadeMix.Value(now);
            var current = new float[o.Shade.Length];
            for (int i = 0; i < current.Length; i++) current[i] = (float)(o.ShadeFrom[i] + (o.Shade[i] - o.ShadeFrom[i]) * mix);
            return current;
        }

        // 淡入 / 淡出到 to（0～1）；remove = true 時淡出完就拿掉
        public void FadeOverlay(IntPtr hwnd, string name, double to, int ms, bool remove)
        {
            WithOverlay(hwnd, name, delegate(Overlay o, long now)
            {
                o.Fade.Retarget(to, ms, now);
                o.RemoveWhenFaded = remove;
            });
        }

        // 展開 / 收起（to = 1 全部露出、0 收起來），從目前的樣子接著變，花 ms 毫秒
        public void RevealOverlay(IntPtr hwnd, string name, double to, int ms)
        {
            WithOverlay(hwnd, name, delegate(Overlay o, long now)
            {
                if (o.RevealW > 0) o.Reveal = new Tween(RevealProgress(o.Reveal, now), to, now, ms);
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

        // 飄動的位置：左右、上下用不同的週期（31 秒、23 秒），軌跡不會一直重複同一條線，移動也很慢
        static void DriftOffset(Overlay o, long now, out double dx, out double dy)
        {
            double seconds = now / 1000.0;
            dx = o.DriftAmpX * Math.Sin(2 * Math.PI * seconds / 31);
            dy = o.DriftAmpY * Math.Sin(2 * Math.PI * seconds / 23 + 1.3);
        }

        // 展開的程度：展開用減速曲線（一開始就動、最後慢慢停下），收起用加速曲線（慢慢起步、滑回去）
        static double RevealProgress(Tween r, long now)
        {
            if (r.Ms <= 0 || now - r.Start >= r.Ms) return r.To;
            double t = (now - r.Start) / (double)r.Ms;
            double eased = r.To >= r.From ? 1 - Math.Pow(1 - t, 3) : t * t;
            return r.From + (r.To - r.From) * eased;
        }

        // 展開到一半的音量條：切成「左邊滑桿｜把手｜右邊滑桿」三塊，兩邊照展開程度拉長、把手保持原樣（不會被壓扁），
        // 整條從喇叭旁邊長出來、同時淡入；完全展開時三塊剛好接回原本的圖。x：這張圖現在在畫面上的左邊
        static void GrowingParts(Overlay o, double x, double reveal, out double[] source, out double[] dest)
        {
            double a = o.RevealX, d = o.RevealX + o.RevealW;
            double b = Math.Max(a, Math.Min(d, o.RevealPivot - o.RevealPivotW));
            double c = Math.Max(b, Math.Min(d, o.RevealPivot + o.RevealPivotW));
            double left = (b - a) * reveal, right = (d - c) * reveal;
            source = new[] { a, b - a, b, c - b, c, d - c };                                      // 每塊在圖上的左邊和寬度
            dest = new[] { x + a, left, x + a + left, c - b, x + a + left + (c - b), right };    // 每塊在畫面上的左邊和寬度
        }

        static double GrowingOpacity(double reveal) { return Math.Min(1, reveal * 1.6); }   // 長到六成多就完全不透明

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
            foreach (var o in t.Ordered)
            {
                if (o.Running(now)) animating = true;   // 光暈要不要重畫由引擎看亮度變了多少決定（見 PulseLevel）
                if (!canvas.HasImage(o)) continue;
                double fade = o.Fade.Value(now);
                if (fade <= 0.004) continue;
                if (o.Drift)
                {
                    // 慢慢飄動的漸層：只在卡片的圓角範圍內，圖的位置隨時間移動
                    double dx, dy;
                    DriftOffset(o, now, out dx, out dy);
                    canvas.Drift(o, dx, dy, Math.Min(1, fade));
                    continue;
                }
                if (o.Pulse) fade *= glow;   // 邊框的光暈：只在重拍時亮
                if (fade <= 0.004) continue;
                if (o.ViewH == 0)
                {
                    double reveal = o.RevealW > 0 ? RevealProgress(o.Reveal, now) : 1;
                    if (reveal <= 0.001) continue;
                    canvas.Glass(o, Math.Min(1, fade));
                    double offset = canvas.Snap(o.Move.Value(now));
                    int clip = o.Clip ? canvas.PushClip(o) : 0;
                    if (reveal < 0.999) canvas.Growing(o, offset, reveal, Math.Min(1, fade));
                    else canvas.Image(o, offset, Math.Min(1, fade));
                    if (o.Clip) canvas.PopClip(clip);
                    continue;
                }
                // 捲動的疊圖：每 2 列一條，各自用遮罩（畫面上的位置）和長圖那幾列的亮度貼上去
                double top = canvas.Snap(o.Scroll.Value(now));
                bool mixing = o.Shade != null && o.ShadeFrom != null && o.ShadeMix.Running(now);
                double mix = mixing ? o.ShadeMix.Value(now) : 1;
                for (int row = 0; row < o.ViewH; row += 2)
                {
                    int h = Math.Min(2, o.ViewH - row);
                    double source = top + row;
                    if (source < 0 || source + h > o.H) continue;
                    double alpha = fade * o.Mask[row];
                    if (o.Shade != null)
                    {
                        int at = Math.Min(o.H - 1, (int)(source + h / 2.0));
                        alpha *= mixing ? o.ShadeFrom[at] + (o.Shade[at] - o.ShadeFrom[at]) * mix : o.Shade[at];
                    }
                    if (alpha <= 0.004) continue;
                    canvas.Strip(o, row, h, source, Math.Min(1, alpha));
                }
            }
            canvas.End();
            return animating;
        }
    }
}
