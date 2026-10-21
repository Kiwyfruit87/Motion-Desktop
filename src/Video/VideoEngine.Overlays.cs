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
            public int Follow;                    // 鎖定畫面滑動時跟著卡片動：0 不動（時鐘）、1 跟著移動（卡片上方的歌詞）、2 跟著移動和變形（卡片本身）
            public Tween Fade = new Tween(1);     // 整張圖的不透明度（淡入淡出）
            public bool RemoveWhenFaded;          // 動畫做完就拿掉（淡出、滑出）
            // 捲動的疊圖（歌詞）：整張長圖只露出 ViewH 高的一段（從第 Scroll 列開始），Mask 是露出那段每一列的亮度（0～1）
            public int ViewH;
            public float[] Mask;
            public Tween Scroll;
            // Shade：長圖每一列的亮度（跟著長圖一起捲動，null = 都一樣亮）；換的時候從 ShadeFrom 慢慢變過去
            public float[] Shade, ShadeFrom;
            public Tween ShadeMix = new Tween(1);
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

            public bool Running(long now) { return Scroll.Running(now) || ShadeMix.Running(now) || Fade.Running(now) || Move.Running(now); }

            public void Release()
            {
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
                if (pixels == null) { if (old != null) old.Release(); return; }

                var o = CreateOverlay(t, pixels, width, height, x, y);
                o.Z = z;
                o.Clip = true; o.ClipX = clipX; o.ClipY = clipY; o.ClipW = clipW; o.ClipH = clipH;
                o.Fade = new Tween(opacity);
                if (shift != 0)
                {
                    if (old != null)
                    {
                        double from = old.Move.Value(now);
                        old.Move = new Tween(from, from - shift, now, ms);
                        old.Fade.Retarget(0, ms, now);   // 一邊滑一邊淡出
                        old.RemoveWhenFaded = true;      // 淡完就拿掉
                        t.Put(name + "#" + now, old);
                    }
                    o.Move = new Tween(shift, 0, now, ms);
                    o.Fade = new Tween(0, opacity, now, ms);   // 新的一邊滑進來一邊淡入
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
                double fade = o.Fade.Value(now);
                if (fade <= 0.004) continue;
                Follow(t, canvas, o.Follow, ref follow);
                if (o.Pulse) fade *= glow;   // 邊框的光暈：只在重拍時亮
                if (fade <= 0.004) continue;
                if (o.ViewH == 0)
                {
                    canvas.Glass(o, Math.Min(1, fade));
                    double offset = canvas.Snap(o.Move.Value(now));
                    int clip = o.Clip ? canvas.PushClip(o) : 0;
                    canvas.Image(o, offset, Math.Min(1, fade));
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
