using System;

namespace VideoWallpaper
{
    // 動畫曲線（鎖定畫面滑動、播放引擎的疊圖動畫共用）
    static class Easing
    {
        // Material Design / ChromeOS 的標準動畫曲線 cubic-bezier(0.4, 0, 0.2, 1)：開頭和結尾都柔和
        public static double StandardCurve(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            double s = t;   // 用牛頓法找出貝茲曲線上 x = t 的參數
            for (int i = 0; i < 8; i++)
            {
                double x = Bezier(s, 0.4, 0.2) - t;
                double dx = BezierSlope(s, 0.4, 0.2);
                if (Math.Abs(x) < 1e-6 || Math.Abs(dx) < 1e-6) break;
                s = Math.Max(0, Math.Min(1, s - x / dx));
            }
            return Bezier(s, 0.0, 1.0);
        }

        static double Bezier(double s, double p1, double p2)
        {
            double u = 1 - s;
            return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
        }

        static double BezierSlope(double s, double p1, double p2)
        {
            double u = 1 - s;
            return 3 * u * u * p1 + 6 * u * s * (p2 - p1) + 3 * s * s * (1 - p2);
        }
    }
}
