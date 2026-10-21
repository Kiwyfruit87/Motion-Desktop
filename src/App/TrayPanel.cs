using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VideoWallpaper
{
    // 點系統匣圖示後彈出的控制面板
    partial class TrayPanel
    {
        // 面板上的中文（XAML 裡整個屬性值）在非中文 Windows 換成英文
        static readonly string[,] XamlEnglish =
        {
            { "尚未選擇影片或圖片", "No video or image selected" },
            { "選擇影片或圖片", "Choose a video or image" },
            { "畫面縮放", "Scaling" },
            { "填滿", "Fill" },
            { "完整顯示", "Fit" },
            { "拉伸", "Stretch" },
            { "選項", "Options" },
            { "自動暫停", "Auto-pause" },
            { "有視窗最大化或全螢幕時暫停，節省資源", "Pause when windows cover the desktop" },
            { "開機時自動啟動", "Start with Windows" },
            { "登入 Windows 後自動播放動態桌布", "Start playing when you sign in" },
            { "節奏跳動", "Beat glow" },
            { "鎖定畫面的音樂卡片跟著大鼓發光", "The lock screen's music card glows with the beat" },
            { "工作列透明", "Transparent taskbar" },
            { "關閉動態桌布，並還原原本的桌布", "Close Motion Desktop and restore your original wallpaper" },
        };

        // 膠囊形（Tag='Pill'）：圓角永遠是高度的一半（CornerRadius 直接給很大的數字，寬的時候兩頭會變成橢圓）。
        // 大小變了的事件不會往上傳，只能掛在 Border 這個類別上
        static TrayPanel()
        {
            EventManager.RegisterClassHandler(typeof(Border), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(delegate(object s, SizeChangedEventArgs e)
            {
                var pill = (Border)s;
                if (pill.Tag as string == "Pill") pill.CornerRadius = new CornerRadius(pill.ActualHeight / 2);
            }));
        }

        static string Localize(string xaml)
        {
            if (Lang.Chinese) return xaml;
            for (int i = 0; i < XamlEnglish.GetLength(0); i++)
                xaml = xaml.Replace("'" + XamlEnglish[i, 0] + "'", "'" + System.Security.SecurityElement.Escape(XamlEnglish[i, 1]) + "'");   // 只換整個屬性值；英文裡的 ' & < 先跳脫，XAML 才不會壞掉
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

    <!-- 面板上按鈕的共同基底（工具列的 BarBtn、面板裡小按鈕的 InnerBtn 都從這裡來）：膠囊形；按下的動畫在程式裡（TrayPanel.Glass） -->
    <Style x:Key='Btn' TargetType='Button'>
      <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
      <Setter Property='Background' Value='{DynamicResource ControlFill}'/>
      <Setter Property='BorderBrush' Value='{DynamicResource ControlStroke}'/>
      <Setter Property='Tag' Value='{DynamicResource HoverOverlay}'/>
      <Setter Property='BorderThickness' Value='1'/>
      <Setter Property='Padding' Value='14,0'/>
      <Setter Property='Height' Value='44'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Grid x:Name='Body'>
              <Border x:Name='Bd' CornerRadius='99' Tag='Pill' Background='{TemplateBinding Background}'
                      BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'/>
              <Border x:Name='Hover' CornerRadius='99' Tag='Pill' Opacity='0'
                      Background='{Binding Tag, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Border x:Name='Glow' CornerRadius='99' Tag='Pill' Opacity='0'/>
              <ContentPresenter x:Name='Cp' Margin='{TemplateBinding Padding}'
                                HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Hover' Property='Opacity' Value='1'/>
              </Trigger>
              <Trigger Property='IsEnabled' Value='False'>
                <Setter TargetName='Cp' Property='Opacity' Value='0.4'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- 工具列膠囊裡的一格（只有圖示）：按下時整組長大一圈（GlassGroup） -->
    <Style x:Key='BarBtn' TargetType='Button' BasedOn='{StaticResource Btn}'>
      <Setter Property='Background' Value='Transparent'/>
      <Setter Property='BorderThickness' Value='0'/>
      <Setter Property='Width' Value='48'/>
      <Setter Property='Height' Value='42'/>
      <Setter Property='Padding' Value='0'/>
    </Style>

    <!-- 面板裡的小按鈕（＋、−）：本身不是一塊玻璃，按下時整顆帶彈性長大、白光亮在它上面（跟工具列一樣；程式的彈簧，見 TrayPanel.Glass） -->
    <Style x:Key='InnerBtn' TargetType='Button' BasedOn='{StaticResource Btn}'>
      <Setter Property='Background' Value='{DynamicResource InnerFill}'/>
      <Setter Property='BorderThickness' Value='0'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Grid x:Name='Body'>
              <Border CornerRadius='99' Tag='Pill' Background='{TemplateBinding Background}'/>
              <Border x:Name='Hover' CornerRadius='99' Tag='Pill' Opacity='0'
                      Background='{Binding Tag, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Border x:Name='Glow' CornerRadius='99' Tag='Pill' Opacity='0'/>
              <ContentPresenter x:Name='Cp' Margin='{TemplateBinding Padding}'
                                HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Hover' Property='Opacity' Value='1'/>
              </Trigger>
              <Trigger Property='IsEnabled' Value='False'>
                <Setter TargetName='Body' Property='Opacity' Value='0.4'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key='Segment' TargetType='RadioButton'>
      <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
      <!-- 字用比較平滑的畫法（不貼齊像素）：被透鏡放大時邊緣才不會一格一格的 -->
      <Setter Property='TextOptions.TextFormattingMode' Value='Ideal'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='RadioButton'>
            <!-- 選取的玻璃水滴由 GlassDrop 負責（切換時像液體一樣滑過去） -->
            <Grid Height='32' Background='Transparent'>
              <Border x:Name='Glow' CornerRadius='99' Tag='Pill' Opacity='0'/>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='0,0,0,1'/>
            </Grid>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- 開關（iOS 26 的寬開關）：旋鈕的位置、大小、變成透鏡都由 GlassSwitch 的物理控制 -->
    <Style x:Key='ToggleRow' TargetType='CheckBox'>
      <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='CheckBox'>
            <Grid Background='Transparent'>
              <Border x:Name='Row' CornerRadius='18' Background='Transparent'/>
              <Border x:Name='Glow' CornerRadius='18' Opacity='0'/>
              <Grid Margin='12,10,2,10'>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition/>
                  <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <ContentPresenter VerticalAlignment='Center'/>
                <Grid x:Name='Track' Grid.Column='1' Width='63' Height='28' Margin='14,0,0,0' VerticalAlignment='Center'>
                  <Border CornerRadius='14' Background='{DynamicResource ToggleOffFill}'/>
                  <Border x:Name='TrackOn' CornerRadius='14' Background='{DynamicResource ToggleOn}' Opacity='0'/>
                  <!-- 旋鈕放在 Canvas 裡：變成透鏡時比軌道高，放在 Grid 裡會被裁掉 -->
                  <Canvas>
                    <Grid x:Name='Knob' Width='37' Height='24'>
                      <Border x:Name='KnobSolid' CornerRadius='12' Background='White'>
                        <Border.Effect><DropShadowEffect BlurRadius='6' ShadowDepth='1' Direction='270' Opacity='0.25'/></Border.Effect>
                      </Border>
                      <Border x:Name='KnobLens' CornerRadius='12' Opacity='0' Background='{DynamicResource KnobLens}'
                              BorderBrush='{DynamicResource KnobRim}' BorderThickness='1'/>
                    </Grid>
                  </Canvas>
                </Grid>
              </Grid>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Row' Property='Background' Value='{DynamicResource HoverOverlay}'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- 滑鼠停在按鈕上的說明：同樣的底色和亮邊、膠囊形、柔和的影子；四周留空間給影子 -->
    <Style TargetType='ToolTip'>
      <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>
      <Setter Property='FontFamily' Value='Segoe UI, Microsoft JhengHei UI'/>
      <Setter Property='FontSize' Value='12'/>
      <Setter Property='HasDropShadow' Value='False'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ToolTip'>
            <!-- 底色、亮邊和影子一層，字在上面另一層（效果加在有字的那層上會連字一起糊掉） -->
            <Grid Margin='20,12,20,24'>
              <Border Background='{DynamicResource TooltipFill}' BorderBrush='{DynamicResource GlassRim}' BorderThickness='1' CornerRadius='15'>
                <Border.Effect>
                  <DropShadowEffect BlurRadius='18' ShadowDepth='4' Direction='270' Opacity='0.3'/>
                </Border.Effect>
              </Border>
              <Border Padding='13,6,13,7'>
                <ContentPresenter/>
              </Border>
            </Grid>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Grid.Resources>


  <!-- 整個面板是一整塊液態玻璃（PanelGlass，Uid='Glass:fixed'：不會被拖），裡面的分區只用淡淡的底色（官方：玻璃不要疊玻璃）；
       工具列的三塊、開關和分段控制按住時浮起的透鏡是第二層玻璃。圓角同心：面板 36 = 分區 24 + 邊距 12；分區 24 = 分段 19 + 邊距 5。
       沒有液態玻璃的電腦：面板是整片毛玻璃（PanelRim 是它的亮邊） -->
  <Border x:Name='PanelGlass' Uid='Glass:fixed' CornerRadius='36' Background='Transparent'/>

  <StackPanel x:Name='Stack' Margin='12'>
    <!-- 桌布預覽：圓角的影片或圖片（跟分區一樣的圓角） -->
    <Border x:Name='PreviewCard' Height='184' CornerRadius='24'>
      <Grid>
        <Grid x:Name='PreviewVideo'/>
        <StackPanel x:Name='PreviewEmpty' HorizontalAlignment='Center' VerticalAlignment='Center'>
          <TextBlock Text='&#xE714;' FontFamily='{StaticResource IconFont}' FontSize='32'
                     Foreground='{DynamicResource TextSecondary}' HorizontalAlignment='Center'/>
          <TextBlock Text='尚未選擇影片或圖片' Foreground='{DynamicResource TextSecondary}' Margin='0,10,0,0' HorizontalAlignment='Center'/>
        </StackPanel>
        <Border x:Name='Caption' VerticalAlignment='Bottom' CornerRadius='0,0,24,24' Padding='16,30,16,13'>
          <Border.Background>
            <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'>
              <GradientStop Color='#00000000' Offset='0'/>
              <GradientStop Color='#A0000000' Offset='1'/>
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

    <!-- 播放控制：照 Apple 工具列的分組——功能相近的放在同一個膠囊（播放、靜音一組；鎖定畫面一組；選擇影片、結束一組），
         三組靠左、正中間、靠右。每一組是一塊浮在面板上的清澈玻璃（第二層的透鏡，邊緣會扭曲底下的東西）；
         按下某一格時整組長大一圈（GlassGroup），白光從手指下面擴散到那一組 -->
    <Grid Margin='0,8,0,0'>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='*'/>
        <ColumnDefinition Width='Auto'/>
        <ColumnDefinition Width='*'/>
      </Grid.ColumnDefinitions>
      <Grid Tag='Group' Height='44' HorizontalAlignment='Left'>
        <Canvas IsHitTestVisible='False'><Border CornerRadius='22' Background='{DynamicResource BarFill}'/></Canvas>
        <StackPanel Orientation='Horizontal'>
          <Button x:Name='PlayButton' Uid='Bar' Style='{StaticResource BarBtn}'>
            <TextBlock x:Name='PlayIcon' FontFamily='{StaticResource IconFont}' FontSize='16'/>
          </Button>
          <Button x:Name='MuteButton' Uid='Bar' Style='{StaticResource BarBtn}'>
            <TextBlock x:Name='MuteIcon' FontFamily='{StaticResource IconFont}' FontSize='16'/>
          </Button>
        </StackPanel>
      </Grid>
      <Grid Grid.Column='1' Tag='Group' Height='44'>
        <Canvas IsHitTestVisible='False'><Border CornerRadius='22' Background='{DynamicResource BarFill}'/></Canvas>
        <StackPanel x:Name='ToolRow' Orientation='Horizontal'>
          <Button x:Name='LockButton' Uid='Bar' Style='{StaticResource BarBtn}'>
            <TextBlock Text='&#xE72E;' FontFamily='{StaticResource IconFont}' FontSize='16'/>
          </Button>
        </StackPanel>
      </Grid>
      <Grid Grid.Column='2' Tag='Group' Height='44' HorizontalAlignment='Right'>
        <Canvas IsHitTestVisible='False'><Border CornerRadius='22' Background='{DynamicResource BarFill}'/></Canvas>
        <StackPanel Orientation='Horizontal'>
          <Button x:Name='ChooseButton' Uid='Bar' Style='{StaticResource BarBtn}' ToolTip='選擇影片或圖片'>
            <TextBlock Text='&#xE838;' FontFamily='{StaticResource IconFont}' FontSize='16'/>
          </Button>
          <Button x:Name='QuitButton' Uid='Bar' Style='{StaticResource BarBtn}' ToolTip='關閉動態桌布，並還原原本的桌布'>
            <TextBlock Text='&#xE7E8;' FontFamily='{StaticResource IconFont}' FontSize='16'/>
          </Button>
        </StackPanel>
      </Grid>
    </Grid>

    <!-- 畫面縮放（分區圓角 24 = 分段的圓角 19 + 邊距 5） -->
    <Border CornerRadius='24' Background='{DynamicResource SectionFill}' Padding='5' Margin='0,8,0,0'>
      <StackPanel>
        <TextBlock Text='畫面縮放' FontWeight='SemiBold' Margin='13,9,0,8'/>
        <Grid>
         <!-- 軌道（按住選取時整個變寬 10、變亮一點；上面的字、選取不跟著拉） -->
         <Grid x:Name='SegmentBack'>
          <Border CornerRadius='99' Tag='Pill' Background='{DynamicResource SegmentTrack}'/>
          <Border x:Name='SegmentShine' CornerRadius='99' Tag='Pill' Background='White' Opacity='0'/>
         </Grid>
         <Border Padding='3'>
          <Grid x:Name='SegmentHost'>
            <!-- 選取的實心膠囊（畫在字的下面）：位置、大小由 GlassDrop 的物理控制；放在 Canvas 裡才不會撐大版面 -->
            <Canvas IsHitTestVisible='False'>
              <Grid x:Name='SegmentThumb'>
                <Border x:Name='SegmentSolid' CornerRadius='99' Tag='Pill' Background='{DynamicResource SegmentSelected}'>
                  <Border.Effect><DropShadowEffect BlurRadius='8' ShadowDepth='1' Direction='270' Opacity='0.18'/></Border.Effect>
                </Border>
                <Border CornerRadius='99' Tag='Pill' BorderBrush='{DynamicResource KnobRim}' BorderThickness='1'/>
              </Grid>
            </Canvas>
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
        </Grid>
        <TextBlock x:Name='StretchHint' FontSize='12' Foreground='{DynamicResource TextSecondary}' Margin='13,8,13,8' TextWrapping='Wrap'/>
      </StackPanel>
    </Border>

    <!-- 選項（分區圓角 24 = 每一列的圓角 18 + 邊距 6） -->
    <Border CornerRadius='24' Background='{DynamicResource SectionFill}' Padding='6' Margin='0,8,0,0'>
      <StackPanel>
        <TextBlock Text='選項' FontWeight='SemiBold' Margin='12,8,0,2'/>
        <CheckBox x:Name='AutoPauseToggle' Style='{StaticResource ToggleRow}'>
          <StackPanel>
            <TextBlock Text='自動暫停'/>
            <TextBlock Text='有視窗最大化或全螢幕時暫停，節省資源' FontSize='12'
                       Foreground='{DynamicResource TextSecondary}' Margin='0,2,0,0'/>
          </StackPanel>
        </CheckBox>
        <CheckBox x:Name='StartupToggle' Style='{StaticResource ToggleRow}'>
          <StackPanel>
            <TextBlock Text='開機時自動啟動'/>
            <TextBlock Text='登入 Windows 後自動播放動態桌布' FontSize='12'
                       Foreground='{DynamicResource TextSecondary}' Margin='0,2,0,0'/>
          </StackPanel>
        </CheckBox>
        <CheckBox x:Name='BeatToggle' Style='{StaticResource ToggleRow}'>
          <StackPanel>
            <TextBlock Text='節奏跳動'/>
            <TextBlock Text='鎖定畫面的音樂卡片跟著大鼓發光' FontSize='12'
                       Foreground='{DynamicResource TextSecondary}' Margin='0,2,0,0'/>
          </StackPanel>
        </CheckBox>
        <CheckBox x:Name='TaskbarToggle' Style='{StaticResource ToggleRow}'>
          <StackPanel>
            <TextBlock Text='工作列透明'/>
            <TextBlock x:Name='TaskbarHint' FontSize='12' TextWrapping='Wrap'
                       Foreground='{DynamicResource TextSecondary}' Margin='0,2,0,0'/>
          </StackPanel>
        </CheckBox>
      </StackPanel>
    </Border>
  </StackPanel>

  <!-- 退回整片毛玻璃時：整個面板外圍的玻璃亮邊（左上、右下比較亮） -->
  <Border x:Name='PanelRim' CornerRadius='36' BorderBrush='{DynamicResource PanelRim}' BorderThickness='1' IsHitTestVisible='False'/>
</Grid>";

        readonly WallpaperApp app;
        readonly Window window;
        readonly Grid root;
        readonly Grid content = new Grid(), outer = new Grid();   // content：面板的內容（第一層玻璃畫在它最底下）；outer：再疊上第二層玻璃
        readonly bool backdrop = Environment.OSVersion.Version.Build >= 22000;   // 沒有液態玻璃時：Windows 11 才開毛玻璃（Win10 移動視窗時會卡）
        readonly Border previewCard, caption;
        readonly Grid previewVideo;
        readonly MediaElement previewMedia;
        readonly Image previewImage;
        string previewImagePath;   // 預覽圖片現在載入的是哪一張
        readonly FrameworkElement previewEmpty;
        readonly TextBlock fileName, statusText, playIcon, muteIcon, stretchHint, taskbarHint;
        readonly System.Windows.Shapes.Ellipse statusDot;
        readonly Button playButton, muteButton;
        readonly RadioButton stretchFill, stretchUniform, stretchStretch;
        readonly CheckBox autoPauseToggle, startupToggle, taskbarToggle, beatToggle;
        IntPtr hwnd;
        string previewPath;
        bool? previewPlaying;
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
            beatToggle = Find<CheckBox>("BeatToggle");
            taskbarHint = Find<TextBlock>("TaskbarHint");

            playButton.Click += delegate { app.TogglePause(); };
            muteButton.Click += delegate { app.ToggleMute(); };
            Find<Button>("ChooseButton").Click += delegate { Hide(); app.ChooseVideo(); };
            Find<Button>("QuitButton").Click += delegate
            {
                bool cancel = false;
                BeforeQuit(ref cancel);   // 其他檔案可以先問一下（取消的話由它接手）
                if (cancel) return;
                HideAndQuit();
            };
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
            RoutedEventHandler beatChanged = delegate { if (!refreshing) app.SetBeatGlow(beatToggle.IsChecked == true); };
            beatToggle.Checked += beatChanged;
            beatToggle.Unchecked += beatChanged;

            // 鎖定畫面：先開（面板這時還在前景，鎖定畫面才拿得到鍵盤），再把面板直接收掉（不播動畫，反正會被蓋住）
            Find<Button>("LockButton").Click += delegate { app.ShowLockScreen(); HideNow(); };
            Find<Button>("LockButton").ToolTip = Lang.T("鎖定畫面（" + WallpaperApp.LockHotKeyText + "）", "Lock screen (" + WallpaperApp.LockHotKeyText + ")");

            // 預覽用自己的小播放器（靜音），停在桌布現在的那一格（不播）
            // ScrubbingEnabled：暫停中跳到某個位置時也要畫出那一格（不開的話暫停時預覽是黑的）
            previewMedia = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, IsMuted = true, ScrubbingEnabled = true };
            previewMedia.MediaOpened += delegate
            {
                previewMedia.Position = app.PlaybackPosition;   // 跟桌面上看到的同一格
                previewMedia.Pause();
            };
            previewVideo.Children.Add(previewMedia);
            previewImage = new Image { Visibility = Visibility.Collapsed };   // 桌布是圖片時的預覽
            RenderOptions.SetBitmapScalingMode(previewImage, BitmapScalingMode.HighQuality);
            previewVideo.Children.Add(previewImage);
            previewVideo.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                previewVideo.Clip = new RectangleGeometry(new Rect(e.NewSize), 24, 24);   // 跟預覽卡片的圓角一樣
            };

            window = new Window
            {
                Title = Lang.AppName,
                Content = outer,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true,
                Background = Brushes.Transparent,
                AllowsTransparency = true,
            };
            content.Children.Add(root);
            outer.Children.Add(content);
            window.SourceInitialized += delegate
            {
                var source = (HwndSource)PresentationSource.FromVisual(window);
                hwnd = source.Handle;
                // 液態玻璃要自己抓面板後面的畫面：面板得設成不會被抓到，做不到就不用
                if (glass != null && !GlassBackdrop.ExcludeFromCapture(hwnd)) DropGlass();
                if (glass != null) glass.Attach(hwnd);
                // 滑鼠停在按鈕上跳出的說明是另一個小視窗：也設成不會被抓到，不然玻璃抓後面的畫面時會把它抓進去，背景多一個重影
                if (glass != null)
                    EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs e)
                    {
                        var tip = PresentationSource.FromVisual((Visual)sender) as HwndSource;
                        if (tip != null) GlassBackdrop.ExcludeFromCapture(tip.Handle);
                    }));
                ApplyWindowTheme();
            };
            // 沒有液態玻璃時切大圓角：大小變了就重切
            window.SizeChanged += delegate { if (glass == null) LaterApplyShape(); };
            window.Deactivated += delegate { FadeGlow(); Let(false); Hide(); };
            window.PreviewKeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Hide(); };
            // Alt+F4 之類的關閉要求：只收起來。視窗真的關掉的話就再也打不開了（之後按系統匣圖示會出錯）
            window.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); };
            AddPressGlow();
            AddExtras();
            SetupMotion();
        }

        // 沒有液態玻璃時，整片毛玻璃面板的大圓角（跟 XAML 外圍亮邊的 CornerRadius 一樣）。Windows 自己的圓角只有 8，用視窗區域切成更圓的
        const double PanelRadius = 36;

        void LaterApplyShape()
        {
            window.Dispatcher.BeginInvoke(new Action(ApplyShape), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        void ApplyShape()
        {
            Native.RECT r;
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out r)) return;
            var source = PresentationSource.FromVisual(window);
            double dpi = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
            int d = (int)Math.Round(PanelRadius * 2 * dpi);
            IntPtr region = Native.CreateRoundRectRgn(0, 0, r.Right - r.Left + 1, r.Bottom - r.Top + 1, d, d);
            if (Native.SetWindowRgn(hwnd, region, true) == 0) Native.DeleteObject(region);   // 成功的話區域歸 Windows 管，不用刪
        }

        // 按下按鈕、開關、分段按鈕：白光從手指下面亮起來、往整顆擴散，放開才慢慢退（跟鎖定畫面的音樂卡片一樣）。
        // 白光畫在各個樣板裡叫 Glow 的那一層
        Border glowing;

        void AddPressGlow()
        {
            root.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(delegate(object s, MouseButtonEventArgs e)
            {
                if (glass != null) return;   // 液態玻璃自己會畫（著色器裡的白光）
                var glow = GlowOf(e.OriginalSource as DependencyObject);
                if (glow == null) return;
                FadeGlow();
                var p = e.GetPosition(glow);
                double reach = Math.Sqrt(glow.ActualWidth * glow.ActualWidth + glow.ActualHeight * glow.ActualHeight);
                var brush = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = p, GradientOrigin = p, RadiusX = 6, RadiusY = 6 };
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x5C, 255, 255, 255), 0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1F, 255, 255, 255), 0.6));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
                var grow = new DoubleAnimation(Math.Max(reach, 6), TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                brush.BeginAnimation(RadialGradientBrush.RadiusXProperty, grow);
                brush.BeginAnimation(RadialGradientBrush.RadiusYProperty, grow);
                glow.Background = brush;
                glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
                glowing = glow;
            }), true);
            root.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(delegate { FadeGlow(); }), true);
        }

        // 滑鼠點到的東西屬於哪個有 Glow 的控制項（停用的不亮）
        Border GlowOf(DependencyObject node)
        {
            while (node != null && node != root)
            {
                var control = node as Control;
                if (control != null && control.Template != null)
                {
                    var glow = control.Template.FindName("Glow", control) as Border;
                    if (glow != null) return control.IsEnabled ? glow : null;
                }
                node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            return null;
        }

        void FadeGlow()
        {
            if (glowing == null) return;
            glowing.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            glowing = null;
        }

        // 其他檔案可以在面板建好之後加東西（沒有實作就什麼都不做）
        partial void AddExtras();
        partial void BeforeQuit(ref bool cancel);

        // 結束程式：面板先收起來，收起的動畫播完（約 0.4 秒）才真的結束，不會一下子消失
        bool quitting;   // 已經要結束了（連按兩下、按住 Enter 不會結束兩次）

        void HideAndQuit()
        {
            if (quitting) return;
            quitting = true;
            Hide();
            UiTimer.After(450, app.Quit);
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
            if (quitting) return;   // 正在結束（面板收起來的那 0.45 秒）：不再打開
            EnsureGlass();
            open = true;
            // 打開面板時重新查一次開機自動啟動（例如在工作排程器裡被改過）：要連線到系統服務，放到背景做，查好再更新開關
            System.Threading.Tasks.Task.Run(() => StartupEntry.IsEnabled(true)).ContinueWith(t => { if (open) Refresh(); },
                System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            // 預覽影片等出現動畫跑完再載入（開檔、解碼第一格會卡一下；OnFrame 裡做）
            deferPreview = true;
            Refresh();
            root.Margin = glass != null ? GlassInset : new Thickness(0);
            Find<FrameworkElement>("PanelRim").Visibility = glass != null ? Visibility.Collapsed : Visibility.Visible;

            var place = Placement();
            double left = place.X, top = place.Y;
            restingTop = top;

            // 整個面板由下往上滑入、同時淡入（如果正在關閉的動畫中被重新打開，就從目前位置接著滑回來）
            bool appearing = !window.IsVisible;
            if (appearing)
            {
                window.BeginAnimation(Window.TopProperty, null);
                outer.BeginAnimation(UIElement.OpacityProperty, null);
                window.Left = left;
                window.Top = glass != null ? top : top + SlideDistance;   // 液態玻璃不滑動，改成每一塊長出來
                outer.Opacity = glass != null ? 1 : 0;
                if (glass != null) root.Opacity = 0;   // 內容從透明開始長出來（不然第一格會先閃出上次的樣子）
            }
            if (glass != null) outer.Opacity = 1;   // 直接收掉（HideNow）的那兩格裡又被打開：它把整個面板設成透明了
            dark = Theme.IsDark();   // 深淺照 Windows（官方：選單、側欄這類大元件不會跟著背景在深淺之間翻轉）
            // 深淺、強調色變了才重新套顏色（每次都套的話，整個面板的東西都要重畫，打開時會卡一下）
            string themeKey = dark + "|" + (glass != null) + "|" + Theme.AccentKey();
            if (themeKey != appliedTheme) { Theme.Apply(root.Resources, dark, glass != null); appliedTheme = themeKey; }
            if (glass == null && !backdrop) window.Background = (Brush)root.Resources["WindowFallback"];
            ApplyWindowTheme();
            if (appearing) window.Show();
            window.Activate();
            if (glass == null) LaterApplyShape();
            Appear(appearing);
            StartMotion();
            if (glass == null) Animate(restingTop, 1, 260, EasingMode.EaseOut, null);
        }

        const double SlideDistance = 20;

        // 放在工作列旁邊（右下角，跟 Windows 11 的快速設定一樣）；液態玻璃四周留給影子的空間不算，玻璃本身離工作區邊緣 12。
        // 工作區太矮放不下（小螢幕、放大 125% 以上）就縮短預覽影片；上緣不會超出工作區
        Rect Placement()
        {
            var area = SystemParameters.WorkArea;
            var inset = glass != null ? GlassInset : new Thickness(0);
            const double margin = 12;
            previewCard.Height = PreviewHeight;
            outer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double over = outer.DesiredSize.Height - inset.Top - inset.Bottom - (area.Height - 2 * margin);
            if (over > 0)
            {
                previewCard.Height = Math.Max(96, PreviewHeight - over);
                outer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            var size = outer.DesiredSize;
            double left = area.Right - size.Width - margin + inset.Right, top = area.Bottom - size.Height - margin + inset.Bottom;
            if (area.Top > 0) top = area.Top + margin - inset.Top;      // 工作列在上方
            if (area.Left > 0) left = area.Left + margin - inset.Left;  // 工作列在左方
            top = Math.Max(top, area.Top + margin - inset.Top);        // 上緣不超出工作區
            return new Rect(left, top, size.Width, size.Height);
        }

        const double PreviewHeight = 184;

        public void Hide()
        {
            if (!open) return;
            open = false;
            lastHidden = DateTime.Now;
            if (glass != null) { Disappear(); return; }

            // 跟打開時相反：往下滑出、同時淡出，動畫結束才真正隱藏
            Animate(restingTop + SlideDistance, 0, 170, EasingMode.EaseIn, delegate
            {
                if (open) return;   // 動畫途中又被打開了
                window.Hide();
                UpdatePreview();
                StopMotion();
            });
        }

        void Animate(double top, double opacity, int ms, EasingMode easing, Action done)
        {
            var duration = TimeSpan.FromMilliseconds(ms);
            var move = new DoubleAnimation(top, duration) { EasingFunction = new CubicEase { EasingMode = easing } };
            var fade = new DoubleAnimation(opacity, duration) { EasingFunction = new CubicEase { EasingMode = easing } };
            if (done != null) move.Completed += delegate { done(); };
            window.BeginAnimation(Window.TopProperty, move);
            outer.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // 不播動畫，直接收起來
        public void HideNow()
        {
            if (!open) return;
            open = false;
            hiding = false;
            window.BeginAnimation(Window.TopProperty, null);
            outer.BeginAnimation(UIElement.OpacityProperty, null);
            // 先變透明、等它真的畫出來（兩格）再藏：透明視窗藏起來時 Windows 會記住最後一格，
            // 直接藏的話下次打開會先閃出完整的面板
            outer.Opacity = 0;
            int frames = 0;
            EventHandler wait = null;
            wait = delegate
            {
                if (++frames < 3) return;
                CompositionTarget.Rendering -= wait;
                if (open) return;   // 這段時間又被打開了
                window.Hide();
                if (glass != null) outer.Opacity = 1;
            };
            CompositionTarget.Rendering += wait;
            UiTimer.After(300, delegate
            {
                if (frames >= 3) return;   // 已經照正常的方式藏起來了
                frames = 3;
                CompositionTarget.Rendering -= wait;
                if (open) return;
                window.Hide();
                if (glass != null) outer.Opacity = 1;
            });
            UpdatePreview();
            StopMotion();
            lastHidden = DateTime.Now;
        }

        bool deferPreview;     // 面板剛打開：預覽等一下再載入
        string appliedTheme;   // 上次套的顏色（深淺、液態玻璃、強調色）

        void UpdatePreview(bool fresh = false)
        {
            if (deferPreview && open) return;
            if (!open || !app.HasVideo || app.IsImage)
            {
                if (previewPath != null) { previewMedia.Close(); previewMedia.Source = null; previewPath = null; }
                UpdatePreviewImage();
                return;
            }
            UpdatePreviewImage();
            if (previewPath != app.VideoPath)
            {
                previewPath = app.VideoPath;
                previewPlaying = null;
                previewMedia.Source = new Uri(previewPath);
            }
            previewMedia.Stretch = app.Stretch;
            // 停在桌布現在的那一格，不播：面板是透明的分層視窗，預覽影片每一格都要把整個面板重畫、讀回來，太耗 CPU
            if (previewPlaying == false)
            {
                if (fresh) previewMedia.Position = app.PlaybackPosition;   // 每次打開都換成桌布現在的那一格
                return;
            }
            bool alreadyLoaded = previewPlaying.HasValue;
            previewPlaying = false;
            previewMedia.Pause();
            if (alreadyLoaded) previewMedia.Position = app.PlaybackPosition;   // 對齊桌布停住的那一格
        }

        // 桌布是圖片：預覽直接顯示那張圖（解碼時就縮小到預覽的大小，省記憶體；跟桌布一樣照 EXIF 轉正）
        void UpdatePreviewImage()
        {
            bool show = open && app.HasVideo && app.IsImage;   // 檔案還不在（例如磁碟還沒準備好）：先不載入，之後出現了才載入
            previewImage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            previewMedia.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            if (!show) { if (previewImagePath != null) { previewImage.Source = null; previewImagePath = null; } return; }
            previewImage.Stretch = app.Stretch;
            if (previewImagePath == app.VideoPath) return;
            previewImagePath = app.VideoPath;
            try
            {
                int w, h, orientation;
                ImageLayer.Probe(previewImagePath, out w, out h, out orientation);
                // 解碼到短邊約 460（蓋滿預覽、高解析度螢幕也清楚），長邊最多 2048
                int longSide = (int)Math.Min(2048, Math.Ceiling(Math.Max(w, h) * 460.0 / Math.Max(1, Math.Min(w, h))));
                var bitmap = ImageLayer.Orient(ImageLayer.Load(previewImagePath, w, h, longSide), orientation);
                bitmap.Freeze();
                previewImage.Source = bitmap;
            }
            catch { previewImage.Source = null; }
        }

        void ApplyWindowTheme()
        {
            if (hwnd == IntPtr.Zero) return;
            Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
            Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 1);   // 不用 Windows 的小圓角（自己切大圓角，見 ApplyShape）
            Native.SetDwm(hwnd, Native.DWMWA_TRANSITIONS_FORCEDISABLED, 1);  // 用自己的滑入動畫
            if (glass != null)
            {
                // 液態玻璃：視窗本身全透明，只有一塊塊玻璃（不要毛玻璃、不要切圓角）
                Native.ClearAccent(hwnd);
                Native.SetWindowRgn(hwnd, IntPtr.Zero, true);
                return;
            }
            if (!backdrop) return;
            // 系統內建的 Acrylic 疊色很厚、視窗沒在前景時還會變純色，
            // 改用可以自訂透明度的 Acrylic 模糊，毛玻璃效果明顯很多；失敗時才退回系統內建的
            Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 1);
            if (!Native.SetAcrylic(hwnd, dark ? AcrylicTintDark : AcrylicTintLight))
                Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 3);
        }

        // 毛玻璃的疊色（AABBGGRR）：最前面兩位是不透明度，數字越小越透
        const uint AcrylicTintDark = 0x50181818;
        const uint AcrylicTintLight = 0x60F5F5F5;

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
                if (app.LastError != null) { status = app.IsImage ? Lang.T("無法顯示這張圖片", "Can't show this image") : Lang.T("無法播放這個影片", "Can't play this video"); dot = Color.FromRgb(0xFF, 0x6B, 0x6B); }
                else if (app.IsImage) { status = Lang.T("圖片桌布", "Image wallpaper"); dot = Color.FromRgb(0x6C, 0xCB, 0x5F); }
                else if (app.UserPaused) { status = Lang.T("已暫停", "Paused"); dot = Color.FromRgb(0xA0, 0xA0, 0xA0); }
                else if (app.AutoPaused) { status = Lang.T("自動暫停中・有視窗遮住桌面", "Auto-paused · A window covers the desktop"); dot = Color.FromRgb(0xFF, 0xB9, 0x00); }
                else { status = Lang.T("播放中", "Playing"); dot = Color.FromRgb(0x6C, 0xCB, 0x5F); }
                statusText.Text = status;
                statusDot.Fill = new SolidColorBrush(dot);

                playIcon.Text = app.UserPaused ? "" : "";
                playButton.ToolTip = app.UserPaused ? Lang.T("播放", "Play") : Lang.T("暫停", "Pause");
                muteIcon.Text = app.Muted ? "" : "";
                muteButton.ToolTip = app.Muted ? Lang.T("取消靜音", "Unmute") : Lang.T("靜音", "Mute");
                playButton.IsEnabled = has && !app.IsImage;   // 圖片沒有播放、聲音
                muteButton.IsEnabled = has && !app.IsImage;

                stretchFill.IsChecked = app.Stretch == Stretch.UniformToFill;
                stretchUniform.IsChecked = app.Stretch == Stretch.Uniform;
                stretchStretch.IsChecked = app.Stretch == Stretch.Fill;
                stretchHint.Text = app.Stretch == Stretch.Uniform ? Lang.T("完整顯示整個畫面，比例不同時會有黑邊", "Shows the whole picture; may add black bars")
                                 : app.Stretch == Stretch.Fill ? Lang.T("拉伸到跟螢幕一樣大，比例可能變形", "Stretches to the screen; may look distorted")
                                 : Lang.T("填滿整個螢幕，比例不同時會裁掉邊緣", "Fills the screen; may crop the edges");

                autoPauseToggle.IsChecked = app.AutoPause;
                startupToggle.IsChecked = StartupEntry.IsEnabled();
                taskbarToggle.IsChecked = app.TaskbarFixEnabled;
                beatToggle.IsChecked = app.BeatGlow;
                taskbarHint.Text = TaskbarFix.TranslucentTBRunning
                    ? Lang.T("搭配 TranslucentTB：開機時提早啟動它，工作列變黑時自動修正", "Works with TranslucentTB: starts it early and fixes a black taskbar")
                    : Lang.T("需要開著 TranslucentTB 才有作用（Microsoft Store 免費下載）", "Requires TranslucentTB to be running (free on the Microsoft Store)");
            }
            finally { refreshing = false; }
        }
    }
}
