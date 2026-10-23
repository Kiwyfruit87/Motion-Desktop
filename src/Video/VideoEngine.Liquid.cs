using System;

namespace VideoWallpaper
{
    // 鎖定畫面音樂卡片的液態玻璃（見 LiquidGlass）：卡片本身（第一層，在影片上）、卡片上的進度條、
    // 歌詞圓鈕（第二層，在卡片內容上），還有鎖定畫面滑進來 / 滑走時卡片的慣性。
    // UI 執行緒只告訴引擎位置和狀態，彈簧動畫在引擎執行緒上跟著螢幕更新頻率算（數值跟網頁原型一樣）
    partial class VideoEngine
    {
        // 卡片的位置和版面（主螢幕鎖定視窗裡的像素座標）
        public class LiquidCardSpec
        {
            public double X, Y, W, H, Radius, Scale;          // 卡片本身（不含影子的空間）、圓角、以 1080p 為 1 的縮放
            public double TintR, TintG, TintB;                // 封面的顏色（淡淡的有色玻璃）
            public double ProgressX0, ProgressX1, ProgressY;  // 進度條
            public double LyricsX, LyricsY, LyricsR;          // 歌詞圓鈕的中心和半徑
        }

        // 卡片上會被按的玻璃：只有歌詞圓鈕（PressLiquidButton 用這個編號）
        public const int LyricsButton = 0;

        // 跟網頁原型一樣的彈簧（SwiftUI 的 response、阻尼比），每秒 240 步細分
        class Spring
        {
            public double X, V;
            bool started;

            public double Step(double target, double response, double zeta, double dt)
            {
                if (!started || double.IsNaN(X)) { X = target; V = 0; started = true; }   // 第一次：直接從目標開始
                double w = 2 * Math.PI / response, k = w * w, c = 2 * zeta * w;
                int n = Math.Max(1, (int)Math.Ceiling(dt * 240));
                double h = dt / n;
                for (int i = 0; i < n; i++) { V += (k * (target - X) - c * V) * h; X += V * h; }
                return X;
            }

            public bool Moving(double target, double eps) { return started && (Math.Abs(X - target) > eps || Math.Abs(V) > eps * 10); }

            public void Jump(double value) { X = value; V = 0; started = true; }
        }

        // 卡片跟著頁面滑動的慣性（每個鎖定視窗一份；換歌重畫卡片時接著動，不會從頭來）
        class CardMotion
        {
            public double From, To, PageHeight;   // 頁面從哪裡滑到哪裡（0 = 蓋住畫面、1 = 在畫面上方）
            public long Start;
            public int Ms;
            public double D, DV;                  // 卡片相對頁面的位移（像素）和速度
            public readonly Spring Stretch = new Spring();   // 沿滑動方向拉長的程度
            public long LastTick;
            public double Scale = 1;

            // 頁面在 time 毫秒時往上的位移（像素，往上是負的）
            public double PageY(double time)
            {
                if (Ms <= 0) return 0;
                double t = Math.Max(0, Math.Min(1, (time - Start) / Ms));
                return -PageHeight * (From + (To - From) * Easing.StandardCurve(t));
            }

            public bool Sliding(long now) { return Ms > 0 && now - Start < Ms; }

            // 往前推到 now：頁面加速時卡片落後一點、減速時往前衝一點再彈回（彈簧 response 0.17、阻尼比 0.5，只受頁面的加速度推；
            // 1080p 上最多偏離約 85 像素，停下時多彈兩下）；卡片移動得越快沿方向拉長越多（最多 6%，彈簧 0.5 / 0.52，停下時會稍微變扁再恢復）
            public bool Step(long now)
            {
                double dt = LastTick == 0 ? 0 : Math.Min(1 / 30.0, (now - LastTick) / 1000.0);
                LastTick = now;
                if (dt <= 0) return Running(now);
                const double h = 4;   // 毫秒：用曲線本身算速度和加速度（不受畫面更新時間不均勻影響）
                double tm = now, pv = 0, pa = 0;
                if (Sliding(now) || Sliding(now - (long)(2 * h)))
                {
                    double y0 = PageY(tm - h), y1 = PageY(tm), y2 = PageY(tm + h);
                    pv = (y2 - y0) / (2 * h) * 1000;
                    pa = (y2 - 2 * y1 + y0) / (h * h) * 1e6;
                }
                double w = 2 * Math.PI / 0.17;
                int n = Math.Max(1, (int)Math.Ceiling(dt * 240));
                double step = dt / n;
                for (int i = 0; i < n; i++) { DV += (-pa - w * w * D - 2 * 0.5 * w * DV) * step; D += DV * step; }
                double speed = Math.Abs(pv + DV);
                Stretch.Step(0.06 * Math.Tanh(speed / (900 * Scale)), 0.5, 0.52, dt);
                return Running(now);
            }

