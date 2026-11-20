using System;

namespace VideoWallpaper
{
    // 鎖定畫面的頁面位置（0 = 蓋住畫面、1 = 在畫面上方）隨時間怎麼變（時間是 AnimationClock 的毫秒）。
    // UI（移動其他螢幕的視窗、知道什麼時候滑完）和播放引擎（頁面在畫面裡滑、卡片的慣性）照同一條算，兩邊才會對得上。
    // 建好就不會變（拖曳中每次移動都換一條新的），可以直接交給別的執行緒
    sealed class PagePath
    {
        const int CurveKind = 0, HoldKind = 1, SpringKind = 2;
        readonly int kind;
        public readonly double From, To;   // 從哪到哪（停住的時候兩個一樣）
        public readonly long Start;        // 開始的時間
        public readonly int Ms;            // 曲線：滑多久
        readonly double v0;                // 停住：放開前的速度；彈簧：一開始的速度（每秒幾個畫面高）

        // 放開後的彈簧：臨界阻尼（不來回晃），response 0.32 秒（約 0.25 秒走完大半、0.5 秒停好）
        const double SpringW = 2 * Math.PI / 0.32;

        PagePath(int kind, double from, double to, long start, int ms, double v0)
        {
            this.kind = kind; From = from; To = to; Start = start; Ms = ms; this.v0 = v0;
        }

        // 照 Material 的標準曲線從 from 滑到 to（開頭、結尾都柔和）
        public static PagePath Curve(double from, double to, long start, int ms) { return new PagePath(CurveKind, from, to, start, Math.Max(1, ms), 0); }

        // 拖曳中：從 from（上一次畫到的位置）在 ms 毫秒內等速走到 at（這次滑鼠的位置），然後停在 at。
        // 滑鼠事件跟畫面更新不同步（有的格子沒有新事件、有的有兩個），直接跳到滑鼠的位置每格移動的量會一大一小；
        // 用大約一個事件間隔的時間等速補過去，每一格照當時的時間取位置，就是平順的（只晚一個事件間隔，約 2～12 毫秒）。
        // velocity：這時滑鼠的速度（每秒幾個畫面高），滑鼠停下來很快就退掉
        public static PagePath Hold(double from, double at, double velocity, long time, int ms) { return new PagePath(HoldKind, from, at, time, Math.Max(0, ms), velocity); }

        // 放開：從 from 帶著 velocity 的速度彈到 to（0 或 1）。衝過頭的部分截掉（直接停在 to）
        public static PagePath Spring(double from, double velocity, double to, long start) { return new PagePath(SpringKind, from, to, start, 0, velocity); }

        public bool IsCurve { get { return kind == CurveKind; } }
        public bool IsHold { get { return kind == HoldKind; } }

        // 在 time 時的位置
        public double At(double time)
        {
            if (kind == HoldKind) return Ms <= 0 || time >= Start + Ms ? To : From + (To - From) * Math.Max(0, (time - Start) / Ms);
            if (kind == CurveKind) return From + (To - From) * Easing.StandardCurve(Progress(time));
            double x, v;
            SpringAt(time, out x, out v);
            return x;
        }

        // 在 time 時的速度（每秒幾個畫面高）
        public double Velocity(double time)
        {
            if (kind == HoldKind) return v0 * Math.Exp(-Math.Max(0, time - Start) / 60.0);
            if (kind == CurveKind)
            {
                const double h = 4;
                return (At(time + h) - At(time - h)) / (2 * h) * 1000;
            }
            double x, v;
            SpringAt(time, out x, out v);
            return v;
        }

        // 走了多少（0～1）：曲線是時間的比例，彈簧是距離的比例，停住是 0
        public double Progress(double time)
        {
            if (kind == CurveKind) return Math.Max(0, Math.Min(1, (time - Start) / Ms));
            if (kind == HoldKind || Math.Abs(To - From) < 1e-6) return kind == HoldKind ? 0 : 1;
            return Math.Max(0, Math.Min(1, (At(time) - From) / (To - From)));
        }

        // 停好了沒（拖曳中的那種補到滑鼠的位置就算停好）
        public bool Done(double time)
        {
            if (kind == HoldKind) return time >= Start + Ms;
            if (kind == CurveKind) return time >= Start + Ms;
            double x, v;
            SpringAt(time, out x, out v);
            return x == To || (Math.Abs(x - To) < 0.0004 && Math.Abs(v) < 0.02) || time - Start > 1500;
        }

        // 臨界阻尼的彈簧：x(t) = to + (c1 + c2·t)·e^(−wt)，衝過頭（超出 0～1、或越過 to）就停在 to
        void SpringAt(double time, out double x, out double v)
        {
            double t = (time - Start) / 1000.0, w = SpringW;
            if (t < 0)   // 放開之前：照放開時的速度直線往回推（卡片的慣性用二階差分算加速度，放開那一刻才不會多出一下假的推力）
            {
                x = Math.Max(0, Math.Min(1, From + v0 * t)); v = v0;
                return;
            }
            double c1 = From - To, c2 = v0 + w * c1, e = Math.Exp(-w * t);
            x = To + (c1 + c2 * t) * e;
            v = (c2 - w * (c1 + c2 * t)) * e;
            // 越過 to 的那一刻（c1 + c2·t 變號）之後都停在 to
            if (c1 != 0 && c2 != 0 && Math.Sign(c2) != Math.Sign(c1) && t >= -c1 / c2) { x = To; v = 0; }
            if (x < 0 || x > 1) { x = Math.Max(0, Math.Min(1, x)); v = 0; }
        }
    }
}
