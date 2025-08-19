@echo off
setlocal EnableExtensions

REM Keep the window around
title Audiobooks Setup
echo Running PowerShell setup...
echo.

REM Prefer the PowerShell in system32 (works on Win10/11)
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"

REM Run setup.ps1 from the current folder, no profile, bypass policy
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1"
if errorlevel 1 (
  echo.
  echo [X] Setup reported an error. See setup.log for details.
  echo Press any key to close...
  pause >nul
  exit /b 1
)

echo.
echo [OK] Setup finished. You can close this window and run start_audiobooks.bat.
echo Press any key to close...
pause >nul
