@echo off
setlocal enableextensions
cd /d "%~dp0"

rem If the venv python exists, just start the app; otherwise run setup first.
if exist ".venv\Scripts\python.exe" goto :RUN

echo Running PowerShell setup...
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1"
if errorlevel 1 (
  echo.
  echo [!] Setup failed. See messages above.
  pause
  exit /b 1
)

:RUN
echo Using: .venv\Scripts\python.exe
".venv\Scripts\python.exe" -m audiobooks
set ec=%ERRORLEVEL%
if not "%ec%"=="0" (
  echo.
  echo [!] The app exited with an error. Please copy the text above.
  pause
)
endlocal
