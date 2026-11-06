using System;

namespace VideoWallpaper
{
    partial class VideoEngine
    {
        // 疊圖畫在哪：Direct2D（D2DCanvas）或退回 GDI（GdiCanvas，這台電腦不支援 Direct2D 時）。
        // 要畫什麼、畫多亮由 DrawOverlay 決定，這裡只負責怎麼畫
        abstract class OverlayCanvas
        {
            public abstract bool Begin(Target t);   // 開始畫；不能畫（例如拿不到 GDI 的 DC）就回傳 false
            public abstract void End();
            public abstract bool HasImage(Overlay o);
            public virtual double Snap(double position) { return position; }   // 位置可以有小數嗎（GDI 只能畫在整數位置）
            public virtual void Glass(Overlay o, double opacity) { }                      // 圖底下先把影片糊掉（只有 Direct2D 做得到）
            public abstract int PushClip(Overlay o);
            public abstract void PopClip(int saved);
            public abstract void Image(Overlay o, double offset, double opacity);                   // 整張圖，往右挪 offset
            // 整張圖跟著卡片的玻璃變形：左右兩邊各往外 growX、上緣往上 growY（負的是往內），中間拉長補起來（只有 Direct2D 做得到，GDI 照原樣畫）
            public virtual void ImageGrown(Overlay o, double growX, double growY, double opacity, double shiftX = 0) { Image(o, 0, opacity); }
            public abstract void Strip(Overlay o, int row, int height, double source, double alpha); // 捲動的歌詞：長圖第 source 列開始的一條
            public virtual void StripBlurred(Overlay o, int row, int height, double source, double alpha) { }   // 同上，糊掉的那張（只有 Direct2D 有）
            // 同上，照 anchor（露出那段的第幾列）、字的左邊（o.LineLeft）縮放 k 倍；blurred：糊掉的那張。不能縮放的就照原本的大小
            public virtual void StripZoomed(Overlay o, bool blurred, int row, int height, double source, double alpha, double k, double anchor)
            {
                if (blurred) StripBlurred(o, row, height, source, alpha); else Strip(o, row, height, source, alpha);
            }
            // 整張圖以畫面上的 (cx, cy) 為中心縮放 zoom 倍（不能縮放的就照原本的大小）
            public virtual void ImageZoomed(Overlay o, double offset, double opacity, double zoom, double cx, double cy) { Image(o, offset, opacity); }
            // 跑馬燈：往左捲了 shift（見 Overlay.Marquee*；不能捲的就照原本的樣子）
            public virtual void Marquee(Overlay o, double offset, double shift, double opacity) { Image(o, offset, opacity); }
            // 液態玻璃卡片用（只有 Direct2D 做得到；GDI 不做）：
            public virtual void SetTransform(double sx, double sy, double dx, double dy) { }   // 接下來畫的整個變形（卡片的慣性）
            public virtual void FillSolid(double x, double y, double w, double h, double radius, double r, double g, double b, double a) { }
            // 只露出 (x, y, w, h) 這段的圓角長方形 (shapeX, y, shapeW, h)：進度條已播放的部分，左端圓、右端切平
            public virtual void FillSolid(double x, double y, double w, double h, double radius, double r, double g, double b, double a, double shapeX, double shapeW) { }
            public virtual void Resume() { }   // End 之後接著畫（中間插了 Direct3D 的第二層玻璃）
        }

        // Direct2D：直接在顯示卡上畫，不用把畫面借給 GDI；位置可以有小數（線性內插），捲動、滑動更滑
        class D2DCanvas : OverlayCanvas
        {
            readonly D2DTarget d2d;
            long blurredAt = -100000;   // 上次重做模糊的時間
            public D2DCanvas(D2DTarget d2d) { this.d2d = d2d; }

            public override bool Begin(Target t)
            {
                // 毛玻璃要讀取底下的影片，所以先在開始畫疊圖之前做好（範圍往外多抓一點，邊緣才糊得自然）。
                // 範圍一樣的話，影片沒換格（只是疊圖在動）、或離上次重做不到 33 毫秒，就沿用上次模糊好的：
                // 後面本來就糊掉了，一秒更新 30 次看起來跟 60 次一樣，卻省下一半最花時間的部分
                long now = AnimationClock.Now;
                bool reblur = t.FreshVideo && now - blurredAt >= 33;
                foreach (var o in t.All)
                {
                    if (!o.Glass || o.D2DBitmap == IntPtr.Zero || t.LiquidActive) { o.GlassReady = false; continue; }   // 液態玻璃取代毛玻璃
                    int margin = o.GlassH / 3;
                    o.BlurX = Math.Max(0, o.GlassX - margin); o.BlurY = Math.Max(0, o.GlassY - margin);
                    o.BlurW = Math.Min(t.Width, o.GlassX + o.GlassW + margin) - o.BlurX;
                    o.BlurH = Math.Min(t.Height, o.GlassY + o.GlassH + margin) - o.BlurY;
                    o.GlassReady = !reblur && d2d.HasBlur(o.BlurX, o.BlurY, o.BlurW, o.BlurH);
                    if (!o.GlassReady && (o.GlassReady = d2d.PrepareBlur(o.BlurX, o.BlurY, o.BlurW, o.BlurH))) blurredAt = now;
                }
                d2d.Begin();
                return true;
            }