            public bool Running(long now) { return Sliding(now) || Math.Abs(D) > 0.05 || Math.Abs(DV) > 0.5 || Stretch.Moving(0, 0.0002); }

            // 卡片的變形：以卡片中心為準，上下拉長 (1 + e)、左右收 1 / (1 + e)（面積不變），再往下移 D
            public void Transform(double cx, double cy, out double sx, out double sy, out double dx, out double dy)
            {
                double e = Math.Max(-0.2, Math.Min(0.2, Stretch.X));
                sx = 1 / (1 + e); sy = 1 + e;
                dx = cx - cx * sx; dy = cy - cy * sy + D;
            }
        }

        // 卡片上的東西（進度、歌詞圓鈕）的狀態和彈簧
        class LiquidCard
        {
            public LiquidCardSpec Spec;
            // 進度
            public double Position, Duration;
            public bool Playing, Scrubbing;
            public long PositionTick;
            public readonly Spring Scrub = new Spring();
            // 歌詞圓鈕
            public double LyricsLevel = 0.6;
            public bool LyricsOn;
            public readonly Spring On = new Spring(), LyricsDim = new Spring();
            // 歌詞圓鈕按下的樣子：按著沒、什麼時候按的、按下的程度
            public bool Held;
            public long PressedAt;
            public readonly Spring Press = new Spring();
            public double Pressed;
            // 按住卡片：從手指下面亮起來、白光往整張卡片擴散（放開才慢慢退），跟原型一樣
            public double TouchX, TouchY, TouchGlow, TouchAge;
            public bool Touching;
            public long LastTick;
            // 這一格算出來的樣子（畫的時候用）
            public double Progress = -1, ScrubAmount, OnAmount, LyricsAlpha;
            // 進度條往回掃（循環播放回到開頭、上一首、往回跳）：從原本的位置帶動畫掃到新的位置
            // （彈簧追的是「跟現在位置差多少」，從跳之前的差距平滑歸零；播放中目標一直往前走也停得下來）
            public readonly Spring Sweep = new Spring();
            public bool Sweeping;
        }

        LiquidGlass liquid;      // 液態玻璃的著色器（建立失敗是 null，卡片退回原本的毛玻璃）
        bool liquidTried;
        LiquidGlass clockLiquid; // 時鐘數字的玻璃（自己一份模糊圖：跟卡片不在同一個地方，共用的話每一格都要重新模糊）
        bool clockTried;
        int featureLevel;
        // 兩層玻璃的質感（每一格重複用，不另外配置）。卡片：清澈的玻璃（色調照 iOS 26「正在播放」卡片的照片量出來的，見 Look.Clear）：
        // 背景輕輕糊（0.25）、加 6.7% 的無色白；邊緣的液態折射：強度 3.0、折射帶 40（圓角最外緣位移約 3.0 × 0.5 × 40 × 1.5 = 90；
        // 上下直邊、圓角打折，見卡片的 Focus）、色散 0.022；細白線和內側提亮的強度 1
        readonly LiquidGlass.Look cardLook = new LiquidGlass.Look { Clear = 1, Scatter = 0.25f, TintAmt = 0.067f, Refract = 3.0f, Bevel = 40, Spec = 1, CA = 0.022f, Sat = 1, Legible = 0, Reflect = 0 };
        readonly LiquidGlass.Look topLook = LiquidGlass.Look.OnTop(1);
        // 時鐘的玻璃數字（照 iOS 26 玻璃時鐘的照片量出來的，見 LiquidGlass 的 TextGlass；以下是 1080p 的像素）：
        // 邊緣往字裡面取樣的鉤 16、衰減 0.9（只在最外緣約 1～3 像素）、色散 0.4；裡面輕輕糊（0.15）、加一層無色的白；輪廓線 1
        readonly LiquidGlass.Look clockLook = new LiquidGlass.Look { Refract = 16, Bevel = 0.9f, CA = 0.4f, Scatter = 0.15f, Legible = 1, Spec = 1 };

        // ---------- 給 UI 執行緒用的指令 ----------

