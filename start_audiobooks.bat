@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "PY=.venv\Scripts\python.exe"
if not exist "%PY%" set "PY=python"

echo Using: %PY%
"%PY%" -m audiobooks
if errorlevel 1 (
  echo.
  echo [!] The app exited with an error. Please copy the text above.
  pause
)
