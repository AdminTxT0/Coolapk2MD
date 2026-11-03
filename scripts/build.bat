@echo off
setlocal

rem Locate csc.exe from .NET Framework 4.x (Visual Studio not required)
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [x] csc.exe not found. .NET Framework 4.x is required.
    exit /b 1
)

rem Switch to repository root (script may be invoked from anywhere)
cd /d "%~dp0.."
if not exist "dist" mkdir "dist"

echo [*] Compiling...

"%CSC%" /target:winexe /out:dist\Coolapk2MD.exe ^
    /win32icon:src\app.ico ^
    /resource:src\app.ico,Coolapk2MD.app.ico ^
    /codepage:65001 ^
    /optimize+ /nologo ^
    /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    src\Coolapk2MD.cs

if errorlevel 1 (
    echo [x] Build failed.
    exit /b 1
)

echo [ok] dist\Coolapk2MD.exe
for %%F in (dist\Coolapk2MD.exe) do echo     size: %%~zF bytes
endlocal