        // 放上 / 換掉卡片（spec 是 null 就拿掉）；卡片上的進度、歌詞狀態保留
        public void SetLiquidCard(IntPtr hwnd, LiquidCardSpec spec)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (spec == null) { t.Liquid = null; ReleaseLiquidScratch(); return; }
                if (t.Liquid == null) t.Liquid = new LiquidCard();
                t.Liquid.Spec = spec;
            });
        }

        // 播放進度（秒）：播放中引擎自己往後推；拖曳中（scrubbing）直接用 position
        public void SetLiquidProgress(IntPtr hwnd, double position, double duration, bool playing, bool scrubbing)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (t.Liquid == null) return;
                var c = t.Liquid;
                c.Position = position; c.Duration = duration; c.Playing = playing && !scrubbing; c.Scrubbing = scrubbing; c.PositionTick = now;
            });
        }

        // 歌詞圓鈕：亮度（有沒有歌詞）、開著沒
        public void SetLiquidLyrics(IntPtr hwnd, double level, bool on)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (t.Liquid == null) return;
                t.Liquid.LyricsLevel = level; t.Liquid.LyricsOn = on;
            });
        }

        // 手指按在卡片上（x, y 是螢幕位置；按下時重新開始擴散，拖曳時跟著手指）/ 放開
        public void TouchLiquid(IntPtr hwnd, double x, double y, bool down)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (t.Liquid == null) return;
                var c = t.Liquid;
                if (down && !c.Touching) { c.TouchAge = 0; c.LastTick = 0; }   // 閒置後的第一格不要一次跳太多（dt 從 0 開始算）
                if (down) { c.TouchX = x; c.TouchY = y; }
                c.Touching = down;
            });
        }

        // 按下 / 放開卡片上的玻璃按鈕（只有歌詞圓鈕：LyricsButton）
        public void PressLiquidButton(IntPtr hwnd, int button, bool down)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (t.Liquid == null || button != LyricsButton) return;
                var c = t.Liquid;
                if (down && !c.Held) { c.PressedAt = now; c.LastTick = 0; }
                c.Held = down;
            });
        }

        // 鎖定畫面開始滑動（from → to，0 = 蓋住畫面、1 = 在上方；start 是 AnimationClock 的時間）：卡片照頁面的動作算慣性
        public void SlideCard(IntPtr hwnd, double from, double to, int ms, long start, int pageHeight, double scale)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (t.Motion == null) t.Motion = new CardMotion();
                var m = t.Motion;
                m.From = from; m.To = to; m.Ms = ms; m.Start = start; m.PageHeight = pageHeight; m.Scale = scale;
                if (m.LastTick == 0 || now - m.LastTick > 100) m.LastTick = now;   // 很久沒畫：從現在開始算
            });
        }

        // 鎖定畫面的時鐘：solid 是實心的字、field 是字形距離圖（兩張一樣大，貼在 (x, y)；scale 以 1080p 為 1）。
        // 兩樣一起換（換分鐘時不會有一格是新的字配舊的玻璃）。畫得出玻璃時，實心字（clockDigits）就不畫；field 是 null 只用實心字。
        // slots 不是 null：換分鐘的動畫（見 LockClock.ClockSlots），舊的距離圖留著畫離開的字；timeTop、digitY、digitH 是時間那一行的上緣、數字的中心和高度
        public void SetClock(IntPtr hwnd, int[] solid, float[] field, int width, int height, int x, int y, double scale,
            float[] slots, float timeTop, float digitY, float digitH)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                Replace(t, "clockDigits", solid == null ? null : CreateOverlay(t, solid, width, height, x, y));
                FreeClockOld(t);
                if (slots != null && t.ClockView != IntPtr.Zero && field != null)
                {
                    // 現在這張變成「舊的」，畫離開的字
                    t.ClockOldTexture = t.ClockTexture; t.ClockOldView = t.ClockView;
                    t.ClockTexture = t.ClockView = IntPtr.Zero;
                    t.ClockOldX = t.ClockX; t.ClockOldY = t.ClockY; t.ClockOldW = t.ClockW; t.ClockOldH = t.ClockH;
                    t.ClockSlots = slots; t.ClockAnimStart = now;
                    t.ClockTimeTop = timeTop; t.ClockDigitY = digitY; t.ClockDigitH = digitH;
                }
                FreeClockField(t);
                t.ClockField = field;
                t.ClockX = x; t.ClockY = y; t.ClockW = width; t.ClockH = height; t.ClockScale = scale;
                if (field == null) ReleaseLiquidScratch();
            });
        }

        // ---------- 引擎執行緒 ----------

        bool LiquidOn(Target t)
        {
            if (t.Liquid == null || t.Liquid.Spec == null) return false;
            return LiquidViews(t);
        }

        // 液態玻璃的著色器和這個畫面的繪圖目標、影片畫布的讀取介面都準備好了嗎（第一次用到時建立）
        bool LiquidViews(Target t)
        {
            if (t.VideoTexture == IntPtr.Zero || t.D2D == null) return false;
            if (liquid == null && !liquidTried && LiquidGlass.Ready)
            {
                liquidTried = true;
                liquid = LiquidGlass.Create(device, context, featureLevel);
            }
            if (liquid == null) return false;
            if (t.LiquidRtv == IntPtr.Zero) t.LiquidRtv = liquid.CreateTargetView(t.BackBuffer);
            if (t.LiquidScene == IntPtr.Zero) t.LiquidScene = liquid.CreateReadView(t.VideoTexture);
            return t.LiquidRtv != IntPtr.Zero && t.LiquidScene != IntPtr.Zero;
        }

        // 時鐘的玻璃數字（影片剛蓋上輸出畫面、還沒貼任何疊圖時畫）。畫不出來（沒有液態玻璃、顯示卡太舊、建不起來）就交給實心數字的疊圖
        // 回傳換分鐘的動畫還在不在跑（要不要再畫下一格）
        bool DrawClockGlass(Target t, long now)
        {
            t.ClockGlassOn = false;
            if (t.ClockField == null || !LiquidViews(t)) { FreeClockOld(t); return false; }
            if (clockLiquid == null && !clockTried)
            {
                clockTried = true;
                clockLiquid = LiquidGlass.Create(device, context, featureLevel);
            }
            if (clockLiquid == null) return false;
            if (t.ClockFieldFailed) return false;   // 這張距離圖放不上顯示卡：等下一分鐘換圖再試，不要每一格都重試
            if (t.ClockView == IntPtr.Zero && !clockLiquid.CreateField(t.ClockField, t.ClockW, t.ClockH, out t.ClockTexture, out t.ClockView)) { t.ClockFieldFailed = true; return false; }
            bool animating = ClockAnim(t, now);
            double s = t.ClockScale;
            // 模糊的範圍：時鐘那張圖加上四周（邊緣往字裡面取樣最多 16；圖本身四周也留了 30）；要畫的範圍就是那張圖（四周已經留了影子的空間）
            int margin = (int)Math.Ceiling(56 * s);
            int bx, by, bw, bh, ex, ey, ew, eh;
            Clip(t, t.ClockX - margin, t.ClockY - margin, t.ClockW + 2 * margin, t.ClockH + 2 * margin, out bx, out by, out bw, out bh);
            Clip(t, t.ClockX, t.ClockY, t.ClockW, t.ClockH, out ex, out ey, out ew, out eh);
            if (bw <= 0 || bh <= 0 || ew <= 0 || eh <= 0) return animating;
            bool reblur = t.FreshVideo && now - t.ClockBlurredAt >= 33 || bx != t.ClockBlurX || by != t.ClockBlurY || bw != t.ClockBlurW || bh != t.ClockBlurH;
            if (reblur) { t.ClockBlurredAt = now; t.ClockBlurX = bx; t.ClockBlurY = by; t.ClockBlurW = bw; t.ClockBlurH = bh; }
            clockLook.Scale = (float)s;
            t.ClockGlassOn = clockLiquid.DrawText(t.LiquidScene, t.Width, t.Height, t.LiquidRtv, t.ClockView, t.ClockX, t.ClockY, t.ClockW, t.ClockH,
                animating ? t.ClockOldView : IntPtr.Zero, animating ? clockAnim : null, clockLook, bx, by, bw, bh, ex, ey, ew, eh, reblur);
            return animating;
        }

        // 換分鐘的動畫這一格的樣子，填進 clockAnim（排法見 LiquidGlass 的 cbuffer ClockAnim）。照 iPhone（iOS 26 玻璃時鐘）的錄影：
        //   字本身不變形，一道上下的漸層遮罩（漸層寬半個字高）掃過去——舊字 0.06 秒抹掉（先慢後快），新字晚 0.03 秒開始、
        //   0.56 秒長滿（先快後慢，p = 1 − (1 − u)^1.5）；換點時慢 1.4 倍；沒變的字整串滑到新位置（指數，τ 0.125 秒）。
        //   全部跑完回傳 false（舊的距離圖還掉）
        bool ClockAnim(Target t, long now)
        {
            if (t.ClockSlots == null || t.ClockOldView == IntPtr.Zero) return false;
            double time = (now - t.ClockAnimStart) / 1000.0;
            const double OldTime = 0.06, NewDelay = 0.03, NewTime = 0.56, SlideTau = 0.125;
            int n = t.ClockSlots.Length / 8;
            double end = 0.8;
            for (int i = 0; i < n; i++) end = Math.Max(end, t.ClockSlots[i * 8 + 5] + (NewDelay + NewTime) * t.ClockSlots[i * 8 + 7] + 0.05);
            if (time > end) { FreeClockOld(t); return false; }
            var a = clockAnim;
            Array.Clear(a, 0, a.Length);
            double h = t.ClockDigitH, top = t.ClockDigitY - 0.53 * h, bottom = t.ClockDigitY + 0.53 * h, span = bottom - top, w = 0.5 * h;
            a[0] = n; a[1] = t.ClockTimeTop; a[2] = t.ClockOldX; a[3] = t.ClockOldY;
            a[4] = t.ClockOldW; a[5] = t.ClockOldH;
            for (int i = 0; i < n; i++)
            {
                int o = i * 8;
                int kind = (int)t.ClockSlots[o];
                double s = time - t.ClockSlots[o + 5], slow = t.ClockSlots[o + 7];
                bool up = t.ClockSlots[o + 6] > 0;
                double edge = -1e5, side = 1, dx = 0;   // 看得到的是 side × (y − edge) > 0 那一邊，漸層寬 w
                if (kind == 0) dx = t.ClockSlots[o + 4] * Math.Exp(-Math.Max(0, time) / SlideTau);
                else if (kind == 1)
                {
                    double u = Math.Max(0, Math.Min(1, (s - NewDelay * slow) / (NewTime * slow)));
                    double p = 1 - Math.Pow(1 - u, 1.5);
                    if (up) { side = 1; edge = bottom - p * (span + w); }    // 由下往上長
                    else { side = -1; edge = top + p * (span + w); }         // 由上往下長
                }
                else
                {
                    double q = Math.Pow(Math.Max(0, Math.Min(1, s / (OldTime * slow))), 1.5);
                    if (up) { side = -1; edge = bottom + w - q * (span + w); }   // 從底下開始抹
                    else { side = 1; edge = top - w + q * (span + w); }          // 從上面開始抹
                }
                int k = 8 + i * 12;
                a[k] = t.ClockSlots[o + 1]; a[k + 1] = t.ClockSlots[o + 2]; a[k + 2] = kind == 2 ? 1 : 0;
                a[k + 4] = t.ClockSlots[o + 3]; a[k + 5] = t.ClockDigitY; a[k + 6] = (float)edge; a[k + 7] = (float)w;
                a[k + 8] = (float)dx; a[k + 9] = (float)side;
            }
            return true;
        }

        readonly float[] clockAnim = new float[LiquidGlass.ClockAnimFloats];

        static void FreeClockOld(Target t)
        {
            LiquidGlass.Free(ref t.ClockOldView);
            LiquidGlass.Free(ref t.ClockOldTexture);
            t.ClockSlots = null;
        }

        static void FreeClockField(Target t)
        {
            LiquidGlass.Free(ref t.ClockView);
            LiquidGlass.Free(ref t.ClockTexture);
            t.ClockField = null;
            t.ClockFieldFailed = false;
            t.ClockGlassOn = false;
        }

        // 這一格卡片的動作和卡片上東西的彈簧；回傳還有沒有在動（要不要再畫下一格）。
        // 按著不放、拖曳時不算「在動」：彈簧停了、白光擴散完就不再一直重畫（拖曳時 UI 每次移動都會送新的位置，那時才畫）
        bool StepLiquid(Target t, long now)
        {
            bool moving = t.Motion != null && t.Motion.Step(now);
            var c = t.Liquid;
            if (c == null || c.Spec == null) return moving;
            double dt = c.LastTick == 0 ? 0 : Math.Min(1 / 30.0, (now - c.LastTick) / 1000.0);
            c.LastTick = now;

            // 進度條：播放中從上次回報的位置往後推；按住拖曳時變粗、變亮。
            // 位置突然往回跳超過 1.5 秒（循環播放回到開頭、上一首、往回跳）：從原本的位置掃回去（約 0.6 秒），不是一下子跳過去；
            // 平常播放器回報的小誤差、拖曳放開都不會觸發
            if (c.Duration > 0)
            {
                double pos = c.Position + (c.Playing ? (now - c.PositionTick) / 1000.0 : 0);
                double target = Math.Max(0, Math.Min(1, pos / c.Duration));
                if (c.Scrubbing || c.Progress < 0) c.Sweeping = false;
                else if (c.Progress - target > 1.5 / c.Duration && (!c.Sweeping || c.Progress - (target + c.Sweep.X) > 1.5 / c.Duration))
                    { c.Sweeping = true; c.Sweep.Jump(c.Progress - target); }   // 掃到一半又往回跳（連按兩次上一首）：從現在的位置接著掃
                if (c.Sweeping)
                {
                    double offset = c.Sweep.Step(0, 0.6, 1, dt);
                    c.Progress = Math.Max(0, Math.Min(1, target + offset));
                    if (!c.Sweep.Moving(0, 0.001)) { c.Sweeping = false; c.Progress = target; }
                }
                else c.Progress = target;
            }
            else { c.Progress = -1; c.Sweeping = false; }
            c.ScrubAmount = Math.Max(0, Math.Min(1, c.Scrub.Step(c.Scrubbing ? 1 : 0, 0.3, 0.85, dt)));

            // 歌詞圓鈕（玻璃）按下的樣子，照原型：按下 0.03 秒後才開始放大、整顆被白光填滿，放開彈回；很快的一下也至少亮 0.12 秒
            long since = now - c.PressedAt;
            bool pressing = c.PressedAt != 0 && (c.Held || since < 120) && since >= 30;
            double pressTarget = pressing ? 1 : 0;
            c.Pressed = Math.Max(0, c.Press.Step(pressTarget, pressing ? 0.24 : 0.54, pressing ? 0.84 : 0.58, dt));
            bool pressMoving = c.Press.Moving(pressTarget, 0.002) || c.PressedAt != 0 && since < 150;   // 等著開始放大、等著最短的 0.12 秒過去
            // 按住卡片的白光：按著時很快亮起來、放開慢慢退；範圍從手指下面往外擴散，大約 1.2 秒擴到最大（照原型）
            c.TouchAge += dt;
            c.TouchGlow += ((c.Touching ? 1 : 0) - c.TouchGlow) * Math.Min(1, dt * (c.Touching ? 8 : 2.5));
            if (!c.Touching && c.TouchGlow < 0.002) c.TouchGlow = 0;
            bool glowMoving = c.Touching ? c.TouchGlow < 0.998 || c.TouchAge < 1.2 : c.TouchGlow > 0;
            c.OnAmount = Math.Max(0, Math.Min(1, c.On.Step(c.LyricsOn ? 1 : 0, 0.3, 1, dt)));
            c.LyricsAlpha = c.LyricsDim.Step(Math.Min(1, c.LyricsLevel / 0.6), 0.3, 1, dt);

            return moving || c.Scrub.Moving(c.Scrubbing ? 1 : 0, 0.002) || c.Sweeping || pressMoving || glowMoving
                || c.On.Moving(c.LyricsOn ? 1 : 0, 0.002) || c.LyricsDim.Moving(Math.Min(1, c.LyricsLevel / 0.6), 0.002);
        }

        // 卡片現在的變形（沒有在動就是不變形）
        void CardTransform(Target t, out double sx, out double sy, out double dx, out double dy)
        {
            sx = sy = 1; dx = dy = 0;
            var spec = t.Liquid != null ? t.Liquid.Spec : null;
            if (t.Motion == null || spec == null) return;
            t.Motion.Transform(spec.X + spec.W / 2, spec.Y + spec.H / 2, out sx, out sy, out dx, out dy);
        }

        // 第一層：卡片本身的液態玻璃（影片剛蓋上輸出畫面、還沒貼任何疊圖時畫）。回傳還有沒有在動
        bool DrawLiquidBase(Target t, long now)
        {
            bool moving = StepLiquid(t, now);
            t.LiquidActive = LiquidOn(t);
            if (!t.LiquidActive) return moving;
            var spec = t.Liquid.Spec;
            double s = spec.Scale, sx, sy, dx, dy;
            CardTransform(t, out sx, out sy, out dx, out dy);
            double cx = (spec.X + spec.W / 2) * sx + dx, cy = (spec.Y + spec.H / 2) * sy + dy;
            var card = new LiquidGlass.Shape
            {
                X = (float)cx, Y = (float)cy, HalfW = (float)(spec.W / 2), HalfH = (float)(spec.H / 2), Radius = (float)spec.Radius,
                Appear = 1, Thick = (float)Math.Max(0.6, Math.Min(1.5, Math.Min(spec.W, spec.H) / 2 / (cardLook.Bevel * s))),
                Mag = 1, D00 = (float)(1 / sx), D11 = (float)(1 / sy),   // 不染色、不放大、沒有影子（照片裡都沒有）
                Focus = 0.45f,   // 上下兩條直邊的折射只剩 55%（背景的字不會被拉長）；左右邊、圓角全強度
            };
            // 模糊的範圍：卡片原本的位置加上四周（卡片的慣性移動也還在範圍裡）；要畫的範圍：卡片現在的位置加上影子
            // 四周 96：邊緣最多往外取樣 90 × 1.022（色差）− 中央放大內縮約 15 ≈ 77，再留模糊的寬度
            int margin = (int)Math.Ceiling(96 * s), slack = (int)Math.Ceiling(90 * s);   // 上下多留 90：卡片慣性晃動最多約 85
            int bx, by, bw, bh, ex, ey, ew, eh;
            Clip(t, spec.X - margin, spec.Y - margin - slack, spec.W + 2 * margin, spec.H + 2 * (margin + slack), out bx, out by, out bw, out bh);
            double hw = spec.W / 2 * sx, hh = spec.H / 2 * sy, pad = 52 * s;
            Clip(t, cx - hw - pad, cy - hh - pad, 2 * (hw + pad), 2 * (hh + pad), out ex, out ey, out ew, out eh);
            if (bw <= 0 || bh <= 0 || ew <= 0 || eh <= 0) return moving;
            // 影片沒換格、範圍也沒變：沿用上次的模糊（後面本來就糊掉了，一秒更新 30 次看起來跟 60 次一樣）
            bool reblur = t.FreshVideo && now - t.LiquidBlurredAt >= 33 || bx != t.LiquidBlurX || by != t.LiquidBlurY || bw != t.LiquidBlurW || bh != t.LiquidBlurH;
            if (reblur) { t.LiquidBlurredAt = now; t.LiquidBlurX = bx; t.LiquidBlurY = by; t.LiquidBlurW = bw; t.LiquidBlurH = bh; }
            liquidShapes[0] = card;
            cardLook.Scale = (float)s;
            // 畫不了（模糊圖建不起來）：這一格退回毛玻璃，下一格再試
            if (!liquid.DrawLayer(t.LiquidScene, t.Width, t.Height, t.LiquidRtv, liquidShapes, 1, WithTouch(t.Liquid, cardLook), bx, by, bw, bh, ex, ey, ew, eh, reblur))
                t.LiquidActive = false;
            return moving;
        }

        readonly LiquidGlass.Shape[] liquidShapes = new LiquidGlass.Shape[LiquidGlass.MaxShapes];

        // 按住卡片的白光（兩層玻璃都有）；沒按的時候清掉（質感是重複用的，不能留著上一次的）
        static LiquidGlass.Look WithTouch(LiquidCard c, LiquidGlass.Look look)
        {
            if (c.TouchGlow <= 0) { look.TouchGlow = 0; return look; }
            double s = c.Spec.Scale;
            look.TouchX = (float)c.TouchX; look.TouchY = (float)c.TouchY; look.TouchGlow = (float)c.TouchGlow;
            look.TouchRadius = (float)((30 + 380 * Math.Min(c.TouchAge, 1.2)) * s);
            return look;
        }

        // 把 (x, y, w, h) 夾在畫面裡、換成整數
        static void Clip(Target t, double x, double y, double w, double h, out int cx, out int cy, out int cw, out int ch)
        {
            int x0 = Math.Max(0, (int)Math.Floor(x)), y0 = Math.Max(0, (int)Math.Floor(y));
            int x1 = Math.Min(t.Width, (int)Math.Ceiling(x + w)), y1 = Math.Min(t.Height, (int)Math.Ceiling(y + h));
            cx = x0; cy = y0; cw = Math.Max(0, x1 - x0); ch = Math.Max(0, y1 - y0);
        }

        // 卡片上引擎自己畫的東西（在卡片內容那幾張圖之前，圓鈕的圖示才會在底上面）：進度條、歌詞圓鈕的底。
        // 座標是卡片沒變形時的位置，變形由 canvas 目前的變形處理
        void DrawLiquidControls(Target t, OverlayCanvas canvas)
        {
            var c = t.Liquid;
            if (c == null || c.Spec == null) return;
            var spec = c.Spec;
            double s = spec.Scale;
            // 進度條：高 6（按住時 9）、軌道白 29%（按住 35%）、已播放白 86%（按住 100%）；不知道歌曲長度就不畫
            if (c.Progress >= 0)
            {
                double sc = c.ScrubAmount, ph = (6 + 3 * sc) * s, plen = spec.ProgressX1 - spec.ProgressX0;
                canvas.FillSolid(spec.ProgressX0, spec.ProgressY - ph / 2, plen, ph, ph / 2, 1, 1, 1, 0.29 + 0.06 * sc);
                double played = plen * c.Progress;
                if (played > 0.5) canvas.FillSolid(spec.ProgressX0, spec.ProgressY - ph / 2, played, ph, ph / 2, 1, 1, 1, 0.86 + 0.14 * sc, spec.ProgressX0, plen);
            }
            // 歌詞圓鈕的底：關著淡白 16.5%、開著白（236，淺色玻璃也不會被當成白字翻黑）；按下時跟玻璃一起放大
            double r = LyricsRadius(c), on = c.OnAmount, la = c.LyricsAlpha;
            double gray = 1 + (236 / 255.0 - 1) * on, alpha = (0.165 * la) * (1 - on) + 0.92 * on;
            canvas.FillSolid(spec.LyricsX - r, spec.LyricsY - r, r * 2, r * 2, r, gray, gray, gray + (240 - 236) / 255.0 * on, alpha);
        }

        // 歌詞圓鈕現在的半徑：按下時放大（小按鈕約 1.25 倍，跟原型一樣）
        static double LyricsRadius(LiquidCard c)
        {
            double r = c.Spec.LyricsR;
            return r + c.Pressed * Math.Min(0.25 * r, 10 * c.Spec.Scale);
        }

        // 第二層：歌詞圓鈕的液態玻璃（卡片內容都畫好、Direct2D 結束之後）
        void DrawLiquidTop(Target t)
        {
            if (!t.LiquidActive) return;
            var c = t.Liquid;
            var spec = c.Spec;
            double s = spec.Scale, sx, sy, dx, dy;
            CardTransform(t, out sx, out sy, out dx, out dy);
            double r = LyricsRadius(c);
            var lyrics = new LiquidGlass.Shape
            {
                X = (float)(spec.LyricsX * sx + dx), Y = (float)(spec.LyricsY * sy + dy), HalfW = (float)(r * sx), HalfH = (float)(r * sy), Radius = (float)(r * Math.Min(sx, sy)),
                Appear = 1, Thick = 0.22f, Mag = 1, Energy = (float)Math.Min(1, c.Pressed), D00 = 1, D11 = 1,
            };
            liquidShapes[0] = lyrics;
            // 範圍：圓鈕加上影子（還有邊緣往外 50 取樣的環境反光）
            double pad = 56 * s;
            int ex, ey, ew, eh;
            Clip(t, lyrics.X - lyrics.HalfW - pad, lyrics.Y - lyrics.HalfH - pad, 2 * (lyrics.HalfW + pad), 2 * (lyrics.HalfH + pad), out ex, out ey, out ew, out eh);
            if (ew <= 0 || eh <= 0) return;
            topLook.Scale = (float)s;
            liquid.DrawOnTop(t.BackBuffer, t.LiquidRtv, liquidShapes, 1, WithTouch(c, topLook), ex, ey, ew, eh);
        }

        static void ReleaseLiquid(Target t)
        {
            FreeClockField(t);
            FreeClockOld(t);
            LiquidGlass.Free(ref t.LiquidRtv);
            LiquidGlass.Free(ref t.LiquidScene);
        }

        // 已經沒有任何畫面上有卡片了（鎖定畫面收起來）：模糊圖、複本圖還給顯示卡（著色器留著，下次打開不用重建）
        void ReleaseLiquidScratch()
        {
            bool card = false, clock = false;
            foreach (var other in targets) { if (other.Liquid != null) card = true; if (other.ClockField != null) clock = true; }
            if (liquid != null && !card)
            {
                liquid.ReleaseScratch();
                foreach (var other in targets) { other.LiquidBlurW = other.LiquidBlurH = 0; }   // 下次一定重新模糊
            }
            if (clockLiquid != null && !clock)
            {
                clockLiquid.ReleaseScratch();
                foreach (var other in targets) { other.ClockBlurW = other.ClockBlurH = 0; }
            }
        }
    }
}
