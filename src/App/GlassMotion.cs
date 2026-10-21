using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VideoWallpaper
{
    // 控制面板玻璃的物理（照網頁原型和 iOS 26 的實測：Codename One 逐格量的分頁列、LiquidGlassKit 量的開關尺寸）。
    // 單位跟面板一樣，時間是秒

    // SwiftUI 式的彈簧：response = 大約多久到位、zeta = 阻尼比（1 = 不回彈；0.58 衝過頭約 11%）；240Hz 細分才穩
    class Spring
    {
        public double X, V;
        bool started;

        public double Step(double target, double response, double zeta, double dt)
        {
            if (!started) { X = target; V = 0; started = true; return X; }   // 第一次用：直接從目標值開始
            double w = 2 * Math.PI / response, k = w * w, c = 2 * zeta * w;
            int n = Math.Max(1, (int)Math.Ceiling(dt * 240));
            double h = dt / n;
            for (int i = 0; i < n; i++) { V += (k * (target - X) - c * V) * h; X += V * h; }
            if (Math.Abs(target - X) < 1e-4 && Math.Abs(V) < 1e-3) { X = target; V = 0; }   // 到位：停在終點，畫面才不會一直重畫
            return X;
        }

        public void Jump(double value) { X = value; V = 0; started = true; }
    }

    static class Motion
    {
        public static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        // 拖到底的橡皮筋：超出盡頭的距離用 tanh 收斂（越拉越拉不動），放開用按鈕的回彈彈簧彈回
        public static double Rubber(Spring s, double excess, double size, bool holding, double dt)
        {
            double m = size * 0.6, target = holding ? m * Math.Tanh(0.5 * excess / m) : 0;
            return s.Step(target, holding ? 0.1 : 0.54, holding ? 0.95 : 0.58, dt);
        }

        // 移到某個位置：重複用同一個 TranslateTransform、數值變了才改（每一格都換新的話，WPF 會以為畫面變了，整個面板一直重畫）
        public static void MoveTo(UIElement e, double x, double y)
        {
            var t = e.RenderTransform as TranslateTransform;
            if (t == null || t.IsFrozen) e.RenderTransform = t = new TranslateTransform();
            if (t.X != x) t.X = x;
            if (t.Y != y) t.Y = y;
        }

        // 元素中心在 content 上的位置（transform 的話：含不含它自己的 RenderTransform）
        public static Point Center(FrameworkElement e, Visual content, bool own)
        {
            if (own) return e.TransformToAncestor(content).Transform(new Point(e.ActualWidth / 2, e.ActualHeight / 2));
            var parent = VisualTreeHelper.GetParent(e) as Visual;
            var offset = VisualTreeHelper.GetOffset(e);
            var p = new Point(offset.X + e.ActualWidth / 2, offset.Y + e.ActualHeight / 2);
            return parent == null || parent == content ? p : parent.TransformToAncestor(content).Transform(p);
        }
    }

    // 第一層的玻璃：整塊面板（固定不動、不能按）。量出它在版面上的位置、大小、圓角，交給著色器
    class GlassPiece
    {
        public readonly FrameworkElement Element;
        double tx, ty, tw, th, tr;   // 版面上的中心、半寬半高、圓角

        public GlassPiece(FrameworkElement element) { Element = element; }

        public bool Layout(Visual root)
        {
            if (!Element.IsVisible || Element.ActualWidth <= 0 || Element.ActualHeight <= 0) return false;
            var c = Motion.Center(Element, root, false);
            tx = c.X; ty = c.Y; tw = Element.ActualWidth / 2; th = Element.ActualHeight / 2;
            var border = Element as Border;
            tr = border != null && border.Tag as string != "Pill" ? border.CornerRadius.TopLeft : th;
            tr = Math.Min(tr, Math.Min(tw, th));
            return true;
        }

        // 出現：上面的字淡入，玻璃的折射量慢慢增加（官方：不縮放）
        public void Apply(GlassShape s, double appear)
        {
            if (Element.Opacity != appear) Element.Opacity = appear;
            if (s == null) return;
            const double thick = 0.7;   // 面板固定 0.7（折射帶寬約 50 × 0.7 = 35，見 GlassLook.Base）
            s.X = tx; s.Y = ty; s.HalfW = tw; s.HalfH = th; s.Radius = tr;
            s.Appear = appear; s.Energy = 0; s.Thick = thick;
            s.TintR = s.TintG = s.TintB = s.TintA = 0;
            s.FillA = 0;
            s.D00 = 1; s.D01 = 0; s.D11 = 1;
            s.Mag = 1.12; s.Shadow = 0.2 * (0.7 + 0.3 * thick);
            s.PullX = s.PullY = s.PullAmount = s.PullRadius = 0;
        }
    }

    // 移動中拉長一點、到站壓扁（照 iPhone 實機逐格量的：拖曳中寬 +4～5%、高 −8%；到站那一下寬最少約 0.8 倍、高約 1.2 倍）。
    // 速度先過一顆會晃的彈簧：停下來時它衝過頭變成負的，就是到站的那一下壓扁
    class GlassStretch
    {
        readonly Spring speed = new Spring();
        public double X = 1, Y = 1;

        public void Step(double velocity, double dt)
        {
            double v = speed.Step(Math.Abs(velocity), 0.3, 0.4, dt);   // 阻尼小一點：停下來時衝過頭多，到站壓扁才明顯
            double t = Math.Tanh(v / 300);
            if (t >= 0) { X = 1 + 0.05 * t; Y = 1 - 0.08 * t; }
            else { X = 1 + 0.25 * t; Y = 1 - 0.25 * t; }   // 到站：橫向壓扁、上下鼓起
        }
    }

    // 開關（iOS 26 的寬開關：軌道 63×28、旋鈕 37×24 的白色膠囊，內縮 2）。
    // 按住：白色淡出、變成 58×38 的透明透鏡（看得到底下軌道的顏色），可以拖，兩端有橡皮筋；
    // 點一下：先膨脹成透鏡、同時滑過去（沒有延遲），到終點才縮回白色；軌道的綠色跟著旋鈕的位置淡入
    class GlassSwitch
    {
        public const double Travel = 22;
        public readonly CheckBox Box;
        public FrameworkElement Track, Knob, KnobSolid, KnobLens, TrackOn;
        readonly Spring pos = new Spring(), lift = new Spring(), solid = new Spring(), rub = new Spring();
        readonly GlassStretch stretch = new GlassStretch();
        public bool Dragging, Tapping;
        public double StartPos, Raw;
        public double X, Y, W, H, Solid;

        public GlassSwitch(CheckBox box) { Box = box; }

        public double Position { get { return pos.X; } }

        public bool Find()
        {
            if (Track != null) return true;
            Box.ApplyTemplate();
            var t = Box.Template;
            if (t == null) return false;
            // 模板裡的元素都要找到才算（少一個就當作不是開關，不動它）
            var track = t.FindName("Track", Box) as FrameworkElement;
            var knob = t.FindName("Knob", Box) as FrameworkElement;
            var solid = t.FindName("KnobSolid", Box) as Border;
            var lens = t.FindName("KnobLens", Box) as Border;
            var on = t.FindName("TrackOn", Box) as FrameworkElement;
            if (track == null || knob == null || solid == null || lens == null || on == null) return false;
            Knob = knob; KnobSolid = solid; KnobLens = lens; TrackOn = on;
            Track = track;
            return true;
        }

        public bool Step(Visual root, double now, double dt)
        {
            if (!Find() || !Box.IsVisible || Track.ActualWidth <= 0) return false;
            bool on = Box.IsChecked == true;
            double raw = Motion.Clamp01(Raw), target = Dragging ? raw : on ? 1 : 0;
            // 拖的時候追手指（0.2 秒、幾乎不回彈）；放開 / 點一下用 0.40 / 0.85 滑過去，從現在的位置和速度接著走
            pos.Step(target, Dragging ? 0.196 : 0.40, Dragging ? 0.90 : 0.85, dt);
            if (Tapping && Math.Abs(target - pos.X) * Travel <= 3.5) Tapping = false;   // 快到了：開始縮回白色
            bool up = Dragging || Tapping;
            lift.Step(up ? 1 : 0, 0.25, 1, dt);
            solid.Step(up ? 0 : 1, up ? 0.25 : 0.40, 1, dt);   // 白色：變透鏡時很快淡掉，回來慢一點
            double kl = Motion.Clamp01(lift.X);
            double r = Motion.Rubber(rub, Dragging ? (Raw - raw) * Travel : 0, 10, Dragging, dt);   // 拖過頭最多約 5
            stretch.Step(pos.V * Travel, dt);
            var c = Motion.Center(Track, root, true);
            X = c.X - Travel / 2 + Travel * pos.X + 0.8 * r; Y = c.Y;
            W = (18.5 + 10.5 * kl + 0.2 * Math.Abs(r)) * stretch.X;   // 透鏡寬 58
            H = (12 + 7.2 * kl) * stretch.Y * (1 - 0.06 * Math.Abs(r) / 10);   // 透鏡高 38：軌道的 1.37 倍
            Solid = Motion.Clamp01(solid.X);
            if (TrackOn != null)
            {
                double green = Motion.Clamp01(pos.X);
                if (TrackOn.Opacity != green) TrackOn.Opacity = green;
            }
            return true;
        }

        // 平常的白色實心旋鈕由 WPF 畫（同樣的位置、大小）；變成透鏡時它淡掉，換第二層玻璃的透鏡出現。
        // 沒有液態玻璃時透鏡也由 WPF 畫（半透明的樣子）
        public void Place(Visual root, bool glass)
        {
            var origin = Track.TransformToAncestor(root).Transform(new Point(0, 0));
            Knob.Width = W * 2; Knob.Height = H * 2;
            ((Border)KnobSolid).CornerRadius = ((Border)KnobLens).CornerRadius = new CornerRadius(H);
            Motion.MoveTo(Knob, X - W - origin.X, Y - H - origin.Y);
            if (KnobSolid.Opacity != Solid) KnobSolid.Opacity = Solid;
            double lens = glass ? 0 : 1 - Solid;
            if (KnobLens.Opacity != lens) KnobLens.Opacity = lens;
        }

        public bool Lifted { get { return Solid < 0.999; } }

        // 第二層玻璃只有透鏡（實心的部分 WPF 畫）
        public void Shape(GlassShape s, double appear)
        {
            double thick = Math.Max(0.6, Math.Min(1.5, Math.Min(W, H) / 40));
            s.X = X; s.Y = Y; s.HalfW = W; s.HalfH = H; s.Radius = H;
            s.Appear = (1 - Solid) * appear; s.Energy = 0; s.Thick = thick;
            s.TintA = 0; s.FillA = 0;
            s.D00 = 1; s.D01 = 0; s.D11 = 1;
            s.Mag = 1.15; s.Shadow = 0;   // 中間放大一點（跟分段的透鏡同一套光學）；透明的透鏡沒有影子（實機）
            s.PullAmount = 0;
        }
    }

    // 分段控制的選取（照 iOS 26 的實測）：平常是畫在字下面的實心膠囊。
    // 按住選取的那一格：抬起成透明透鏡（寬多 20、高多 8，裡面的字放大 1.2 倍），跟著手指（0.2 秒的彈簧，會落後一點），
    // 只在第一格到最後一格之間；放開選離手指最近的那一格，從透鏡現在的位置和速度滑過去（0.40 / 0.85）。
    // 點別格：抬起和移動同時開始、沒有延遲；移動中拉長、到站壓扁；離目標不到 3.5 就開始落下，實心慢慢回來
    class GlassDrop
    {
        public readonly FrameworkElement Host;
        public readonly RadioButton[] Items;
        public FrameworkElement Visual;   // 實心的那塊（WPF 畫在字的下面）
        public FrameworkElement Solid;
        readonly Spring pos = new Spring(), lift = new Spring(), solid = new Spring(), rub = new Spring();
        readonly GlassStretch stretch = new GlassStretch();
        public bool Dragging, Tapping;
        public double DragRaw, DragX;
        public double X, Y, W, H, SolidAmount, Mag, Lift;
        public int Selected;

        public GlassDrop(FrameworkElement host, RadioButton[] items) { Host = host; Items = items; }

        public double CenterOf(Visual root, int i) { return Motion.Center(Items[i], root, true).X; }
        public double Half { get { return Items[0].ActualWidth / 2; } }

        public int Nearest(Visual root, double x)
        {
            int best = 0;
            for (int i = 1; i < Items.Length; i++) if (Math.Abs(CenterOf(root, i) - x) < Math.Abs(CenterOf(root, best) - x)) best = i;
            return best;
        }

        public bool Step(Visual root, double now, double dt)
        {
            if (!Host.IsVisible || Host.ActualWidth <= 0) return false;
            for (int i = 0; i < Items.Length; i++) if (Items[i].IsChecked == true) Selected = i;
            double half = Half, hh = Items[0].ActualHeight / 2;   // 用格子的高度（不能用 Host：選取塊變大會撐大它）；四周都內縮 3
            double goal = Dragging ? DragX : CenterOf(root, Selected);
            pos.Step(goal, Dragging ? 0.196 : 0.40, Dragging ? 0.90 : 0.85, dt);
            if (Tapping && Math.Abs(goal - pos.X) <= 3.5) Tapping = false;   // 快到了：開始落下
            bool up = Dragging || Tapping;
            lift.Step(up ? 1 : 0, 0.25, 1, dt);
            solid.Step(up ? 0 : 1, up ? 0.25 : 0.40, 1, dt);
            double l = Motion.Clamp01(lift.X);
            // 拖過頭：可以超出最後一格一點點（約透鏡高的 1/10），越拉越拉不動，到底時壓扁（寬少、高多）
            double r = Motion.Rubber(rub, Dragging ? DragRaw - DragX : 0, 8, Dragging, dt), squeeze = Math.Abs(r) / 4.8;
            stretch.Step(pos.V, dt);
            var c = Motion.Center(Host, root, true);
            X = pos.X + r; Y = c.Y;
            W = (half + 10 * l) * stretch.X * (1 - 0.2 * squeeze);
            H = (hh + 4 * l) * stretch.Y * (1 + 0.2 * squeeze);   // 只比軌道高一點（實機：上下各超出約 6%）
            Mag = 1 + 0.2 * l;   // 透鏡底下放大 1.2 倍（實機）；放大用 Catmull-Rom 取樣，字才不會粗糙
            Lift = l;
            SolidAmount = Motion.Clamp01(solid.X);
            return true;
        }

        // 實心的那塊由 WPF 畫在字的下面（第二層玻璃疊在最上面，實心的話會把字蓋掉）；變成透鏡時淡掉，換玻璃出現
        public void Place(Visual root, bool glass)
        {
            var origin = Host.TransformToAncestor(root).Transform(new Point(0, 0));
            Visual.Width = W * 2; Visual.Height = H * 2;
            Motion.MoveTo(Visual, X - W - origin.X, Y - H - origin.Y);
            double o = glass ? SolidAmount : 1;
            if (Visual.Opacity != o) Visual.Opacity = o;
            double so = glass ? 1 : 0.3 + 0.7 * SolidAmount;
            if (Solid.Opacity != so) Solid.Opacity = so;
        }

        public bool Lifted { get { return SolidAmount < 0.999; } }

        // 選取的樣子（字變粗）跟著透鏡走：拖曳中透鏡蓋到哪一格就是哪一格，平常是選到的那一格。
        public void Mark(Visual root)
        {
            int at = Dragging || Tapping ? Nearest(root, X) : Selected;
            for (int i = 0; i < Items.Length; i++)
            {
                var weight = i == at ? FontWeights.SemiBold : FontWeights.Normal;
                if (Items[i].FontWeight != weight) Items[i].FontWeight = weight;
            }
        }

        // 第二層玻璃只有透鏡（實心的部分 WPF 畫）
        public void Shape(GlassShape s, double appear)
        {
            s.X = X; s.Y = Y; s.HalfW = W; s.HalfH = H; s.Radius = H;
            s.Appear = (1 - SolidAmount) * appear; s.Energy = 0; s.Thick = Math.Max(0.6, Math.Min(1.5, Math.Min(W, H) / 40));
            s.TintA = 0; s.FillA = 0;
            s.D00 = 1; s.D01 = 0; s.D11 = 1;
            s.Mag = Mag; s.Shadow = 0;
            s.PullAmount = 0;
        }
    }

    // 工具列的一組（iOS 26：功能相近的按鈕共用一塊膠囊）：按下其中任何一格，整塊膠囊長大一圈（多約 13，圓角照新的高度算），
    // 上面的圖示等比放大；放開用同一顆彈簧縮回（0.36 / 0.59，約 9% 過衝）；白光從手指下面擴散到這塊膠囊（面板那邊做）。
    // 膠囊的底色放在 Canvas 裡，長大不會推擠旁邊的版面
    class GlassGroup
    {
        public readonly FrameworkElement Root;
        public readonly Border Back;          // 膠囊的底色
        readonly UIElement icons;
        public readonly List<ButtonBase> Items = new List<ButtonBase>();
        readonly Spring press = new Spring();
        readonly ScaleTransform scale = new ScaleTransform();

        public GlassGroup(FrameworkElement group)
        {
            Root = group;
            foreach (var child in LogicalTreeHelper.GetChildren(group))
            {
                var canvas = child as Canvas;
                if (canvas != null && canvas.Children.Count > 0) Back = canvas.Children[0] as Border;
                else if (child is UIElement) icons = (UIElement)child;
            }
            if (icons != null) { icons.RenderTransformOrigin = new Point(0.5, 0.5); icons.RenderTransform = scale; }
        }

        // 面板收起來：放大的樣子歸位（不然下次打開會先看到它縮回去）
        public void Reset() { press.Jump(0); Step(0); }

        public void Step(double dt)
        {
            bool pressed = false;
            foreach (var b in Items) if (b.IsPressed) pressed = true;
            double p = press.Step(pressed ? 1 : 0, 0.36, 0.59, dt);
            double w = Root.ActualWidth, h = Root.ActualHeight;
            if (w <= 0 || h <= 0 || Back == null) return;
            double g = Math.Max(0, p) * Math.Min(0.3 * Math.Min(w, h) / 2, 10);
            if (Back.Width != w + 2 * g) Back.Width = w + 2 * g;
            if (Back.Height != h + 2 * g) Back.Height = h + 2 * g;
            var r = new CornerRadius(h / 2 + g);
            if (Back.CornerRadius != r) Back.CornerRadius = r;
            Motion.MoveTo(Back, -g, -g);
            double k = Math.Min((w / 2 + g) / (w / 2), (h / 2 + g) / (h / 2));
            if (scale.ScaleX != k) { scale.ScaleX = k; scale.ScaleY = k; }
        }

        // 第二層的玻璃（跟分段、開關的透鏡同一套光學：中間清澈、邊緣把底下的東西折進來、細亮邊）。
        // 上面的圖示不放大（Mag 1），只有邊緣那一圈會扭曲
        public bool Shape(Visual root, GlassShape s, double appear)
        {
            if (Back == null || !Root.IsVisible || Back.Width <= 0 || double.IsNaN(Back.Width)) return false;
            Rect r;
            try { r = Back.TransformToAncestor(root).TransformBounds(new Rect(0, 0, Back.Width, Back.Height)); }
            catch (InvalidOperationException) { return false; }
            s.X = r.X + r.Width / 2; s.Y = r.Y + r.Height / 2; s.HalfW = r.Width / 2; s.HalfH = r.Height / 2; s.Radius = r.Height / 2;
            s.Appear = appear; s.Energy = 0; s.Thick = 0.6;
            s.TintA = 0; s.FillA = 0;
            s.D00 = 1; s.D01 = 0; s.D11 = 1;
            s.Mag = 1; s.Shadow = 0;
            s.PullAmount = 0;
            return true;
        }
    }
}
