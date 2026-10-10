using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoWallpaper
{
    // 右下角的音樂卡片：版面、按鈕、音量元件
    static partial class LockClock
    {
        // 音樂卡片：好幾張同樣大小、疊在同樣位置的圖（播放引擎各自做動畫）
        public class MusicCard
        {
            public int[] Chrome;   // 毛玻璃底＋邊框（不動）
            public int[] Info;     // 歌名、歌手、播放按鈕（換歌時整頁滑動；沒有封面時的音符方塊也在這張）
            public int[] Mic;      // 歌詞按鈕（全亮畫好，亮度交給播放引擎慢慢調）
            public int[] Volume;   // 喇叭（音量變了只重畫這張和 VolumeBar）
            public int[] VolumeBar;   // 音量滑桿（平常收起來，滑鼠停在喇叭上才展開）
            public CardColor Color;   // 卡片的底色（封面糊成一片的顏色，或跟著封面色相的漸層；疊在毛玻璃上、會慢慢飄動）和帶顏色的邊框
            public int[] Cover;       // 專輯封面（不加陰影，跟著換歌一起翻頁；沒有封面是 null）
            public string ColorKey;   // 顏色有沒有變（換歌時顏色變了才淡入淡出）
            public int Width, Height, X, Y;        // 每張圖的大小和位置（四周有留給陰影的空間）
            public int InnerX, InnerY, InnerW, InnerH;   // 卡片本身在螢幕上的範圍（扣掉陰影的空間）
            public double TrackLeft, TrackWidth;   // 音量滑桿在螢幕上的左端和長度（點或拖曳時換算成音量）
            public int RevealX, RevealW;           // 音量滑桿在圖上的範圍（展開動畫從左邊長出來）
            public double KnobRadius;              // 把手的半徑（展開時把手保持原樣，不跟著拉長）
            internal CardLayout Layout;            // 畫這張卡片的版面（音量變了只要改滑桿再畫兩張，不用整個重排）

            // 音量是 volume 時，把手中心在圖上的位置
            public double KnobX(double volume) { return TrackLeft - X + TrackWidth * Math.Max(0, Math.Min(1, volume)); }

            public bool InnerContains(int x, int y) { return x >= InnerX && x < InnerX + InnerW && y >= InnerY && y < InnerY + InnerH; }

            // 音量變了：只改滑桿和喇叭圖示，重畫喇叭和滑桿那兩張（跟卡片其他圖一樣大小、一樣位置）
            public int[] RenderVolume(double volume, out int[] bar) { return Layout.RenderVolume(volume, out bar); }
        }

        // 卡片的版面：同一個版面畫好幾次，每次只留一層（Hidden 會保留位置，每張圖完全對齊）
        internal class CardLayout
        {
            public const int Chrome = 0, Info = 1, Mic = 2, Speaker = 3, Bar = 4;
            readonly Border root;
            readonly List<UIElement>[] layers;   // 照上面的編號：chrome、info、mic、喇叭、滑桿
            readonly VolumeControl volume;
            readonly int width, height;

            public CardLayout(Border root, List<UIElement>[] layers, VolumeControl volume, int width, int height)
            {
                this.root = root; this.layers = layers; this.volume = volume; this.width = width; this.height = height;
            }

            // 只顯示第 index 層、藏起其他層，畫一張
            public int[] Snapshot(int index)
            {
                for (int i = 0; i < layers.Length; i++)
                    foreach (var e in layers[i]) e.Visibility = i == index ? Visibility.Visible : Visibility.Hidden;
                root.UpdateLayout();
                return LockClock.Snapshot(root, width, height);
            }

            public int[] RenderVolume(double level, out int[] bar)
            {
                volume.Show(level);
                bar = Snapshot(Bar);
                return Snapshot(Speaker);
            }
        }

        // 右下角：正在播放的歌（沒在播就回傳 null）。
        // buttons：回傳各個按鈕在螢幕上的位置，讓鎖定畫面知道滑鼠點到哪個；volume：Spotify 現在的音量（0～1）
        public static MusicCard RenderMusic(NowPlaying.Track track, int screenWidth, int screenHeight, double volume, List<KeyValuePair<Rect, string>> buttons)
        {
            if (track == null) return null;
            double s = screenHeight / 1080.0;
            double pad = ShadowPad(screenHeight);
            var controls = new List<KeyValuePair<FrameworkElement, string>>();
            var infoParts = new List<UIElement>();
            var chromeParts = new List<UIElement>();
            UIElement micPart;
            VolumeControl volumeControl;
            Border root;
            var card = new MusicCard();
            var art = DecodeArt(track.Art, (int)Math.Round(300 * s));
            FrameworkElement coverSlot;
            var content = BuildNowPlaying(track, art != null, s, controls, infoParts, chromeParts, out micPart, out volumeControl, out coverSlot);
            Wrap(content, s, pad, out root, out card.Width, out card.Height);
            // 封面另外畫（不加陰影，看起來是嵌在卡片裡，不是貼上去的照片），位置就是版面上留給它的那一格
            if (art != null) card.Cover = RenderCover(art, card.Width, card.Height, coverSlot.TransformToAncestor(root).Transform(new Point(0, 0)), coverSlot.ActualWidth, s);
            // 卡片的底色（另外畫，不加陰影，免得卡片外面多一圈影子）：有封面就是封面糊成一片的顏色；
            // 沒有封面就用封面的色相或歌名算出來的漸層，每首歌都有顏色
            var hues = AlbumHues(track.Art) ?? SongHues(Lyrics.SongKey(track));
            card.Color = RenderCardColor(hues, art, card.Width, card.Height, pad, content.ActualWidth, content.ActualHeight, s);
            card.ColorKey = string.Join(",", hues.Select(v => v.ToString("0.000", CultureInfo.InvariantCulture))) + "," + (art == null ? 0 : track.Art.Length);
            // 離右邊和下面一樣是 52（下緣跟左下角的時鐘對齊）
            card.X = (int)Math.Round(screenWidth - 52 * s - card.Width + pad);
            card.Y = (int)Math.Round(screenHeight - 52 * s - card.Height + pad);
            card.InnerX = card.X + (int)pad; card.InnerY = card.Y + (int)pad;
            card.InnerW = card.Width - 2 * (int)pad; card.InnerH = card.Height - 2 * (int)pad;
            foreach (var c in controls)
            {
                var p = c.Key.TransformToAncestor(root).Transform(new Point(0, 0));
                buttons.Add(new KeyValuePair<Rect, string>(new Rect(p.X + card.X, p.Y + card.Y, c.Key.RenderSize.Width, c.Key.RenderSize.Height), c.Value));
            }
            double trackLeft = volumeControl.Panel.TransformToAncestor(root).Transform(new Point(volumeControl.TrackLeft, 0)).X;
            card.TrackLeft = trackLeft + card.X;
            card.TrackWidth = volumeControl.TrackWidth;
            // 展開的範圍：滑桿左右各多留把手的半徑和一點空間
            double margin = volumeControl.KnobRadius + Math.Round(3 * s);
            card.RevealX = (int)Math.Floor(trackLeft - margin);
            card.RevealW = (int)Math.Ceiling(volumeControl.TrackWidth + 2 * margin);
            card.KnobRadius = volumeControl.KnobRadius;

            card.Layout = new CardLayout(root, new[] { chromeParts, infoParts, new List<UIElement> { micPart }, volumeControl.IconParts, volumeControl.BarParts },
                volumeControl, card.Width, card.Height);
            card.Chrome = card.Layout.Snapshot(CardLayout.Chrome);
            card.Info = card.Layout.Snapshot(CardLayout.Info);
            card.Mic = card.Layout.Snapshot(CardLayout.Mic);
            card.Volume = card.RenderVolume(volume, out card.VolumeBar);
            return card;
        }

        // 橫向的卡片（半透明深色圓角長方形）：左邊專輯封面；右邊上面歌名、下面歌手，
        // 再下面一排上一首、播放暫停、下一首、音量，最右邊是歌詞按鈕
        // info：歌名、歌手、播放按鈕（換歌時整頁滑動；封面另外畫，見 RenderCover，也跟著滑）；mic：歌詞按鈕（也跟著滑，亮度另外調）；
        // volume：喇叭和音量滑桿（也跟著滑，音量變了單獨重畫）；chrome：卡片的毛玻璃底和邊框（不動）
        static FrameworkElement BuildNowPlaying(NowPlaying.Track track, bool hasArt, double s, List<KeyValuePair<FrameworkElement, string>> controls,
            List<UIElement> info, List<UIElement> chrome, out UIElement mic, out VolumeControl volume, out FrameworkElement coverSlot)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };

            // 有封面：封面另外畫（RenderCover，不加陰影），這裡只留位置；沒有封面才顯示半透明方塊和音符
            double artSize = Math.Round(76 * s);
            var cover = new Border { Width = artSize, Height = artSize };
            coverSlot = cover;
            if (!hasArt)
            {
                cover.CornerRadius = new CornerRadius(8 * s);
                cover.Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
                cover.Child = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 30 * s, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            row.Children.Add(cover);
            info.Add(cover);

            // 右邊固定寬度：不管歌名長短，卡片大小和按鈕位置都一樣（太長的歌名會顯示「…」）
            double buttonSize = Math.Round(32 * s);
            var right = new StackPanel { Margin = new Thickness(12 * s, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = Math.Round(196 * s) };
            var title = new TextBlock { Text = track.Title, FontFamily = ClockFont, FontSize = 16 * s, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
            var artist = new TextBlock { Text = track.Artist, FontFamily = ClockFont, FontSize = 13 * s, Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 255, 255, 255)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1 * s, 0, 0) };
            right.Children.Add(title);
            right.Children.Add(artist);
            info.Add(title);
            info.Add(artist);

            // 按鈕列：左邊上一首、播放暫停、下一首（第一個圖示跟文字左邊對齊），右邊歌詞
            double inset = (buttonSize - 15 * s) / 2;
            var bar = new Grid { Margin = new Thickness(-inset, 3 * s, -inset, -inset / 2) };
            var playback = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            AddControl(playback, "", "prev", s, buttonSize, controls);
            AddControl(playback, track.Playing ? "" : "", "toggle", s, buttonSize, controls);
            AddControl(playback, "", "next", s, buttonSize, controls);
            bar.Children.Add(playback);
            info.Add(playback);
            // 歌詞（斜放的麥克風）：畫成全亮，亮度（顯示中全白、有歌詞但沒開半亮、沒有歌詞很暗）交給播放引擎慢慢變
            var lyricsHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var lyrics = AddControl(lyricsHost, "", "lyrics", s, buttonSize, controls);
            lyrics.Child = MicIcon(s);
            mic = lyricsHost;
            bar.Children.Add(lyricsHost);
            // 音量：下一首和歌詞按鈕中間的空間
            double barWidth = Math.Round(196 * s) + 2 * inset;
            volume = BuildVolume(s, buttonSize, barWidth - 4 * buttonSize, controls);
            volume.Panel.HorizontalAlignment = HorizontalAlignment.Left;
            volume.Panel.Margin = new Thickness(3 * buttonSize, 0, 0, 0);
            bar.Children.Add(volume.Panel);
            right.Children.Add(bar);
            row.Children.Add(right);
            // 按鈕跟著一起翻頁（固定不動的話，滑過去的封面會蓋到按鈕）：播放按鈕在 info、歌詞按鈕在 mic、音量在 volume，三張一起滑

            // 毛玻璃：最底下是播放引擎糊掉的影片（看得到後面燈光的模糊輪廓），
            // 上面只疊很淡的霧白、上緣一道反光、細微的顆粒，讓後面的顏色透出來
            var radius = new CornerRadius(CardRadius(s));
            var card = new Grid();
            var tint = new Border { CornerRadius = radius, Background = new SolidColorBrush(Color.FromArgb(0x0D, 255, 255, 255)) };   // 5% 霧白
            var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 255, 255, 255), 0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.55));
            var shine = new Border { CornerRadius = radius, Background = sheen };                                                     // 上緣反光
            var grain = new Border { CornerRadius = radius, Background = NoiseBrush() };                                              // 顆粒
            // 玻璃邊緣：1.5 像素寬、淡一點（跟 1 像素的亮度差不多）。1 像素的細線繞小圓角時，反鋸齒只能一格一格地畫，
            // 在像素比較大的螢幕上看得出階梯；寬一點的線，圓角才平滑
            var edge = new Border { CornerRadius = radius, BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), BorderThickness = new Thickness(EdgeWidth) };
            card.Children.Add(tint);
            card.Children.Add(shine);
            card.Children.Add(grain);
            card.Children.Add(edge);
            card.Children.Add(new Border { Child = row, Padding = new Thickness(CardPadding(s) + 1) });   // +1：邊框的寬度
            chrome.Add(tint); chrome.Add(shine); chrome.Add(grain); chrome.Add(edge);
            return card;
        }

        // 毛玻璃的細微顆粒（跟 Windows 壓克力效果一樣的雜訊）：固定的亂數種子，每次畫出來都一樣，不會閃
        static ImageBrush noise;
        static ImageBrush NoiseBrush()
        {
            if (noise != null) return noise;
            const int size = 96;
            var random = new Random(12345);
            var pixels = new int[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                int a = random.Next(0, 14);                  // 0～5% 不透明
                int c = random.Next(2) == 0 ? 0 : a;         // 一半白點、一半黑點（預先乘好 alpha）
                pixels[i] = (a << 24) | (c << 16) | (c << 8) | c;
            }
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
            bitmap.Freeze();
            noise = new ImageBrush(bitmap) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, size, size), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
            noise.Freeze();
            return noise;
        }

        // 斜放的麥克風（線條）：右上是圓形的頭，握把往左下斜、越來越細（只畫一條線的話很像放大鏡）
        static FrameworkElement MicIcon(double s)
        {
            double size = Math.Round(18 * s), stroke = Math.Max(1, 1.4 * s);
            var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            double r = 3.8 * s, cx = size - r - 1.4 * s, cy = r + 1.4 * s;
            var head = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Stroke = Brushes.White, StrokeThickness = stroke };
            Canvas.SetLeft(head, cx - r); Canvas.SetTop(head, cy - r);
            canvas.Children.Add(head);

            // 握把的中線往左下（u），寬度方向跟它垂直（n）；靠近頭的那端寬、尾端窄，頭蓋住的那條邊不畫
            double k = Math.Sqrt(0.5), ux = -k, uy = k, nx = k, ny = k;
            double sx = cx + ux * r * 0.8, sy = cy + uy * r * 0.8;      // 握把起點（在頭的圓圈上）
            double length = 9.5 * s, w1 = 2.2 * s, w2 = 1.2 * s;        // 長度、起點半寬、尾端半寬
            double ex = sx + ux * length, ey = sy + uy * length;
            var body = new System.Windows.Shapes.Polyline
            {
                Points = new PointCollection
                {
                    new Point(sx + nx * w1, sy + ny * w1), new Point(ex + nx * w2, ey + ny * w2),
                    new Point(ex - nx * w2, ey - ny * w2), new Point(sx - nx * w1, sy - ny * w1),
                },
                Stroke = Brushes.White, StrokeThickness = stroke, StrokeLineJoin = PenLineJoin.Round,
            };
            canvas.Children.Add(body);
            return canvas;
        }

        // 音量：左邊喇叭（按了靜音 / 取消靜音；停 0.5 秒展開滑桿），右邊滑桿（點或拖曳調整）
        internal class VolumeControl
        {
            public Canvas Panel;
            public List<UIElement> IconParts = new List<UIElement>(), BarParts = new List<UIElement>();   // 喇叭、滑桿各畫成一張
            public double TrackLeft, TrackWidth;   // 滑桿在 Panel 裡的左端和長度
            public TextBlock Icon;
            public Border Fill;
            public FrameworkElement Knob;
            public double KnobRadius;

            public void Show(double volume)
            {
                volume = Math.Max(0, Math.Min(1, volume));
                // 靜音是喇叭打叉，其他依音量大小顯示一到三道聲波
                Icon.Text = volume <= 0 ? "" : volume < 0.34 ? "" : volume < 0.67 ? "" : "";
                Fill.Width = TrackWidth * volume;
                Canvas.SetLeft(Knob, TrackLeft + TrackWidth * volume - KnobRadius);
            }
        }

        static VolumeControl BuildVolume(double s, double buttonSize, double width, List<KeyValuePair<FrameworkElement, string>> controls)
        {
            var v = new VolumeControl { Panel = new Canvas { Width = width, Height = buttonSize }, KnobRadius = Math.Round(5 * s) };

            // 喇叭：跟播放按鈕一樣大，圖示的間距也跟它們一樣
            v.Icon = new TextBlock { FontFamily = IconFont, FontSize = 15 * s, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var mute = new Border { Width = buttonSize, Height = buttonSize, Background = Brushes.Transparent, Child = v.Icon };
            v.Panel.Children.Add(mute);
            v.IconParts.Add(mute);
            controls.Add(new KeyValuePair<FrameworkElement, string>(mute, "mute"));

            // 滑桿：從喇叭圖示右邊一點，到歌詞按鈕的麥克風前面（把手拉到最右邊也不會碰到麥克風）
            double inset = (buttonSize - 15 * s) / 2, gap = Math.Round(7 * s);
            double micLeft = width + (buttonSize - Math.Round(18 * s)) / 2;
            v.TrackLeft = buttonSize - inset + gap;
            v.TrackWidth = micLeft - gap - v.KnobRadius - v.TrackLeft;
            double thickness = Math.Max(2, Math.Round(4 * s)), top = (buttonSize - thickness) / 2;
            var track = new Border { Width = v.TrackWidth, Height = thickness, CornerRadius = new CornerRadius(thickness / 2), Background = new SolidColorBrush(Color.FromArgb(0x4D, 255, 255, 255)) };
            v.Fill = new Border { Height = thickness, CornerRadius = new CornerRadius(thickness / 2), Background = Brushes.White };
            v.Knob = new System.Windows.Shapes.Ellipse { Width = v.KnobRadius * 2, Height = v.KnobRadius * 2, Fill = Brushes.White };
            Canvas.SetLeft(track, v.TrackLeft); Canvas.SetTop(track, top);
            Canvas.SetLeft(v.Fill, v.TrackLeft); Canvas.SetTop(v.Fill, top);
            Canvas.SetTop(v.Knob, buttonSize / 2 - v.KnobRadius);
            v.Panel.Children.Add(track);
            v.Panel.Children.Add(v.Fill);
            v.Panel.Children.Add(v.Knob);

            // 點或拖曳的範圍：喇叭右邊到歌詞按鈕前面，整排按鈕的高度（比滑桿本身好點）
            var hit = new Border { Width = width - buttonSize, Height = buttonSize, Background = Brushes.Transparent };
            Canvas.SetLeft(hit, buttonSize);
            v.Panel.Children.Add(hit);
            v.BarParts.AddRange(new UIElement[] { track, v.Fill, v.Knob, hit });
            controls.Add(new KeyValuePair<FrameworkElement, string>(hit, "volume"));
            v.Show(0);
            return v;
        }

        static Border AddControl(Panel panel, string glyph, string action, double s, double size, List<KeyValuePair<FrameworkElement, string>> controls)
        {
            var button = new Border
            {
                Width = size, Height = size, Background = Brushes.Transparent,
                Child = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 15 * s, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            panel.Children.Add(button);
            controls.Add(new KeyValuePair<FrameworkElement, string>(button, action));
            return button;
        }
    }
}
