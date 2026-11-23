@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish.ps1"
set "publishExit=%ERRORLEVEL%"
pause
exit /b %publishExit%
