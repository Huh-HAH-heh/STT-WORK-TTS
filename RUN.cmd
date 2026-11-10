@echo off
setlocal
cd /d "%~dp0"

if not exist "%~dp0dist\STTWorkTTS\Start.cmd" (
    echo Portable build not found. Creating it now...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_portable.ps1"
    if errorlevel 1 (
        echo Portable build failed. Read the error above.
        pause
        exit /b 1
    )
)

call "%~dp0dist\STTWorkTTS\Start.cmd"
endlocal
