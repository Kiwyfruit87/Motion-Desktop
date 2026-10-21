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
            public abstract void Strip(Overlay o, int row, int height, double source, double alpha); // 捲動的歌詞：長圖第 source 列開始的一條
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
