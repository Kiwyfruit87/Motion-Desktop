using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;

namespace VideoWallpaper
{
    // 點系統匣圖示後彈出的控制面板
    class TrayPanel
    {
        // 面板上的中文（XAML 裡整個屬性值）在非中文 Windows 換成英文
        static readonly string[,] XamlEnglish =
        {
            { "尚未選擇影片", "No video selected" },
            { "鎖定畫面", "Lock screen" },
            { "選擇影片", "Choose video" },
            { "畫面縮放", "Scaling" },
            { "填滿", "Fill" },
            { "完整顯示", "Fit" },
            { "拉伸", "Stretch" },
            { "選項", "Options" },
            { "自動暫停", "Auto-pause" },
            { "有視窗最大化或全螢幕時暫停，節省資源", "Pause when windows cover the desktop" },
            { "開機時自動啟動", "Start with Windows" },
            { "登入 Windows 後自動播放動態桌布", "Start playing when you sign in" },
            { "工作列透明", "Transparent taskbar" },
            { "動態桌布", "Motion Desktop" },
            { "關閉動態桌布，並還原原本的桌布", "Close Motion Desktop and restore your original wallpaper" },
            { "結束", "Exit" },
        };

        static string Localize(string xaml)
        {
            if (Lang.Chinese) return xaml;
            for (int i = 0; i < XamlEnglish.GetLength(0); i++)
                xaml = xaml.Replace("'" + XamlEnglish[i, 0] + "'", "'" + XamlEnglish[i, 1] + "'");   // 只換整個屬性值，不會換到別的字串裡的一部分
            return xaml;
        }

        const string Xaml = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
      xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
      Width='360' UseLayoutRounding='True' SnapsToDevicePixels='True'
      TextElement.FontFamily='Segoe UI, Microsoft JhengHei UI'
      TextElement.FontSize='13'
      TextElement.Foreground='{DynamicResource TextPrimary}'
      TextOptions.TextFormattingMode='Display'>
  <Grid.Resources>
    <FontFamily x:Key='IconFont'>Segoe Fluent Icons, Segoe MDL2 Assets</FontFamily>

    <Style x:Key='Btn' TargetType='Button'>
      <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
      <Setter Property='Background' Value='{DynamicResource ControlFill}'/>
      <Setter Property='BorderBrush' Value='{DynamicResource ControlStroke}'/>
      <Setter Property='Tag' Value='{DynamicResource HoverOverlay}'/>
      <Setter Property='BorderThickness' Value='1'/>
      <Setter Property='Padding' Value='14,0'/>
      <Setter Property='Height' Value='36'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bd' CornerRadius='6' Background='{TemplateBinding Background}'
                    BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
              <Grid>
                <Border x:Name='Hover' CornerRadius='5' Opacity='0'
                        Background='{Binding Tag, RelativeSource={RelativeSource TemplatedParent}}'/>
                <ContentPresenter x:Name='Cp' Margin='{TemplateBinding Padding}'
                                  HorizontalAlignment='Center' VerticalAlignment='Center'/>
              </Grid>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Hover' Property='Opacity' Value='1'/>
              </Trigger>
              <Trigger Property='IsPressed' Value='True'>
                <Setter TargetName='Hover' Property='Opacity' Value='0.5'/>
                <Setter TargetName='Cp' Property='Opacity' Value='0.75'/>
              </Trigger>
              <Trigger Property='IsEnabled' Value='False'>
                <Setter TargetName='Bd' Property='Opacity' Value='0.4'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='AccentBtn' TargetType='Button' BasedOn='{StaticResource Btn}'>
      <Setter Property='Background' Value='{DynamicResource Accent}'/>
      <Setter Property='Foreground' Value='{DynamicResource TextOnAccent}'/>
      <Setter Property='Tag' Value='{DynamicResource AccentOverlay}'/>
      <Setter Property='BorderThickness' Value='0'/>
    </Style>
    <Style x:Key='SubtleBtn' TargetType='Button' BasedOn='{StaticResource Btn}'>
      <Setter Property='Background' Value='Transparent'/>
      <Setter Property='BorderThickness' Value='0'/>
    </Style>

