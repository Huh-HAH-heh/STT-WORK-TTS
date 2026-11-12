[CmdletBinding()]
param([switch]$Clean)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $Root

try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET 10 SDK is required on the build computer."
    }

    $Assets = Join-Path $Root "assets"
    $ModelDir = Join-Path $Assets "models\vosk-model-small-ru-0.22"
    $PiperDir = Join-Path $Assets "piper"
    $VoiceDir = Join-Path $Assets "voices"
    $TempDir = Join-Path $Root ".build-temp"
    $PublishDir = Join-Path $Root "dist\STTWorkTTS"

    if ($Clean) {
        Remove-Item (Join-Path $Root "dist") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Force -Path $ModelDir, $PiperDir, $VoiceDir, $TempDir | Out-Null

    if (-not (Test-Path (Join-Path $ModelDir "conf\model.conf"))) {
        $ModelZip = Join-Path $TempDir "vosk-model.zip"
        $ExtractDir = Join-Path $TempDir "vosk"
        Write-Host "Downloading Vosk Russian Small model..."
        Invoke-WebRequest -Uri "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip" -OutFile $ModelZip
        Expand-Archive -Path $ModelZip -DestinationPath $ExtractDir -Force
        $Extracted = Join-Path $ExtractDir "vosk-model-small-ru-0.22"
        if (-not (Test-Path $Extracted)) { throw "Unexpected Vosk model archive structure." }
        Copy-Item (Join-Path $Extracted "*") $ModelDir -Recurse -Force
    }

    if (-not (Test-Path (Join-Path $PiperDir "piper.exe"))) {
        $PiperZip = Join-Path $TempDir "piper-windows.zip"
        $PiperExtract = Join-Path $TempDir "piper"
        Write-Host "Downloading native Piper for Windows x64..."
        Invoke-WebRequest -Uri "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip" -OutFile $PiperZip
        Expand-Archive -Path $PiperZip -DestinationPath $PiperExtract -Force
        $PiperExe = Get-ChildItem -Path $PiperExtract -Filter "piper.exe" -Recurse | Select-Object -First 1
        if (-not $PiperExe) { throw "piper.exe was not found in the Piper archive." }
        Copy-Item (Join-Path $PiperExe.Directory.FullName "*") $PiperDir -Recurse -Force
    }

    $VoiceModel = Join-Path $VoiceDir "ru_RU-denis-medium.onnx"
    $VoiceConfig = "$VoiceModel.json"
    $VoiceBase = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/ru/ru_RU/denis/medium"
    if (-not (Test-Path $VoiceModel)) {
        Write-Host "Downloading Russian Piper Denis voice..."
        Invoke-WebRequest -Uri "$VoiceBase/ru_RU-denis-medium.onnx?download=true" -OutFile $VoiceModel
    }
    if (-not (Test-Path $VoiceConfig)) {
        Invoke-WebRequest -Uri "$VoiceBase/ru_RU-denis-medium.onnx.json?download=true" -OutFile $VoiceConfig
    }
    if ((Get-Item $VoiceModel).Length -lt 1000000) {
        throw "Piper model download is incomplete."
    }

    Write-Host "Publishing self-contained .NET application..."
    Remove-Item $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
    $PublishArgs = @(
        "publish",
        ".\src\STT.WorkTTS\STT.WorkTTS.csproj",
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:PublishTrimmed=false",
        "--output", $PublishDir
    )
    & dotnet @PublishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    # Copy models and native Piper DLLs beside the executable.
    $PublishAssets = Join-Path $PublishDir "assets"
    New-Item -ItemType Directory -Force -Path $PublishAssets | Out-Null
    Copy-Item (Join-Path $Assets "*") $PublishAssets -Recurse -Force

    # Include only startup/install scripts and lightweight TTS adapters.
    # Python environments, downloaded models and caches are created on first launch.
    $PortableToolFiles = @(
        "tools\Start-Assistant.ps1",
        "tools\Install-VoskModel.ps1",
        "tools\intent\Install-QwenIntentModel.ps1",
        "tools\intent\Start-IntentModel.ps1",
        "tools\tts-lab\Install-TtsLab.ps1",
        "tools\tts-runtime\silero_worker.py",
        "tools\tts-runtime\xvibe_worker.py"
    )
    foreach ($RelativePath in $PortableToolFiles) {
        $SourcePath = Join-Path $Root $RelativePath
        if (-not (Test-Path $SourcePath)) {
            throw "Required portable startup file is missing: $SourcePath"
        }

        $DestinationPath = Join-Path $PublishDir $RelativePath
        $DestinationDirectory = Split-Path $DestinationPath -Parent
        New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
        Copy-Item $SourcePath $DestinationPath -Force
    }

    Copy-Item ".\Start.cmd" $PublishDir -Force
    Copy-Item ".\THIRD_PARTY_NOTICES.md" $PublishDir -Force
    Copy-Item ".\README.md" $PublishDir -Force

    $Archive = Join-Path $Root "STT-WORK-TTS-Windows-x64.zip"
    Remove-Item $Archive -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $Archive -CompressionLevel Optimal

    $SizeMb = [math]::Round((Get-Item $Archive).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Portable archive created: $Archive"
    Write-Host "Archive size: $SizeMb MB"
    Write-Host "Extract the ZIP and run Start.cmd. Python is not required."
}
finally {
    Pop-Location
}