            public override void End() { d2d.End(); }

            public override bool HasImage(Overlay o) { return o.D2DBitmap != IntPtr.Zero; }

            public override void Glass(Overlay o, double opacity)
            {
                if (o.GlassReady) d2d.FillBlur(o.GlassX, o.GlassY, o.GlassW, o.GlassH, o.GlassRadius, o.BlurX, o.BlurY, o.BlurW, o.BlurH, opacity);
            }

            public override int PushClip(Overlay o) { d2d.PushClip(o.ClipX, o.ClipY, o.ClipW, o.ClipH); return 0; }

            public override void PopClip(int saved) { d2d.PopClip(); }

            public override void Image(Overlay o, double offset, double opacity)
            {
                d2d.Draw(o.D2DBitmap, o.X + offset, o.Y, o.W, o.H, 0, opacity);
            }

            public override void Strip(Overlay o, int row, int height, double source, double alpha)
            {
                d2d.Draw(o.D2DBitmap, o.X, o.Y + row, o.W, height, source, alpha);
            }

            // 圖從正中間切成九塊：四個角照原樣挪出去（左邊往左、右邊往右各 growX，上面往上 growY），
            // 中間的十字從正中間那兩列 / 兩欄拉長補起來（邊框的光是一圈，中間那段直的，拉長看不出來）
            public override void ImageGrown(Overlay o, double growX, double growY, double opacity, double shiftX = 0)
            {
                double mx = Math.Round(o.W / 2.0), my = Math.Round(o.H / 2.0), ox = o.X + shiftX;   // shiftX：整張左右挪（卡片的玻璃往一側鼓出去）
                // 往內縮（grow 是負的：玻璃變窄、縮回去時稍微縮過頭）：角一樣往內挪，但從靠中間那側切掉縮掉的寬度，
                // 兩半才不會疊在一起（疊起來那段的光會變兩倍亮）；切掉的是中間直的那段，看不出來
                double cx = Math.Max(0, Math.Min(mx, -growX)), cy = Math.Max(0, Math.Min(my, -growY));
                // 每一欄 / 每一列：來源從哪裡、多寬，畫到哪裡、多寬（中間那段沒有往外長就不畫）
                double[] sx = { 0, mx - 1, mx + cx }, sw = { mx - cx, 2, o.W - mx - cx }, dx = { ox - growX, ox + mx - growX, ox + mx + Math.Max(0, growX) }, dw = { mx - cx, 2 * growX, o.W - mx - cx };
                double[] sy = { 0, my - 1, my }, sh = { my - cy, 2, o.H - my }, dy = { o.Y - growY, o.Y + my - growY, o.Y + my }, dh = { my - cy, growY, o.H - my };
                for (int j = 0; j < 3; j++)
                    for (int i = 0; i < 3; i++)
                        if (dw[i] > 0.01 && dh[j] > 0.01) d2d.DrawPart(o.D2DBitmap, sx[i], sy[j], sw[i], sh[j], dx[i], dy[j], dw[i], dh[j], opacity);
            }

            public override void StripBlurred(Overlay o, int row, int height, double source, double alpha)
            {
                d2d.Draw(o.D2DBlur, o.X, o.Y + row, o.W, height, source, alpha);
            }

            public override void StripZoomed(Overlay o, bool blurred, int row, int height, double source, double alpha, double k, double anchor)
            {
                var bitmap = blurred ? o.D2DBlur : o.D2DBitmap;
                if (bitmap == IntPtr.Zero) return;
                if (Math.Abs(k - 1) < 0.0005) { d2d.Draw(bitmap, o.X, o.Y + row, o.W, height, source, alpha); return; }
                d2d.DrawPart(bitmap, 0, source, o.W, height, o.X + o.LineLeft * (1 - k), o.Y + anchor + (row - anchor) * k, o.W * k, height * k, alpha);
            }

