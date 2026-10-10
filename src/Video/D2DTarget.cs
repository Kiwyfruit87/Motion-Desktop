using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VideoWallpaper
{
    // Direct2D：把疊圖（時鐘、正在播放、歌詞）直接用顯示卡畫在影片畫面上。
    // 以前用 GDI 的 GetDC 貼圖，GetDC 偶爾會卡住 100 毫秒以上，影片跟著卡一下、那一格的疊圖還會變亮。
    // 直接呼叫 COM 介面的函式表（d2d1.h 的順序），不需要額外的套件。
    class D2DTarget
    {
        [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid riid, IntPtr options, out IntPtr factory);
        static readonly Guid IID_ID2D1Factory = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
        static readonly Guid IID_IDXGISurface = new Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
        // 像素格式：87 = DXGI_FORMAT_B8G8R8A8_UNORM（BGRA）；alpha：1 = 預先乘好、3 = 忽略（輸出畫面不用 alpha）
        const int Bgra = 87, AlphaPremultiplied = 1, AlphaIgnore = 3;

        [StructLayout(LayoutKind.Sequential)] struct RectF { public float Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct SizeU { public uint Width, Height; }
        [StructLayout(LayoutKind.Sequential)] struct SizeF { public float Width, Height; }
        [StructLayout(LayoutKind.Sequential)] struct PointU { public uint X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RectU { public uint Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct PixelFormat { public int Format, AlphaMode; }
        [StructLayout(LayoutKind.Sequential)] struct RoundedRect { public RectF Rect; public float RadiusX, RadiusY; }
        [StructLayout(LayoutKind.Sequential)] struct Matrix { public float M11, M12, M21, M22, Dx, Dy; }
        [StructLayout(LayoutKind.Sequential)] struct BitmapBrushProperties { public int ExtendModeX, ExtendModeY, Interpolation; }
        [StructLayout(LayoutKind.Sequential)] struct BrushProperties { public float Opacity; public Matrix Transform; }
        [StructLayout(LayoutKind.Sequential)] struct BitmapProperties { public int Format, AlphaMode; public float DpiX, DpiY; }
        [StructLayout(LayoutKind.Sequential)] struct RenderTargetProperties { public int Type, Format, AlphaMode; public float DpiX, DpiY; public int Usage, MinLevel; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateDxgiSurfaceRenderTargetFn(IntPtr self, IntPtr surface, ref RenderTargetProperties props, out IntPtr target);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBitmapFn(IntPtr self, SizeU size, IntPtr data, uint pitch, ref BitmapProperties props, out IntPtr bitmap);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DrawBitmapFn(IntPtr self, IntPtr bitmap, ref RectF dest, float opacity, int interpolation, ref RectF source);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void BeginDrawFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EndDrawFn(IntPtr self, out ulong tag1, out ulong tag2);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateCompatibleRenderTargetFn(IntPtr self, ref SizeF size, ref SizeU pixelSize, ref PixelFormat format, int options, out IntPtr target);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBitmapFn(IntPtr self, out IntPtr bitmap);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CopyFromRenderTargetFn(IntPtr self, ref PointU dest, IntPtr renderTarget, ref RectU source);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBitmapBrushFn(IntPtr self, IntPtr bitmap, ref BitmapBrushProperties props, ref BrushProperties brushProps, out IntPtr brush);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetTransformFn(IntPtr self, ref Matrix transform);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetOpacityFn(IntPtr self, float opacity);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FillRoundedRectangleFn(IntPtr self, ref RoundedRect rect, IntPtr brush);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void PushAxisAlignedClipFn(IntPtr self, ref RectF rect, int antialias);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void PopAxisAlignedClipFn(IntPtr self);
        PushAxisAlignedClipFn pushClip;
        PopAxisAlignedClipFn popClip;

        // 接下來畫的東西只在 (x, y, w, h) 範圍內看得到，畫完要 PopClip
        public void PushClip(double x, double y, double w, double h)
        {
            if (pushClip == null) { pushClip = ComVtable.Method<PushAxisAlignedClipFn>(target, 45); popClip = ComVtable.Method<PopAxisAlignedClipFn>(target, 46); }   // ID2D1RenderTarget::PushAxisAlignedClip / PopAxisAlignedClip
            var rect = new RectF { Left = (float)x, Top = (float)y, Right = (float)(x + w), Bottom = (float)(y + h) };
            pushClip(target, ref rect, 0 /* 邊緣反鋸齒 */);
        }

        public void PopClip() { popClip(target); }

        readonly IntPtr target;
        readonly CreateBitmapFn createBitmap;
        readonly DrawBitmapFn drawBitmap;
        readonly BeginDrawFn beginDraw;
        readonly EndDrawFn endDraw;

        D2DTarget(IntPtr target)
        {
            this.target = target;
            createBitmap = ComVtable.Method<CreateBitmapFn>(target, 4);    // ID2D1RenderTarget::CreateBitmap
            drawBitmap = ComVtable.Method<DrawBitmapFn>(target, 26);      // ::DrawBitmap
            beginDraw = ComVtable.Method<BeginDrawFn>(target, 48);        // ::BeginDraw
            endDraw = ComVtable.Method<EndDrawFn>(target, 49);            // ::EndDraw
        }

        // ---------- 毛玻璃 ----------
        // 把畫面上一塊複製下來，連續縮小一半 3 次（每次把 2×2 個點平均成 1 個點），再一次放大 2 倍慢慢放大回去：
        // 等於把影片糊掉，但還看得出後面燈光、建築的模糊輪廓（縮太多會變成一片均勻的灰）。
        // 影片每換一格就重做，所以後面的影片在動，毛玻璃也會跟著變（沒換格時沿用，見 HasBlur）。
        const int BlurLevels = 2;   // 縮到 1/4：後面的燈光、建築還看得出模糊的輪廓

        class Level
        {
            public IntPtr Target, Bitmap;
            public int W, H;
            public BeginDrawFn Begin;
            public EndDrawFn End;
            public DrawBitmapFn Draw;
        }

        IntPtr copy, brush;
        int copyW, copyH;
        readonly List<Level> levels = new List<Level>();
        CopyFromRenderTargetFn copyFrom;

        int blurX, blurY, blurW, blurH;   // 現在模糊圖是哪一塊（blurW = 0：沒有）

        // 現在留著的模糊圖就是 (x, y, w, h) 這塊
        public bool HasBlur(int x, int y, int w, int h) { return blurW > 0 && x == blurX && y == blurY && w == blurW && h == blurH; }

        // 準備好 (x, y, w, h) 這塊的模糊圖；要在 Begin() 之前呼叫（這時畫面上只有影片）
        public bool PrepareBlur(int x, int y, int w, int h)
        {
            blurW = 0;
            if (w < 32 || h < 32) return false;
            if ((w != copyW || h != copyH) && !CreateBlurResources(w, h)) return false;
            var dest = new PointU();
            var source = new RectU { Left = (uint)x, Top = (uint)y, Right = (uint)(x + w), Bottom = (uint)(y + h) };
            if (copyFrom(copy, ref dest, target, ref source) < 0) return false;

            IntPtr from = copy;
            int fromW = w, fromH = h;
            foreach (var level in levels)
            {
                DrawLevel(level, from, fromW, fromH);
                from = level.Bitmap; fromW = level.W; fromH = level.H;
            }
            // 再一次放大 2 倍、慢慢放大回 1/2（最後一次放大在 FillBlur），比一次放大 8 倍平滑，不會有格子感
            for (int i = levels.Count - 2; i >= 0; i--)
                DrawLevel(levels[i], levels[i + 1].Bitmap, levels[i + 1].W, levels[i + 1].H);
            blurX = x; blurY = y; blurW = w; blurH = h;
            return true;
        }

        static void DrawLevel(Level level, IntPtr bitmap, int w, int h)
        {
            var dest = new RectF { Right = level.W, Bottom = level.H };
            var source = new RectF { Right = w, Bottom = h };
            ulong tag1, tag2;
            level.Begin(level.Target);
            level.Draw(level.Target, bitmap, ref dest, 1, 1 /* 線性 */, ref source);
            level.End(level.Target, out tag1, out tag2);
        }

        // 把模糊圖填進圓角長方形 (x, y, w, h)；(blurX, blurY, blurW, blurH) 是 PrepareBlur 時的那塊範圍
        public void FillBlur(double x, double y, double w, double h, double radius, int blurX, int blurY, int blurW, int blurH, double opacity)
        {
            var up = levels[0];
            Fill(brush, new Matrix { M11 = blurW / (float)up.W, M22 = blurH / (float)up.H, Dx = blurX, Dy = blurY }, opacity, x, y, w, h, radius);
        }

        // ---------- 慢慢飄動的漸層（音樂卡片的顏色）----------
        CreateBitmapBrushFn createBrush;

        // 用 bitmap 做一支筆刷（之後用它填滿圓角長方形，位置可以隨時移動）；失敗回傳 0
        public IntPtr CreateBrush(IntPtr bitmap)
        {
            if (createBrush == null) createBrush = ComVtable.Method<CreateBitmapBrushFn>(target, 7);   // ID2D1RenderTarget::CreateBitmapBrush
            var props = new BitmapBrushProperties { ExtendModeX = 0, ExtendModeY = 0, Interpolation = 1 };   // 邊緣延伸、線性內插
            var common = new BrushProperties { Opacity = 1, Transform = new Matrix { M11 = 1, M22 = 1 } };
            IntPtr result;
            return createBrush(target, bitmap, ref props, ref common, out result) < 0 ? IntPtr.Zero : result;
        }

        // 用筆刷填滿圓角長方形 (x, y, w, h)；筆刷的圖左上角放在 (left, top)（可以有小數，移動才平順）
        public void FillRounded(IntPtr brushHandle, double left, double top, double x, double y, double w, double h, double radius, double opacity)
        {
            Fill(brushHandle, new Matrix { M11 = 1, M22 = 1, Dx = (float)left, Dy = (float)top }, opacity, x, y, w, h, radius);
        }

        SetTransformFn brushTransform;
        SetOpacityFn brushOpacity;
        FillRoundedRectangleFn fillRounded;

        // 用筆刷填滿圓角長方形：筆刷的圖照 m 擺放、縮放，透明度 opacity。
        // 這幾個函式每一格都會用，第一次用到時記起來（所有點陣圖筆刷的函式表都一樣）
        void Fill(IntPtr brushHandle, Matrix m, double opacity, double x, double y, double w, double h, double radius)
        {
            if (fillRounded == null)
            {
                brushTransform = ComVtable.Method<SetTransformFn>(brushHandle, 5);   // ID2D1Brush::SetTransform
                brushOpacity = ComVtable.Method<SetOpacityFn>(brushHandle, 4);       // ID2D1Brush::SetOpacity
                fillRounded = ComVtable.Method<FillRoundedRectangleFn>(target, 19);  // ID2D1RenderTarget::FillRoundedRectangle
            }
            brushTransform(brushHandle, ref m);
            brushOpacity(brushHandle, (float)opacity);
            var rect = new RoundedRect { Rect = new RectF { Left = (float)x, Top = (float)y, Right = (float)(x + w), Bottom = (float)(y + h) }, RadiusX = (float)radius, RadiusY = (float)radius };
            fillRounded(target, ref rect, brushHandle);
        }

        bool CreateBlurResources(int w, int h)
        {
            ReleaseBlurResources();
            try
            {
                // 複製畫面用的圖（格式要跟畫面一樣：BGRA、不管 alpha）
                var props = new BitmapProperties { Format = Bgra, AlphaMode = AlphaIgnore, DpiX = 96, DpiY = 96 };
                if (createBitmap(target, new SizeU { Width = (uint)w, Height = (uint)h }, IntPtr.Zero, 0, ref props, out copy) < 0) { copy = IntPtr.Zero; return false; }
                copyFrom = ComVtable.Method<CopyFromRenderTargetFn>(copy, 9);   // ID2D1Bitmap::CopyFromRenderTarget

                var createCompatible = ComVtable.Method<CreateCompatibleRenderTargetFn>(target, 12);   // ID2D1RenderTarget::CreateCompatibleRenderTarget
                var format = new PixelFormat { Format = Bgra, AlphaMode = AlphaIgnore };
                int lw = w, lh = h;
                for (int i = 0; i < BlurLevels; i++)
                {
                    lw = Math.Max(1, (lw + 1) / 2); lh = Math.Max(1, (lh + 1) / 2);
                    var size = new SizeF { Width = lw, Height = lh };
                    var pixels = new SizeU { Width = (uint)lw, Height = (uint)lh };
                    IntPtr rt, bitmap;
                    if (createCompatible(target, ref size, ref pixels, ref format, 0, out rt) < 0) return false;
                    var level = new Level { Target = rt, W = lw, H = lh };
                    levels.Add(level);
                    if (ComVtable.Method<GetBitmapFn>(rt, 57)(rt, out bitmap) < 0) return false;   // ID2D1BitmapRenderTarget::GetBitmap
                    level.Bitmap = bitmap;
                    level.Begin = ComVtable.Method<BeginDrawFn>(rt, 48);
                    level.End = ComVtable.Method<EndDrawFn>(rt, 49);
                    level.Draw = ComVtable.Method<DrawBitmapFn>(rt, 26);
                }

                brush = CreateBrush(levels[0].Bitmap);
                if (brush == IntPtr.Zero) return false;
                copyW = w; copyH = h;
                return true;
            }
            catch { return false; }
        }

        void ReleaseBlurResources()
        {
            if (brush != IntPtr.Zero) { Marshal.Release(brush); brush = IntPtr.Zero; }
            foreach (var level in levels)
            {
                if (level.Bitmap != IntPtr.Zero) Marshal.Release(level.Bitmap);
                if (level.Target != IntPtr.Zero) Marshal.Release(level.Target);
            }
            levels.Clear();
            if (copy != IntPtr.Zero) { Marshal.Release(copy); copy = IntPtr.Zero; }
            copyW = copyH = 0;
        }

        public static IntPtr CreateFactory()
        {
            try
            {
                var iid = IID_ID2D1Factory;
                IntPtr factory;
                return D2D1CreateFactory(0 /* 單執行緒 */, ref iid, IntPtr.Zero, out factory) >= 0 ? factory : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        // 在 Direct3D 的輸出畫面（texture）上建立 Direct2D 的繪圖目標；不支援就回傳 null（改用 GDI）
        public static D2DTarget Create(IntPtr factory, IntPtr texture)
        {
            if (factory == IntPtr.Zero) return null;
            IntPtr surface;
            var iid = IID_IDXGISurface;
            if (Marshal.QueryInterface(texture, ref iid, out surface) < 0) return null;
            try
            {
                // 96 DPI：座標就是像素
                var props = new RenderTargetProperties { Format = Bgra, AlphaMode = AlphaIgnore, DpiX = 96, DpiY = 96 };
                IntPtr target;
                int hr = ComVtable.Method<CreateDxgiSurfaceRenderTargetFn>(factory, 15)(factory, surface, ref props, out target);   // ID2D1Factory::CreateDxgiSurfaceRenderTarget
                return hr < 0 ? null : new D2DTarget(target);
            }
            catch { return null; }
            finally { Marshal.Release(surface); }
        }

        // pixels：預先乘好 alpha 的 BGRA（WPF 的 Pbgra32）
        public IntPtr CreateBitmap(int[] pixels, int width, int height)
        {
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var props = new BitmapProperties { Format = Bgra, AlphaMode = AlphaPremultiplied, DpiX = 96, DpiY = 96 };
                IntPtr bitmap;
                int hr = createBitmap(target, new SizeU { Width = (uint)width, Height = (uint)height }, pin.AddrOfPinnedObject(), (uint)(width * 4), ref props, out bitmap);
                return hr < 0 ? IntPtr.Zero : bitmap;
            }
            finally { pin.Free(); }
        }

        public void Begin() { beginDraw(target); }

        // 把 bitmap 從 (sourceY 開始、高 height 的一段) 貼到 (x, y)；位置可以有小數（線性內插，捲動更滑）
        public void Draw(IntPtr bitmap, double x, double y, double width, double height, double sourceY, double opacity)
        {
            DrawPart(bitmap, 0, sourceY, width, height, x, y, width, height, opacity);
        }

        // 把 bitmap 上 (sx, sy, sw, sh) 這塊貼到 (dx, dy, dw, dh)；大小不一樣就拉伸
        public void DrawPart(IntPtr bitmap, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh, double opacity)
        {
            var dest = new RectF { Left = (float)dx, Top = (float)dy, Right = (float)(dx + dw), Bottom = (float)(dy + dh) };
            var source = new RectF { Left = (float)sx, Top = (float)sy, Right = (float)(sx + sw), Bottom = (float)(sy + sh) };
            drawBitmap(target, bitmap, ref dest, (float)opacity, 1 /* 線性 */, ref source);
        }

        // 關掉邊緣反鋸齒（拼接好幾塊時，接縫的地方才不會兩邊都只畫一半、變成一條淡淡的縫）
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetAntialiasModeFn(IntPtr self, int mode);
        SetAntialiasModeFn setAntialias;
        public void SetAliased(bool aliased)
        {
            if (setAntialias == null) setAntialias = ComVtable.Method<SetAntialiasModeFn>(target, 32);   // ID2D1RenderTarget::SetAntialiasMode
            setAntialias(target, aliased ? 1 /* 不反鋸齒 */ : 0 /* 預設 */);
        }

        public int End() { ulong tag1, tag2; return endDraw(target, out tag1, out tag2); }

        public void Release()
        {
            ReleaseBlurResources();
            Marshal.Release(target);
        }
    }
}
