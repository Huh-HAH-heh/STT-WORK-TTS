@echo off
setlocal
cd /d "%~dp0"

if not exist "%~dp0tools\Start-Assistant.ps1" goto :portable

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Start-Assistant.ps1" %*
set "APP_EXIT=%ERRORLEVEL%"
if not "%APP_EXIT%"=="0" echo Startup failed with exit code %APP_EXIT%.
if not "%APP_EXIT%"=="0" pause
endlocal & exit /b %APP_EXIT%

:portable
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
