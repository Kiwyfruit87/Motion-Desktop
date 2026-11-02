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

            // 頁面往下蓋上來（打開鎖定畫面）
            bool Down { get { return To < From; } }

            // 頁面在 time 毫秒時的加速度（像素 / 秒²，用曲線本身的二階差分算），滑完就是 0。
            // 收起時推力在滑到 35%～50% 時平順地放掉；打開時一路推到底（一般的彈跳）
            double PageAccel(double time)
            {
                const double h = 4;
                if (Ms <= 0 || time > Start + Ms + 2 * h) return 0;
                double f = Down ? 0 : Math.Max(0, Math.Min(1, ((time - Start) / Ms - 0.35) / 0.15));
                return (PageY(time + h) - 2 * PageY(time) + PageY(time - h)) / (h * h) * 1e6 * (1 - f * f * (3 - 2 * f));
            }

            // 往前推到 now：頁面加速時卡片落後一點、減速時往前衝一點再彈回（彈簧 response 0.17，受頁面的加速度推）。
            // 打開：一般的彈跳（阻尼比 0.5，推到底）——1080p 約落後 45、衝過頭 37 像素就停。
            // 收起：阻尼比 0.3，推力在頁面滑到 35%～50% 時平順地放掉，之後卡片靠自己的彈性彈回來（頁面減速的後半段一直推，會把往回彈的那一下抵銷掉）：
            // 第二下看得出來、但比第一下弱——1080p 約 61 → 22 → 8；
            // 卡片移動得越快沿方向拉長越多（最多 6%，彈簧 0.5 / 0.52，停下時會稍微變扁再恢復）
            public bool Step(long now)
            {
                // 推力是照時間直接算的，這一格隔了多久都照實際的時間一小步一小步算（引擎卡一下也不會少算頁面的加速度）
                double dt = LastTick == 0 ? 0 : Math.Min(0.1, (now - LastTick) / 1000.0);
                LastTick = now;
                if (dt <= 0) return Running(now);
                const double h = 4;   // 毫秒：用曲線本身算速度（不受畫面更新時間不均勻影響）
                double pv = Sliding(now) || Sliding(now - (long)(2 * h)) ? (PageY(now + h) - PageY(now - h)) / (2 * h) * 1000 : 0;
                double w = 2 * Math.PI / 0.17, zeta = Down ? 0.5 : 0.3;
                int n = Math.Max(1, (int)Math.Ceiling(dt * 240));
                double step = dt / n, t0 = now - dt * 1000;
                for (int i = 0; i < n; i++)   // 每一小步在那一小步的中間取頁面的加速度
                {
                    double pa = PageAccel(t0 + (i + 0.5) * step * 1000);
                    DV += (-pa - w * w * D - 2 * zeta * w * DV) * step; D += DV * step;
                }
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
            public bool Playing, Scrubbing, CanSeek = true;
            public long PositionTick;
            public readonly Spring Scrub = new Spring();
            // 歌詞圓鈕
            public double LyricsLevel = 0.6;
            public bool LyricsOn;
            public readonly Spring On = new Spring(), LyricsDim = new Spring();
            // 歌詞顯示時玻璃往上延伸多少（像素，Extend 是目標；Extended 是彈簧現在長到哪）
            public double Extend, Extended;   // （Extended 縮回時可以短暫是負的：回彈）
            public double ExtendGoal;          // 彈簧追的目標：往上長時直接是 Extend；縮回時約 0.06 秒滑下去（力道慢慢加上去，頂點是圓的、不會像撞到天花板）
            public readonly Spring Grow = new Spring();
            // 左右跟著彈（像果凍）：寬度多（正）或少（負）幾成，被長高的速度推一下以後自己晃一兩下才停
            public double Wobbled;
            public readonly Spring Wobble = new Spring();
            // 按住歌詞圓鈕蓄力（按著時卡片上緣往下壓一點）、放開時照按了多久彈上去：
            // 力道 0～1（輕點是 0，跟原本一樣；越用力長得越快、衝過頭越多、回彈一兩下）；方向 −1～1（按在圓鈕的左邊、右邊）
            public bool Charging;
            public long ChargeStart;
            public bool LaunchPending;
            public long LaunchAt;
            public double LaunchPower, LaunchDir;
            // 往一側鼓出去（像果凍被從旁邊推了一下）：右緣往外多少像素（負的是左緣往外），彈一兩下回到 0
            public double Bulged;
            public readonly Spring Bulge = new Spring();
            // 歌詞圓鈕按下的樣子：按著沒、什麼時候按的、按下的程度
            public bool Held;
            public long PressedAt;
            public readonly Spring Press = new Spring();
            public double Pressed;
            // 按住卡片：從手指下面亮起來、白光往整張卡片擴散（放開才慢慢退），跟原型一樣
            public double TouchX, TouchY, TouchGlow, TouchAge;
            public double OldX, OldY, OldGlow, OldRadius;   // 上一次按的白光（很快又按一下時還沒退完）：留著照常慢慢退
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
        // 兩層玻璃的質感（每一格重複用，不另外配置）。卡片（見 Look.Clear）：以字好讀為先的半透明霧面玻璃——
        // 中央的散射跟著背景調：背景平順 0.6（微糊和輕模糊之間），背景很雜（燈、輪廓、紋理）0.82（輕模糊和重模糊之間）；靠邊那一圈 0.4（透明一點，看得到折射）；
        // 飽和度 1.25（暗的地方不加）、5% 帶一點環境色調的光；外圍折射帶 30 × 1.5 = 45（彎月面，最外緣往外取樣約 0.88 × 45 ≈ 40；上下直邊打折、圓角稍微加強，見卡片的 Focus）、
        // 色散 0.025（最外緣紅藍分開約 0.9 像素；分不到 0.12 像素的地方不算色散，省取樣）；細高光、內側提亮、細線的強度 1.15。
        // 之前的清澈版（中央 0.32、白霧 6.7%、折射帶 60、位移約 72）記在 E:\vw-tests\notes\clear_card；改成自適應之前的版本在 E:\vw-tests\cardlab\before
        readonly LiquidGlass.Look cardLook = new LiquidGlass.Look { Clear = 1, Scatter = 0.6f, ScatterBusy = 0.82f, ScatterRim = 0.4f, Sat = 1.25f, TintAmt = 0.05f, Refract = 0.88f, Bevel = 30, Spec = 1.15f, CA = 0.025f, Legible = 0, Reflect = 0 };
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
        public void SetLiquidProgress(IntPtr hwnd, double position, double duration, bool playing, bool scrubbing, bool canSeek)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (t.Liquid == null) return;
                var c = t.Liquid;
                c.Position = position; c.Duration = duration; c.Playing = playing && !scrubbing; c.Scrubbing = scrubbing; c.PositionTick = now;
                c.CanSeek = canSeek;
            });
        }

        // 歌詞顯示時卡片的玻璃往上延伸 up 像素，把歌詞包進來；0 = 收回。用彈簧長上去、縮回來（稍微回彈一點）
        public void SetLiquidExtend(IntPtr hwnd, double up)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (t.Liquid != null) t.Liquid.Extend = up;
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
                // 閒置後的第一格不要一次跳太多：很久沒畫才從現在開始算（還在動的話照常往下算，玻璃不會停一格）
                // 按下時白光從手指下面重新擴散。上一次的白光還沒退完（很快又按一下）：舊的留著照常慢慢退（另一層），
                // 新的從手指重新亮、重新擴散，畫的時候兩個取亮的（整片不會一下子暗掉、閃一下，也不會疊得比按一下更亮）
                if (down && !c.Touching)
                {
                    if (c.TouchGlow >= 0.002 && c.TouchGlow >= c.OldGlow) { c.OldX = c.TouchX; c.OldY = c.TouchY; c.OldGlow = c.TouchGlow; c.OldRadius = TouchRadius(c); }
                    c.TouchGlow = 0; c.TouchAge = 0;
                    if (now - c.LastTick > 100) c.LastTick = now;
                }
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
                if (down && !c.Held) { c.PressedAt = now; if (now - c.LastTick > 100) c.LastTick = now; }
                c.Held = down;
            });
        }

        // 按住歌詞圓鈕準備打開歌詞（蓄力，卡片上緣往下壓一點）/ 取消
        public void ChargeLiquid(IntPtr hwnd, bool on)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (t.Liquid == null) return;
                var c = t.Liquid;
                if (on && !c.Charging) { c.ChargeStart = now; if (now - c.LastTick > 100) c.LastTick = now; }
                c.Charging = on;
                if (!on) { c.LaunchPending = false; c.LaunchPower = 0; }   // 取消（放開前歌詞不見了）：不彈
            });
        }

        // 放開歌詞圓鈕、歌詞要往上展開了：power 0～1 是蓄了多少力（照按住的時間），dir −1～1 是按在圓鈕的哪一邊。
        // 要在 SetLiquidExtend 之前送（玻璃開始長的那一格就照這個力道彈）
        public void LaunchLiquid(IntPtr hwnd, double power, double dir)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (t.Liquid == null) return;
                var c = t.Liquid;
                c.LaunchPending = true; c.LaunchAt = now;   // 還是按著的樣子（壓著）：玻璃開始長的那一格才放開，中間不會先彈回原位一格
                c.LaunchPower = Math.Max(0, Math.Min(1, power)); c.LaunchDir = Math.Max(-1, Math.Min(1, dir));
                if (now - c.LastTick > 100) c.LastTick = now;
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
        // sameTime：時間沒變（只有日期、天氣變了）——正在跑的換分鐘動畫留著，不要切掉
        public void SetClock(IntPtr hwnd, int[] solid, float[] field, int width, int height, int x, int y, double scale,
            float[] slots, float timeTop, float digitY, float digitH, bool sameTime)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                Replace(t, "clockDigits", solid == null ? null : CreateOverlay(t, solid, width, height, x, y));
                if (!sameTime || field == null) FreeClockOld(t);
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
            if (t.ClockField == null) { FreeClockOld(t); return false; }
            if (!LiquidViews(t)) return false;   // 暫時畫不了（影片還沒第一格）：等著，換分鐘的動畫留著
            if (clockLiquid == null && !clockTried)
            {
                clockTried = true;
                clockLiquid = LiquidGlass.Create(device, context, featureLevel);
            }
            if (clockLiquid == null) { FreeClockOld(t); return false; }
            if (t.ClockFieldFailed) { FreeClockOld(t); return false; }   // 這張距離圖放不上顯示卡：等下一分鐘換圖再試，不要每一格都重試
            if (t.ClockView == IntPtr.Zero && !clockLiquid.CreateField(t.ClockField, t.ClockW, t.ClockH, out t.ClockTexture, out t.ClockView)) { t.ClockFieldFailed = true; FreeClockOld(t); return false; }
            bool animating = ClockAnim(t, now);
            double s = t.ClockScale;
            // 模糊的範圍：時鐘那張圖加上四周（邊緣往字裡面取樣最多 16；圖本身四周也留了 30）；要畫的範圍就是那張圖（四周已經留了影子的空間）
            int margin = (int)Math.Ceiling(56 * s);
            // 換分鐘的動畫中：新舊兩張圖的聯集（12:59 → 1:00 新的比較窄，舊的字在新圖外面還要畫）
            int cx0 = t.ClockX, cy0 = t.ClockY, cx1 = t.ClockX + t.ClockW, cy1 = t.ClockY + t.ClockH;
            if (animating)
            {
                cx0 = Math.Min(cx0, t.ClockOldX); cy0 = Math.Min(cy0, t.ClockOldY);
                cx1 = Math.Max(cx1, t.ClockOldX + t.ClockOldW); cy1 = Math.Max(cy1, t.ClockOldY + t.ClockOldH);
            }
            int bx, by, bw, bh, ex, ey, ew, eh;
            Clip(t, cx0 - margin, cy0 - margin, cx1 - cx0 + 2 * margin, cy1 - cy0 + 2 * margin, out bx, out by, out bw, out bh);
            Clip(t, cx0, cy0, cx1 - cx0, cy1 - cy0, out ex, out ey, out ew, out eh);
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
            int n = Math.Min(10, t.ClockSlots.Length / 8);   // cbuffer 只放得下 10 格
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
            // 放開時臨界阻尼：軟軟地落回原本的大小，不會縮過頭再被硬擋住（Apple 的按鈕放開不回彈）
            c.Pressed = Math.Max(0, c.Press.Step(pressTarget, pressing ? 0.24 : 0.35, pressing ? 0.84 : 1.0, dt));
            bool pressMoving = c.Press.Moving(pressTarget, 0.002) || c.PressedAt != 0 && since < 150;   // 等著開始放大、等著最短的 0.12 秒過去
            // 按住卡片的白光：按著時很快亮起來、放開慢慢退；範圍從手指下面往外擴散，大約 1.2 秒擴到最大（照原型）
            c.TouchAge += dt;
            c.TouchGlow += ((c.Touching ? 1 : 0) - c.TouchGlow) * Math.Min(1, dt * (c.Touching ? 8 : 2.5));
            if (!c.Touching && c.TouchGlow < 0.002) c.TouchGlow = 0;
            c.OldGlow -= c.OldGlow * Math.Min(1, dt * 2.5);   // 舊的白光跟放開時一樣慢慢退
            if (c.OldGlow < 0.002) c.OldGlow = 0;
            bool glowMoving = (c.Touching ? c.TouchGlow < 0.998 || c.TouchAge < 1.2 : c.TouchGlow > 0) || c.OldGlow > 0;
            c.OnAmount = Math.Max(0, Math.Min(1, c.On.Step(c.LyricsOn ? 1 : 0, 0.3, 1, dt)));
            c.LyricsAlpha = c.LyricsDim.Step(Math.Min(1, c.LyricsLevel / 0.6), 0.3, 1, dt);
            // 蓄力：按住歌詞圓鈕時卡片上緣往下壓（約 0.6 秒壓到最多 10 像素，先快後慢），像把彈簧壓緊
            double s = c.Spec.Scale;
            double charge = c.Charging ? 1 - Math.Pow(1 - Math.Min(1, (now - c.ChargeStart) / 600.0), 2) : 0;
            // 放開：玻璃開始往上長的那一格照蓄的力彈出去——力道越大彈簧越軟、越慢（response 0.45 → 0.62 秒）、阻尼越小，
            // 長上去的速度跟輕點差不多，但衝過頭多、回彈慢慢地晃一兩下；按在圓鈕的一邊，卡片的另一邊往外鼓一下再彈回來
            // （照偏離中心的平方：按在中間附近幾乎不鼓）。輕點（力道 0）跟原本一樣乾淨地長上去
            if (c.LaunchPending && c.Extend > 0)
            {
                c.LaunchPending = false; c.Charging = false;
                c.Bulge.Step(0, 0.4, 0.45, 0);
                c.Bulge.V -= c.LaunchDir * Math.Abs(c.LaunchDir) * c.LaunchPower * 320 * s;   // 蓄滿、按在圓鈕最邊緣：另一邊鼓出約 12 像素
            }
            else if (c.LaunchPending && now - c.LaunchAt > 1000) { c.LaunchPending = false; c.Charging = false; c.LaunchPower = 0; }   // 玻璃一直沒長（歌詞的圖還沒畫好）：不等了
            // 彈簧的軟硬一路連續地變回原本的（不在某個時間點突然換，同一個動作早一點、晚一點收回才不會像兩種材質）：
            // 長上去之後 0.8 秒內維持蓄的力，之後約 0.6 秒的時間常數慢慢變硬；中途收回的話，約 1.2 秒內慢慢變回原本的彈簧——
            // 收回時還是同一顆軟彈簧接著動（不會在半空中急停），還帶著彈性落地：上緣往下壓一點、左右鼓出來（見下面的寬度），再一起彈回。
            // 力道還沒退完就算卡片停了也要繼續畫（見最後的 return），不然停在一半，很久以後收回還帶著軟彈簧
            if (!c.LaunchPending && c.LaunchPower > 0)
            {
                if (c.Extend <= 0) c.LaunchPower -= dt / 1.2;
                else if (now - c.LaunchAt > 800) c.LaunchPower *= Math.Exp(-dt / 0.6);
                if (c.LaunchPower < 0.01) c.LaunchPower = 0;
            }
            double power = c.LaunchPending ? 0 : c.LaunchPower;
            // 長上去、縮回來：輕點幾乎不過衝（1080p 約 1 像素），乾淨地停在定位；蓄滿的衝過頭約 38 像素再慢慢彈回。
            // 往下（蓄力壓下、縮回時回彈）最多比原本的卡片矮約 8：上方的內距 16，封面、歌名跟玻璃上緣至少還留 8。
            // 越往下越難壓（g / (g + 8)，一路慢慢變硬），不是撞到一條線硬停
            c.ExtendGoal = c.Extend >= c.ExtendGoal ? c.Extend : c.Extend + (c.ExtendGoal - c.Extend) * Math.Exp(-dt / 0.06);
            if (Math.Abs(c.ExtendGoal - c.Extend) < 0.05) c.ExtendGoal = c.Extend;
            double growTarget = c.ExtendGoal - 10 * s * (c.LaunchPending ? 1 - Math.Pow(1 - Math.Min(1, (c.LaunchAt - c.ChargeStart) / 600.0), 2) : charge);
            // 縮回（歌詞收起來、沒有按著蓄力）用跟彈上去同一顆彈簧（一樣的快慢），只是阻尼大一點（蓄滿時 0.65，彈上去是 0.48）：
            // 一樣快快地落下、接近底時順順地慢下來，圓滑地衝過頭約 5 像素再回來（像彈上去的頂點倒過來）。
            // 不能照彈上去的阻尼：會往下衝過頭約 20 像素，上緣最多只能壓 8（封面、歌名要留距離），只好硬壓扁，看起來像卡住
            bool down = c.Extend <= 0 && !c.Charging;
            double grown = c.Grow.Step(growTarget, 0.45 + 0.17 * power, down ? 0.85 - 0.2 * power : 0.85 - 0.37 * power, dt), sink = 8 * s, g = Math.Max(0, -grown);
            // 往下多少（g）照 tanh 收斂：一開始跟著彈簧走，越深越難壓，不會超過 8；蓄力壓下照原本的 g / (g + 8)（壓得比較淺）
            c.Extended = grown >= 0 ? grown : down ? -sink * Math.Tanh(g / sink) : -sink * g / (g + sink);
            c.Bulged = c.Bulge.Step(0, 0.4, 0.45, dt);
            // 寬度跟高度是同一塊果凍（一顆彈簧追一個目標，目標由長高的彈簧算出來）：
            //   往上長得快時被拉長變窄（照長高的速度推，tanh 柔和地飽和，最多約 3.5%；蓄力彈出時推力最多 2.2 倍、上限 4%）；
            //   落下途中不變寬（還在空中、沒被壓；蓄力後縮回反而被拉細，見下面），只有真的往下壓扁（蓄力、縮回時衝過頭）時左右撐開：
            //   彈簧原本要往下多少（g），寬度就多 g / 卡片高 × 0.8 的比例（蓄力後縮回見下面）；
            //   按住蓄力（靜靜地壓著）時 0.5（上緣壓下約 4，兩邊各撐約 5）。
            // 兩個一起推同一顆彈簧，所以蓄力壓扁→放開往上拉長變細→落地壓扁變寬是同一個連續的動作，不會各晃各的；
            // 收窄最多 4%（每邊 8：左右內距 16，兩邊的字、圓鈕跟玻璃至少還留 8）
            double wob = 0.035 * (1 + 0.143 * power);
            // 縮回也跟彈上去一樣：落得快時被拉長變細，到底衝過頭時壓扁、左右鼓出來（比平常多：2 倍，約 11 像素）。
            // 照力道慢慢加上去（力道 0.2 以上才全部）：輕點收回跟原本一樣
            double h = Math.Max(1, c.Spec.H), hard = down ? Math.Min(1, power / 0.2) : 0;
            double speed = c.Grow.V > 0 ? c.Grow.V : -c.Grow.V * hard;
            double push = wob * Math.Tanh(-0.008 * (1 + 1.2 * power) * speed / h / wob)
                + (c.Charging ? 0.5 : 0.8 + 1.2 * hard) * g / h;
            double wobbled = c.Wobble.Step(push, 0.2, 0.85, dt);   // 跟得比較緊（0.2 秒）：壓扁時馬上鼓、變細時馬上收
            c.Wobbled = wobbled >= 0 ? wobbled : -0.04 * Math.Tanh(-wobbled / 0.04);

            return moving || c.ExtendGoal != c.Extend || c.Grow.Moving(growTarget, 0.3) || c.Wobble.Moving(push, 0.0005) || c.Bulge.Moving(0, 0.05) || c.Charging && charge < 1 || c.LaunchPower > 0 || c.Scrub.Moving(c.Scrubbing ? 1 : 0, 0.002) || c.Sweeping || pressMoving || glowMoving
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
            // 歌詞顯示時往上延伸 ext（下緣不動；折射帶的寬度照原本的卡片算，不跟著變）；左右跟著彈（見 Wobble）、往一側鼓出去（見 Bulge）
            double ext = t.Liquid.Extended, wobble = t.Liquid.Wobbled, bulge = t.Liquid.Bulged;
            double cx = (spec.X + spec.W / 2 + bulge / 2) * sx + dx, cy = (spec.Y + spec.H / 2 - ext / 2) * sy + dy;
            var card = new LiquidGlass.Shape
            {
                X = (float)cx, Y = (float)cy, HalfW = (float)(spec.W / 2 * (1 + wobble) + Math.Abs(bulge) / 2), HalfH = (float)((spec.H + ext) / 2), Radius = (float)spec.Radius,
                Appear = 1, Thick = (float)Math.Max(0.6, Math.Min(1.5, Math.Min(spec.W, spec.H) / 2 / (cardLook.Bevel * s))),
                Mag = 1, Shadow = 0.11f, D00 = (float)(1 / sx), D11 = (float)(1 / sy),   // 不染色、不放大；很淡、很柔的影子（浮在桌布上，不是黑框）
                Focus = 0.45f,   // 上下兩條直邊的折射只剩 55%（背景的字不會被拉長）；左右邊、圓角全強度
            };
            // 模糊的範圍：卡片原本的位置加上四周（卡片的慣性移動也還在範圍裡）；要畫的範圍：卡片現在的位置加上影子
            // 四周 96：邊緣最多往外取樣 90 × 1.022（色差）− 中央放大內縮約 15 ≈ 77，再留模糊的寬度
            int margin = (int)Math.Ceiling(96 * s), slack = (int)Math.Ceiling(90 * s);   // 上下多留 90：卡片慣性晃動最多約 85
            int bx, by, bw, bh, ex, ey, ew, eh;
            Clip(t, spec.X - margin, spec.Y - ext - margin - slack, spec.W + 2 * margin, spec.H + ext + 2 * (margin + slack), out bx, out by, out bw, out bh);
            double hw = (spec.W / 2 * (1 + wobble) + Math.Abs(bulge) / 2) * sx, hh = (spec.H + ext) / 2 * sy, pad = 52 * s;
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
            look.TouchGlow = (float)Math.Max(0, c.TouchGlow); look.Touch2Glow = (float)c.OldGlow;
            if (c.TouchGlow > 0) { look.TouchX = (float)c.TouchX; look.TouchY = (float)c.TouchY; look.TouchRadius = (float)TouchRadius(c); }
            if (c.OldGlow > 0) { look.Touch2X = (float)c.OldX; look.Touch2Y = (float)c.OldY; look.Touch2Radius = (float)c.OldRadius; }
            return look;
        }

        // 白光現在擴散到多大（從手指下面 30 像素開始，約 1.2 秒擴到 410）
        static double TouchRadius(LiquidCard c) { return (30 + 380 * Math.Min(c.TouchAge, 1.2)) * c.Spec.Scale; }

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
            // 進度條：高 6（按住時 9）、軌道白 29%（按住 35%；播放器不接受跳轉、不能拖時 20%）、已播放白 86%（按住 100%）；不知道歌曲長度就不畫
            if (c.Progress >= 0)
            {
                double sc = c.ScrubAmount, ph = (6 + 3 * sc) * s, plen = spec.ProgressX1 - spec.ProgressX0;
                canvas.FillSolid(spec.ProgressX0, spec.ProgressY - ph / 2, plen, ph, ph / 2, 1, 1, 1, c.CanSeek ? 0.29 + 0.06 * sc : 0.2);
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