    <Style x:Key='Segment' TargetType='RadioButton'>
      <Setter Property='Foreground' Value='{DynamicResource TextSecondary}'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='RadioButton'>
            <!-- 選取的底色與小橫條由共用的 SegmentThumb 負責，切換時會滑過去 -->
            <Border x:Name='Bd' CornerRadius='4' Height='32' Background='Transparent'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='0,0,0,1'/>
            </Border>
            <ControlTemplate.Triggers>
              <MultiTrigger>
                <MultiTrigger.Conditions>
                  <Condition Property='IsMouseOver' Value='True'/>
                  <Condition Property='IsChecked' Value='False'/>
                </MultiTrigger.Conditions>
                <Setter TargetName='Bd' Property='Background' Value='{DynamicResource HoverOverlay}'/>
              </MultiTrigger>
              <Trigger Property='IsChecked' Value='True'>
                <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key='ToggleRow' TargetType='CheckBox'>
      <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='CheckBox'>
            <Border x:Name='Row' CornerRadius='5' Background='Transparent' Padding='12,10'>
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition/>
                  <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <ContentPresenter VerticalAlignment='Center'/>
                <Grid Grid.Column='1' Width='40' Height='20' Margin='16,0,0,0' VerticalAlignment='Center'>
                  <Border x:Name='Track' CornerRadius='10' BorderThickness='1'
                          Background='{DynamicResource ToggleOffFill}' BorderBrush='{DynamicResource ToggleOffStroke}'/>
                  <Ellipse x:Name='Knob' Width='12' Height='12' Margin='4,0,0,0'
                           HorizontalAlignment='Left' VerticalAlignment='Center' Fill='{DynamicResource ToggleOffStroke}'/>
                </Grid>
              </Grid>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Row' Property='Background' Value='{DynamicResource HoverOverlay}'/>
                <Setter TargetName='Knob' Property='Width' Value='14'/>
                <Setter TargetName='Knob' Property='Height' Value='14'/>
              </Trigger>
              <Trigger Property='IsChecked' Value='True'>
                <Setter TargetName='Track' Property='Background' Value='{DynamicResource Accent}'/>
                <Setter TargetName='Track' Property='BorderBrush' Value='{DynamicResource Accent}'/>
                <Setter TargetName='Knob' Property='Fill' Value='{DynamicResource TextOnAccent}'/>
                <Trigger.EnterActions>
                  <BeginStoryboard>
                    <Storyboard>
                      <ThicknessAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='Margin'
                                          To='24,0,0,0' Duration='0:0:0.18'>
                        <ThicknessAnimation.EasingFunction><CubicEase EasingMode='EaseOut'/></ThicknessAnimation.EasingFunction>
                      </ThicknessAnimation>
                    </Storyboard>
                  </BeginStoryboard>
                </Trigger.EnterActions>
                <Trigger.ExitActions>
                  <BeginStoryboard>
                    <Storyboard>
                      <ThicknessAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='Margin'
                                          To='4,0,0,0' Duration='0:0:0.18'>
                        <ThicknessAnimation.EasingFunction><CubicEase EasingMode='EaseOut'/></ThicknessAnimation.EasingFunction>
                      </ThicknessAnimation>
                    </Storyboard>
                  </BeginStoryboard>
                </Trigger.ExitActions>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType='ToolTip'>
      <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
      <Setter Property='FontFamily' Value='Segoe UI, Microsoft JhengHei UI'/>
      <Setter Property='FontSize' Value='12'/>
      <Setter Property='HasDropShadow' Value='False'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ToolTip'>
            <Border Background='{DynamicResource TooltipFill}' BorderBrush='{DynamicResource ControlStroke}'
                    BorderThickness='1' CornerRadius='4' Padding='8,5'>
              <ContentPresenter/>
            </Border>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Grid.Resources>

