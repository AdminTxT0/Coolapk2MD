@echo off
setlocal

rem Shortcut for the Python version; arguments are passed through
cd /d "%~dp0.."

where python >nul 2>nul
if errorlevel 1 (
    echo [x] python not found in PATH.
    exit /b 1
)

python "src\coolapk2md.py" %*
endlocal