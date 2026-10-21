@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0AAH_LAG_MONITOR.ps1" -Seconds 300
echo.
pause
