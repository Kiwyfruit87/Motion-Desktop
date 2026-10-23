# Motion Desktop

**English** | [中文](#中文說明)

Set a video (or a picture) as your Windows desktop wallpaper. Works on Windows 10 and Windows 11 (including the new desktop structure in 24H2 and later).

## Download

Download `MotionDesktop-vX.X.X.zip` from [Releases](https://github.com/Kiwyfruit87/Motion-Desktop/releases), unzip it, and double-click `VideoWallpaper.exe`.
Keep the `fonts` folder next to the exe (it holds the lock screen fonts).

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

1. Double-click `VideoWallpaper.exe`. On first launch it asks you to pick a video or an image.
2. After that, click the tray icon (a blue-purple play button, left or right click) to open the control panel:
   - Preview (the frame the wallpaper is on right now, or the picture, shown with the current scaling mode) and playback status
   - Play / pause, mute, lock screen, choose a video or image, exit (restores your original wallpaper)
   - Scaling: Fill, Fit, Stretch
   - Auto-pause: pause when a window is maximized or full screen (on by default, saves power)
   - Start with Windows
   - Beat glow (the lock screen's music card glows on the kick drum)
   - Transparent taskbar (works with TranslucentTB, see below)
3. If the program is already running, double-clicking the exe again also opens the control panel.
4. The panel follows Windows dark / light mode and your accent color. Click outside it or press Esc to close it.
   - On Windows 10 2004 or later with a graphics card that supports Shader Model 3, the panel is one sheet of **liquid glass** (like iOS 26 / macOS Tahoe):
     it refracts whatever is behind it live (its edge squeezes what's just outside into it, like the rim of a lens, same as the lock screen music card), and the toolbar buttons, switches and the scaling selector behave like Apple's
     (press to grow and light up, switches and the selection lift into a clear lens you can drag, springy motion).
     To do this the panel reads the screen area behind it while it is open (in memory only), so the panel itself
     doesn't show up in screenshots or screen recordings. On older systems it falls back to frosted glass.
5. The interface language follows Windows: Chinese on Chinese Windows, English on everything else.

## Files

| File | Purpose |
| --- | --- |
| `src\` | Source code, one folder per part: `App` (tray panel, settings, startup), `Desktop` (windows behind the desktop icons), `Video` (playback engine), `LockScreen`, `Media` (now playing, lyrics, weather), `Beat` (beat detection), `Common` (animation and timer helpers), `Interop` (Windows API declarations) |
| `app.manifest` | Declares DPI awareness and supported Windows versions (needed on 24H2) |
| `build.cmd` | Double-click to build `VideoWallpaper.exe` |
| `VideoWallpaper.exe` | The program (appears after building) |
| `settings.ini` | Created automatically; stores the current video and options |
| `fonts\` | Lock screen fonts (clock and music card) and their licenses |

Settings are stored next to the exe, not on drive C. The only exception: when "Start with Windows" is on,
a logon task named "VideoWallpaper" is added to Windows Task Scheduler (it starts much earlier than the registry startup list).
Turning the option off removes it. Only if creating the task fails does it fall back to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Lock screen (ChromeOS style)

- The lock button in the control panel (or **Win+Shift+L** from anywhere) slides the whole screen down from the top and plays the video full screen,
  with a clock (12-hour, without AM / PM like the iPhone), the current weather (°C) and the date (for example "Saturday, October 3") in the bottom-left corner.
  The time digits are liquid glass, like the Glass clock on the iOS 26 Lock Screen: the video behind them shows through, bent at the edges, with a bright rim
  (on graphics cards older than Direct3D 10 they are plain white).
- Press Space (or Enter, Esc) or click the mouse: the screen slides up and you're back on the desktop.
- Switching to another app (Alt+Tab, Win key) or pressing Alt+F4 also slides it away.
- This is a full-screen view that *looks like* a lock screen. It does not ask for a password; use Win+L to actually lock your PC.
- While it's open, the PC won't turn the display off or go to sleep because of inactivity (like when a video is playing). Win+L, closing the lid or the power button still work as usual.
- The music card's border glows on the kick drum: the program listens to what the PC is playing in real time (in memory only; nothing is recorded, saved or sent) and lights up on each beat. No drums, no glow. Turn it off with "Beat glow" in the control panel (then nothing is listened to at all).
- While music is playing (Spotify, YouTube Music in a browser, or any app that shows up in the Windows media controls),
  a **liquid glass** music card (like Apple's Liquid Glass in iOS 26 / macOS Tahoe) appears in the bottom-right corner.
  The glass is drawn by a GPU pixel shader: the video behind it shows through lightly blurred and slightly magnified,
  and the edge works like the rim of a lens, squeezing what's just outside the card into it (sharper toward the edge, curving smoothly around the corners).
  A bright rim glows softly inward, brightest at the top-left and bottom-right, with a soft shadow underneath and a faint tint of the album art's color.
  The layout follows Now Playing on the macOS Tahoe lock screen: album art on the left, the song title and "artist — album" next to it and a lyrics button at the top right,
  a progress bar with the elapsed and remaining time on either side, and shuffle, previous / play-pause / next and repeat along the bottom.
  Clicking the buttons doesn't dismiss the lock screen. The card has equal margins to the right and bottom edges.
  When the song changes, only the album art, title and artist move: the old ones slide a little to the left and fade out
  while the new ones slide in from the right and fade in (going back to the previous song reverses the direction). The buttons and progress bar stay put.
  The data comes from Windows "System Media Transport Controls" (the same info shown in the volume flyout) and updates every second while the lock screen is open.
  If several apps are playing (or none is), the one already on the card stays as long as it's still there; otherwise it shows the one Windows considers current.
- When the lock screen slides in or out, the card follows with a little inertia: it lags slightly as the screen starts moving,
  overshoots a few pixels and settles back when it stops, and stretches slightly along the direction of motion.
- On graphics cards older than Direct3D 10 (Direct3D 9 class), the card falls back to frosted glass.
- The **progress bar** shows how far the song has played. Drag it to jump to another part of the song (the jump happens when you let go).
  It only appears when the app reports the song's length, and can't be dragged if the app doesn't allow jumping.
- The **lyrics button** (a speech bubble with a quotation mark, the round button at the top right) toggles lyrics
  (white with a dark icon when showing, half-bright when available but off, and it fades smoothly when its state changes).
  Click it and lyrics fade in above the card: the current line is brightest,
  lines farther away get dimmer, and it scrolls smoothly with playback. Click again to fade it out.
  Lyrics stay on when the song changes; closing the lock screen turns them off, so they won't appear next time until you click the button again.
  - As soon as lyrics are found, the whole song is pre-rendered in the background; scrolling and fading are drawn by the playback engine at the display's refresh rate, so they stay smooth.
  - Lyrics come from [LRCLIB](https://lrclib.net) (a free, public, community lyrics database). The song title, artist, album and duration are sent to look them up.
  - When no lyrics are found, or for instrumentals and podcasts, the button is dimmed and does nothing.
  - Time-synced lyrics follow the singing precisely. Plain lyrics (without timestamps) scroll evenly over the song's length without highlighting a line.
- **Shuffle** and **repeat** (off → all → one) work with players that support them; they are dimmed for players that don't.
- The mouse cursor only appears when you move the mouse and hides after 2.5 seconds (it stays visible while it's over the music card).
- The clock and the music card use Inter, and the lyrics use Google Sans Flex (both in the `fonts` folder under the SIL Open Font License, see `fonts\Inter-OFL.txt` and `fonts\OFL.txt`).
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
- Videos up to 256 MB are loaded into memory once, so a hard drive that has spun down doesn't stall playback.
- On the lock screen, the clock, now-playing card and lyrics are drawn onto the video on the GPU with Direct2D (falls back to GDI if Direct2D isn't available).
  The card's liquid glass is a Direct3D 11 pixel shader that only runs around the card (the blur is done at quarter resolution);
  it takes about 0.2 ms of GPU time per frame even on an entry-level graphics card (GeForce GT 1030).
- Monitors covered by windows aren't drawn. When all monitors are covered, playback is paused manually, the PC is locked / asleep, or the display is off, the whole engine pauses.
- The program checks itself: if engine output fails, the graphics driver resets, or the picture isn't actually moving after unlocking, it rebuilds the playback engine.

## Video and image formats

H.264 `.mp4` is recommended. `.wmv` works too. HEVC (H.265) requires the HEVC extension to be installed;
`.mkv` and `.webm` may not play. If a video can't be played, a tray notification appears.

Pictures work too: `.jpg`, `.png`, `.bmp`, `.gif` (first frame only), `.tif` and `.jxr`; `.webp` and `.heic` need the matching Windows extension.
Phone photos are rotated upright using their EXIF orientation, and pictures larger than 4096 pixels on the long side are scaled down first.
A picture is drawn once by the graphics card and only redrawn when something changes, so it uses almost no resources; play / pause and mute are disabled.

## License

- Code: [MIT License](LICENSE).
- Font Google Sans Flex: SIL Open Font License 1.1, see [`fonts/OFL.txt`](fonts/OFL.txt).
- Font Inter: SIL Open Font License 1.1, see [`fonts/Inter-OFL.txt`](fonts/Inter-OFL.txt).
- Lyrics are not included in this project; they are looked up from [LRCLIB](https://lrclib.net) at runtime.

---

# 中文說明

[English](#motion-desktop) | **中文**

把影片（或圖片）設成 Windows 桌布。支援 Windows 10 和 Windows 11（包含 24H2 以後的新桌面結構）。

## 下載

到 [Releases](https://github.com/Kiwyfruit87/Motion-Desktop/releases) 下載 `MotionDesktop-vX.X.X.zip`，解壓縮後雙擊 `VideoWallpaper.exe`。
`fonts` 資料夾要跟 exe 放在一起（鎖定畫面用的字體）。

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

1. 雙擊 `VideoWallpaper.exe`，第一次執行會跳出視窗讓你選影片或圖片。
2. 之後點右下角系統匣的圖示（藍紫色播放鍵，左鍵右鍵都可以），會彈出控制面板：
   - 預覽（桌布現在播到的那一格，或那張圖片，會照目前的縮放方式顯示）與播放狀態
   - 播放 / 暫停、靜音、鎖定畫面、選擇影片或圖片、結束（還原原本的桌布）
   - 畫面縮放：填滿、完整顯示、拉伸
   - 自動暫停：有視窗最大化或全螢幕時暫停（預設開啟，省電）
   - 開機時自動啟動
   - 節奏跳動（鎖定畫面的音樂卡片跟著大鼓發光）
   - 工作列透明（搭配 TranslucentTB，見下方說明）
3. 程式已經在執行時，再雙擊一次 exe 也會打開控制面板。
4. 面板會跟著 Windows 的深色 / 淺色模式與強調色變化；點面板外面或按 Esc 就會收起來。
   - Windows 10 2004 以後、顯示卡支援 Shader Model 3 時，面板是一整塊**液態玻璃**（像 iOS 26 / macOS Tahoe）：
     即時折射面板後面的畫面（邊緣像透鏡的邊，把面板外面的東西壓進來，跟鎖定畫面的音樂卡片一樣）；工具列按鈕、開關、畫面縮放的選取都照 Apple 的樣子動（按下會長大、發光，
     開關和選取按住會浮起成清澈的透鏡、可以拖，動作有彈性）。
     為了做到這點，面板開著時會讀它後面那一塊螢幕畫面（只在記憶體裡用），所以面板本身不會出現在截圖和螢幕錄影裡。
     比較舊的系統會退回毛玻璃。
5. 介面語言跟著 Windows：中文 Windows 顯示中文，其他語言顯示英文。

## 檔案說明

| 檔案 | 用途 |
| --- | --- |
| `src\` | 原始碼，依功能分資料夾：`App`（控制面板、設定、開機啟動）、`Desktop`（桌面圖示後面的視窗）、`Video`（播放引擎）、`LockScreen`（鎖定畫面）、`Media`（正在播放、歌詞、天氣）、`Beat`（抓拍子）、`Common`（動畫、計時器的小工具）、`Interop`（Windows API 宣告） |
| `app.manifest` | 宣告 DPI 與 Windows 版本支援（24H2 需要） |
| `build.cmd` | 雙擊編譯，產生 `VideoWallpaper.exe` |
| `VideoWallpaper.exe` | 主程式（編譯後才有） |
| `settings.ini` | 執行後自動產生，存目前的影片與選項 |
| `fonts\` | 鎖定畫面用的字體（時鐘、音樂卡片）與授權條款 |

設定檔放在 exe 旁邊，不會寫進 C 槽。唯一例外是勾選「開機時自動啟動」時，
會在 Windows 工作排程器加一個登入時執行的工作「VideoWallpaper」（比登錄檔的啟動清單早很多開始播放），
取消勾選就會移除。萬一工作建立失敗，才會改用登錄檔 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。

## 鎖定畫面（仿 ChromeOS）

- 控制面板的鎖頭按鈕（或在任何地方按 **Win+Shift+L**）：整個畫面會從上面滑下來，全螢幕播放影片，
  左下角顯示時鐘（12 小時制，跟 iPhone 一樣不顯示 AM / PM）、目前天氣（攝氏）和英文日期（例如「Saturday, October 3」）。
  時間的數字是液態玻璃，像 iOS 26 鎖定畫面的「玻璃」時鐘：透出後面的影片、邊緣會折射，還有一圈亮邊（Direct3D 10 以前的舊顯示卡是一般的白字）。
- 按空白鍵（或 Enter、Esc）、或按一下滑鼠：畫面往上滑走，回到桌面。
- 切到別的程式（Alt+Tab、Win 鍵）或按 Alt+F4 也會滑走。
- 這是「看起來像鎖定畫面」的全螢幕畫面，不會要求密碼；要真正鎖定電腦請用 Win+L。
- 開著的時候，電腦不會因為閒置而關螢幕或睡眠（跟播放影片時一樣）；按 Win+L、闔上螢幕、按電源鍵照常有效。
- 音樂卡片的邊框會跟著大鼓發光：程式即時聽電腦正在播出的聲音（只在記憶體裡算，不錄音、不存檔、不傳出去），每一下重拍亮一下；沒有鼓就不亮。不要的話可以在控制面板關掉「節奏跳動」（關掉就完全不聽聲音）。
- 有音樂在播放時（Spotify、瀏覽器裡的 YouTube Music，或其他會出現在 Windows 媒體控制裡的程式），
  右下角會出現一張**液態玻璃**的音樂卡片（像 iOS 26 / macOS Tahoe 的 Liquid Glass）。
  玻璃是用顯示卡的像素著色器畫的：後面的影片輕輕模糊、稍微放大地透過來；邊緣像透鏡的邊，把卡片外面的東西壓進來（越靠邊越清楚，在圓角平順地彎過去）。
  外緣一圈亮邊往內柔和地發光（左上、右下最亮），底下淡淡的影子，再帶一點專輯封面的顏色。
  版面照 macOS Tahoe 鎖定畫面的「正在播放」：左邊專輯封面，旁邊歌名和「歌手 — 專輯」，右上角是歌詞按鈕；
  中間是進度條，兩端是已經播了多久、還剩多久；最下面是隨機播放、上一首 / 播放暫停 / 下一首、循環。
  按鈕可以直接點，不會讓鎖定畫面滑走。卡片離螢幕右邊和下面的距離一樣。
  換歌時只有封面、歌名、歌手在動：舊的往左滑一小段淡出、新的從右邊滑進來淡入（回到前一首時方向相反）；按鈕、進度條都不動。
  資料來自 Windows 的「系統媒體控制」（跟音量浮動視窗上的歌名是同一份），鎖定畫面開著時每秒更新一次。
  同時有好幾個在播時（或都沒在播時），卡片上原本那個只要還在就繼續顯示；沒有的話才顯示 Windows 認定的「目前」那個。
- 鎖定畫面滑進來、滑走的時候，卡片跟著動但帶一點慣性：剛開始動時慢半拍、停下時往前多衝幾個像素再彈回，移動中沿方向稍微拉長。
- 比 Direct3D 10 還舊的顯示卡（Direct3D 9 等級），卡片會退回毛玻璃。
- **進度條**顯示播到哪裡，按住拖曳可以跳到別的地方（放開時才真的跳過去）。播放器有回報歌曲長度時才會出現；播放器不接受跳轉時只能看、不能拖。
- 右上角的**歌詞按鈕**（對話框裡一個引號）是歌詞（顯示中是白底深色圖示，有歌詞但沒開時半亮，狀態改變時會慢慢變亮或變暗）：
  按一下，卡片上方會淡入歌詞，目前唱到的那一行最亮、前後幾行越來越淡，
  跟著播放進度平順地往上捲；再按一下就淡出收起來。打開後換歌也會繼續顯示；
  鎖定畫面收起來之後歌詞就關掉，下次打開鎖定畫面時不會自動出現，要再按一次按鈕。
  - 查到歌詞時就先在背景把整首歌畫好，捲動和淡入淡出由播放引擎跟著螢幕更新頻率畫，不會卡。
  - 歌詞來自 [LRCLIB](https://lrclib.net)（免費公開的社群歌詞資料庫），會把歌名、歌手、專輯、歌曲長度送去查詢。
  - 查不到歌詞、純音樂、Podcast 時，按鈕會變暗，按了也沒反應。
  - 有時間標記的歌詞會準確跟著唱；只有一般歌詞（沒有時間標記）的話，照歌曲長度平均慢慢捲，不會特別強調哪一行。
- **隨機播放**和**循環**（關 → 全部 → 單曲）：播放器支援就能用，不支援的會變淡。
- 移動滑鼠時才會出現游標，停 2.5 秒自動隱藏（停在音樂卡片上時不會隱藏）。
- 時鐘和音樂卡片用 Inter，歌詞用 Google Sans Flex（都放在 `fonts` 資料夾，SIL Open Font License，授權條款見 `fonts\Inter-OFL.txt`、`fonts\OFL.txt`）。
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
- 256 MB 以內的影片會先整支讀進記憶體，放在傳統硬碟上時，硬碟停轉也不會讓影片卡住。
- 鎖定畫面的時鐘、正在播放和歌詞，用 Direct2D 直接在顯示卡上畫到影片上（這台電腦不支援時才退回 GDI）。
  卡片的液態玻璃是 Direct3D 11 的像素著色器，只在卡片附近執行（模糊也是四分之一解析度）；
  就算是入門顯示卡（GeForce GT 1030），每一格也只花顯示卡約 0.2 毫秒。
- 被視窗蓋住的螢幕不會繪製；所有螢幕都被蓋住、手動暫停、電腦鎖定 / 睡眠、或螢幕關閉時，整個引擎會暫停。
- 程式會自我檢查：引擎輸出失敗、顯示卡重設、或解鎖後畫面沒有真的在動時，會自動重建播放引擎。

## 影片和圖片格式

建議用 H.264 編碼的 `.mp4`。`.wmv` 也可以。HEVC（H.265）需要系統有安裝 HEVC 擴充功能；
`.mkv`、`.webm` 不一定能播，播不了會在系統匣跳出提示。

圖片也可以：`.jpg`、`.png`、`.bmp`、`.gif`（只顯示第一格）、`.tif`、`.jxr`；`.webp`、`.heic` 要 Windows 有裝對應的擴充功能。
手機拍的照片會照 EXIF 的方向轉正，長邊超過 4096 像素的圖片會先縮小。
圖片由顯示卡畫一次，畫面有變化時才重畫，幾乎不耗資源；播放 / 暫停、靜音按鈕這時不能按。

## 授權

- 程式碼：[MIT License](LICENSE)。
- 字體 Google Sans Flex：SIL Open Font License 1.1，見 [`fonts/OFL.txt`](fonts/OFL.txt)。
- 字體 Inter：SIL Open Font License 1.1，見 [`fonts/Inter-OFL.txt`](fonts/Inter-OFL.txt)。
- 歌詞不包含在專案裡，是執行時才向 [LRCLIB](https://lrclib.net) 查詢的。
