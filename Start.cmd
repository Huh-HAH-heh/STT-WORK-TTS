@echo off
setlocal
cd /d "%~dp0"

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
