@echo off
setlocal
cd /d "%~dp0"

if exist "%~dp0tools\Start-Assistant.ps1" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Start-Assistant.ps1" %*
    set "APP_EXIT=%ERRORLEVEL%"
    if not "%APP_EXIT%"=="0" (
        echo.
        echo Startup failed with exit code %APP_EXIT%.
        pause
    )
    exit /b %APP_EXIT%
)

if exist "%~dp0STT.WorkTTS.exe" (
    "%~dp0STT.WorkTTS.exe" %*
    goto :end
)

if exist "%~dp0dist\STTWorkTTS\STT.WorkTTS.exe" (
    "%~dp0dist\STTWorkTTS\STT.WorkTTS.exe" %*
    goto :end
)

echo Portable build not found. Starting the project from source...
dotnet run --project "%~dp0src\STT.WorkTTS\STT.WorkTTS.csproj" -c Release -- %*
:end
if errorlevel 1 pause
endlocal
