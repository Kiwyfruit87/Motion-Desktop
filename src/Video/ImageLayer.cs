using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoWallpaper
{
    // 圖片當桌布：圖片解碼成一張顯示卡上的材質（附縮小用的各級小圖，縮小時才不會一格一格、閃爍），
    // 要畫的時候照縮放方式（填滿 / 完整顯示 / 拉伸）用一個小著色器畫到輸出畫面上。畫面沒變就不重畫，幾乎不耗資源
    class ImageLayer
    {
        // 當成圖片的副檔名（WIC 讀得懂的；.webp、.heic 要 Windows 裝了對應的解碼器）
        static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".jxr" };

        public static bool IsImage(string path)
        {
            return !string.IsNullOrEmpty(path) && Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
        }

        // 選檔視窗用的副檔名清單（*.jpg;*.png;…）
        public static string Patterns { get { return "*" + string.Join(";*", Extensions); } }

        const int MaxSide = 4096;   // 太大的圖先縮到長邊 4096（省記憶體和解碼時間；4K 螢幕也夠清楚）

        // 讀圖片的大小和 EXIF 的方向（只讀檔頭，不解碼整張）；沒有 EXIF（PNG 之類）方向是 1
        public static void Probe(string path, out int width, out int height, out int orientation)
        {
            orientation = 1;
            using (var stream = File.OpenRead(path))
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None).Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
                try
                {
                    var meta = frame.Metadata as BitmapMetadata;
                    object value = meta != null && meta.ContainsQuery("System.Photo.Orientation") ? meta.GetQuery("System.Photo.Orientation") : null;
                    if (value != null) orientation = Convert.ToInt32(value);
                }
                catch { }
            }
        }

        // 照 EXIF 的方向轉正：2 左右翻、3 轉 180、4 上下翻、5 轉 90 再左右翻、6 轉 90、7 轉 270 再左右翻、8 轉 270
        // （TransformGroup 照加入的順序套用：先轉，再翻）
        public static BitmapSource Orient(BitmapSource bitmap, int orientation)
        {
            var transform = new TransformGroup();
            if (orientation == 3) transform.Children.Add(new RotateTransform(180));
            if (orientation == 5 || orientation == 6) transform.Children.Add(new RotateTransform(90));
            if (orientation == 7 || orientation == 8) transform.Children.Add(new RotateTransform(270));
            if (orientation == 2 || orientation == 5 || orientation == 7) transform.Children.Add(new ScaleTransform(-1, 1));
            if (orientation == 4) transform.Children.Add(new ScaleTransform(1, -1));
            return transform.Children.Count > 0 ? new TransformedBitmap(bitmap, transform) : bitmap;
        }

        // 讀成縮小過的 BitmapSource（長邊最多 maxSide；JPEG 解碼時就直接縮小，大照片也很快），不轉方向
        public static BitmapSource Load(string path, int width, int height, int maxSide)
        {
            var bitmap = new BitmapImage();
            using (var stream = File.OpenRead(path))   // 用串流讀（檔名裡的 %、# 不會被當成網址解讀）
            {
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 讀完就放開檔案
                if (Math.Max(width, height) > maxSide)
                {
                    if (width >= height) bitmap.DecodePixelWidth = maxSide;
                    else bitmap.DecodePixelHeight = maxSide;
                }
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }

        // 解碼成要放上顯示卡的像素（在自己的一條執行緒上做，不卡畫面）：太大的先縮小、透明的地方墊黑、照 EXIF 轉正；
        // 讀不了就丟例外。只取第一格（GIF 動畫不會動）
        public static byte[] Decode(string path, out int width, out int height)
        {
            int w, h, orientation;
            Probe(path, out w, out h, out orientation);
            // 先換成預先乘上透明度的格式（透明的地方顏色是 0，等於墊在黑色上；之後轉正時邊緣才不會滲出雜色），再轉正
            BitmapSource bitmap = new FormatConvertedBitmap(Load(path, w, h, MaxSide), PixelFormats.Pbgra32, null, 0);
            bitmap = Orient(bitmap, orientation);
            width = bitmap.PixelWidth;
            height = bitmap.PixelHeight;
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return pixels;
        }

        // ---------- 顯示卡 ----------
        const string Source = @"
Texture2D Img : register(t0);
SamplerState Lin : register(s0);
cbuffer Placement : register(b0)
{
    float4 Dst;   // 畫在畫面上的哪一塊：左上 xy、大小 zw（像素）
    float4 Src;   // 取圖片的哪一塊：左上 xy、大小 zw（0～1）
};

float4 VS(uint id : SV_VertexID) : SV_Position
{
    return float4(id == 2 ? 3.0 : -1.0, id == 1 ? 3.0 : -1.0, 0.0, 1.0);   // 蓋滿整個 viewport 的三角形
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 t = (pos.xy - Dst.xy) / Dst.zw;
    return float4(Img.Sample(Lin, Src.xy + t * Src.zw).rgb, 1.0);   // 縮小時自動用合適的那一級小圖
}
";

        [StructLayout(LayoutKind.Sequential)] struct BufferDesc { public uint ByteWidth; public int Usage; public uint BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }
        [StructLayout(LayoutKind.Sequential)] struct Texture2DDesc { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality; public int Usage; public uint BindFlags, CpuAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] struct SamplerDesc { public int Filter, AddressU, AddressV, AddressW; public float MipLodBias; public uint MaxAnisotropy; public int ComparisonFunc; public float Border0, Border1, Border2, Border3, MinLod, MaxLod; }
        [StructLayout(LayoutKind.Sequential)] struct Viewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }

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
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UpdatePixelsFn(IntPtr self, IntPtr dst, uint sub, IntPtr box, byte[] data, uint rowPitch, uint depthPitch);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UpdateFloatsFn(IntPtr self, IntPtr dst, uint sub, IntPtr box, float[] data, uint rowPitch, uint depthPitch);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ClearTargetFn(IntPtr self, IntPtr rtv, float[] color);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GenerateMipsFn(IntPtr self, IntPtr srv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ClearStateFn(IntPtr self);

        const int Bgra = 87;   // DXGI_FORMAT_B8G8R8A8_UNORM（跟輸出畫面一樣）

        readonly IntPtr device, context;
        IntPtr texture, srv, vs, ps, sampler, constants;
        readonly float[] placement = new float[8];
        readonly float[] black = { 0, 0, 0, 1 };
        readonly IntPtr[] one = new IntPtr[1];
        public int Width { get; private set; }
        public int Height { get; private set; }

        readonly ClearStateFn clearState;
        readonly ClearTargetFn clearTarget;
        readonly SetTargetsFn omSetTargets;
        readonly SetViewportsFn rsSetViewports;
        readonly SetPointerFn iaSetLayout;
        readonly SetTopologyFn iaSetTopology;
        readonly SetShaderFn vsSetShader, psSetShader;
        readonly UpdateFloatsFn updateFloats;
        readonly SetResourcesFn psSetConstants, psSetSamplers, psSetResources;
        readonly DrawFn draw;

        ImageLayer(IntPtr device, IntPtr context)
        {
            this.device = device;
            this.context = context;
            clearState = ComVtable.Method<ClearStateFn>(context, 110);             // ID3D11DeviceContext::ClearState
            clearTarget = ComVtable.Method<ClearTargetFn>(context, 50);            // ::ClearRenderTargetView
            omSetTargets = ComVtable.Method<SetTargetsFn>(context, 33);            // ::OMSetRenderTargets
            rsSetViewports = ComVtable.Method<SetViewportsFn>(context, 44);        // ::RSSetViewports
            iaSetLayout = ComVtable.Method<SetPointerFn>(context, 17);             // ::IASetInputLayout
            iaSetTopology = ComVtable.Method<SetTopologyFn>(context, 24);          // ::IASetPrimitiveTopology
            vsSetShader = ComVtable.Method<SetShaderFn>(context, 11);              // ::VSSetShader
            psSetShader = ComVtable.Method<SetShaderFn>(context, 9);               // ::PSSetShader
            updateFloats = ComVtable.Method<UpdateFloatsFn>(context, 48);          // ::UpdateSubresource
            psSetConstants = ComVtable.Method<SetResourcesFn>(context, 16);        // ::PSSetConstantBuffers
            psSetSamplers = ComVtable.Method<SetResourcesFn>(context, 10);         // ::PSSetSamplers
            psSetResources = ComVtable.Method<SetResourcesFn>(context, 8);         // ::PSSetShaderResources
            draw = ComVtable.Method<DrawFn>(context, 13);                          // ::Draw
        }

        // 把解碼好的圖片放上顯示卡；不行回傳 null，why 是原因
        public static ImageLayer Create(IntPtr device, IntPtr context, int featureLevel, byte[] pixels, int width, int height, out string why)
        {
            why = null;
            if (featureLevel < 0xa000) { why = Lang.T("顯示卡太舊（需要 Direct3D 10 以上）", "the graphics card is too old (Direct3D 10 or later is needed)"); return null; }
            var layer = new ImageLayer(device, context) { Width = width, Height = height };
            try
            {
                if (layer.Init(pixels)) return layer;
                why = Lang.T("顯示卡建立不了圖片", "the graphics card couldn't create the image");
            }
            catch (Exception ex) { why = ex.Message; }
            layer.Release();
            return null;
        }

        bool Init(byte[] pixels)
        {
            var src = Encoding.UTF8.GetBytes(Source);
            byte[] vsCode = LiquidGlass.Compile(src, "VS", "vs_4_0"), psCode = LiquidGlass.Compile(src, "PS", "ps_4_0");
            var createShader = ComVtable.Method<CreateShaderFn>(device, 12);   // ID3D11Device::CreateVertexShader
            if (createShader(device, vsCode, (IntPtr)vsCode.Length, IntPtr.Zero, out vs) < 0) return false;
            if (ComVtable.Method<CreateShaderFn>(device, 15)(device, psCode, (IntPtr)psCode.Length, IntPtr.Zero, out ps) < 0) return false;   // ::CreatePixelShader

            // 先試有各級小圖的（要能當繪圖目標，顯示卡才能自己算小圖）；不行就只放原圖
            var createTexture = ComVtable.Method<CreateTexture2DFn>(device, 5);   // ::CreateTexture2D
            var desc = new Texture2DDesc { Width = (uint)Width, Height = (uint)Height, MipLevels = 0, ArraySize = 1, Format = Bgra, SampleCount = 1, BindFlags = 0x8 | 0x20, MiscFlags = 0x1 /* GENERATE_MIPS */ };
            bool mips = createTexture(device, ref desc, IntPtr.Zero, out texture) >= 0;
            if (!mips)
            {
                desc.MipLevels = 1; desc.BindFlags = 0x8; desc.MiscFlags = 0;
                if (createTexture(device, ref desc, IntPtr.Zero, out texture) < 0) return false;
            }
            ComVtable.Method<UpdatePixelsFn>(context, 48)(context, texture, 0, IntPtr.Zero, pixels, (uint)(Width * 4), 0);   // ID3D11DeviceContext::UpdateSubresource
            if (ComVtable.Method<CreateViewFn>(device, 7)(device, texture, IntPtr.Zero, out srv) < 0) return false;   // ::CreateShaderResourceView
            if (mips) ComVtable.Method<GenerateMipsFn>(context, 54)(context, srv);   // ::GenerateMips

            var sd = new SamplerDesc { Filter = 0x15 /* 線性（含小圖之間） */, AddressU = 3, AddressV = 3, AddressW = 3 /* 超出範圍用邊緣的顏色 */, ComparisonFunc = 1, MaxLod = float.MaxValue };
            if (ComVtable.Method<CreateSamplerFn>(device, 23)(device, ref sd, out sampler) < 0) return false;   // ::CreateSamplerState
            var bd = new BufferDesc { ByteWidth = (uint)(placement.Length * 4), BindFlags = 0x4 /* 常數 */ };
            return ComVtable.Method<CreateBufferFn>(device, 3)(device, ref bd, IntPtr.Zero, out constants) >= 0;   // ::CreateBuffer
        }

        // 輸出畫面（或影片畫布）用的繪圖目標；失敗回傳 0
        public IntPtr CreateTargetView(IntPtr target)
        {
            IntPtr view;
            return ComVtable.Method<CreateViewFn>(device, 9)(device, target, IntPtr.Zero, out view) < 0 ? IntPtr.Zero : view;   // ::CreateRenderTargetView
        }

        // 整張先塗黑（完整顯示時旁邊的黑邊），再把圖片的 (u, v, du, dv) 那一塊畫到畫面的 (x, y, w, h)
        public void Draw(IntPtr rtv, float x, float y, float w, float h, float u, float v, float du, float dv)
        {
            if (w <= 0 || h <= 0) return;
            clearState(context);
            clearTarget(context, rtv, black);
            one[0] = rtv;
            omSetTargets(context, 1, one, IntPtr.Zero);
            var vp = new Viewport { X = x, Y = y, Width = w, Height = h, MaxDepth = 1 };
            rsSetViewports(context, 1, ref vp);
            iaSetLayout(context, IntPtr.Zero);
            iaSetTopology(context, 4 /* 三角形清單 */);
            vsSetShader(context, vs, IntPtr.Zero, 0);
            psSetShader(context, ps, IntPtr.Zero, 0);
            placement[0] = x; placement[1] = y; placement[2] = w; placement[3] = h;
            placement[4] = u; placement[5] = v; placement[6] = du; placement[7] = dv;
            updateFloats(context, constants, 0, IntPtr.Zero, placement, 0, 0);
            one[0] = constants; psSetConstants(context, 0, 1, one);
            one[0] = sampler; psSetSamplers(context, 0, 1, one);
            one[0] = srv; psSetResources(context, 0, 1, one);
            draw(context, 3, 0);
            clearState(context);   // 狀態還原成預設，不影響 Direct2D 和液態玻璃
        }

        static void Free(ref IntPtr com) { if (com != IntPtr.Zero) { Marshal.Release(com); com = IntPtr.Zero; } }

        public void Release()
        {
            Free(ref constants); Free(ref sampler); Free(ref srv); Free(ref texture); Free(ref ps); Free(ref vs);
        }
    }
}
