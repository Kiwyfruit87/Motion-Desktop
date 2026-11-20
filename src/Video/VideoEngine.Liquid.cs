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
            public double ProgressGrow;                       // 按住進度條時兩端各往外長多少（整數像素，停住時字才清楚）
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
            public PagePath Path;                  // 頁面怎麼滑（0 = 蓋住畫面、1 = 在畫面上方；拖曳中每次移動換一條）
            public double PageHeight;
            public double D, DV;                  // 卡片相對頁面的位移（像素）和速度
            public readonly Spring Stretch = new Spring();   // 沿滑動方向拉長的程度
            public long LastTick;
            public double Scale = 1;
            double TrackV;                         // 拖曳中這一格用的速度（像素 / 秒，見 Step）
            double EvY, EvV; long EvT;             // 拖曳中最後一次滑鼠移動：頁面位置、去掉抖動的速度、時間（見 Feed）

            // 拖曳中每次滑鼠移動（新的停住的 path）：照滑鼠事件本身的時間算速度——不用畫面一格一格去差，
            // 滑鼠事件和畫面更新不同步，那樣有的格子剛好沒有新事件（速度 0）、有的有兩個（速度加倍），卡片會一直抖。
            // 再用 1€ 濾波（像相機的防手震：慢的時候濾得重、把細碎的抖動吃掉；快的時候濾得輕、跟得上，不會慢半拍）
            public void Feed(PagePath path)
            {
                double y = -PageHeight * path.To;   // 滑鼠的位置（頁面畫的時候是從上一次補過來的，見 PagePath.Hold）
                bool first = Path == null || !Path.IsHold || path.Start - EvT > 200;
                if (first) { EvY = y; EvT = path.Start; EvV = 0; return; }
                long gap = path.Start - EvT;
                if (gap <= 0) { EvY = y; return; }   // 同一毫秒裡的兩次移動：位置照新的，速度下次一起算
                double dts = gap / 1000.0, raw = (y - EvY) / dts;
                double cutoff = 3 + 0.006 * Math.Abs(EvV) / Scale;   // 1€：基本 3 Hz，每快 1000 像素 / 秒多 6 Hz
                double tau = 1 / (2 * Math.PI * cutoff);
                EvV += (raw - EvV) * dts / (dts + tau);
                EvY = y; EvT = path.Start;
            }

            // 頁面在 time 毫秒時往上的位移（像素，往上是負的）
            public double PageY(double time) { return Path == null ? 0 : -PageHeight * Path.At(time); }

            public bool Sliding(long now) { return Path != null && !Path.Done(now); }

            // 頁面往下蓋上來（打開鎖定畫面）
            bool Down { get { return Path != null && Path.To < Path.From; } }

            // 頁面在 time 毫秒時的加速度（像素 / 秒²，用曲線本身的二階差分算），滑完就是 0（拖曳中頁面跟著滑鼠，也是 0）。
            // 收起時推力在滑到 35%～50% 時平順地放掉；打開時一路推到底（一般的彈跳）
            double PageAccel(double time)
            {
                const double h = 4;
                if (Path == null || Path.IsHold || Path.Done(time - 2 * h)) return 0;   // 拖曳中的速度變化另外算（見 Step；補位置的那段是分段直線，二階差分會在接點冒出假的推力）
                double f = Down ? 0 : Math.Max(0, Math.Min(1, (Path.Progress(time) - 0.35) / 0.15));
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
                // 用曲線本身算速度（不受畫面更新時間不均勻影響）
                double pv = Path == null ? 0 : -PageHeight * Path.Velocity(now);
                bool hold = Path != null && Path.IsHold;
                if (hold)
                {
                    // 拖曳中：速度用滑鼠事件算好的那個（見 Feed，已經去掉抖動）；滑鼠 20 毫秒沒動開始當作在停、50 毫秒就是停了（目標 0，
                    // 平順地降，偶爾一次事件來得晚不會突然掉一下）。
                    // 這一格的速度再平順地追上目標（15 毫秒），速度變了多少，卡片就反方向被推多少（急煞時往前衝一下再彈回，跟滑完停下一樣）
                    double target = EvV * Math.Max(0, Math.Min(1, (50 - (now - EvT)) / 30.0));
                    double nv = TrackV + (target - TrackV) * (1 - Math.Exp(-dt / 0.015));
                    double cap = 1800 * Scale;   // 甩很快時推力不要無限大（彈性軟、同樣的推力會帶得比較遠，上限低一點：最多約 50 像素）
                    DV -= cap * (Math.Tanh(nv / cap) - Math.Tanh(TrackV / cap));
                    TrackV = nv;
                    pv = nv;
                }
                else TrackV = 0;
                // 用滑鼠拖（和放開後彈到定位）：軟一點的彈性（0.3 秒、阻尼比 0.7）——急煞時順勢往前帶一下、慢慢回來，不會像彈簧一樣彈好幾下
                bool soft = Path != null && !Path.IsCurve;
                double w = 2 * Math.PI / (soft ? 0.3 : 0.17), zeta = soft ? 0.7 : Down ? 0.5 : 0.3;
                int n = Math.Max(1, (int)Math.Ceiling(dt * 240));
                double step = dt / n, t0 = now - dt * 1000;
                for (int i = 0; i < n; i++)   // 每一小步在那一小步的中間取頁面的加速度
                {
                    double pa = PageAccel(t0 + (i + 0.5) * step * 1000);
                    DV += (-pa - w * w * D - 2 * zeta * w * DV) * step; D += DV * step;
                }
                double speed = Math.Abs(pv + DV);
                // 拖曳中拉長跟得緊一點（0.25 秒）：急煞時馬上縮回、稍微變扁再恢復，不會慢半拍
                Stretch.Step(0.06 * Math.Tanh(speed / (900 * Scale)), hold ? 0.3 : 0.5, hold ? 0.75 : 0.52, dt);   // 拖曳中：跟得緊但不晃（阻尼比 0.75）
                return Running(now);
            }

            public bool Running(long now) { return Sliding(now) || Math.Abs(TrackV) > 1 || Math.Abs(D) > 0.05 || Math.Abs(DV) > 0.5 || Stretch.Moving(0, 0.0002); }

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
            public double ExtendFull;          // 上一次往上長的目標（縮回去時歌詞照它算還差多少，跟著玻璃沉下去）
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
            public long SwellAt;               // 換歌時邊框亮一下（見 CardSwell）：什麼時候開始的（0 = 沒有）
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
        // 飽和度 1.25（暗的地方不加）、5% 帶一點環境色調的光；外圍折射帶 30 × 1.5 = 45（彎月面，最外緣往外取樣約 0.45 × 45 ≈ 20，往內很快衰減；上下直邊打折、圓角稍微加強，見卡片的 Focus）、
        // 色散 0.025（最外緣紅藍分開約 0.9 像素；分不到 0.12 像素的地方不算色散，省取樣）；細高光、內側提亮、細線的強度 1.15。
        // 之前的清澈版（中央 0.32、白霧 6.7%、折射帶 60、位移約 72）記在 E:\vw-tests\notes\clear_card；改成自適應之前的版本在 E:\vw-tests\cardlab\before
        readonly LiquidGlass.Look cardLook = new LiquidGlass.Look { Clear = 1, Scatter = 0.6f, ScatterBusy = 0.82f, ScatterRim = 0.4f, Sat = 1.25f, TintAmt = 0.05f, Refract = 0.45f, Bevel = 30, Spec = 1.15f, CA = 0.025f, Legible = 0, Reflect = 0 };
        readonly LiquidGlass.Look topLook = LiquidGlass.Look.OnTop(1);
        // 時鐘的玻璃數字（照 iOS 26 玻璃時鐘的照片量出來的，見 LiquidGlass 的 TextGlass；以下是 1080p 的像素）：
        // 邊緣往字裡面取樣的鉤 16、衰減 0.9（只在最外緣約 1～3 像素）、色散 0.4；裡面輕輕糊（0.15）、加一層無色的白；輪廓線 1
        // Clear = 1：模糊時多算一層微糊（時鐘的字身要用，見 TextGlass 的 frost）；Clear 的另一個用處只在卡片的著色器裡，時鐘不讀
        readonly LiquidGlass.Look clockLook = new LiquidGlass.Look { Refract = 16, Bevel = 0.9f, CA = 0.4f, Scatter = 0.15f, Legible = 1, Spec = 1, Clear = 1 };

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
                if (t.Liquid == null) return;
                t.Liquid.Extend = up;
                if (up > 0) t.Liquid.ExtendFull = up;
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

        // 換歌、封面的顏色變了：邊框亮一下
        public void SwellCardGlow(IntPtr hwnd)
        {
            WithTarget(hwnd, delegate(Target t, long now) { if (t.Liquid != null) t.Liquid.SwellAt = now; });
        }

        // 換歌時邊框的光現在多亮（0～1）：約 0.2 秒亮起來、0.45 秒開始退、1.6 秒退完。
        // 光暈平常只有重拍才亮，換歌時節奏偵測又剛重置，顏色漸變都發生在暗的時候看不到；
        // 這一下讓舊封面的顏色換到新封面的顏色看得見（節奏開關都有）。只亮一次，平常玻璃還是沒有自己的顏色
        static double CardSwell(LiquidCard c, long now)
        {
            if (c.SwellAt == 0) return 0;
            double t = (now - c.SwellAt) / 1000.0;
            if (t < 0 || t >= 1.6) { c.SwellAt = 0; return 0; }
            double up = Math.Min(1, t / 0.2), down = Math.Max(0, Math.Min(1, (t - 0.45) / 1.15));
            return 0.8 * (up * up * (3 - 2 * up)) * (1 - down * down * (3 - 2 * down));
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

        // 鎖定畫面滑進來、滑走（照 iPhone 從主畫面拉下鎖定畫面）：視窗不動，頁面在畫面裡移動。
        // from → to（0 = 蓋住畫面、1 = 在上方），start 是 AnimationClock 的時間，曲線跟 LockScreen.Animate 一樣；
        // snapshot 是滑動開始前底下桌面的快照（w × h 的 BGRA，跟這個畫面一樣大）。頁面下緣以下是快照、下緣是一條玻璃的邊；
        // 頁面裡的背景一開始也是快照（透明的玻璃），打開時滑到 55%～95% 換成桌布、收起時一開始就換回快照。
        // 準備不起來（顯示卡不支援、大小不合）就什麼都不做：呼叫的人要用 SheetCanSlide 先問，不行就照原本移動視窗
        // 回傳的 SheetRequest：State 變成 1 表示準備好、已經畫出還沒開始滑的第一格（打開時就是桌面本身），UI 這時呼叫 SheetPath 才開始滑；
        // 變成 2 表示準備不起來（顯示卡不支援、建不起來、大小不合），UI 要照原本移動整個視窗。等太久沒結果就 CancelSheet
        public SheetRequest SlideSheet(IntPtr hwnd, int[] snapshot, int w, int h, double from, double to)
        {
            var request = new SheetRequest();
            Post(delegate
            {
                var t = targets.Find(x => x.Hwnd == hwnd);
                if (t == null || !t.CanOverlay) { request.State = 2; return; }
                FreeSheet(t);
                if (snapshot == null || w != t.Width || h != t.Height || !LiquidViews(t) || !liquid.SheetSupported) { request.State = 2; return; }
                var s = new SheetSlide { From = from, To = to, Request = request };
                IntPtr none;
                if (!liquid.CreateCanvas(w, h, snapshot, false, out s.SnapTexture, out s.SnapView, out none)
                    || !liquid.CreateCanvas(w, h, null, true, out s.SceneTexture, out s.SceneView, out s.SceneRtv)
                    || !liquid.CreateCanvas(w, h, null, false, out s.FrameTexture, out s.FrameView, out none))
                {
                    s.Free();
                    request.State = 2;
                    return;
                }
                t.Sheet = s;
                redraw = true;
            });
            return request;
        }

        // 準備好以後開始滑：照 path 走（照曲線滑；拖曳中每次滑鼠移動換一條停在新位置的；放開後換成彈簧）
        public void SheetPath(IntPtr hwnd, PagePath path)
        {
            WithTarget(hwnd, delegate(Target t, long now) { if (t.Sheet != null) { t.Sheet.Path = path; redraw = true; } });
        }

        // UI 等太久沒結果：不滑了，收掉
        public void CancelSheet(IntPtr hwnd)
        {
            WithTarget(hwnd, delegate(Target t, long now) { FreeSheet(t); });
        }

        // SlideSheet 的結果（UI 執行緒讀、引擎執行緒寫）：0 還在準備、1 好了、2 不行
        public class SheetRequest { public volatile int State; }

        // 這台電腦能不能用頁面在畫面裡移動的滑法（著色器編好了；顯示卡建不起來的話 SlideSheet 會什麼都不做）
        public static bool SheetCanSlide { get { return LiquidGlass.SheetCompiled; } }

        // 鎖定畫面滑進來、滑走的狀態（見 SlideSheet）
        internal class SheetSlide
        {
            public double From, To;      // 打開（1 → 0）還是收起（0 → 1）
            public PagePath Path;        // null：還沒開始（停在 from，等 UI 呼叫 SheetPath）
            public SheetRequest Request; // 第一格畫好就告訴 UI
            public const int CardIn = 280;   // 打開滑完以後卡片淡入多久（毫秒；這段時間頁面已經停好，合成的結果就是頁面本身）
            // 頁面外露出的桌面（照 iPhone 的錄影量的）：頁面在畫面上時暗到 75%；打開時頁面一開始動就在 0.17 秒內暗下去，
            // 收起滑完以後 0.3 秒亮回原樣（UI 等亮完才把視窗收掉，見 LockScreen.CloseAfterSlide）
            public const double Dim = 0.75;
            public const int DimIn = 170, DimOut = 300;
            public long DoneAt;          // 收起：滑完的時間（亮回來從這時開始；0 = 還沒滑完）
            public double DrawnAt;       // 這一格的頁面位置是照哪個時間算的（SheetEnd 照它判斷滑完了沒：畫的時候還差一點沒到，就要再畫一格）
            public bool Opening { get { return To < From; } }
            public IntPtr SnapTexture, SnapView, SceneTexture, SceneView, SceneRtv, FrameTexture, FrameView;
            public IntPtr VideoScene;   // 這一格畫背景前的 LiquidScene（影片），畫完換回去
            public bool Drawing;        // 這一格有沒有畫背景（SheetEnd 才要合成）
            public float Oy, Fade, Edge;
            public double U;            // 滑到哪（0～1）

            public void Free()
            {
                LiquidGlass.Free(ref FrameView); LiquidGlass.Free(ref FrameTexture);
                LiquidGlass.Free(ref SceneRtv); LiquidGlass.Free(ref SceneView); LiquidGlass.Free(ref SceneTexture);
                LiquidGlass.Free(ref SnapView); LiquidGlass.Free(ref SnapTexture);
            }
        }

        static void FreeSheet(Target t)
        {
            if (t.Sheet == null) return;
            if (t.Sheet.Drawing) t.LiquidScene = t.Sheet.VideoScene;
            t.Sheet.Free();
            t.Sheet = null;
        }

        // 窗戶式（照 iPhone）：頁面裡的影片固定在畫面上不動、也不淡成桌面，頁面像一扇往上拉的窗，只有時鐘和卡片跟著頁面走。
        // false：影片跟著頁面移動，收到一半前慢慢淡成底下的桌面（頁面最後看起來變透明）
        const bool SheetWindow = false;

        static double SheetSmooth(double a, double b, double x) { double t = Math.Max(0, Math.Min(1, (x - a) / (b - a))); return t * t * (3 - 2 * t); }

        // 這一格的背景（影片和快照混好）畫進 SceneTexture、蓋到輸出畫面上，卡片和時鐘的玻璃這一格改折射它。沒在滑回傳 false（照平常蓋影片）
        bool SheetBegin(Target t, long now)
        {
            var s = t.Sheet;
            if (s == null) return false;
            s.Drawing = false;
            if (!LiquidViews(t)) return false;
            var path = s.Path;
            s.DrawnAt = AnimationClock.Precise;   // 帶小數的毫秒：高更新率的螢幕上每格移動的量才平均
            double hidden = path == null ? s.From : path.At(s.DrawnAt);
            if (path == null || path.IsCurve)
            {
                s.U = path == null ? 0 : path.Progress(now);
                s.Edge = (float)(SheetSmooth(0, 0.08, s.U) * (1 - SheetSmooth(0.88, 1, s.U)));   // 玻璃的邊只在頁面移動時有（剛開始動、快停下時淡掉）
                s.Fade = (float)(s.Opening ? SheetSmooth(0.55, 0.95, s.U) : 1 - SheetSmooth(0, 0.35, s.U));
            }
            else
            {
                // 用滑鼠拖（和放開後彈到定位）：跟著頁面的位置走，拖回原位就跟平常一模一樣（桌布全亮、沒有邊）
                s.U = hidden;
                s.Edge = (float)(SheetSmooth(0, 0.03, hidden) * (1 - SheetSmooth(0.97, 1, hidden)));
                s.Fade = (float)(1 - SheetSmooth(0, 0.5, hidden));
            }
            if (SheetWindow) s.Fade = 1;   // 窗戶式：頁面裡一直是影片（不淡成桌面）
            // 整數像素：頁面整張平移時不用雙線性重取樣（時鐘 1 像素的細輪廓、細高光才不會一格清楚、一格糊成兩條半亮的線，看起來一直在閃）
            s.Oy = (float)Math.Round(-t.Height * hidden);
            liquid.DrawSheet(false, t.LiquidScene, s.SnapView, s.SceneRtv, t.Width, t.Height, s.Oy, s.Fade, SheetWindow ? 1 : 0, 0, 0, 1);
            copyResource(context, t.BackBuffer, s.SceneTexture);
            s.VideoScene = t.LiquidScene;
            t.LiquidScene = s.SceneView;
            t.FreshVideo = true;   // 背景每一格都在變：玻璃要重新模糊
            s.Drawing = true;
            return true;
        }

        // 畫好的頁面整張移到現在的位置，下面露出快照、邊上一條玻璃，畫回輸出畫面。
        // 打開滑完（頁面蓋滿、桌布全亮，跟平常一樣）就收掉；收起滑完留著（之後的每一格都是快照，直到視窗關掉）
        void SheetEnd(Target t, long now)
        {
            var s = t.Sheet;
            if (s == null || !s.Drawing) return;
            t.LiquidScene = s.VideoScene;
            s.Drawing = false;
            copyResource(context, s.FrameTexture, t.BackBuffer);
            double scale = t.Height / 1080.0;
            var path = s.Path;
            // 收起滑完（不是拖曳中停住的那種）：記下時間，桌面從這時亮回來
            bool closed = path != null && !s.Opening && !path.IsHold && path.To >= 1 && path.Done(now);
            if (closed && s.DoneAt == 0) s.DoneAt = now;
            liquid.DrawSheet(true, s.FrameView, s.SnapView, t.LiquidRtv, t.Width, t.Height, s.Oy, s.Fade, (float)(42 * scale), (float)scale, s.Edge, (float)SheetDim(s, now));
            if (s.Request != null && s.Request.State == 0) s.Request.State = 1;   // 第一格畫好了（這一格接著就 Present）
            if (path == null) return;                                              // 還沒開始滑：等 UI（SheetPath 會再要求重畫）
            if (s.Opening && now >= path.Start + path.Ms + SheetSlide.CardIn) FreeSheet(t);
            else if (!path.Done(s.DrawnAt) || s.Opening || closed && now - s.DoneAt < SheetSlide.DimOut + 50) redraw = true;   // 拖曳中停住的那種不用一直畫（滑鼠一動 SheetPath 就會要求重畫）
        }

        // 頁面外露出的桌面現在多亮（見 SheetSlide.Dim）
        static double SheetDim(SheetSlide s, long now)
        {
            var path = s.Path;
            double dark = 1 - SheetSlide.Dim;
            if (s.Opening) return path == null ? 1 : 1 - dark * SheetSmooth(0, 1, (now - path.Start) / (double)SheetSlide.DimIn);
            if (s.DoneAt == 0) return SheetSlide.Dim;
            return SheetSlide.Dim + dark * SheetSmooth(0, 1, (now - s.DoneAt) / (double)SheetSlide.DimOut);
        }

        // 卡片（玻璃、卡片上的圖、進度條和圓鈕）現在看得到多少：照 iPhone 從主畫面拉下鎖定畫面，滑下來時卡片先藏起來，
        // 頁面停好以後約 0.28 秒淡入。收起（滑走、用滑鼠往上拖）時一邊跟著頁面滑一邊淡出（見 TopFade），拖回原位又淡回來
        static double CardAlpha(Target t, long now)
        {
            var s = t.Sheet;
            if (s == null) return 1;
            if (!s.Opening)
            {
                var spec = t.Liquid != null ? t.Liquid.Spec : null;
                return spec == null ? 1 : TopFade(t, spec.Y + spec.H / 2.0, now);
            }
            if (s.Path == null) return 0;
            return SheetSmooth(0, 1, (now - s.Path.Start - s.Path.Ms) / (double)SheetSlide.CardIn);
        }

        // 收起（滑走、用滑鼠往上拖）時，頁面上的東西照自己現在的位置淡出：中心越靠近螢幕上緣越淡，到上緣就看不到。
        // 從離上緣 35% 螢幕高（或原本的位置，比較近的那個）開始淡——上面的時鐘一動就開始淡、很快淡完，下面的卡片要滑到上半部才開始淡。
        // center 是頁面停好時的中心 y（畫面座標）。打開、沒在滑時是 1
        static double TopFade(Target t, double center, long now)
        {
            var s = t.Sheet;
            if (s == null || s.Opening || s.Path == null) return 1;
            double zone = Math.Min(center, 0.35 * t.Height);
            if (zone < 1) return 1;
            return SheetSmooth(0, zone, center - t.Height * s.Path.At(now));
        }

        // 時鐘現在看得到多少（見 TopFade）
        static double ClockAlpha(Target t, long now)
        {
            return t.ClockH > 0 ? TopFade(t, t.ClockY + t.ClockH / 2.0, now) : 1;
        }

        // 鎖定畫面的頁面開始照 path 滑（0 = 蓋住畫面、1 = 在上方；拖曳中每次移動換一條）：卡片照頁面的動作算慣性
        public void SlideCard(IntPtr hwnd, PagePath path, int pageHeight, double scale)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (t.Motion == null) t.Motion = new CardMotion();
                var m = t.Motion;
                m.PageHeight = pageHeight; m.Scale = scale;
                if (path.IsHold) m.Feed(path);   // 拖曳中：照滑鼠事件算速度（要在換掉 Path 之前，看上一條是不是也是拖曳中）
                m.Path = path;
                redraw = true;
                if (m.LastTick == 0 || now - m.LastTick > 100) m.LastTick = now;   // 很久沒畫：從現在開始算
            });
        }

        // 鎖定畫面的時鐘：solid 是實心的字、field 是字形距離圖（兩張一樣大，貼在 (x, y)；scale 以 1080p 為 1）。
        // 兩樣一起換（換分鐘時不會有一格是新的字配舊的玻璃）。畫得出玻璃時，實心字（clockDigits）就不畫；field 是 null 只用實心字。
        // slots 不是 null：換分鐘的動畫（見 LockClock.ClockSlots），舊的距離圖留著畫離開的字；timeTop、digitY、digitH 是時間那一行的上緣、數字的中心和高度
        // sameTime：時間沒變（只有日期、天氣變了）——正在跑的換分鐘動畫留著，不要切掉（日期那一行自己有動畫要跑時例外）；
        // dateY、dateH：日期那一行的中心和高度（日期、溫度、天氣圖示換掉時的動畫用）
        public void SetClock(IntPtr hwnd, int[] solid, float[] field, int width, int height, int x, int y, double scale,
            float[] slots, float timeTop, float digitY, float digitH, bool sameTime, float dateY, float dateH)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                Replace(t, "clockDigits", solid == null ? null : CreateOverlay(t, solid, width, height, x, y));
                if (!sameTime || field == null || slots != null) FreeClockOld(t);
                if (slots != null && t.ClockView != IntPtr.Zero && field != null)
                {
                    // 現在這張變成「舊的」，畫離開的字
                    t.ClockOldTexture = t.ClockTexture; t.ClockOldView = t.ClockView;
                    t.ClockTexture = t.ClockView = IntPtr.Zero;
                    t.ClockOldX = t.ClockX; t.ClockOldY = t.ClockY; t.ClockOldW = t.ClockW; t.ClockOldH = t.ClockH;
                    t.ClockSlots = slots; t.ClockAnimStart = now;
                    t.ClockTimeTop = timeTop; t.ClockDigitY = digitY; t.ClockDigitH = digitH; t.ClockDateY = dateY; t.ClockDateH = dateH;
                }
                t.ClockTimeTop = timeTop; t.ClockDigitY = digitY; t.ClockDigitH = digitH;   // 滑動時拉長也要用（沒有換分鐘的動畫時也記著）
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
            double warp = ClockWarp(t, now);
            bool warpMoving = t.ClockWarpS.Moving(0, 0.05);   // 拉長還在彈回來：要再畫下一格
            double s = t.ClockScale;
            // 模糊的範圍：時鐘那張圖加上四周（邊緣往字裡面取樣最多 16；圖本身四周也留了 30）；要畫的範圍就是那張圖（四周已經留了影子的空間）
            int margin = (int)Math.Ceiling(56 * s);
            // 下面固定多留拉長的最大量（ClockWarp 最多 45% 字高）：範圍不跟著拉長變，不然每變一點就要重建模糊用的貼圖（會掉格）
            double rowH = t.ClockDigitY + 0.53 * t.ClockDigitH - t.ClockTimeTop;
            int reserve = t.ClockDigitH > 0 && rowH > 0 ? (int)Math.Ceiling(0.45 * rowH) : 0;
            // 換分鐘的動畫中：新舊兩張圖的聯集（12:59 → 1:00 新的比較窄，舊的字在新圖外面還要畫）
            int cx0 = t.ClockX, cy0 = t.ClockY, cx1 = t.ClockX + t.ClockW, cy1 = t.ClockY + t.ClockH + reserve;   // 拉長的字超出原本的圖
            if (animating)
            {
                cx0 = Math.Min(cx0, t.ClockOldX); cy0 = Math.Min(cy0, t.ClockOldY);
                cx1 = Math.Max(cx1, t.ClockOldX + t.ClockOldW); cy1 = Math.Max(cy1, t.ClockOldY + t.ClockOldH);
            }
            int bx, by, bw, bh, ex, ey, ew, eh;
            Clip(t, cx0 - margin, cy0 - margin, cx1 - cx0 + 2 * margin, cy1 - cy0 + 2 * margin, out bx, out by, out bw, out bh);
            Clip(t, cx0, cy0, cx1 - cx0, cy1 - cy0, out ex, out ey, out ew, out eh);
            if (bw <= 0 || bh <= 0 || ew <= 0 || eh <= 0) return animating || warpMoving;
            // 頁面在畫面裡滑的時候背景每一格都在變：每一格都重新模糊（四分之一大小，很便宜；不然字裡面約 30Hz 一頓一頓）
            bool sliding = t.Sheet != null && t.Sheet.Drawing;
            bool reblur = t.FreshVideo && (sliding || now - t.ClockBlurredAt >= 33) || bx != t.ClockBlurX || by != t.ClockBlurY || bw != t.ClockBlurW || bh != t.ClockBlurH;
            if (reblur) { t.ClockBlurredAt = now; t.ClockBlurX = bx; t.ClockBlurY = by; t.ClockBlurW = bw; t.ClockBlurH = bh; }
            clockLook.Scale = (float)s;
            float[] anim = animating ? clockAnim : null;
            if (warp > 0.01 && rowH > 0)   // 門檻很小（著色器自己有 1.0001 的門檻）：拉長是連續的，不會在 0 和半像素之間跳
            {
                if (!animating) Array.Clear(clockAnim, 0, clockAnim.Length);
                clockAnim[WarpAt] = t.ClockTimeTop; clockAnim[WarpAt + 1] = (float)((rowH + warp) / rowH);
                anim = clockAnim;
            }
            else if (animating) { clockAnim[WarpAt] = 0; clockAnim[WarpAt + 1] = 0; }
            t.ClockGlassOn = clockLiquid.DrawText(t.LiquidScene, t.Width, t.Height, t.LiquidRtv, t.ClockView, t.ClockX, t.ClockY, t.ClockW, t.ClockH,
                animating ? t.ClockOldView : IntPtr.Zero, anim, clockLook, bx, by, bw, bh, ex, ey, ew, eh, reblur, (float)ClockAlpha(t, now));
            return animating || warpMoving;
        }

        const int WarpAt = (2 + 48) * 4;   // cbuffer ClockAnim 的 Warp

        // 鎖定畫面滑進來、滑走時，時間那一行跟著慣性往下拉長多少像素（照 iPhone 從主畫面拉下鎖定畫面的錄影：
        // 數字的上緣不動，滑動中拉長、停下的瞬間下緣再往下衝約三成字高，約 0.15 秒彈回）。
        // 用卡片的慣性（CardMotion：頁面加速時落後、減速時衝過頭的彈簧）：往下衝過頭多少就拉長 2.8 倍那麼多，加上跟速度有關的拉長；最多 45% 字高
        // 拉長量被放大好幾倍（2.8 倍 D、4 倍拉長 × 字高），卡片上看不出來的細碎抖動在時鐘上會很明顯：
        // |D| 用平滑的絕對值（D 過零時不會折一下），再用臨界阻尼約 0.08 秒的彈簧把細碎的抖動吃掉（只影響拉長，不影響位置）
        double ClockWarp(Target t, long now)
        {
            var m = t.Motion;
            double rowH = t.ClockDigitY + 0.53 * t.ClockDigitH - t.ClockTimeTop, goal = 0;
            if (m != null && t.ClockDigitH > 0 && rowH > 0)
            {
                // 只算卡片往下衝的那一邊（D > 0：頁面往下停住時下緣往下衝）。原本用 |D|：往下急拉時開始的落後（D < 0）和停住時的衝過頭
                // 各拉長一次，時鐘會連續彈兩下。平滑的 max(0, D)：D 過零時不會折一下
                double eps = 3 * t.ClockScale;
                double ad = Math.Max(0, 0.5 * (Math.Sqrt(m.D * m.D + eps * eps) + m.D) - 0.5 * eps);
                goal = Math.Min(0.45 * rowH, 2.8 * ad + 4.0 * Math.Max(0, m.Stretch.X) * rowH);
            }
            // 很久沒畫（上次打開鎖定畫面時留下的拉長量）：直接從現在的目標開始，打開時時鐘不會多拉長一下
            if (t.ClockWarpAt == 0 || now - t.ClockWarpAt > 100) t.ClockWarpS.Jump(goal);
            double dt = t.ClockWarpAt == 0 ? 0 : Math.Min(0.05, (now - t.ClockWarpAt) / 1000.0);
            t.ClockWarpAt = now;
            return Math.Max(0, t.ClockWarpS.Step(goal, 0.08, 1.0, dt));
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
            int n = Math.Min(16, t.ClockSlots.Length / 8);   // cbuffer 只放得下 16 格
            double end = 0.8;
            for (int i = 0; i < n; i++) end = Math.Max(end, t.ClockSlots[i * 8 + 5] + (NewDelay + NewTime) * t.ClockSlots[i * 8 + 7] + 0.05);
            if (time > end) { FreeClockOld(t); return false; }
            var a = clockAnim;
            Array.Clear(a, 0, a.Length);
            a[0] = n; a[1] = t.ClockTimeTop; a[2] = t.ClockOldX; a[3] = t.ClockOldY;
            a[4] = t.ClockOldW; a[5] = t.ClockOldH;
            for (int i = 0; i < n; i++)
            {
                int o = i * 8;
                // 種類 10 以上是日期那一行的（同樣的三種動作，遮罩照那一行的高度）
                int kind = (int)t.ClockSlots[o];
                bool dateRow = kind >= 10;
                if (dateRow) { kind -= 10; a[6] = 1; } else a[7] = 1;
                double h = dateRow ? t.ClockDateH : t.ClockDigitH, mid = dateRow ? t.ClockDateY : t.ClockDigitY;
                double top = mid - 0.53 * h, bottom = mid + 0.53 * h, span = bottom - top, w = 0.5 * h;
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
                a[k] = t.ClockSlots[o + 1]; a[k + 1] = t.ClockSlots[o + 2]; a[k + 2] = kind == 2 ? 1 : 0; a[k + 3] = dateRow ? 1 : 0;
                a[k + 4] = t.ClockSlots[o + 3]; a[k + 5] = (float)mid; a[k + 6] = (float)edge; a[k + 7] = (float)w;
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
            // 按住：很快長大、稍微衝過頭（約 8%）；放開：慢一點縮回、稍微縮過頭（約 16%）再彈回來。停住時剛好是 0 或 1（像素才對齊）
            double scrubGoal = c.Scrubbing ? 1 : 0, scrubbed = c.Scrub.Step(scrubGoal, c.Scrubbing ? 0.26 : 0.42, c.Scrubbing ? 0.62 : 0.5, dt);
            c.ScrubAmount = c.Scrub.Moving(scrubGoal, 0.002) ? scrubbed : scrubGoal;


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
            // 長上去：輕點（沒蓄力）稍微衝過頭約 3%（1080p 約 6 像素）再輕輕回來，點綴一下就好；蓄滿的衝過頭約 38 像素再慢慢彈回。
            // 縮回來幾乎不過衝，乾淨地停在定位。
            // 往下（蓄力壓下、縮回時回彈）最多比原本的卡片矮約 8：上方的內距 16，封面、歌名跟玻璃上緣至少還留 8。
            // 越往下越難壓（g / (g + 8)，一路慢慢變硬），不是撞到一條線硬停
            c.ExtendGoal = c.Extend >= c.ExtendGoal ? c.Extend : c.Extend + (c.ExtendGoal - c.Extend) * Math.Exp(-dt / 0.06);
            if (Math.Abs(c.ExtendGoal - c.Extend) < 0.05) c.ExtendGoal = c.Extend;
            double growTarget = c.ExtendGoal - 10 * s * (c.LaunchPending ? 1 - Math.Pow(1 - Math.Min(1, (c.LaunchAt - c.ChargeStart) / 600.0), 2) : charge);
            // 縮回（歌詞收起來、沒有按著蓄力）用跟彈上去同一顆彈簧（一樣的快慢），只是阻尼大一點（蓄滿時 0.65，彈上去是 0.48）：
            // 一樣快快地落下、接近底時順順地慢下來，圓滑地衝過頭約 5 像素再回來（像彈上去的頂點倒過來）。
            // 不能照彈上去的阻尼：會往下衝過頭約 20 像素，上緣最多只能壓 8（封面、歌名要留距離），只好硬壓扁，看起來像卡住
            bool down = c.Extend <= 0 && !c.Charging;
            double grown = c.Grow.Step(growTarget, 0.45 + 0.17 * power, down ? 0.85 - 0.2 * power : 0.75 - 0.27 * power, dt), sink = 8 * s, g = Math.Max(0, -grown);
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
                Appear = (float)CardAlpha(t, now), Thick = (float)Math.Max(0.6, Math.Min(1.5, Math.Min(spec.W, spec.H) / 2 / (cardLook.Bevel * s))),
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
            bool reblur = t.FreshVideo && (t.Sheet != null && t.Sheet.Drawing || now - t.LiquidBlurredAt >= 33) ||   // 頁面在滑：每一格都重新模糊（同時鐘）
 bx != t.LiquidBlurX || by != t.LiquidBlurY || bw != t.LiquidBlurW || bh != t.LiquidBlurH;
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
            double cardAlpha = CardAlpha(t, AnimationClock.Now);   // 鎖定畫面滑下來時卡片先藏起來（見 CardAlpha）
            // 進度條：高 6（按住時 10，兩端也各往外長 ProgressGrow，兩邊的時間同時讓開）、軌道白 29%（按住 35%；播放器不接受跳轉、不能拖時 20%）、
            // 已播放白 86%（按住 100%）；不知道歌曲長度就不畫。ScrubAmount 衝過頭時大小跟著彈，亮度不用
            if (c.Progress >= 0)
            {
                double sc = c.ScrubAmount, lit = Math.Max(0, Math.Min(1, sc)), ph = (6 + 4 * sc) * s, grow = spec.ProgressGrow * sc;
                double px = spec.ProgressX0 - grow, plen = spec.ProgressX1 - spec.ProgressX0 + 2 * grow;
                canvas.FillSolid(px, spec.ProgressY - ph / 2, plen, ph, ph / 2, 1, 1, 1, (c.CanSeek ? 0.29 + 0.06 * lit : 0.2) * cardAlpha);
                double played = plen * c.Progress;
                if (played > 0.5) canvas.FillSolid(px, spec.ProgressY - ph / 2, played, ph, ph / 2, 1, 1, 1, (0.86 + 0.14 * lit) * cardAlpha, px, plen);
            }
            // 歌詞圓鈕的底：關著淡白 16.5%、開著白（236，淺色玻璃也不會被當成白字翻黑）；按下時跟玻璃一起放大
            double r = LyricsRadius(c), on = c.OnAmount, la = c.LyricsAlpha;
            double gray = 1 + (236 / 255.0 - 1) * on, alpha = (0.165 * la) * (1 - on) + 0.92 * on;
            canvas.FillSolid(spec.LyricsX - r, spec.LyricsY - r, r * 2, r * 2, r, gray, gray, gray + (240 - 236) / 255.0 * on, alpha * cardAlpha);
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
                Appear = (float)CardAlpha(t, AnimationClock.Now), Thick = 0.22f, Mag = 1, Energy = (float)Math.Min(1, c.Pressed), D00 = 1, D11 = 1,
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
            FreeSheet(t);
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