  <Grid.RowDefinitions>
    <RowDefinition Height='Auto'/>
    <RowDefinition Height='Auto'/>
  </Grid.RowDefinitions>

  <StackPanel Margin='16,16,16,14'>
    <!-- 影片預覽 -->
    <Border x:Name='PreviewCard' Height='186' CornerRadius='8' BorderBrush='{DynamicResource CardStroke}' BorderThickness='1'>
      <Grid>
        <Grid x:Name='PreviewVideo'/>
        <StackPanel x:Name='PreviewEmpty' HorizontalAlignment='Center' VerticalAlignment='Center'>
          <TextBlock Text='&#xE714;' FontFamily='{StaticResource IconFont}' FontSize='32'
                     Foreground='{DynamicResource TextSecondary}' HorizontalAlignment='Center'/>
          <TextBlock Text='尚未選擇影片' Foreground='{DynamicResource TextSecondary}' Margin='0,10,0,0' HorizontalAlignment='Center'/>
        </StackPanel>
        <Border x:Name='Caption' VerticalAlignment='Bottom' CornerRadius='0,0,7,7' Padding='14,30,14,11'>
          <Border.Background>
            <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'>
              <GradientStop Color='#00000000' Offset='0'/>
              <GradientStop Color='#B4000000' Offset='1'/>
            </LinearGradientBrush>
          </Border.Background>
          <StackPanel>
            <TextBlock x:Name='FileName' Foreground='White' FontSize='14' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/>
            <StackPanel Orientation='Horizontal' Margin='0,4,0,0'>
              <Ellipse x:Name='StatusDot' Width='7' Height='7' VerticalAlignment='Center'/>
              <TextBlock x:Name='StatusText' Foreground='#DDFFFFFF' FontSize='12' Margin='7,0,0,0' VerticalAlignment='Center'/>
            </StackPanel>
          </StackPanel>
        </Border>
      </Grid>
    </Border>

    <!-- 播放控制 -->
    <Grid Margin='0,12,0,0'>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='Auto'/>
        <ColumnDefinition Width='Auto'/>
        <ColumnDefinition Width='Auto'/>
        <ColumnDefinition Width='*'/>
      </Grid.ColumnDefinitions>
      <Button x:Name='PlayButton' Style='{StaticResource AccentBtn}' Width='56' Padding='0'>
        <TextBlock x:Name='PlayIcon' FontFamily='{StaticResource IconFont}' FontSize='16'/>
      </Button>
      <Button x:Name='MuteButton' Grid.Column='1' Style='{StaticResource Btn}' Width='44' Padding='0' Margin='8,0,0,0'>
        <TextBlock x:Name='MuteIcon' FontFamily='{StaticResource IconFont}' FontSize='16'/>
      </Button>
      <Button x:Name='LockButton' Grid.Column='2' Style='{StaticResource Btn}' Width='44' Padding='0' Margin='8,0,0,0'
              ToolTip='鎖定畫面'>
        <TextBlock Text='&#xE72E;' FontFamily='{StaticResource IconFont}' FontSize='16'/>
      </Button>
      <Button x:Name='ChooseButton' Grid.Column='3' Style='{StaticResource Btn}' Margin='8,0,0,0'>
        <StackPanel Orientation='Horizontal'>
          <TextBlock Text='&#xE838;' FontFamily='{StaticResource IconFont}' FontSize='15' VerticalAlignment='Center'/>
          <TextBlock Text='選擇影片' Margin='8,0,0,0' VerticalAlignment='Center'/>
        </StackPanel>
      </Button>
    </Grid>

