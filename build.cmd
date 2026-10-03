@echo off
chcp 65001 >nul
rem 用 Windows 內建的 C# 編譯器（.NET Framework 4.x）編譯出 VideoWallpaper.exe，不需要另外安裝任何東西
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 ^
  /out:"%~dp0VideoWallpaper.exe" /win32manifest:"%~dp0app.manifest" ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" ^
  /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" ^
  /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  "%~dp0VideoWallpaper.cs"
if errorlevel 1 (echo 編譯失敗 / Build failed & exit /b 1)
echo 編譯完成 / Build succeeded: %~dp0VideoWallpaper.exe
