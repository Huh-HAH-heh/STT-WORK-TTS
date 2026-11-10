@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0STTWorkTTS.exe" (
    echo STTWorkTTS.exe was not found. This is the source folder, not a portable build.
    echo Run build_portable.ps1 on a computer with internet access to create the offline ZIP.
    pause
    exit /b 1
)
"%~dp0STTWorkTTS.exe"
if errorlevel 1 pause
endlocal
