@echo off
rem Builds bin\WinCleaner.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
cd /d "%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo csc.exe not found. Enable .NET Framework 4.x in "Turn Windows features on or off".
  pause
  exit /b 1
)
if not exist bin mkdir bin
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu /out:bin\WinCleaner.exe /win32icon:assets\app.ico /win32manifest:assets\app.manifest /lib:"%FW%\WPF" /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll /r:System.Core.dll /r:System.Management.dll /r:Microsoft.VisualBasic.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /resource:src\Wpf\Theme.xaml,Theme.xaml /recurse:src\Core\*.cs /recurse:src\Wpf\*.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED - see the error text above
  pause
  exit /b 1
)
echo.
echo OK: %~dp0bin\WinCleaner.exe
echo The exe is self-contained: copy it anywhere, nothing else is needed to run it.
pause
