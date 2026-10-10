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
            public abstract void Drift(Overlay o, double dx, double dy, double opacity);   // 飄動的漸層，填滿卡片的圓角範圍
            public virtual void Glass(Overlay o, double opacity) { }                      // 圖底下先把影片糊掉（只有 Direct2D 做得到）
            public abstract int PushClip(Overlay o);
            public abstract void PopClip(int saved);
            public abstract void Image(Overlay o, double offset, double opacity);                   // 整張圖，往右挪 offset
            public abstract void Growing(Overlay o, double offset, double reveal, double opacity);  // 展開到一半的音量條
            public abstract void Strip(Overlay o, int row, int height, double source, double alpha); // 捲動的歌詞：長圖第 source 列開始的一條
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
                    if (!o.Glass || o.D2DBitmap == IntPtr.Zero) { o.GlassReady = false; continue; }
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

            // 用這張圖當筆刷填滿卡片的圓角長方形，圖的位置隨時間移動（圓角由填滿的形狀決定，不會跑掉）
            public override void Drift(Overlay o, double dx, double dy, double opacity)
            {
                if (o.DriftBrush == IntPtr.Zero) o.DriftBrush = d2d.CreateBrush(o.D2DBitmap);
                if (o.DriftBrush != IntPtr.Zero)
                    d2d.FillRounded(o.DriftBrush, o.X + dx, o.Y + dy, o.DriftX, o.DriftY, o.DriftW, o.DriftH, o.DriftRadius, opacity);
            }

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

            // 三塊各自拉伸貼上；關掉邊緣反鋸齒，接縫才不會兩邊都只畫一半、變成一條淡淡的縫
            public override void Growing(Overlay o, double offset, double reveal, double opacity)
            {
                double[] source, dest;
                GrowingParts(o, o.X + offset, reveal, out source, out dest);
                double alpha = opacity * GrowingOpacity(reveal);
                d2d.SetAliased(true);
                for (int i = 0; i < 6; i += 2)
                    if (dest[i + 1] > 0.01 && source[i + 1] > 0)
                        d2d.DrawPart(o.D2DBitmap, source[i], 0, source[i + 1], o.H, dest[i], o.Y, dest[i + 1], o.H, alpha);
                d2d.SetAliased(false);
            }

            public override void Strip(Overlay o, int row, int height, double source, double alpha)
            {
                d2d.Draw(o.D2DBitmap, o.X, o.Y + row, o.W, height, source, alpha);
            }
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

            // 只在卡片的圓角範圍內貼上，圖的位置隨時間移動
            public override void Drift(Overlay o, double dx, double dy, double opacity)
            {
                int keep = Native.SaveDC(dc), corner = (int)Math.Round(o.DriftRadius * 2);
                IntPtr round = Native.CreateRoundRectRgn(o.DriftX, o.DriftY, o.DriftX + o.DriftW + 1, o.DriftY + o.DriftH + 1, corner, corner);
                Native.ExtSelectClipRgn(dc, round, 1 /* RGN_AND */);
                Native.DeleteObject(round);
                Native.AlphaBlend(dc, o.X + (int)Math.Round(dx), o.Y + (int)Math.Round(dy), o.W, o.H, o.DC, 0, 0, o.W, o.H, Blend(opacity));
                Native.RestoreDC(dc, keep);
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

            // 三塊各自拉伸貼上（位置取整數，接縫才不會重疊或空一條）
            public override void Growing(Overlay o, double offset, double reveal, double opacity)
            {
                double[] source, dest;
                GrowingParts(o, o.X + (int)offset, reveal, out source, out dest);
                var growing = new Native.BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Round(255 * opacity * GrowingOpacity(reveal)), AlphaFormat = 1 };
                for (int i = 0; i < 6; i += 2)
                {
                    int sx = (int)Math.Round(source[i]), sw = (int)Math.Round(source[i] + source[i + 1]) - sx;
                    int dx = (int)Math.Round(dest[i]), dw = (int)Math.Round(dest[i] + dest[i + 1]) - dx;
                    if (sw > 0 && dw > 0) Native.AlphaBlend(dc, dx, o.Y, dw, o.H, o.DC, sx, 0, sw, o.H, growing);
                }
            }

            public override void Strip(Overlay o, int row, int height, double source, double alpha)
            {
                Native.AlphaBlend(dc, o.X, o.Y + row, o.W, height, o.DC, 0, (int)source, o.W, height, Blend(alpha));
            }
        }
    }
}
