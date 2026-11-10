[CmdletBinding()]
param(
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $Root

try {
    $Python = Join-Path $Root ".build-venv\Scripts\python.exe"
    if ($Clean) {
        Remove-Item (Join-Path $Root ".build-venv") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $Root "build") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $Root "dist") -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path $Python)) {
        Write-Host "Creating build environment with Python 3.12..."
        py -3.12 -m venv .build-venv
    }
    if (-not (Test-Path $Python)) {
        throw "Python 3.12 x64 is required on the build computer."
    }

    Write-Host "Installing the small CPU-only runtime and packager..."
    & $Python -m pip install --disable-pip-version-check --upgrade pip
    if ($LASTEXITCODE -ne 0) { throw "pip upgrade failed." }
    & $Python -m pip install --disable-pip-version-check -e ".[build]"
    if ($LASTEXITCODE -ne 0) { throw "Project dependency installation failed." }

    $ModelDir = Join-Path $Root "assets\models\vosk-model-small-ru-0.22"
    $VoiceDir = Join-Path $Root "assets\voices"
    New-Item -ItemType Directory -Force -Path $ModelDir, $VoiceDir | Out-Null

    if (-not (Test-Path (Join-Path $ModelDir "conf\model.conf"))) {
        $ModelZip = Join-Path $Root "assets\vosk-model-small-ru-0.22.zip"
        Write-Host "Downloading the Russian Vosk Small model (~45 MB)..."
        Invoke-WebRequest -Uri "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip" -OutFile $ModelZip
        $TempModel = Join-Path $Root "assets\models\_vosk_extract"
        Remove-Item $TempModel -Recurse -Force -ErrorAction SilentlyContinue
        Expand-Archive -Path $ModelZip -DestinationPath $TempModel -Force
        $ExtractedModel = Join-Path $TempModel "vosk-model-small-ru-0.22"
        if (-not (Test-Path $ExtractedModel)) {
            throw "Unexpected Vosk archive structure."
        }
        Copy-Item (Join-Path $ExtractedModel "*") $ModelDir -Recurse -Force
        Remove-Item $TempModel -Recurse -Force
        Remove-Item $ModelZip -Force
    }

    $VoiceModels = @(
        @{ Name = "irina"; File = "ru_RU-irina-medium" },
        @{ Name = "denis"; File = "ru_RU-denis-medium" }
    )

    foreach ($Voice in $VoiceModels) {
        $FileName = $Voice.File
        $BaseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/ru/ru_RU/$($Voice.Name)/medium"
        $ModelPath = Join-Path $VoiceDir "$FileName.onnx"
        $ConfigPath = Join-Path $VoiceDir "$FileName.onnx.json"
        $CardPath = Join-Path $VoiceDir "$($Voice.Name)-MODEL_CARD.txt"

        if (-not (Test-Path $ModelPath)) {
            Write-Host "Downloading Piper voice: $($Voice.Name)..."
            Invoke-WebRequest -Uri "$BaseUrl/$FileName.onnx?download=true" -OutFile $ModelPath
        }
        if (-not (Test-Path $ConfigPath)) {
            Invoke-WebRequest -Uri "$BaseUrl/$FileName.onnx.json?download=true" -OutFile $ConfigPath
        }
        if (-not (Test-Path $CardPath)) {
            Invoke-WebRequest -Uri "$BaseUrl/MODEL_CARD?download=true" -OutFile $CardPath
        }
        if ((Get-Item $ModelPath).Length -lt 1000000) {
            throw "The Piper model download looks incomplete: $ModelPath"
        }
    }

    Write-Host "Building the portable application..."
    Remove-Item (Join-Path $Root "build") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $Root "dist\STTWorkTTS") -Recurse -Force -ErrorAction SilentlyContinue

    & $Python -m PyInstaller --noconfirm --clean --onedir --name STTWorkTTS --paths src --collect-all vosk --collect-all piper --collect-all onnxruntime --collect-all sounddevice --collect-all _sounddevice_data --collect-all _cffi_backend run_app.py
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller build failed." }

    $PortableDir = Join-Path $Root "dist\STTWorkTTS"
    Copy-Item (Join-Path $Root "config.toml") $PortableDir -Force
    Copy-Item (Join-Path $Root "assets") (Join-Path $PortableDir "assets") -Recurse -Force
    Copy-Item (Join-Path $Root "Start.cmd") $PortableDir -Force
    Copy-Item (Join-Path $Root "THIRD_PARTY_NOTICES.md") $PortableDir -Force

    $Archive = Join-Path $Root "STT-WORK-TTS-Windows-x64.zip"
    Remove-Item $Archive -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $PortableDir "*") -DestinationPath $Archive -CompressionLevel Optimal

    $SizeMb = [math]::Round((Get-Item $Archive).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Portable archive created: $Archive"
    Write-Host "Archive size: $SizeMb MB"
    Write-Host "Copy the ZIP to the target PC, extract it, and run Start.cmd."
    Write-Host "The target PC does not need Python, internet access, Ollama or a GPU."
}
finally {
    Pop-Location
}
