using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace VideoWallpaper
{
    // 跟著重拍的邊框光暈：聽電腦正在播出的聲音（Windows 的 loopback 擷取；只在記憶體裡即時算音量，不錄音、不存檔、不傳出去），
    // 大鼓、貝斯一進來，音樂卡片的邊框就泛出一圈光再慢慢退回去（演算法在 Beat.Detector.cs）。只在鎖定畫面開著時聽
    static partial class Beat
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);   // 用不到，佔位置
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
            [PreserveSig] int ReleaseBuffer(int frames);
            [PreserveSig] int GetNextPacketSize(out int frames);
        }

        static readonly Guid FloatFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");   // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        const double ReleaseMs = 250;   // 暗回去：每 0.25 秒剩大約 37%（指數衰減，像呼吸一樣慢慢退）

        // 算好的亮度，四個數字塞在一個 long 裡（每 2.5 毫秒更新一次也不用配置新物件，畫面那邊也不會讀到一半）：
        // 聽到的聲音是什麼時候的（毫秒，低 46 位元）、那時候該多亮（0～1，12 位元）、往下退多快（45～250 毫秒分 16 級，4 位元）、
        // 是不是正在往下退（1 位元）
        static long shown;
        static volatile bool running, resetWanted;
        static double strongest = 0.08, drumness = 0.8;   // 上次聽到的鼓有多重、像不像鼓（下次打開鎖定畫面接著用，不用從頭適應）
        static Thread worker;

        // 換歌了：之前那首多大聲、鼓點多重都不能用了，從頭重新聽
        public static void Reset() { resetWanted = true; }

        public static void Start()
        {
            if (running) return;
            running = true;
            if (worker == null || !worker.IsAlive)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "Beat" };
                worker.Start();
            }
        }

        public static void Stop() { running = false; }

        // 邊框光暈現在的亮度（0～1，now 是播放引擎的動畫時間）：最近算好的亮度。聲音每 10 毫秒才來一包，
        // 正在往下退的話，還沒來的這一小段照樣接著退；正在變亮就先停在那裡等下一包（不然會一退一補、微微閃爍）。
        // 超過 30 毫秒都沒有新的聲音（暫停了）就一律退掉
        public static double Glow(long now)
        {
            long packed = Interlocked.Read(ref shown);
            long time = packed & TimeMask;
            double value = ((packed >> 46) & 0xFFF) / 4095.0;
            double release = FastestRelease + ((packed >> 58) & 0xF) * ReleaseStep;
            bool falling = (packed >> 62 & 1) != 0;
            double waited = falling ? now - time : now - time - 30;
            if (waited > 0) value *= Math.Exp(-waited / release);
            return value < 0.005 ? 0 : value;
        }

        const long TimeMask = (1L << 46) - 1;

        const double FastestRelease = 45, ReleaseStep = (ReleaseMs - FastestRelease) / 15;

        static void Publish(long time, double value, bool falling, double release)
        {
            long level = (long)Math.Round(Math.Max(0, Math.Min(1, value)) * 4095);
            long speed = (long)Math.Round(Math.Max(0, Math.Min(15, (release - FastestRelease) / ReleaseStep)));
            Interlocked.Exchange(ref shown, (Math.Max(0, time) & TimeMask) | (level << 46) | (speed << 58) | (falling ? 1L << 62 : 0));
        }

        [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);

        static void Run()
        {
            while (running)
            {
                try { Capture(); } catch { }
                for (int i = 0; i < 20 && running; i++) Thread.Sleep(50);   // 斷掉了（例如換了喇叭 / 耳機）：1 秒後重新開始
            }
        }

        // 擷取預設輸出裝置正在播的聲音，一來就交給 Detector 算。
        // 優先用「聲音來了 Windows 叫醒我們」（每 10 毫秒一包，來了馬上算、沒聲音時完全不醒來）；
        // 這台電腦不支援的話，退回自己每 4 毫秒看一次。
        // 用到的 COM 物件每開一次鎖定畫面就建一份，結束時馬上釋放（不等垃圾回收）
        static void Capture()
        {
            IMMDeviceEnumerator devices = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            var ready = new AutoResetEvent(false);
            try
            {
                devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (devices.GetDefaultAudioEndpoint(0 /* 輸出 */, 1 /* 多媒體 */, out device) != 0) return;
                int channels = 0, rate = 0, bits = 0;
                bool isFloat = false, events = false, initialized = false;
                foreach (bool tryEvents in new[] { true, false })
                {
                    if (client != null) { Marshal.ReleaseComObject(client); client = null; }
                    var iid = typeof(IAudioClient).GUID;
                    object activated;
                    if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out activated) != 0) return;
                    client = (IAudioClient)activated;
                    IntPtr format;
                    if (client.GetMixFormat(out format) != 0) return;
                    try
                    {
                        int tag = (ushort)Marshal.ReadInt16(format, 0);
                        channels = (ushort)Marshal.ReadInt16(format, 2);
                        rate = Marshal.ReadInt32(format, 4);
                        bits = (ushort)Marshal.ReadInt16(format, 14);
                        isFloat = tag == 3 || (tag == 0xFFFE && (Guid)Marshal.PtrToStructure(format + 24, typeof(Guid)) == FloatFormat);
                        // LOOPBACK：擷取正在播出的聲音；EVENTCALLBACK：聲音來了叫醒我們。緩衝 0.2 秒
                        int flags = 0x00020000 | (tryEvents ? 0x00040000 : 0);
                        if (client.Initialize(0 /* 共用 */, flags, 2000000, 0, format, IntPtr.Zero) != 0) continue;
                    }
                    finally { Marshal.FreeCoTaskMem(format); }
                    events = tryEvents && client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()) == 0;
                    if (tryEvents && !events) continue;
                    initialized = true;
                    break;
                }
                if (!initialized) return;
                if (channels <= 0 || rate <= 0 || !(isFloat && bits == 32) && !(!isFloat && bits == 16)) return;   // 只處理常見的 32 位元浮點數、16 位元整數
                var captureIid = typeof(IAudioCaptureClient).GUID;
                object obj;
                if (client.GetService(ref captureIid, out obj) != 0) return;
                capture = (IAudioCaptureClient)obj;
                if (client.Start() != 0) return;
                Listen(client, capture, rate, channels, isFloat, events ? ready : null);
            }
            finally
            {
                foreach (object com in new object[] { capture, client, device, devices })
                    if (com != null) Marshal.ReleaseComObject(com);
                ready.Dispose();
            }
        }

        // 一直讀擷取到的聲音交給 Detector，直到不再聽（鎖定畫面收起來）或裝置斷掉。
        // ready：聲音來了會被叫醒的事件（null = 這台電腦不支援，自己每 4 毫秒看一次）
        static void Listen(IAudioClient client, IAudioCaptureClient capture, int rate, int channels, bool isFloat, AutoResetEvent ready)
        {
            Detector detector = null;
            detector = new Detector(rate) { Show = (time, value, falling) => Publish(time, value, falling, detector.ReleaseNow), Strongest = strongest, Drumness = drumness };
            bool fineTimer = false;
            try
            {
                var floats = new float[0];
                var shorts = new short[0];
                while (running)
                {
                    int frames;
                    if (ready != null)
                    {
                        // 等聲音來（沒聲音時每 0.1 秒醒來看一下要不要停）；等不到、卻已經有聲音在排隊，代表叫醒的功能其實沒作用，改成自己看
                        if (!ready.WaitOne(100) && capture.GetNextPacketSize(out frames) == 0 && frames > 0) ready = null;
                    }
                    else
                    {
                        // 自己看：把計時器調到 1 毫秒（Thread.Sleep 平常最短大約 15 毫秒，聽到鼓聲會慢那麼多才亮）
                        if (!fineTimer) fineTimer = timeBeginPeriod(1) == 0;
                        Thread.Sleep(4);
                    }
                    if (capture.GetNextPacketSize(out frames) != 0) return;
                    while (frames > 0)
                    {
                        IntPtr data; int got, flags; long devicePosition, qpcPosition;
                        if (capture.GetBuffer(out data, out got, out flags, out devicePosition, out qpcPosition) != 0) return;
                        // 這段聲音是什麼時候播出去的（換算成播放引擎的時間）：qpcPosition 以 100 奈秒為單位；拿不到就從現在往回推
                        double age = (System.Diagnostics.Stopwatch.GetTimestamp() * (10000000.0 / System.Diagnostics.Stopwatch.Frequency) - qpcPosition) / 10000;
                        if ((flags & 0x4) != 0 || age < 0 || age > 1000) age = got * 1000.0 / rate;   // AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR
                        double packetTime = AnimationClock.Now - age;
                        int count = got * channels;
                        if (floats.Length < count) floats = new float[count];
                        if ((flags & 0x2) != 0) Array.Clear(floats, 0, count);   // AUDCLNT_BUFFERFLAGS_SILENT：這段是無聲的
                        else if (isFloat) Marshal.Copy(data, floats, 0, count);
                        else
                        {
                            if (shorts.Length < count) shorts = new short[count];
                            Marshal.Copy(data, shorts, 0, count);
                            for (int i = 0; i < count; i++) floats[i] = shorts[i] / 32768f;
                        }
                        detector.Add(floats, got, channels, packetTime);
                        capture.ReleaseBuffer(got);
                        if (capture.GetNextPacketSize(out frames) != 0) return;
                    }
                }
            }
            finally
            {
                client.Stop();
                if (fineTimer) timeEndPeriod(1);
                strongest = detector.Strongest;
                drumness = detector.Drumness;
            }
        }
    }
}
