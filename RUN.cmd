@echo off
setlocal
cd /d "%~dp0"
if exist "%~dp0dist\STTWorkTTS\Start.cmd" (
    call "%~dp0dist\STTWorkTTS\Start.cmd"
    exit /b %errorlevel%
)
if exist "%~dp0.venv\Scripts\python.exe" (
    "%~dp0.venv\Scripts\python.exe" -m stt_work_tts
    pause
    exit /b %errorlevel%
)
echo This is the source checkout. To build the self-contained offline archive:
echo   powershell -ExecutionPolicy Bypass -File .\build_portable.ps1
echo The build computer needs Python 3.12 and internet access once.
pause
endlocal
