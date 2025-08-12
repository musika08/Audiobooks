@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "PY=.venv\Scripts\python.exe"
if not exist "%PY%" set "PY=python"
echo Using: %PY%
"%PY%" -X faulthandler -m audiobooks 2> debug_stderr.log 1> debug_stdout.log
type debug_stdout.log
type debug_stderr.log
echo Exit code: %ERRORLEVEL%
echo.
pause
