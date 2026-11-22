using System;

namespace VideoWallpaper
{
    // 鎖定畫面放大歌詞（像 Apple Music 的全螢幕歌詞）：音樂卡片連同歌詞整塊移到畫面正中間放大，背景糊掉、暗一點，時鐘淡出。
    // 卡片和歌詞的圖由 UI 照放大後的大小重畫（字才清楚），這裡負責：
    //   換大小的那一下玻璃從原本的範圍有彈性地長成新的範圍，卡片上的東西跟著移動、淡入淡出（MorphCard）；
    //   背景的模糊和變暗（SetBackdrop）；換圖的那幾個指令還沒送完前先別畫（Freeze），不會有一格是新舊混在一起的
    partial class VideoEngine
    {
        LiquidGlass backdropLiquid;   // 背景模糊專用（模糊圖跟畫面一樣大，不跟卡片、時鐘共用，不然每一格都要重建）
        bool backdropTried;

        // 先別畫這個畫面（on = false：恢復，下一次螢幕更新就畫）
        public void Freeze(IntPtr hwnd, bool on)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                t.Frozen = on;
                if (!on) redrawAll = true;
            });
        }

        // 放大歌詞的版面（stage = true）：歌詞不在卡片的玻璃裡（不照玻璃裁切），卡片（底部的膠囊）和上面的控制可以整個淡出（見 SetStageControls）
        public void SetStage(IntPtr hwnd, bool stage)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                t.Stage = stage;
                t.StageFrom = t.StageTo = 1; t.StageMs = 0;
            });
        }

        // 放大歌詞時的播放控制（底部的膠囊、右上角的縮回鈕）：花 ms 毫秒淡入 / 淡出
        public void SetStageControls(IntPtr hwnd, bool show, int ms)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                t.StageFrom = StageAlpha(t, now);
                t.StageTo = show ? 1 : 0;
                t.StageStart = now; t.StageMs = Math.Max(1, ms);
            });
        }

        // 放大歌詞時播放控制現在看得到多少（不是放大歌詞就是 1）
        static double StageAlpha(Target t, long now)
        {
            if (!t.Stage) return 1;
            if (t.StageMs <= 0) return t.StageTo;
            double u = Math.Max(0, Math.Min(1, (now - t.StageStart) / (double)t.StageMs));
            return t.StageFrom + (t.StageTo - t.StageFrom) * u * u * (3 - 2 * u);
        }

        static bool StageMoving(Target t, long now) { return t.Stage && t.StageMs > 0 && now - t.StageStart < t.StageMs; }

        // 馬上拿掉一張疊圖（不淡出）
        public void RemoveOverlay(IntPtr hwnd, string name)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                Overlay o;
                if (!t.TryGet(name, out o)) return;
                t.Remove(name);
                o.Release();
            });
        }

        // 卡片換大小（放大 / 縮回歌詞）：玻璃整塊（連往上延伸的歌詞區）花 ms 毫秒有彈性地從 a 長成 b（畫面上的範圍；兩個的寬高比不一樣也可以），
        // 圓角從 ra 跟著變到 rb。卡片上的東西：一開始很快淡掉（玻璃空著變形），UI 趁這時照新的大小重畫，
        // 畫好呼叫 MorphSwap，等玻璃停好再淡入（玻璃在變的時候裡面一直是空的）
        public const int MorphFadeOut = 2, MorphSwapped = 3;

        // ga、gb：玻璃在起點、終點看得到多少（放大歌詞的版面沒有玻璃：放大時玻璃一邊變形一邊淡掉，縮回時從淡掉的地方長回來）
        public void MorphCard(IntPtr hwnd, double ax, double ay, double aw, double ah, double bx, double by, double bw, double bh, double ra, double rb, double ga, double gb, int ms)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                t.MorphA = new[] { ax, ay, aw, ah, ra, ga };
                t.MorphB = new[] { bx, by, bw, bh, rb, gb };
                t.MorphStart = now; t.MorphMs = Math.Max(1, ms); t.MorphMode = MorphFadeOut;
            });
        }

        // 新的圖放上去了：從變形開始算 after 毫秒（玻璃停好）起花 fadeMs 淡入
        public void MorphSwap(IntPtr hwnd, int after, int fadeMs)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                if (t.MorphMs <= 0) { t.MorphStart = now; t.MorphMs = 1; t.MorphA = t.MorphB = null; }   // 沒在變形（保險）：直接淡入
                t.MorphMode = MorphSwapped;
                t.MorphFadeAt = Math.Max(now, t.MorphStart + after);
                t.MorphFadeMs = Math.Max(1, fadeMs);
            });
        }

        // 保險：淡出了、UI 卻沒來換圖（中途被收掉、換了引擎）超過 2 秒：當作結束，照卡片本身畫，不會一直空著、一直重畫
        static void ExpireMorph(Target t, long now)
        {
            if (t.MorphMode == MorphFadeOut && now - t.MorphStart > t.MorphMs + 2000) { t.MorphMs = 0; t.MorphMode = 0; }
        }

        // 這時玻璃的範圍（左上、寬高）和圓角；沒在變（照卡片本身畫）回傳 false
        static bool MorphRect(Target t, long now, out double x, out double y, out double w, out double h, out double r)
        {
            x = y = w = h = r = 0;
            if (t.MorphMs <= 0 || t.MorphA == null) return false;
            double u = (now - t.MorphStart) / (double)t.MorphMs;
            if (u >= 1 && t.MorphMode != MorphFadeOut) return false;   // 停好、新的圖也放上去了：照卡片本身（範圍跟 b 一樣）
            double e = u >= 1 ? 1 : Elastic(Math.Max(0, u) * t.MorphMs / 1000.0);
            double[] a = t.MorphA, b = t.MorphB;
            x = a[0] + (b[0] - a[0]) * e; y = a[1] + (b[1] - a[1]) * e;
            w = Math.Max(1, a[2] + (b[2] - a[2]) * e); h = Math.Max(1, a[3] + (b[3] - a[3]) * e);
            r = Math.Max(0, Math.Min(Math.Min(w, h) / 2, a[4] + (b[4] - a[4]) * Math.Max(0, Math.Min(1, e))));
            return true;
        }

        static bool MorphRect(Target t, long now, out double x, out double y, out double w, out double h)
        {
            double r;
            return MorphRect(t, now, out x, out y, out w, out h, out r);
        }

        // 卡片的玻璃這時看得到多少：變形中照起點、終點的設定慢慢變，放大歌詞的版面（沒在變形）沒有玻璃
        static double GlassAlpha(Target t, long now)
        {
            if (t.MorphMs > 0 && t.MorphA != null && t.MorphA.Length > 5)
            {
                double u = (now - t.MorphStart) / (double)t.MorphMs;
                if (u < 1 || t.MorphMode == MorphFadeOut)
                {
                    // 淡掉（放大）：玻璃先撐到整個畫面（撞到邊緣，約變形的 45%）才開始淡；浮現（縮回）：一開始就出來
                    double k = t.MorphB[5] < t.MorphA[5] ? SheetSmooth(0.45, 0.85, u) : SheetSmooth(0, 0.35, Math.Max(0, u));
                    return t.MorphA[5] + (t.MorphB[5] - t.MorphA[5]) * k;
                }
            }
            return t.Stage ? 0 : 1;
        }

        // 卡片上的東西（圖、歌詞、進度條、歌詞圓鈕）這時看得到多少
        static double MorphAlpha(Target t, long now)
        {
            if (t.MorphMs <= 0 || t.MorphMode == 0) return 1;
            if (t.MorphMode == MorphSwapped) return now <= t.MorphFadeAt ? 0 : SheetSmooth(0, 1, (now - t.MorphFadeAt) / (double)t.MorphFadeMs);
            double u = Math.Max(0, Math.Min(1, (now - t.MorphStart) / (double)t.MorphMs));
            return 1 - SheetSmooth(0, 0.15, u);   // 一開始就很快淡掉，玻璃空著變形
        }

        // 有彈性的移動（彈簧從 0 走到 1，response 0.42 秒、阻尼比 0.7：衝過頭約 5% 再回來，約 0.7 秒停好）；t 是秒
        static double Elastic(double t)
        {
            const double zeta = 0.7;
            double w = 2 * Math.PI / 0.42, wd = w * Math.Sqrt(1 - zeta * zeta);
            return 1 - Math.Exp(-zeta * w * t) * (Math.Cos(wd * t) + zeta * w / wd * Math.Sin(wd * t));
        }

        // 玻璃正在變（動畫中、或停在終點等換圖）
        static bool Zooming(Target t, long now)
        {
            return t.MorphMs > 0 && (now - t.MorphStart < t.MorphMs || t.MorphMode == MorphFadeOut
                || t.MorphMode == MorphSwapped && now < t.MorphFadeAt + t.MorphFadeMs);
        }

        // 背景糊掉、暗一點的程度花 ms 毫秒變到 to（0 = 原樣、1 = 放大歌詞）
        public void SetBackdrop(IntPtr hwnd, double to, int ms)
        {
            WithTarget(hwnd, delegate(Target t, long now)
            {
                t.BackdropFrom = BackdropAt(t, now);
                t.BackdropTo = to;
                t.BackdropStart = now;
                t.BackdropMs = Math.Max(1, ms);
                redrawAll = true;
            });
        }

        static double BackdropAt(Target t, long now)
        {
            if (t.BackdropMs <= 0) return t.BackdropTo;
            double u = Math.Max(0, Math.Min(1, (now - t.BackdropStart) / (double)t.BackdropMs));
            return t.BackdropFrom + (t.BackdropTo - t.BackdropFrom) * u * u * (3 - 2 * u);
        }

        // 影片剛蓋上輸出畫面之後：要糊的話把糊好、暗好的背景畫進 BackdropTexture、蓋到輸出畫面上，
        // 這一格卡片的玻璃改折射它（跟 SheetBegin 一樣換掉 LiquidScene，BackdropEnd 換回來）。頁面在滑的時候不糊
        void BackdropBegin(Target t, long now)
        {
            double a = BackdropAt(t, now);
            bool moving = t.BackdropMs > 0 && now - t.BackdropStart < t.BackdropMs;
            if (moving) redraw = true;
            if (a <= 0.001 && !moving && t.BackdropTexture != IntPtr.Zero)
            {
                // 背景已經變回清楚了：畫好的背景還回去（下次放大再建）
                LiquidGlass.Free(ref t.BackdropRtv); LiquidGlass.Free(ref t.BackdropView); LiquidGlass.Free(ref t.BackdropTexture);
                t.BackdropBlurred = false;
            }
            if (a <= 0.001 || t.Sheet != null || !LiquidViews(t)) return;
            if (backdropLiquid == null && !backdropTried)
            {
                backdropTried = true;
                backdropLiquid = LiquidGlass.Create(device, context, featureLevel);
            }
            if (backdropLiquid == null || !backdropLiquid.BackdropSupported) return;
            if (t.BackdropTexture == IntPtr.Zero && !backdropLiquid.CreateCanvas(t.Width, t.Height, null, true, out t.BackdropTexture, out t.BackdropView, out t.BackdropRtv)) return;
            bool reblur = t.FreshVideo || !t.BackdropBlurred;
            t.BackdropBlurred = true;
            // 一點點封面的顏色（卡片的色調就是照封面算的）
            var spec = t.Liquid != null ? t.Liquid.Spec : null;
            int rgb = 0; float tintAmount = 0;
            if (spec != null)
            {
                Func<double, int> b = v => (int)Math.Max(0, Math.Min(255, Math.Round(v * 255)));
                rgb = b(spec.TintR) * 65536 + b(spec.TintG) * 256 + b(spec.TintB);
                tintAmount = 0.15f;
            }
            if (!backdropLiquid.DrawBackdrop(t.LiquidScene, t.Width, t.Height, t.BackdropRtv, (float)a, 0.6f, (float)(t.Height / 1080.0), reblur, tintAmount, rgb)) return;
            copyResource(context, t.BackBuffer, t.BackdropTexture);
            t.BackdropScene = t.LiquidScene;
            t.LiquidScene = t.BackdropView;
            if (moving) t.FreshVideo = true;   // 背景還在變：玻璃要重新模糊
        }

        void BackdropEnd(Target t)
        {
            if (t.BackdropScene == IntPtr.Zero) return;
            t.LiquidScene = t.BackdropScene;
            t.BackdropScene = IntPtr.Zero;
        }

        static void FreeBackdrop(Target t)
        {
            if (t.BackdropScene != IntPtr.Zero) { t.LiquidScene = t.BackdropScene; t.BackdropScene = IntPtr.Zero; }
            LiquidGlass.Free(ref t.BackdropRtv); LiquidGlass.Free(ref t.BackdropView); LiquidGlass.Free(ref t.BackdropTexture);
            t.BackdropBlurred = false;
            t.BackdropFrom = t.BackdropTo = 0; t.BackdropMs = 0;
            t.MorphMs = 0; t.MorphMode = 0;
            t.Frozen = false;
        }
    }
}
