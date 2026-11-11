@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-TtsLab.ps1" %*
if errorlevel 1 pause
endlocal