    <!-- 畫面縮放 -->
    <TextBlock Text='畫面縮放' FontWeight='SemiBold' Margin='2,18,0,8'/>
    <Border CornerRadius='7' Background='{DynamicResource SegmentTrack}' BorderBrush='{DynamicResource ControlStroke}'
            BorderThickness='1' Padding='3'>
      <Grid x:Name='SegmentHost'>
        <Border x:Name='SegmentThumb' HorizontalAlignment='Left' CornerRadius='4' IsHitTestVisible='False'
                Background='{DynamicResource SegmentSelected}' BorderBrush='{DynamicResource ControlStroke}' BorderThickness='1'>
          <Border.RenderTransform><TranslateTransform/></Border.RenderTransform>
          <Border x:Name='SegmentPill' Width='16' Height='3' CornerRadius='1.5' Background='{DynamicResource Accent}'
                  VerticalAlignment='Bottom' Margin='0,0,0,2' RenderTransformOrigin='0.5,0.5'>
            <Border.RenderTransform><ScaleTransform/></Border.RenderTransform>
          </Border>
        </Border>
        <!-- 三個按鈕之間的間隔（3）跟它們離外框的距離（外框的 Padding 3）一樣 -->
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width='*'/><ColumnDefinition Width='3'/>
            <ColumnDefinition Width='*'/><ColumnDefinition Width='3'/>
            <ColumnDefinition Width='*'/>
          </Grid.ColumnDefinitions>
          <RadioButton x:Name='StretchFill' Grid.Column='0' Style='{StaticResource Segment}' Content='填滿'/>
          <RadioButton x:Name='StretchUniform' Grid.Column='2' Style='{StaticResource Segment}' Content='完整顯示'/>
          <RadioButton x:Name='StretchStretch' Grid.Column='4' Style='{StaticResource Segment}' Content='拉伸'/>
        </Grid>
      </Grid>
    </Border>
    <TextBlock x:Name='StretchHint' FontSize='12' Foreground='{DynamicResource TextSecondary}' Margin='2,7,0,0'/>

    <!-- 選項 -->
    <TextBlock Text='選項' FontWeight='SemiBold' Margin='2,18,0,8'/>
    <Border CornerRadius='8' Background='{DynamicResource CardFill}' BorderBrush='{DynamicResource CardStroke}'
            BorderThickness='1' Padding='4'>
      <StackPanel>
        <CheckBox x:Name='AutoPauseToggle' Style='{StaticResource ToggleRow}'>
          <StackPanel>
            <TextBlock Text='自動暫停'/>
            <TextBlock Text='有視窗最大化或全螢幕時暫停，節省資源' FontSize='12'
                       Foreground='{DynamicResource TextSecondary}' Margin='0,2,0,0'/>
          </StackPanel>
        </CheckBox>
        <CheckBox x:Name='StartupToggle' Style='{StaticResource ToggleRow}' Margin='0,2,0,0'>
          <StackPanel>
            <TextBlock Text='開機時自動啟動'/>
            <TextBlock Text='登入 Windows 後自動播放動態桌布' FontSize='12'
                       Foreground='{DynamicResource TextSecondary}' Margin='0,2,0,0'/>
          </StackPanel>
        </CheckBox>
        <CheckBox x:Name='TaskbarToggle' Style='{StaticResource ToggleRow}' Margin='0,2,0,0'>
          <StackPanel>
            <TextBlock Text='工作列透明'/>
            <TextBlock x:Name='TaskbarHint' FontSize='12' TextWrapping='Wrap'
                       Foreground='{DynamicResource TextSecondary}' Margin='0,2,0,0'/>
          </StackPanel>
        </CheckBox>
      </StackPanel>
    </Border>
  </StackPanel>

  <!-- 底部列 -->
  <Border Grid.Row='1' Background='{DynamicResource FooterFill}' BorderBrush='{DynamicResource Divider}'
          BorderThickness='0,1,0,0' Padding='16,8,10,8'>
    <Grid>
      <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
        <Border Width='20' Height='15' CornerRadius='4'>
          <Border.Background>
            <LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
              <GradientStop Color='#3B82F6' Offset='0'/>
              <GradientStop Color='#8B5CF6' Offset='1'/>
            </LinearGradientBrush>
          </Border.Background>
          <Path Data='M0,0 L0,7 L6,3.5 Z' Fill='White' HorizontalAlignment='Center' VerticalAlignment='Center' Margin='1,0,0,0'/>
        </Border>
        <TextBlock Text='動態桌布' Margin='9,0,0,0' VerticalAlignment='Center' FontWeight='SemiBold'/>
      </StackPanel>
      <Button x:Name='QuitButton' Style='{StaticResource SubtleBtn}' HorizontalAlignment='Right' Height='32' Padding='10,0'
              ToolTip='關閉動態桌布，並還原原本的桌布'>
        <StackPanel Orientation='Horizontal'>
          <TextBlock Text='&#xE7E8;' FontFamily='{StaticResource IconFont}' FontSize='14' VerticalAlignment='Center'/>
          <TextBlock Text='結束' Margin='8,0,0,0' VerticalAlignment='Center'/>
        </StackPanel>
      </Button>
    </Grid>
  </Border>
</Grid>";

