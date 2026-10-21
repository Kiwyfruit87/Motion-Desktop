using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoWallpaper
{
    // 控制面板的液態玻璃和它的動畫（照 iOS 26 實機）：
    //   整塊面板是第一層玻璃（Uid 是 Glass:fixed，固定不動）；
    //   工具列每一組、開關的旋鈕、分段按鈕的選取水滴是第二層玻璃：按下時整組長大，旋鈕和水滴浮起來變成透鏡、像液體一樣滑過去，拖過頭有橡皮筋；
    //   按住的地方白光擴散。
    // 這台電腦用不了液態玻璃時（太舊的 Windows、顯示卡）一樣有這些動畫，只是玻璃換成整片的毛玻璃
    partial class TrayPanel
    {
        GlassBackdrop glass;
        bool glassDecided;
        // 視窗四周留給玻璃的空間：影子要畫得下（下面影子比較長），邊緣往外取樣的折射也要取得到面板外面的畫面
        static readonly Thickness GlassInset = new Thickness(36, 36, 36, 48);

        readonly List<GlassPiece> pieces = new List<GlassPiece>();
        readonly List<GlassSwitch> switches = new List<GlassSwitch>();
        GlassDrop drop;
        readonly List<GlassShape> lower = new List<GlassShape>(), upper = new List<GlassShape>();
        readonly GlassShape[] pool = new GlassShape[GlassBackdrop.MaxShapes * 2];   // 兩層各最多 12 塊
        readonly Stopwatch clock = new Stopwatch();
        bool moving;
        TimeSpan lastFrame;

        // 出現 / 收起來（官方：玻璃的折射量慢慢增加、上面的內容淡入，不縮放；彈簧 0.6 秒）
        readonly Spring appear = new Spring();
        double appearTarget = 1;
        bool hiding;

        // 面板裡的小按鈕（＋、−）：按下時整顆長大（跟工具列那幾組一樣）
        readonly List<KeyValuePair<Button, Spring>> inners = new List<KeyValuePair<Button, Spring>>();
        readonly List<GlassGroup> groups = new List<GlassGroup>();   // 工具列的每一組（裡面的按鈕 Uid 是 Bar）
        readonly Spring glowLevel = new Spring(), glowSize = new Spring();   // 按住的白光：亮度、大小

        // 正按著的東西
        bool touching, moved, switchArea;
        Point downAt;
        GlassSwitch heldSwitch;
        bool dropHeld;
        double touchGlow;
        Point glowAt;
        FrameworkElement glowOn;   // 白光亮在哪個元件裡（被按的工具列那組、小按鈕；開關、分段亮在它們的透鏡上）
        GlassSwitch glowSwitch;    // 按的是開關 / 分段：白光只亮在浮起來的透鏡裡（第二層）
        bool glowDrop;
        readonly GlassLook panelLook = new GlassLook();
        FrameworkElement segmentBack, segmentShine;   // 分段控制的軌道：按住時整個變寬、變亮

        void SetupMotion()
        {
            GlassBackdrop.Prepare();   // 先在背景編譯著色器
            for (int i = 0; i < pool.Length; i++) pool[i] = new GlassShape();
            Collect(root);
            segmentBack = Find<FrameworkElement>("SegmentBack");
            segmentShine = Find<FrameworkElement>("SegmentShine");
            segmentBack.RenderTransformOrigin = new Point(0.5, 0.5);
            segmentBack.RenderTransform = new ScaleTransform();
            drop = new GlassDrop(Find<FrameworkElement>("SegmentHost"), new[] { stretchFill, stretchUniform, stretchStretch })
            {
                Visual = Find<FrameworkElement>("SegmentThumb"),
                Solid = Find<FrameworkElement>("SegmentSolid"),
            };

            content.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnDown), true);
            content.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnMove), true);
            content.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnUp), true);
            content.LostMouseCapture += delegate { if (touching && Mouse.Captured == null) Let(false); };
        }

        // 找出每一塊玻璃（Uid）和每一個開關（包括其他檔案在 AddExtras 加的）
        void Collect(DependencyObject node)
        {
            var e = node as FrameworkElement;
            if (e != null && e.Uid != null && e.Uid.StartsWith("Glass")) pieces.Add(new GlassPiece(e));
            var box = node as CheckBox;
            if (box != null) switches.Add(new GlassSwitch(box));
            var button = node as Button;
            if (button != null && button.Uid == "Bar") GroupOf(button).Items.Add(button);
            else if (button != null && (button.Uid == null || !button.Uid.StartsWith("Glass"))) inners.Add(new KeyValuePair<Button, Spring>(button, new Spring()));
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                var d = child as DependencyObject;
                if (d != null) Collect(d);
            }
        }

        // 工具列按鈕所在的那一組（往上找到 Tag 是 Group 的那一層）
        GlassGroup GroupOf(FrameworkElement item)
        {
            DependencyObject node = item;
            while (node != null && !(node is FrameworkElement && ((FrameworkElement)node).Tag as string == "Group")) node = LogicalTreeHelper.GetParent(node);
            var group = (FrameworkElement)node ?? item;
            var g = groups.Find(x => x.Root == group);
            if (g == null) groups.Add(g = new GlassGroup(group));
            return g;
        }

        // 第一次打開面板時決定用不用液態玻璃（著色器通常已經在背景編好了）
        void EnsureGlass()
        {
            if (glassDecided) return;
            glassDecided = true;
            var code = GlassBackdrop.Shader;
            if (code == null) return;
            try
            {
                glass = new GlassBackdrop(content, outer, code);
                System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered += delegate { window.Dispatcher.BeginInvoke(new Action(DropGlass)); };
            }
            catch { glass = null; }
        }

        // 用不了（例如顯示卡重設之後不支援）：退回整片毛玻璃
        void DropGlass()
        {
            if (glass == null) return;
            glass.Remove();
            glass = null;
            root.Margin = new Thickness(0);
            Find<FrameworkElement>("PanelRim").Visibility = Visibility.Visible;
            dark = Theme.IsDark();
            Theme.Apply(root.Resources, dark, false);
            if (!backdrop) window.Background = (Brush)root.Resources["WindowFallback"];
            ApplyWindowTheme();
            if (open)
            {
                // 視窗四周不用再留玻璃的影子：重新放到角落
                Rect place = Placement();
                window.BeginAnimation(Window.TopProperty, null);
                window.Left = place.X; window.Top = restingTop = place.Y;
            }
            LaterApplyShape();
        }

        // 打開：每一塊玻璃長出來（視窗不滑動：後面的畫面是一格一格抓的，視窗一動玻璃裡的背景會跟不上）
        void Appear(bool fresh)
        {
            hiding = false;
            appearTarget = 1;
            if (fresh) appear.Jump(glass != null ? 0 : 1);
        }

        // 收起來：玻璃的折射量慢慢減少、上面的內容淡出，收完才藏起視窗（OnFrame）
        int hideCount;   // 第幾次收起來（保險計時器認得是不是同一次）

        void Disappear()
        {
            appearTarget = 0;
            hiding = true;
            int hide = ++hideCount;
            // 保險：收起動畫靠每一格畫面更新推進，萬一畫面停止更新（例如這時剛好鎖定電腦、螢幕關掉），
            // 0.8 秒後直接收起來，背景的抓畫面也一起停掉
            UiTimer.After(800, delegate
            {
                if (hide != hideCount || !hiding || open) return;   // 這段時間又被打開、再收起來過：交給新的那次
                hiding = false;
                window.Hide();
                UpdatePreview();
                StopMotion();
            });
        }

        void StartMotion()
        {
            if (moving) return;
            moving = true;
            clock.Restart();
            lastFrame = TimeSpan.Zero;
            if (glass != null) glass.Start();
            CompositionTarget.Rendering += OnFrame;
        }

        void StopMotion()
        {
            if (!moving) return;
            moving = false;
            Let(false);
            touchGlow = 0;
            // 按著按鈕時面板被收起來（按鎖定畫面之類會馬上收起來的）：白光、放大的樣子都歸位，
            // 不然下次打開會先在舊的地方閃一下白光、看到它縮回去
            glowLevel.Jump(0);
            glowOn = null; glowSwitch = null; glowDrop = false;
            foreach (var g in groups) g.Reset();
            foreach (var inner in inners)
            {
                inner.Value.Jump(0);
                var scale = inner.Key.RenderTransform as ScaleTransform;
                if (scale != null && !scale.IsFrozen) { scale.ScaleX = 1; scale.ScaleY = 1; }
            }
            CompositionTarget.Rendering -= OnFrame;
            if (glass != null) glass.Stop();
        }

        void OnFrame(object sender, EventArgs e)
        {
            var time = clock.Elapsed;
            if (time == lastFrame) return;
            double dt = lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (time - lastFrame).TotalSeconds);
            lastFrame = time;
            double now = time.TotalSeconds;

            if (glass != null) glass.Kick();   // 每一格叫背景執行緒抓一次後面的畫面

            // 出現 / 收起來；收完了才真的把視窗藏起來
            appear.Step(appearTarget, appearTarget > 0.5 ? 0.6 : 0.28, appearTarget > 0.5 ? 0.92 : 1, dt);
            double shown = Motion.Clamp01(appear.X);
            if (root.Opacity != shown) root.Opacity = shown;   // 上面的字、預覽、分區淡入淡出（玻璃本身由折射量處理）
            // 出現動畫跑完才載入預覽（開檔會卡一下）；沒有液態玻璃時 appear 一開始就是 1，等滑入動畫（0.26 秒）跑完
            if (deferPreview && appear.X > 0.98 && (glass != null || now > 0.3)) { deferPreview = false; UpdatePreview(true); }
            if (hiding && appear.X < 0.03)
            {
                hiding = false;
                window.Hide();
                UpdatePreview();
                StopMotion();
                return;
            }

            // 面板裡的小按鈕（＋、−）：跟工具列那幾組一樣，按下時整顆帶彈性長大（半徑多 0.3 倍、最多 10），白光亮在它上面；放開彈回去
            foreach (var inner in inners)
            {
                var button = inner.Key;
                double pressed = Math.Max(0, inner.Value.Step(button.IsPressed ? 1 : 0, 0.36, 0.59, dt));
                double r = Math.Min(button.ActualWidth, button.ActualHeight) / 2;
                double k = r > 0 ? 1 + pressed * Math.Min(0.3 * r, 10) / r : 1;
                var scale = button.RenderTransform as ScaleTransform;
                if (scale == null || scale.IsFrozen)
                {
                    if (k == 1) continue;
                    button.RenderTransformOrigin = new Point(0.5, 0.5);
                    button.RenderTransform = scale = new ScaleTransform();
                }
                if (scale.ScaleX != k) { scale.ScaleX = k; scale.ScaleY = k; }
            }

            // 按住的白光（iOS 26 實測）：手指下面一團直徑約 93 的柔光，約 0.1 秒亮起來、按著一直亮；
            // 放開後在放開的地方一邊擴大到 4 倍一邊淡掉，大約 0.3 秒看不到。按到不會亮的地方就不亮
            touchGlow = glowOn == null ? 0 : Math.Max(0, glowLevel.Step(touching ? 1 : 0, touching ? 0.10 : 0.30, 1, dt));
            if (!touching && touchGlow < 0.002) touchGlow = 0;
            double glowRadius = glowSize.Step(touching ? 23 : 92, 0.5, 1, dt);
            var finger = Mouse.GetPosition(content);
            if (touching) glowAt = finger;
            if (touchGlow == 0) glowAt = new Point();   // 沒在亮就不用動（數值不變，畫面才不用重畫）

            int used = 0;
            lower.Clear();
            upper.Clear();
            foreach (var p in pieces)
            {
                if (!p.Layout(content)) continue;
                GlassShape s = null;
                if (glass != null && lower.Count < GlassBackdrop.MaxShapes) { s = pool[used++]; lower.Add(s); }
                p.Apply(s, shown);
            }
            foreach (var sw in switches)
            {
                if (!sw.Step(content, now, dt)) continue;
                sw.Place(content, glass != null);
                if (glass != null && sw.Lifted && upper.Count < GlassBackdrop.MaxShapes) { var s = pool[used++]; sw.Shape(s, shown); upper.Add(s); }
            }
            foreach (var g in groups)
            {
                g.Step(dt);
                if (glass != null && upper.Count < GlassBackdrop.MaxShapes && g.Shape(content, pool[used], shown)) upper.Add(pool[used++]);
            }
            if (drop.Step(content, now, dt))
            {
                drop.Mark(content);
                // 按住時整個分段控制變寬、變亮約 +11/255（官方實測寬 14.6；分區的邊距只有 5，最多每邊 5）
                var back = (ScaleTransform)segmentBack.RenderTransform;
                double wide = segmentBack.ActualWidth > 0 ? (segmentBack.ActualWidth + 10 * drop.Lift) / segmentBack.ActualWidth : 1;
                if (back.ScaleX != wide) back.ScaleX = wide;
                double shine = 0.043 * drop.Lift;
                if (segmentShine.Opacity != shine) segmentShine.Opacity = shine;
                drop.Place(content, glass != null);
                if (glass != null && drop.Lifted && upper.Count < GlassBackdrop.MaxShapes) { var s = pool[used++]; drop.Shape(s, shown); upper.Add(s); }
            }
            if (glass != null)
            {
                // 深淺照 Windows；玻璃後面哪裡太亮（深色玻璃）就在那裡多壓暗、哪裡太暗（淺色玻璃）就多加白霧（著色器逐點算）
                glass.Style = dark ? 0 : 1;
                panelLook.CopyFrom(GlassLook.Base);
                if (dark) { panelLook.Fog = 0.08; panelLook.Dim = 0.1; panelLook.DimAdapt = 0.45; }
                else { panelLook.Fog = 0.3; panelLook.FogAdapt = 0.25; }
                // 白光的範圍：工具列那組、小按鈕亮在它們自己（0.24）；開關、分段亮在透鏡底下那一塊（0.1，透鏡會把它放大），
                // 都畫在第一層（透鏡本身不另外加光，不然會疊兩次）
                Rect mask = Rect.Empty;
                double maskRadius = 0, glowStrength = 0.24;
                if (touchGlow > 0)
                {
                    if (glowSwitch != null) { mask = new Rect(glowSwitch.X - glowSwitch.W, glowSwitch.Y - glowSwitch.H, glowSwitch.W * 2, glowSwitch.H * 2); maskRadius = glowSwitch.H; glowStrength = 0.1; }
                    else if (glowDrop)
                    {
                        try { mask = segmentBack.TransformToAncestor(content).TransformBounds(new Rect(0, 0, segmentBack.ActualWidth, segmentBack.ActualHeight)); }
                        catch (InvalidOperationException) { }
                        maskRadius = mask.IsEmpty ? 0 : mask.Height / 2;
                        glowStrength = 0.1;
                    }
                    else if (glowOn != null && glowOn.IsVisible)
                    {
                        try { mask = glowOn.TransformToAncestor(content).TransformBounds(new Rect(0, 0, glowOn.ActualWidth, glowOn.ActualHeight)); }
                        catch (InvalidOperationException) { }
                        var b = glowOn as Border;
                        maskRadius = b != null && b.Tag as string != "Pill" ? b.CornerRadius.TopLeft * mask.Height / Math.Max(1, glowOn.ActualHeight) : Math.Min(mask.Width, mask.Height) / 2;
                    }
                }
                glass.Draw(false, lower, panelLook, glowAt, touchGlow, glowRadius, mask, maskRadius, glowStrength);
                glass.Draw(true, upper, GlassLook.Top, glowAt, 0, glowRadius, Rect.Empty, 0, 0);
            }
        }

        // ---------- 按住、拖、放開 ----------
        void OnDown(object sender, MouseButtonEventArgs e)
        {
            Let(false);
            var p = e.GetPosition(content);
            downAt = p;
            moved = false;
            touching = true;
            glowSize.Jump(23);
            glowOn = null;
            glowSwitch = null;
            glowDrop = false;
            for (var node = e.OriginalSource as DependencyObject; node != null && node != content; node = Parent(node))
            {
                var box = node as CheckBox;
                var sw = box == null ? null : switches.Find(x => x.Box == box);
                if (sw != null && sw.Find())
                {
                    // 開關：按在開關上可以拖；按在那一列的字上就是點一下
                    heldSwitch = sw;
                    var c = Motion.Center(sw.Track, content, true);
                    switchArea = Math.Abs(p.X - c.X) < 31 + 8 && Math.Abs(p.Y - c.Y) < 22;
                    sw.StartPos = sw.Position; sw.Raw = sw.Position; sw.Tapping = false;
                    sw.Dragging = switchArea;
                    glowOn = sw.Track;
                    glowSwitch = sw;
                    content.CaptureMouse();
                    e.Handled = true;
                    return;
                }
                if (node == drop.Host || node == segmentBack || (node is RadioButton && Array.IndexOf(drop.Items, node) >= 0))
                {
                    // 按在選取的那一格：馬上浮起成透鏡、跟著手指；按在別格：放開才切過去（中途拖了也會浮起來跟著手指）
                    dropHeld = true;
                    drop.Dragging = drop.Nearest(content, p.X) == drop.Selected;
                    drop.DragRaw = p.X; drop.DragX = DropClamp(p.X); drop.Tapping = false;
                    glowOn = segmentBack;
                    glowDrop = true;
                    content.CaptureMouse();
                    e.Handled = true;
                    return;
                }
                if (pieces.Exists(x => x.Element == node)) { touching = false; return; }   // 按在面板本身（不是任何控制項）：不會被拖、也不亮
                var button = node as Button;
                if (button != null)
                {
                    // 工具列的按鈕：白光亮在整組膠囊；面板裡的小按鈕：亮在它自己
                    if (button.IsEnabled) glowOn = button.Uid == "Bar" ? (FrameworkElement)GroupOf(button).Back : button;
                    return;
                }
            }
        }

        double DropClamp(double x)
        {
            return Math.Min(Math.Max(x, drop.CenterOf(content, 0)), drop.CenterOf(content, drop.Items.Length - 1));
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (!touching) return;
            var p = e.GetPosition(content);
            var d = p - downAt;
            if (d.Length > 5) moved = true;
            if (heldSwitch != null && switchArea) heldSwitch.Raw = heldSwitch.StartPos + d.X / GlassSwitch.Travel;
            if (dropHeld)
            {
                if (moved) drop.Dragging = true;
                drop.DragRaw = p.X; drop.DragX = DropClamp(p.X);
            }
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!touching) return;
            if (heldSwitch != null || dropHeld) e.Handled = true;
            Let(true);
        }

        // 放開（act：照放開的位置切換開關、選分段；false 就只是放掉，例如視窗失去焦點）
        void Let(bool act)
        {
            if (!touching) return;
            touching = false;
            if (heldSwitch != null)
            {
                var sw = heldSwitch;
                bool on = sw.Box.IsChecked == true;
                bool next = !act ? on : moved ? (switchArea ? Motion.Clamp01(sw.Raw) > 0.5 : on) : !on;
                if (act && next != on || act && moved) sw.Tapping = true;   // 點一下 / 拖完放開：還是透鏡、滑過去，到了才縮回白色
                sw.Dragging = false;
                if (next != on) sw.Box.IsChecked = next;
            }
            if (dropHeld)
            {
                int best = drop.Nearest(content, drop.Dragging ? drop.DragRaw : downAt.X);   // 選離手指最近的那一格
                if (act && (moved || best != drop.Selected)) drop.Tapping = true;   // 點別格 / 拖完放開：抬起著滑過去，快到了才落下
                drop.Dragging = false;
                if (act) drop.Items[best].IsChecked = true;
            }
            heldSwitch = null;
            dropHeld = false;
            if (content.IsMouseCaptured) content.ReleaseMouseCapture();
        }

        static DependencyObject Parent(DependencyObject node)
        {
            return node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
    }
}
