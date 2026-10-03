# Motion Desktop

**English** | [中文](#中文說明)

Set a video as your Windows desktop wallpaper. Works on Windows 10 and Windows 11 (including the new desktop structure in 24H2 and later).

## Download

Download `MotionDesktop-vX.X.X.zip` from [Releases](https://github.com/Kiwyfruit87/Motion-Desktop/releases), unzip it, and double-click `VideoWallpaper.exe`.
Keep the `fonts` folder next to the exe (it holds the lock screen clock font).

### If Windows shows a warning

The exe is not digitally signed (a signing certificate costs a yearly fee), so Windows can't confirm who made it:

- A blue "Windows protected your PC" (SmartScreen) screen may appear. Click **More info** → **Run anyway**.
- Your antivirus may also ask; allow it, or build it yourself from source (see below).

To avoid these prompts for everything in the ZIP, right-click the downloaded ZIP **before unzipping** → **Properties** →
tick **Unblock** at the bottom → **OK**, then unzip. (Windows marks downloaded files as "from the internet",
and files unzipped from a marked ZIP inherit the mark.)

## Build

Nothing to install: double-click `build.cmd`. It uses the C# compiler that ships with Windows (.NET Framework 4.x)
and creates `VideoWallpaper.exe` in the same folder.

If you downloaded the source as a ZIP from GitHub, double-clicking `build.cmd` may show
"Open File - Security Warning: The publisher could not be verified". This is the same "from the internet" mark, not a problem with the file:
click **Run** (untick "Always ask before opening this file" so it won't ask again), or unblock the ZIP before unzipping as described above.
`build.cmd` is only a few lines that call the built-in compiler; you can open it in Notepad to check.

## Usage

1. Double-click `VideoWallpaper.exe`. On first launch it asks you to pick a video.
2. After that, click the tray icon (a blue-purple play button, left or right click) to open the control panel:
   - Video preview (shown with the current scaling mode) and playback status
   - Play / pause, mute, choose video
   - Scaling: Fill, Fit, Stretch
   - Auto-pause: pause when a window is maximized or full screen (on by default, saves power)
   - Start with Windows
   - Transparent taskbar (works with TranslucentTB, see below)
   - Exit (restores your original wallpaper)
3. If the program is already running, double-clicking the exe again also opens the control panel.
4. The panel follows Windows dark / light mode and your accent color. Click outside it or press Esc to close it.
5. The interface language follows Windows: Chinese on Chinese Windows, English on everything else.

## Files

| File | Purpose |
| --- | --- |
| `VideoWallpaper.cs` | Source code |
| `app.manifest` | Declares DPI awareness and supported Windows versions (needed on 24H2) |
| `build.cmd` | Double-click to build `VideoWallpaper.exe` |
| `VideoWallpaper.exe` | The program (appears after building) |
| `settings.ini` | Created automatically; stores the current video and options |
| `fonts\` | Lock screen clock font and its license |

Settings are stored next to the exe, not on drive C. The only exception: when "Start with Windows" is on,
a logon task named "VideoWallpaper" is added to Windows Task Scheduler (it starts much earlier than the registry startup list).
Turning the option off removes it. Only if creating the task fails does it fall back to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Lock screen (ChromeOS style)

- The lock button in the control panel (or **Win+Shift+L** from anywhere) slides the whole screen down from the top and plays the video full screen,
  with a clock (12-hour, with AM / PM), the current weather (°C) and the date (for example "Saturday, October 3") in the bottom-left corner.
- Press Space (or Enter, Esc) or click the mouse: the screen slides up and you're back on the desktop.
- Switching to another app (Alt+Tab, Win key) or pressing Alt+F4 also slides it away.
- This is a full-screen view that *looks like* a lock screen. It does not ask for a password; use Win+L to actually lock your PC.
- While music is playing (Spotify, YouTube Music in a browser, or any app that shows up in the Windows media controls),
  a frosted-glass music card appears in the bottom-right corner (the video behind it is blurred in real time):
  album art on the left, song title and artist on the right,
  with previous / play-pause / next buttons below (clicking them doesn't dismiss the lock screen). The card has equal margins to the right and bottom edges.
  When the song changes, the card content turns like a page: the old page slides out to the left while the new one slides in from the right,
  with a constant gap between them. Going back to the previous song reverses the direction.
  The data comes from Windows "System Media Transport Controls" (the same info shown in the volume flyout) and updates every second while the lock screen is open.
  If several apps are playing, the one already on the card stays; when nothing is playing, it shows the one Windows considers current.
- The card takes its color from the album art: a dark, semi-transparent gradient over the frosted glass.
  If the art has two distinct colors (for example blue and yellow), one sits in the top-left and the other in the bottom-right;
  otherwise it's a light-to-dark gradient of one color family. The border picks up a light tint of the nearby color.
  When the song changes, the colors cross-fade. Black-and-white art keeps the plain frosted glass.
- The **tilted microphone button** at the bottom-right of the card toggles lyrics (bright white when showing, half-bright when available but off,
  and it fades smoothly when its state changes). Click it and lyrics fade in above the card: the current line is brightest,
  lines farther away get dimmer, and it scrolls smoothly with playback. Click again to fade it out.
  Lyrics stay on when the song changes; closing the lock screen turns them off, so they won't appear next time until you click the microphone again.
  - As soon as lyrics are found, the whole song is pre-rendered in the background; scrolling and fading are drawn by the playback engine at the display's refresh rate, so they stay smooth.
  - Lyrics come from [LRCLIB](https://lrclib.net) (a free, public, community lyrics database). The song title, artist, album and duration are sent to look them up.
  - When no lyrics are found, or for instrumentals and podcasts, the button is dimmed and does nothing.
  - Time-synced lyrics follow the singing precisely. Plain lyrics (without timestamps) scroll evenly over the song's length without highlighting a line.
- The **speaker button** next to the playback buttons controls Spotify's own volume (the volume bar inside Spotify, not the Windows volume or volume mixer):
  - Click it to mute; click again to go back to the exact volume before muting (it presses Spotify's own mute button).
  - Hover over it for half a second and a volume slider grows out of it. Click or drag the slider; Spotify follows while you drag.
    The mouse wheel over the card also changes the volume. The slider tucks back in a second after the mouse leaves.
  - It works through Windows UI Automation (the accessibility interface screen readers use) on Spotify's volume bar,
    so it changes in 10% steps, the finest step Spotify's volume bar accepts from outside.
  - If Spotify's volume bar can't be found (for example, Spotify's window is closed), or the card is showing another app, the button is dimmed.
- The mouse cursor only appears when you move the mouse and hides after 2.5 seconds.
- The clock font is Google Sans Flex (in the `fonts` folder, SIL Open Font License, see `fonts\OFL.txt`).
- Weather: the rough location is found from your IP address with ipapi.co (fallback: ipwho.is), then the weather comes from Open-Meteo, updated at most every 20 minutes.
  To set a location instead of using your IP, add a line `weatherlocation=latitude,longitude` to `settings.ini` (for example `weatherlocation=22.99,120.21`).

## Transparent taskbar (with TranslucentTB)

- Making the taskbar transparent is done by [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB) (free on the Microsoft Store).
  Keep it running and set its state to "Clear".
- On recent Windows 11 builds (24H2, 25H2), the taskbar often turns completely **black** after TranslucentTB sets it to clear:
  TranslucentTB has already removed the taskbar background, but Explorer's composition state didn't update, so the black layer underneath shows through.
- When the "Transparent taskbar" option is on (default), this program checks the top row of taskbar pixels every 2 seconds and after window switches.
  If it is almost entirely pure black, it sends a `WM_DWMCOMPOSITIONCHANGED` message asking Explorer to reapply its look, and the taskbar is transparent again within about a second.
  No system or TranslucentTB settings are changed, and nothing flickers when the taskbar is already transparent.
- It doesn't read the screen while a full-screen app (game, video) covers the taskbar, to avoid affecting performance.
- **Starts TranslucentTB early**: TranslucentTB's own autostart sits in Windows' startup-apps queue and often takes several minutes.
  Motion Desktop starts right at logon, so when the option is on it also launches TranslucentTB when it starts (about 2 seconds).
  This happens only once at startup: if you close TranslucentTB later, Motion Desktop won't reopen it.
  Launching TranslucentTB while it's already running does nothing, so you can keep its own autostart enabled.

## How it works

- Videos are decoded with Windows' built-in Media Foundation engine (GPU hardware acceleration) and drawn to the wallpaper with Direct3D 11.
- Multiple monitors share one decoder; each monitor is scaled separately.
- On the lock screen, the clock, now-playing card and lyrics are drawn onto the video on the GPU with Direct2D (falls back to GDI if Direct2D isn't available).
- Monitors covered by windows aren't drawn. When all monitors are covered, playback is paused manually, the PC is locked / asleep, or the display is off, the whole engine pauses.
- The program checks itself: if engine output fails, the graphics driver resets, or the picture isn't actually moving after unlocking, it rebuilds the playback engine.

## Video formats

H.264 `.mp4` is recommended. `.wmv` works too. HEVC (H.265) requires the HEVC extension to be installed;
`.mkv` and `.webm` may not play. If a video can't be played, a tray notification appears.

## License

- Code: [MIT License](LICENSE).
- Font Google Sans Flex: SIL Open Font License 1.1, see [`fonts/OFL.txt`](fonts/OFL.txt).
- Lyrics are not included in this project; they are looked up from [LRCLIB](https://lrclib.net) at runtime.

---

# 中文說明

[English](#motion-desktop) | **中文**

把影片設成 Windows 桌布。支援 Windows 10 和 Windows 11（包含 24H2 以後的新桌面結構）。

## 下載

到 [Releases](https://github.com/Kiwyfruit87/Motion-Desktop/releases) 下載 `MotionDesktop-vX.X.X.zip`，解壓縮後雙擊 `VideoWallpaper.exe`。
`fonts` 資料夾要跟 exe 放在一起（鎖定畫面時鐘用的字體）。

### 如果 Windows 跳出警告

這個 exe 沒有數位簽章（簽章憑證每年要付費），Windows 沒辦法確認是誰做的：

- 可能會出現藍色的「Windows 已保護您的電腦」（SmartScreen），按 **其他資訊** → **仍要執行**。
- 防毒軟體也可能會詢問，選允許即可；或照下面的方式自己編譯。

想讓 ZIP 裡的檔案都不再跳警告：**解壓縮之前**，在下載的 ZIP 上按右鍵 → **內容** →
勾選最下面的 **解除封鎖** → **確定**，再解壓縮。（Windows 會在下載的檔案上做「來自網路」的記號，
從有記號的 ZIP 解壓出來的檔案也會帶著這個記號。）

## 編譯

不需要安裝任何東西：雙擊 `build.cmd`，它會用 Windows 內建的 C# 編譯器（.NET Framework 4.x）
在同一個資料夾產生 `VideoWallpaper.exe`。

如果原始碼是從 GitHub 下載 ZIP 解壓的，雙擊 `build.cmd` 時可能會出現「開啟檔案 - 安全性警告：無法確認發行者」。
這也是上面說的「來自網路」記號，不是檔案有問題：按 **執行** 即可（把「開啟這個檔案前一定要先詢問」取消勾選，下次就不會再問），
或照上面的方式先解除 ZIP 的封鎖再解壓縮。`build.cmd` 只有幾行，作用是呼叫內建的編譯器，可以用記事本打開確認。

## 使用方式

1. 雙擊 `VideoWallpaper.exe`，第一次執行會跳出視窗讓你選影片。
2. 之後點右下角系統匣的圖示（藍紫色播放鍵，左鍵右鍵都可以），會彈出控制面板：
   - 影片預覽（會照目前的縮放方式顯示）與播放狀態
   - 播放 / 暫停、靜音、選擇影片
   - 畫面縮放：填滿、完整顯示、拉伸
   - 自動暫停：有視窗最大化或全螢幕時暫停（預設開啟，省電）
   - 開機時自動啟動
   - 工作列透明（搭配 TranslucentTB，見下方說明）
   - 結束（還原原本的桌布）
3. 程式已經在執行時，再雙擊一次 exe 也會打開控制面板。
4. 面板會跟著 Windows 的深色 / 淺色模式與強調色變化；點面板外面或按 Esc 就會收起來。
5. 介面語言跟著 Windows：中文 Windows 顯示中文，其他語言顯示英文。

## 檔案說明

| 檔案 | 用途 |
| --- | --- |
| `VideoWallpaper.cs` | 原始碼 |
| `app.manifest` | 宣告 DPI 與 Windows 版本支援（24H2 需要） |
| `build.cmd` | 雙擊編譯，產生 `VideoWallpaper.exe` |
| `VideoWallpaper.exe` | 主程式（編譯後才有） |
| `settings.ini` | 執行後自動產生，存目前的影片與選項 |
| `fonts\` | 鎖定畫面時鐘用的字體與授權條款 |

設定檔放在 exe 旁邊，不會寫進 C 槽。唯一例外是勾選「開機時自動啟動」時，
會在 Windows 工作排程器加一個登入時執行的工作「VideoWallpaper」（比登錄檔的啟動清單早很多開始播放），
取消勾選就會移除。萬一工作建立失敗，才會改用登錄檔 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。

## 鎖定畫面（仿 ChromeOS）

- 控制面板的鎖頭按鈕（或在任何地方按 **Win+Shift+L**）：整個畫面會從上面滑下來，全螢幕播放影片，
  左下角顯示時鐘（12 小時制，後面有 AM / PM）、目前天氣（攝氏）和英文日期（例如「Saturday, October 3」）。
- 按空白鍵（或 Enter、Esc）、或按一下滑鼠：畫面往上滑走，回到桌面。
- 切到別的程式（Alt+Tab、Win 鍵）或按 Alt+F4 也會滑走。
- 這是「看起來像鎖定畫面」的全螢幕畫面，不會要求密碼；要真正鎖定電腦請用 Win+L。
- 有音樂在播放時（Spotify、瀏覽器裡的 YouTube Music，或其他會出現在 Windows 媒體控制裡的程式），
  右下角會出現一張毛玻璃的音樂卡片（卡片後面的影片會即時模糊）：左邊專輯封面，右邊歌名、歌手，
  下面是上一首 / 播放暫停 / 下一首按鈕（可以直接點，不會讓鎖定畫面滑走）。卡片離螢幕右邊和下面的距離一樣。
  換歌時卡片內容會像翻頁一樣：舊的那頁往左滑出、新的從右邊滑進來，兩頁間距固定；回到前一首時方向相反。
  資料來自 Windows 的「系統媒體控制」（跟音量浮動視窗上的歌名是同一份），鎖定畫面開著時每秒更新一次。
  同時有好幾個在播時，卡片上原本那個會繼續顯示；都沒在播時，顯示 Windows 認定的「目前」那個。
- 卡片的顏色跟著專輯封面：在毛玻璃上疊一層偏深、半透明的漸層。封面有兩個明顯不同的顏色時（例如藍和黃），
  一個在左上、一個在右下；不然就是同一個色系由淺到深。邊框也會帶一點旁邊卡片的淡淡顏色。
  換歌時顏色會慢慢換過去；黑白的封面維持原本的毛玻璃。
- 卡片右下的**斜放麥克風按鈕**是歌詞（顯示中是全白，有歌詞但沒開時半亮，狀態改變時會慢慢變亮或變暗）：按一下，卡片上方會淡入歌詞，目前唱到的那一行最亮、前後幾行越來越淡，
  跟著播放進度平順地往上捲；再按一下就淡出收起來。打開後換歌也會繼續顯示；
  鎖定畫面收起來之後歌詞就關掉，下次打開鎖定畫面時不會自動出現，要再按一次麥克風。
  - 查到歌詞時就先在背景把整首歌畫好，捲動和淡入淡出由播放引擎跟著螢幕更新頻率畫，不會卡。
  - 歌詞來自 [LRCLIB](https://lrclib.net)（免費公開的社群歌詞資料庫），會把歌名、歌手、專輯、歌曲長度送去查詢。
  - 查不到歌詞、純音樂、Podcast 時，按鈕會變暗，按了也沒反應。
  - 有時間標記的歌詞會準確跟著唱；只有一般歌詞（沒有時間標記）的話，照歌曲長度平均慢慢捲，不會特別強調哪一行。
- 播放按鈕旁邊的**喇叭按鈕**調的是 Spotify 自己的音量（Spotify 程式裡的音量條，不是 Windows 的音量或音量混音器）：
  - 按一下靜音；再按一下回到靜音前的確切音量（按的是 Spotify 自己的靜音鈕）。
  - 滑鼠停在喇叭上半秒，音量條會從喇叭旁邊長出來，可以點或拖曳，拖的時候 Spotify 會即時跟上；
    在卡片上滾滑鼠滾輪也能調。滑鼠離開一秒後音量條會縮回去。
  - 是透過 Windows 的協助工具介面（UI Automation，螢幕閱讀器用的那套）操作 Spotify 的音量條，
    所以一格是 10%（Spotify 的音量條從外面設定時最細就是這樣）。
  - 找不到 Spotify 的音量條時（例如 Spotify 的視窗關掉了），或卡片上顯示的是別的程式，按鈕會變暗。
- 移動滑鼠時才會出現游標，停 2.5 秒自動隱藏。
- 時鐘字體是 Google Sans Flex（放在 `fonts` 資料夾，SIL Open Font License，授權條款見 `fonts\OFL.txt`）。
- 天氣：先用 ipapi.co（備用 ipwho.is）依網路 IP 查大概位置，再向 Open-Meteo 查天氣，每 20 分鐘最多更新一次。
  想指定位置、不用 IP 判斷的話，在 `settings.ini` 加一行 `weatherlocation=緯度,經度`（例如 `weatherlocation=22.99,120.21`）。

## 工作列透明（搭配 TranslucentTB）

- 讓工作列變透明的部分由 [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB)（Microsoft Store 免費下載）負責，要開著它，並把狀態設成「Clear（透明）」。
- 新版 Windows 11（24H2、25H2）上，TranslucentTB 設成透明後，工作列常常整條變成**黑色**：
  它已經把工作列背景清掉了，但檔案總管的合成狀態沒跟上，露出底下的黑色。
- 控制面板的「工作列透明」開關打開時（預設開啟），本程式每 2 秒、以及切換視窗後，會檢查工作列最上面一排像素；
  幾乎全是純黑就代表變黑了，會送一個 `WM_DWMCOMPOSITIONCHANGED` 訊息請檔案總管重新套用外觀，約 1 秒內恢復透明。
  不會修改任何系統或 TranslucentTB 的設定，工作列本來就透明時也不會閃。
- 全螢幕程式（遊戲、影片）蓋住工作列時不會去讀畫面，避免影響效能。
- **提早啟動 TranslucentTB**：TranslucentTB 自己的開機啟動排在 Windows「啟動應用程式」的佇列裡，常常要等好幾分鐘；
  動態桌布是登入當下就啟動，所以開關打開時，會在動態桌布剛啟動的時候順便把 TranslucentTB 叫起來（約 2 秒）。
  只在剛啟動時做一次：之後你自己關掉 TranslucentTB，動態桌布不會再把它打開。
  TranslucentTB 已經在執行時再被啟動一次不會有任何反應，所以它自己的開機啟動留著也沒關係。

## 運作方式

- 影片用 Windows 內建的 Media Foundation 播放引擎解碼（顯示卡硬體加速），再用 Direct3D 11 畫到桌布上。
- 多個螢幕共用同一個解碼器，每個螢幕各自縮放。
- 鎖定畫面的時鐘、正在播放和歌詞，用 Direct2D 直接在顯示卡上畫到影片上（這台電腦不支援時才退回 GDI）。
- 被視窗蓋住的螢幕不會繪製；所有螢幕都被蓋住、手動暫停、電腦鎖定 / 睡眠、或螢幕關閉時，整個引擎會暫停。
- 程式會自我檢查：引擎輸出失敗、顯示卡重設、或解鎖後畫面沒有真的在動時，會自動重建播放引擎。

## 影片格式

建議用 H.264 編碼的 `.mp4`。`.wmv` 也可以。HEVC（H.265）需要系統有安裝 HEVC 擴充功能；
`.mkv`、`.webm` 不一定能播，播不了會在系統匣跳出提示。

## 授權

- 程式碼：[MIT License](LICENSE)。
- 字體 Google Sans Flex：SIL Open Font License 1.1，見 [`fonts/OFL.txt`](fonts/OFL.txt)。
- 歌詞不包含在專案裡，是執行時才向 [LRCLIB](https://lrclib.net) 查詢的。
