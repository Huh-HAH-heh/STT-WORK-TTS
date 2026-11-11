@echo off
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_portable.ps1"
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

echo.
echo Build complete: STT-WORK-TTS-Windows-x64.zip
echo Extract the archive and run Start.cmd.
pause
endlocal