            public override void ImageZoomed(Overlay o, double offset, double opacity, double zoom, double cx, double cy)
            {
                d2d.DrawPart(o.D2DBitmap, 0, 0, o.W, o.H, cx + (o.X + offset - cx) * zoom, cy + (o.Y - cy) * zoom, o.W * zoom, o.H * zoom, opacity);
            }

            // 兩份一前一後；中間那段整張貼，兩端淡出的那段每 2 像素一條、各自照離邊緣多遠調亮度。
            // 左邊的淡出跟著捲了多少出現、快捲完時收回去：停著的時候字的開頭是清楚的，捲到終點的樣子跟出發前完全一樣，不會跳一下
            public override void Marquee(Overlay o, double offset, double shift, double opacity)
            {
                double v0 = o.MarqueeViewX + offset, v1 = v0 + o.MarqueeViewW, period = o.MarqueeW + o.MarqueeGap;
                double right = o.MarqueeFade, left = o.MarqueeFade * Math.Min(1, Math.Min(shift, period - shift) / Math.Max(1, o.MarqueeFade));
                double[] at = { o.X + offset - shift, o.X + offset - shift + period };
                d2d.PushClip(v0 + left, o.Y, v1 - right - v0 - left, o.H);
                foreach (double x in at) d2d.Draw(o.D2DBitmap, x, o.Y, o.W, o.H, 0, opacity);
                d2d.PopClip();
                for (double x = v0; x < v0 + left; x += 2) Column(o, at, x, Math.Min(2, v0 + left - x), opacity * Ease((x + 1 - v0) / left));
                for (double x = v1 - right; x < v1; x += 2) Column(o, at, x, Math.Min(2, v1 - x), opacity * Ease((v1 - x - 1) / right));
            }

            void Column(Overlay o, double[] at, double x, double w, double alpha)
            {
                if (alpha <= 0.004) return;
                foreach (double bx in at)
                {
                    double sx = x - bx, s0 = Math.Max(0, sx), s1 = Math.Min(o.W, sx + w);
                    if (s1 > s0) d2d.DrawPart(o.D2DBitmap, s0, 0, s1 - s0, o.H, bx + s0, o.Y, s1 - s0, o.H, alpha);
                }
            }

            static double Ease(double x) { x = Math.Max(0, Math.Min(1, x)); return x * x * (3 - 2 * x); }

            public override void SetTransform(double sx, double sy, double dx, double dy) { d2d.SetTransform(sx, sy, dx, dy); }

            public override void FillSolid(double x, double y, double w, double h, double radius, double r, double g, double b, double a)
            {
                d2d.FillSolid(x, y, w, h, radius, r, g, b, a);
            }

            public override void FillSolid(double x, double y, double w, double h, double radius, double r, double g, double b, double a, double shapeX, double shapeW)
            {
                d2d.PushClip(x, y, w, h);
                d2d.FillSolid(shapeX, y, shapeW, h, radius, r, g, b, a);
                d2d.PopClip();
            }

            public override void Resume() { d2d.Begin(); }
        }

        // 退回 GDI：這台電腦不支援 Direct2D 時才用。只能畫在整數位置、不能做毛玻璃
        class GdiCanvas : OverlayCanvas
        {
            readonly IDXGISurface1 surface;
            IntPtr dc;
            public GdiCanvas(IDXGISurface1 surface) { this.surface = surface; }

            public override bool Begin(Target t) { return surface.GetDC(0, out dc) >= 0; }

            public override void End() { surface.ReleaseDC(IntPtr.Zero); }

            public override bool HasImage(Overlay o) { return true; }

            public override double Snap(double position) { return Math.Round(position); }

            static Native.BLENDFUNCTION Blend(double opacity)
            {
                return new Native.BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Round(255 * opacity), AlphaFormat = 1 };
            }

            public override int PushClip(Overlay o)
            {
                int saved = Native.SaveDC(dc);
                Native.IntersectClipRect(dc, o.ClipX, o.ClipY, o.ClipX + o.ClipW, o.ClipY + o.ClipH);
                return saved;
            }

            public override void PopClip(int saved) { Native.RestoreDC(dc, saved); }

            public override void Image(Overlay o, double offset, double opacity)
            {
                Native.AlphaBlend(dc, o.X + (int)offset, o.Y, o.W, o.H, o.DC, 0, 0, o.W, o.H, Blend(opacity));
            }

            public override void Strip(Overlay o, int row, int height, double source, double alpha)
            {
                Native.AlphaBlend(dc, o.X, o.Y + row, o.W, height, o.DC, 0, (int)source, o.W, height, Blend(alpha));
            }
        }
    }
}