        readonly WallpaperApp app;
        readonly Window window;
        readonly Grid root;
        readonly bool backdrop = Environment.OSVersion.Version.Build >= 22000;   // Windows 11 才開毛玻璃（Win10 移動視窗時會卡）
        readonly Border previewCard, caption, segmentThumb, segmentPill;
        readonly Grid previewVideo, segmentHost;
        readonly MediaElement previewMedia;
        readonly FrameworkElement previewEmpty;
        readonly TextBlock fileName, statusText, playIcon, muteIcon, stretchHint, taskbarHint;
        readonly System.Windows.Shapes.Ellipse statusDot;
        readonly Button playButton, muteButton;
        readonly RadioButton stretchFill, stretchUniform, stretchStretch;
        readonly CheckBox autoPauseToggle, startupToggle, taskbarToggle;
        IntPtr hwnd;
        string previewPath;
        bool? previewPlaying;
        Stretch? shownStretch;
        double restingTop;
        DateTime lastHidden = DateTime.MinValue;
        bool open, dark, refreshing;

        public TrayPanel(WallpaperApp app)
        {
            this.app = app;
            root = (Grid)XamlReader.Parse(Localize(Xaml));
            previewCard = Find<Border>("PreviewCard");
            previewVideo = Find<Grid>("PreviewVideo");
            previewEmpty = Find<FrameworkElement>("PreviewEmpty");
            caption = Find<Border>("Caption");
            fileName = Find<TextBlock>("FileName");
            statusText = Find<TextBlock>("StatusText");
            statusDot = Find<System.Windows.Shapes.Ellipse>("StatusDot");
            playIcon = Find<TextBlock>("PlayIcon");
            muteIcon = Find<TextBlock>("MuteIcon");
            stretchHint = Find<TextBlock>("StretchHint");
            playButton = Find<Button>("PlayButton");
            muteButton = Find<Button>("MuteButton");
            stretchFill = Find<RadioButton>("StretchFill");
            stretchUniform = Find<RadioButton>("StretchUniform");
            stretchStretch = Find<RadioButton>("StretchStretch");
            autoPauseToggle = Find<CheckBox>("AutoPauseToggle");
            startupToggle = Find<CheckBox>("StartupToggle");
            taskbarToggle = Find<CheckBox>("TaskbarToggle");
            taskbarHint = Find<TextBlock>("TaskbarHint");
            segmentHost = Find<Grid>("SegmentHost");
            segmentThumb = Find<Border>("SegmentThumb");
            segmentPill = Find<Border>("SegmentPill");
            segmentHost.SizeChanged += delegate { MoveThumb(false); };

            playButton.Click += delegate { app.TogglePause(); };
            muteButton.Click += delegate { app.ToggleMute(); };
            Find<Button>("ChooseButton").Click += delegate { Hide(); app.ChooseVideo(); };
            Find<Button>("QuitButton").Click += delegate { Hide(); app.Quit(); };
            stretchFill.Checked += delegate { if (!refreshing) app.SetStretch(Stretch.UniformToFill); };
            stretchUniform.Checked += delegate { if (!refreshing) app.SetStretch(Stretch.Uniform); };
            stretchStretch.Checked += delegate { if (!refreshing) app.SetStretch(Stretch.Fill); };
            // 用 Checked / Unchecked（不管是滑鼠、鍵盤或輔助工具切換都會觸發），refreshing 期間是程式自己在更新畫面，不算
            RoutedEventHandler autoPauseChanged = delegate { if (!refreshing) app.SetAutoPause(autoPauseToggle.IsChecked == true); };
            autoPauseToggle.Checked += autoPauseChanged;
            autoPauseToggle.Unchecked += autoPauseChanged;
            RoutedEventHandler startupChanged = delegate { if (!refreshing) app.SetStartup(startupToggle.IsChecked == true); };
            startupToggle.Checked += startupChanged;
            startupToggle.Unchecked += startupChanged;
            RoutedEventHandler taskbarChanged = delegate { if (!refreshing) app.SetTaskbarFix(taskbarToggle.IsChecked == true); };
            taskbarToggle.Checked += taskbarChanged;
            taskbarToggle.Unchecked += taskbarChanged;

            // 鎖定畫面：先開（面板這時還在前景，鎖定畫面才拿得到鍵盤），再把面板直接收掉（不播動畫，反正會被蓋住）
            Find<Button>("LockButton").Click += delegate { app.ShowLockScreen(); HideNow(); };
            Find<Button>("LockButton").ToolTip = Lang.T("鎖定畫面（" + WallpaperApp.LockHotKeyText + "）", "Lock screen (" + WallpaperApp.LockHotKeyText + ")");

            // 預覽用自己的小播放器（靜音），只在面板開著時播放
            // ScrubbingEnabled：暫停中跳到某個位置時也要畫出那一格（不開的話暫停時預覽是黑的）
            previewMedia = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, IsMuted = true, ScrubbingEnabled = true };
            previewMedia.MediaOpened += delegate
            {
                previewMedia.Position = app.PlaybackPosition;   // 從桌布目前的位置開始，跟桌面上看到的同一格
                if (previewPlaying == true) previewMedia.Play(); else previewMedia.Pause();
            };
            previewMedia.MediaEnded += delegate { previewMedia.Position = TimeSpan.Zero; previewMedia.Play(); };
            previewVideo.Children.Add(previewMedia);
            previewVideo.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                previewVideo.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
            };

