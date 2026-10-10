using System;
using System.Runtime.InteropServices;

namespace VideoWallpaper
{
    // ================= 播放引擎：Media Foundation Media Engine 解碼 + Direct3D 11 輸出 =================
    // 以下是 Windows COM 介面的宣告。方法的順序必須跟 Windows SDK 標頭檔完全一致，用不到的方法只是佔位置。

    [StructLayout(LayoutKind.Sequential)] public struct MFVideoNormalizedRect { public float Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MFARGB { public byte Blue, Green, Red, Alpha; }
    [StructLayout(LayoutKind.Sequential)] public struct D3DRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct DXGI_SWAP_CHAIN_DESC1
    {
        public uint Width, Height;
        public int Format, Stereo;
        public uint SampleCount, SampleQuality, BufferUsage, BufferCount;
        public int Scaling, SwapEffect, AlphaMode;
        public uint Flags;
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
        void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob();
        [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    }

    [ComImport, Guid("fee7c112-e776-42b5-9bbf-0048524e2bd5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEngineNotify
    {
        [PreserveSig] int EventNotify(uint meEvent, UIntPtr param1, uint param2);
    }

    [ComImport, Guid("fc0e10d2-ab2a-4501-a951-06bb1075184c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaError
    {
        [PreserveSig] ushort GetErrorCode();
        [PreserveSig] int GetExtendedErrorCode();
    }

    [ComImport, Guid("98a1b0bb-03eb-4935-ae7c-93c1fa0e1c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEngine
    {
        [PreserveSig] int GetError(out IMFMediaError error);
        [PreserveSig] int SetErrorCode(int error);
        [PreserveSig] int SetSourceElements(IntPtr elements);
        [PreserveSig] int SetSource([MarshalAs(UnmanagedType.BStr)] string url);
        [PreserveSig] int GetCurrentSource(out IntPtr url);
        [PreserveSig] ushort GetNetworkState();
        [PreserveSig] int GetPreload();
        [PreserveSig] int SetPreload(int preload);
        [PreserveSig] int GetBuffered(out IntPtr buffered);
        [PreserveSig] int Load();
        [PreserveSig] int CanPlayType([MarshalAs(UnmanagedType.BStr)] string type, out int answer);
        [PreserveSig] ushort GetReadyState();
        [PreserveSig] int IsSeeking();
        [PreserveSig] double GetCurrentTime();
        [PreserveSig] int SetCurrentTime(double seconds);
        [PreserveSig] double GetStartTime();
        [PreserveSig] double GetDuration();
        [PreserveSig] int IsPaused();
        [PreserveSig] double GetDefaultPlaybackRate();
        [PreserveSig] int SetDefaultPlaybackRate(double rate);
        [PreserveSig] double GetPlaybackRate();
        [PreserveSig] int SetPlaybackRate(double rate);
        [PreserveSig] int GetPlayed(out IntPtr played);
        [PreserveSig] int GetSeekable(out IntPtr seekable);
        [PreserveSig] int IsEnded();
        [PreserveSig] int GetAutoPlay();
        [PreserveSig] int SetAutoPlay(int autoPlay);
        [PreserveSig] int GetLoop();
        [PreserveSig] int SetLoop(int loop);
        [PreserveSig] int Play();
        [PreserveSig] int Pause();
        [PreserveSig] int GetMuted();
        [PreserveSig] int SetMuted(int muted);
        [PreserveSig] double GetVolume();
        [PreserveSig] int SetVolume(double volume);
        [PreserveSig] int HasVideo();
        [PreserveSig] int HasAudio();
        [PreserveSig] int GetNativeVideoSize(out uint width, out uint height);
        [PreserveSig] int GetVideoAspectRatio(out uint x, out uint y);
        [PreserveSig] int Shutdown();
        [PreserveSig] int TransferVideoFrame(IntPtr dstSurface, [In] ref MFVideoNormalizedRect src, [In] ref D3DRect dst, [In] ref MFARGB borderColor);
        [PreserveSig] int OnVideoStreamTick(out long pts);
    }

    [ComImport, Guid("4D645ACE-26AA-4688-9BE1-DF3516990B93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEngineClassFactory
    {
        [PreserveSig] int CreateInstance(uint flags, IMFAttributes attributes, out IMFMediaEngine engine);
    }

    [ComImport, Guid("B44392DA-499B-446b-A4CB-005FEAD0E6D5")]
    public class MFMediaEngineClassFactory { }

    [ComImport, Guid("eb533d5d-2db6-40f8-97a9-494692014f07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFDXGIDeviceManager
    {
        void CloseDeviceHandle(); void GetVideoService(); void LockDevice(); void OpenDeviceHandle();
        [PreserveSig] int ResetDevice(IntPtr device, uint resetToken);
    }

    [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D11Device
    {
        void CreateBuffer(); void CreateTexture1D(); void CreateTexture2D(); void CreateTexture3D(); void CreateShaderResourceView();
        void CreateUnorderedAccessView(); void CreateRenderTargetView(); void CreateDepthStencilView(); void CreateInputLayout(); void CreateVertexShader();
        void CreateGeometryShader(); void CreateGeometryShaderWithStreamOutput(); void CreatePixelShader(); void CreateHullShader(); void CreateDomainShader();
        void CreateComputeShader(); void CreateClassLinkage(); void CreateBlendState(); void CreateDepthStencilState(); void CreateRasterizerState();
        void CreateSamplerState(); void CreateQuery(); void CreatePredicate(); void CreateCounter(); void CreateDeferredContext();
        void OpenSharedResource(); void CheckFormatSupport(); void CheckMultisampleQualityLevels(); void CheckCounterInfo(); void CheckCounter();
        void CheckFeatureSupport(); void GetPrivateData(); void SetPrivateData(); void SetPrivateDataInterface();
        [PreserveSig] int GetFeatureLevel();
        [PreserveSig] uint GetCreationFlags();
        [PreserveSig] int GetDeviceRemovedReason();
    }

    [ComImport, Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D10Multithread
    {
        void Enter(); void Leave();
        [PreserveSig] int SetMultithreadProtected(int protect);
    }

    [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIDevice
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int GetAdapter(out IDXGIAdapter adapter);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIAdapter
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData();
        [PreserveSig] int GetParent([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object parent);
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIOutput
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void GetDesc(); void GetDisplayModeList(); void FindClosestMatchingMode();
        [PreserveSig] int WaitForVBlank();
    }

    [ComImport, Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIFactory2
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters();
        [PreserveSig] int MakeWindowAssociation(IntPtr hwnd, uint flags);
        void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        void EnumAdapters1(); void IsCurrent();
        void IsWindowedStereoEnabled();
        [PreserveSig] int CreateSwapChainForHwnd(IntPtr device, IntPtr hwnd, [In] ref DXGI_SWAP_CHAIN_DESC1 desc,
            IntPtr fullscreenDesc, IntPtr restrictToOutput, out IDXGISwapChain1 swapChain);
    }

    [ComImport, Guid("4AE63092-6327-4c1b-80AE-BFE12EA32B86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGISurface1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void GetDevice();
        void GetDesc(); void Map(); void Unmap();
        [PreserveSig] int GetDC(int discard, out IntPtr hdc);
        [PreserveSig] int ReleaseDC(IntPtr dirtyRect);
    }

    [ComImport, Guid("790a45f7-0d42-4876-983a-0a55cfe6f4aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGISwapChain1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void GetDevice();
        [PreserveSig] int Present(uint syncInterval, uint flags);
        [PreserveSig] int GetBuffer(uint buffer, [In] ref Guid riid, out IntPtr surface);
    }

    // Media Engine 的事件通知（在 Media Foundation 自己的執行緒上呼叫）
    public class EngineNotify : IMFMediaEngineNotify
    {
        readonly VideoEngine owner;
        internal EngineNotify(VideoEngine owner) { this.owner = owner; }

        public int EventNotify(uint meEvent, UIntPtr param1, uint param2)
        {
            try { owner.OnEngineEvent(meEvent, param2); } catch { }
            return 0;
        }
    }
}
