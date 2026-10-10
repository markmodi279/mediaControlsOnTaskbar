@echo off
setlocal

rem  MediaController build script.
rem
rem    build.cmd            -> windowed build (no console), for normal use
rem    build.cmd console    -> console build, for milestone testing / selftest
rem
rem  Targets the in-box .NET Framework compiler, so nothing needs installing.
rem  NOTE: that compiler is C# 5 only - no string interpolation, nameof, ?. etc.
rem
rem  -recurse: rather than a plain wildcard, so src\ can be foldered by layer.
rem  A plain "src\*.cs" does not descend, and would silently compile nothing.
rem
rem  -win32icon: the file icon Explorer and Task Manager show. Generated once and committed
rem  under assets\ - there is no build step that redraws it. Version metadata is not here:
rem  csc stamps that from the attributes in src\app\AssemblyInfo.cs.
rem
rem  The three .winmd references are what make Windows.Media.Control (SMTC) callable from
rem  C# 5 without an SDK. System.Runtime.dll is the facade the winmd types resolve
rem  through. Do NOT add System.Runtime.WindowsRuntime.dll: its AsTask() wants a merged
rem  "Windows" assembly that only the Windows SDK ships, and breaks the build. MediaService
rem  waits on WinRT operations itself instead. System.Runtime.InteropServices.WindowsRuntime.dll
rem  is the other one, and is fine: it only supplies EventRegistrationToken, which WinRT
rem  events need because this compiler will not do += on them.

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
set "WINMD=C:\Windows\System32\WinMetadata"
set "OUTDIR=%LOCALAPPDATA%\MediaController"
set "OUTEXE=%OUTDIR%\MediaController.exe"

set "TARGET=winexe"
if /i "%~1"=="console" set "TARGET=exe"

if not exist "%CSC%" (
  echo ERROR: C# compiler not found at:
  echo        %CSC%
  exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo Building [%TARGET%] -^> %OUTEXE%

"%CSC%" -nologo -target:%TARGET% -platform:x64 -optimize+ ^
  -out:"%OUTEXE%" ^
  -win32icon:"%~dp0assets\MediaController.ico" ^
  -r:System.dll ^
  -r:System.Core.dll ^
  -r:"%FW%\System.Web.Extensions.dll" ^
  -r:System.Drawing.dll ^
  -r:System.Windows.Forms.dll ^
  -r:"%FW%\System.Runtime.dll" ^
  -r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  -r:"%WINMD%\Windows.Foundation.winmd" ^
  -r:"%WINMD%\Windows.Media.winmd" ^
  -r:"%WINMD%\Windows.Storage.winmd" ^
  -recurse:"%~dp0src\*.cs"

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)

echo BUILD OK
exit /b 0