            window = new Window
            {
                Title = Lang.AppName,
                Content = root,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true,
                Background = Brushes.Transparent,
            };
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(backdrop ? -1 : 0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
            window.SourceInitialized += delegate
            {
                var source = (HwndSource)PresentationSource.FromVisual(window);
                hwnd = source.Handle;
                if (backdrop) source.CompositionTarget.BackgroundColor = Colors.Transparent;
                ApplyWindowTheme();
            };
            window.Deactivated += delegate { Hide(); };
            window.PreviewKeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Hide(); };
            // Alt+F4 之類的關閉要求：只收起來。視窗真的關掉的話就再也打不開了（之後按系統匣圖示會出錯）
            window.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); };
        }

        T Find<T>(string name) where T : class
        {
            return root.FindName(name) as T;
        }

        public bool IsOpen { get { return open; } }

        public void Toggle()
        {
            if (open) { Hide(); return; }
            // 面板開著時點系統匣圖示，會先因失去焦點而關閉，這時不要又馬上打開
            if ((DateTime.Now - lastHidden).TotalMilliseconds < 400) return;
            Show();
        }

        public void Show()
        {
            dark = Theme.IsDark();
            Theme.Apply(root.Resources, dark);
            if (!backdrop) window.Background = (Brush)root.Resources["WindowFallback"];
            ApplyWindowTheme();
            open = true;
            StartupEntry.IsEnabled(true);   // 打開面板時重新查一次開機自動啟動（例如在工作排程器裡被改過）
            Refresh();

            // 放在工作列旁邊（右下角，跟 Windows 11 的快速設定一樣）
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = root.DesiredSize;
            var area = SystemParameters.WorkArea;
            const double margin = 12;
            double left = area.Right - size.Width - margin, top = area.Bottom - size.Height - margin;
            if (area.Top > 0) top = area.Top + margin;     // 工作列在上方
            if (area.Left > 0) left = area.Left + margin;  // 工作列在左方
            restingTop = top;

            // 整個面板由下往上滑入、同時淡入（如果正在關閉的動畫中被重新打開，就從目前位置接著滑回來）
            if (!window.IsVisible)
            {
                window.BeginAnimation(Window.TopProperty, null);
                root.BeginAnimation(UIElement.OpacityProperty, null);
                window.Left = left;
                window.Top = top + SlideDistance;
                root.Opacity = 0;
                window.Show();
            }
            window.Activate();
            Animate(restingTop, 1, 260, EasingMode.EaseOut, null);
        }

        const double SlideDistance = 20;

        public void Hide()
        {
            if (!open) return;
            open = false;
            lastHidden = DateTime.Now;

            // 跟打開時相反：往下滑出、同時淡出，動畫結束才真正隱藏
            Animate(restingTop + SlideDistance, 0, 170, EasingMode.EaseIn, delegate
            {
                if (open) return;   // 動畫途中又被打開了
                window.Hide();
                UpdatePreview();
            });
        }

        void Animate(double top, double opacity, int ms, EasingMode easing, Action done)
        {
            var duration = TimeSpan.FromMilliseconds(ms);
            var move = new DoubleAnimation(top, duration) { EasingFunction = new CubicEase { EasingMode = easing } };
            var fade = new DoubleAnimation(opacity, duration) { EasingFunction = new CubicEase { EasingMode = easing } };
            if (done != null) move.Completed += delegate { done(); };
            window.BeginAnimation(Window.TopProperty, move);
            root.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // 畫面縮放的選取框：切換時滑到新的位置，底下的小橫條滑動中會先拉長再縮回
        void MoveThumb(bool animate)
        {
            const double gap = 3;   // 按鈕之間的間隔（跟 XAML 裡的一樣）
            double width = (segmentHost.ActualWidth - 2 * gap) / 3;
            if (width <= 0) return;
            segmentThumb.Width = width;   // 選取框剛好跟一個按鈕一樣大
            int index = app.Stretch == Stretch.Uniform ? 1 : app.Stretch == Stretch.Fill ? 2 : 0;
            double x = index * (width + gap);
            var shift = (TranslateTransform)segmentThumb.RenderTransform;

            if (!animate)
            {
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.X = x;
                return;
            }

            var duration = TimeSpan.FromMilliseconds(320);
            shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });

            var stretchPill = new DoubleAnimationUsingKeyFrames { Duration = duration };
            stretchPill.KeyFrames.Add(new EasingDoubleKeyFrame(2.4, KeyTime.FromPercent(0.45), new CubicEase { EasingMode = EasingMode.EaseOut }));
            stretchPill.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            ((ScaleTransform)segmentPill.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, stretchPill);
        }

        // 不播動畫，直接收起來
        public void HideNow()
        {
            if (!open) return;
            open = false;
            window.BeginAnimation(Window.TopProperty, null);
            root.BeginAnimation(UIElement.OpacityProperty, null);
            window.Hide();
            UpdatePreview();
            lastHidden = DateTime.Now;
        }

        void UpdatePreview()
        {
            if (!open || !app.HasVideo)
            {
                if (previewPath != null) { previewMedia.Close(); previewMedia.Source = null; previewPath = null; }
                return;
            }
            if (previewPath != app.VideoPath)
            {
                previewPath = app.VideoPath;
                previewPlaying = null;
                previewMedia.Source = new Uri(previewPath);
            }
            previewMedia.Stretch = app.Stretch;
            bool play = !app.UserPaused;
            if (previewPlaying == play) return;
            bool alreadyLoaded = previewPlaying.HasValue;
            previewPlaying = play;
            if (play) previewMedia.Play();
            else
            {
                previewMedia.Pause();
                if (alreadyLoaded) previewMedia.Position = app.PlaybackPosition;   // 對齊桌布停住的那一格
            }
        }

        void ApplyWindowTheme()
        {
            if (hwnd == IntPtr.Zero) return;
            Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
            Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2);   // 圓角
            Native.SetDwm(hwnd, Native.DWMWA_TRANSITIONS_FORCEDISABLED, 1);  // 用自己的滑入動畫
            if (!backdrop) return;
            // 系統內建的 Acrylic 疊色很厚、視窗沒在前景時還會變純色，
            // 改用可以自訂透明度的 Acrylic 模糊，毛玻璃效果明顯很多；失敗時才退回系統內建的
            Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 1);
            if (!Native.SetAcrylic(hwnd, dark ? AcrylicTintDark : AcrylicTintLight))
                Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 3);
        }

        // 毛玻璃的疊色（AABBGGRR）：最前面兩位是不透明度，數字越小越透
        const uint AcrylicTintDark = 0x60202020;
        const uint AcrylicTintLight = 0x70F3F3F3;

        public void Refresh()
        {
            refreshing = true;
            try
            {
                bool has = app.HasVideo;
                previewEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
                caption.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                previewCard.Background = has ? Brushes.Black : (Brush)root.Resources["CardFill"];
                fileName.Text = has ? Path.GetFileNameWithoutExtension(app.VideoPath) : "";

                UpdatePreview();

                string status; Color dot;
                if (app.LastError != null) { status = Lang.T("無法播放這個影片", "Can't play this video"); dot = Color.FromRgb(0xFF, 0x6B, 0x6B); }
                else if (app.UserPaused) { status = Lang.T("已暫停", "Paused"); dot = Color.FromRgb(0xA0, 0xA0, 0xA0); }
                else if (app.AutoPaused) { status = Lang.T("自動暫停中・有視窗遮住桌面", "Auto-paused · A window covers the desktop"); dot = Color.FromRgb(0xFF, 0xB9, 0x00); }
                else { status = Lang.T("播放中", "Playing"); dot = Color.FromRgb(0x6C, 0xCB, 0x5F); }
                statusText.Text = status;
                statusDot.Fill = new SolidColorBrush(dot);

                playIcon.Text = app.UserPaused ? "" : "";
                playButton.ToolTip = app.UserPaused ? Lang.T("播放", "Play") : Lang.T("暫停", "Pause");
                muteIcon.Text = app.Muted ? "" : "";
                muteButton.ToolTip = app.Muted ? Lang.T("取消靜音", "Unmute") : Lang.T("靜音", "Mute");
                playButton.IsEnabled = has;
                muteButton.IsEnabled = has;

                stretchFill.IsChecked = app.Stretch == Stretch.UniformToFill;
                stretchUniform.IsChecked = app.Stretch == Stretch.Uniform;
                stretchStretch.IsChecked = app.Stretch == Stretch.Fill;
                MoveThumb(open && window.IsVisible && shownStretch.HasValue && shownStretch.Value != app.Stretch);
                shownStretch = app.Stretch;
                stretchHint.Text = app.Stretch == Stretch.Uniform ? Lang.T("完整顯示整個畫面，比例不同時會有黑邊", "Shows the whole video; may add black bars")
                                 : app.Stretch == Stretch.Fill ? Lang.T("拉伸到跟螢幕一樣大，比例可能變形", "Stretches to the screen; may look distorted")
                                 : Lang.T("填滿整個螢幕，比例不同時會裁掉邊緣", "Fills the screen; may crop the edges");

                autoPauseToggle.IsChecked = app.AutoPause;
                startupToggle.IsChecked = StartupEntry.IsEnabled();
                taskbarToggle.IsChecked = app.TaskbarFixEnabled;
                taskbarHint.Text = TaskbarFix.TranslucentTBRunning
                    ? Lang.T("搭配 TranslucentTB：開機時提早啟動它，工作列變黑時自動修正", "Works with TranslucentTB: starts it early and fixes a black taskbar")
                    : Lang.T("需要開著 TranslucentTB 才有作用（Microsoft Store 免費下載）", "Requires TranslucentTB to be running (free on the Microsoft Store)");
            }
            finally { refreshing = false; }
        }
    }
}
