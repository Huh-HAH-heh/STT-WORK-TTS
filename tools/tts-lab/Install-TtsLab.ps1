[CmdletBinding()]
param(
    [ValidateSet("all", "piper", "xvibe", "silero", "vosk", "xtts")][string]$Engine = "all",
    [switch]$AllPiperVoices
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$LabRoot = $PSScriptRoot
$EnvRoot = Join-Path $LabRoot "envs"
$CacheRoot = Join-Path $LabRoot "cache"
$ModelRoot = Join-Path $LabRoot "models"
$AssetsRoot = Join-Path $Root "assets"
New-Item -ItemType Directory -Force -Path $EnvRoot, $CacheRoot, $ModelRoot | Out-Null

function Invoke-Checked {
    param([string]$File, [string[]]$Arguments, [string]$Label)
    Write-Host ""
    Write-Host ("== " + $Label + " ==") -ForegroundColor Cyan
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Label завершился с кодом $LASTEXITCODE." }
}

function New-PythonEnvironment {
    param([string]$Name)
    $Venv = Join-Path $EnvRoot $Name
    $Python = Join-Path $Venv "Scripts\python.exe"
    if (-not (Test-Path $Python)) {
        Invoke-Checked "py" @("-3.12", "-m", "venv", $Venv) ("Создание окружения " + $Name)
    }
    return $Python
}

function Install-PiperAssets {
    $PiperRoot = Join-Path $AssetsRoot "piper"
    $VoiceRoot = Join-Path $AssetsRoot "voices"
    $TempRoot = Join-Path $LabRoot "tmp\piper"
    New-Item -ItemType Directory -Force -Path $PiperRoot, $VoiceRoot, $TempRoot | Out-Null

    if (-not (Test-Path (Join-Path $PiperRoot "piper.exe"))) {
        $Zip = Join-Path $TempRoot "piper-windows.zip"
        $Extract = Join-Path $TempRoot "unpacked"
        Write-Host "Скачивание Piper CLI для Windows x64..."
        Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip" -OutFile $Zip
        Expand-Archive -Path $Zip -DestinationPath $Extract -Force
        $Exe = Get-ChildItem -Path $Extract -Filter "piper.exe" -Recurse | Select-Object -First 1
        if (-not $Exe) { throw "В архиве Piper не найден piper.exe." }
        Copy-Item (Join-Path $Exe.Directory.FullName "*") $PiperRoot -Recurse -Force
    }

    $VoiceNames = if ($AllPiperVoices -or $Engine -eq "all") {
        @("denis", "dmitri", "irina", "ruslan")
    }
    else {
        @("denis")
    }

    foreach ($Voice in $VoiceNames) {
        $ModelName = "ru_RU-$Voice-medium.onnx"
        $ModelPath = Join-Path $VoiceRoot $ModelName
        $ConfigPath = "$ModelPath.json"
        # The v1.0.0 path is no longer available; current voice files are published on main.
        $Base = "https://huggingface.co/rhasspy/piper-voices/resolve/main/ru/ru_RU/$Voice/medium"
        if (-not (Test-Path $ModelPath) -or (Get-Item $ModelPath).Length -lt 10000000) {
            if (Test-Path $ModelPath) { Remove-Item $ModelPath -Force }
            Write-Host "Скачивание Piper: $Voice"
            $TemporaryModel = "$ModelPath.download"
            if (Test-Path $TemporaryModel) { Remove-Item $TemporaryModel -Force }
            $ModelUri = "${Base}/${ModelName}?download=true"
            try {
                Invoke-WebRequest -UseBasicParsing -Uri $ModelUri -OutFile $TemporaryModel
            }
            catch {
                throw "Не удалось скачать Piper ONNX '$Voice' по адресу '$ModelUri': $($_.Exception.Message)"
            }
            if ((Get-Item $TemporaryModel).Length -lt 10000000) { throw "Модель Piper $Voice скачана не полностью." }
            Move-Item $TemporaryModel $ModelPath -Force
        }
        if (-not (Test-Path $ConfigPath) -or (Get-Item $ConfigPath).Length -lt 1000) {
            if (Test-Path $ConfigPath) { Remove-Item $ConfigPath -Force }
            $TemporaryConfig = "$ConfigPath.download"
            if (Test-Path $TemporaryConfig) { Remove-Item $TemporaryConfig -Force }
            $ConfigUri = "${Base}/${ModelName}.json?download=true"
            try {
                Invoke-WebRequest -UseBasicParsing -Uri $ConfigUri -OutFile $TemporaryConfig
            }
            catch {
                throw "Не удалось скачать конфигурацию Piper '$Voice' по адресу '$ConfigUri': $($_.Exception.Message)"
            }
            if ((Get-Item $TemporaryConfig).Length -lt 1000) { throw "Конфигурация Piper $Voice скачана не полностью." }
            Move-Item $TemporaryConfig $ConfigPath -Force
        }
        if ((Get-Item $ModelPath).Length -lt 10000000) { throw "Модель Piper $Voice скачана не полностью." }
        if ((Get-Item $ConfigPath).Length -lt 1000) { throw "Конфигурация Piper $Voice скачана не полностью." }
        # Windows PowerShell 5.1 ConvertFrom-Json stores object keys in a
        # case-insensitive map. IPA keys such as "ɡ" and "ɢ" in Piper's
        # phoneme_id_map can then be reported as duplicates. Read UTF-8
        # explicitly and validate the required JSON sections without
        # converting the phoneme map into a PowerShell hashtable.
        try {
            $VoiceConfigText = [System.IO.File]::ReadAllText($ConfigPath, [System.Text.Encoding]::UTF8)
            if (-not $VoiceConfigText.Trim().StartsWith("{") -or -not $VoiceConfigText.Trim().EndsWith("}")) {
                throw "Файл не похож на JSON-объект."
            }
            if ($VoiceConfigText -notmatch '"audio"\s*:\s*\{') {
                throw 'Отсутствует секция "audio".'
            }
            if ($VoiceConfigText -notmatch '"phoneme_id_map"\s*:\s*\{') {
                throw 'Отсутствует секция "phoneme_id_map".'
            }
        }
        catch {
            throw "Конфигурация Piper $Voice не прошла проверку UTF-8/структуры: $($_.Exception.Message)"
        }
        Write-Host "Готов голос: $ModelName" -ForegroundColor Green
    }
}

function Install-XVibe {
    $Source = Join-Path $LabRoot "vendor\xVibePocketTTS"
    if (-not (Test-Path (Join-Path $Source "pyproject.toml"))) {
        New-Item -ItemType Directory -Force -Path (Split-Path $Source -Parent) | Out-Null
        Invoke-Checked "git" @("clone", "https://github.com/GenVoice/xVibePocketTTS.git", $Source) "Загрузка xVibePocketTTS"
    }
    $Python = Join-Path $Source ".venv\Scripts\python.exe"
    if (-not (Test-Path $Python)) {
        Invoke-Checked "py" @("-3.12", "-m", "venv", (Join-Path $Source ".venv")) "Создание окружения xVibe"
        $Python = Join-Path $Source ".venv\Scripts\python.exe"
    }
    Invoke-Checked $Python @("-m", "pip", "install", "--upgrade", "pip") "Обновление pip для xVibe"
    Invoke-Checked $Python @("-m", "pip", "install", $Source, "soundfile") "Установка xVibePocketTTS"
}

function Install-Silero {
    $Python = New-PythonEnvironment "silero"
    Invoke-Checked $Python @("-m", "pip", "install", "--upgrade", "pip") "Обновление pip для Silero"
    Invoke-Checked $Python @("-m", "pip", "install", "torch", "torchaudio", "--index-url", "https://download.pytorch.org/whl/cpu") "Установка PyTorch CPU для Silero"
    Invoke-Checked $Python @("-m", "pip", "install", "omegaconf", "soundfile") "Установка зависимостей Silero"
}

function Install-VoskTts {
    $Python = New-PythonEnvironment "vosk"
    Invoke-Checked $Python @("-m", "pip", "install", "--upgrade", "pip") "Обновление pip для Vosk-TTS"
    Invoke-Checked $Python @("-m", "pip", "install", "vosk-tts", "soundfile") "Установка Vosk-TTS"

    $ModelPath = Join-Path $ModelRoot "vosk-model-tts-ru-0.9-multi"
    if (-not (Test-Path $ModelPath)) {
        $Zip = Join-Path $LabRoot "tmp\vosk-model-tts-ru-0.9-multi.zip"
        New-Item -ItemType Directory -Force -Path (Split-Path $Zip -Parent) | Out-Null
        Write-Host "Скачивание русской модели Vosk-TTS (около 1 ГБ)..."
        Invoke-WebRequest -UseBasicParsing -Uri "https://alphacephei.com/vosk/models/vosk-model-tts-ru-0.9-multi.zip" -OutFile $Zip
        Expand-Archive -Path $Zip -DestinationPath $ModelRoot -Force
        Remove-Item $Zip -Force
    }
    if (-not (Test-Path $ModelPath)) { throw "После распаковки не найдена модель Vosk-TTS." }
}

function Install-Xtts {
    $Python = New-PythonEnvironment "xtts"
    Invoke-Checked $Python @("-m", "pip", "install", "--upgrade", "pip") "Обновление pip для XTTS"
    Invoke-Checked $Python @("-m", "pip", "install", "torch", "torchaudio", "torchcodec", "--index-url", "https://download.pytorch.org/whl/cpu") "Установка PyTorch CPU для XTTS"
    Invoke-Checked $Python @("-m", "pip", "install", "coqui-tts==0.28.0", "transformers<5") "Установка Coqui TTS / XTTS-v2"
}

$Tasks = if ($Engine -eq "all") { @("piper", "xvibe", "silero", "vosk", "xtts") } else { @($Engine) }
$Failures = @()
foreach ($Task in $Tasks) {
    try {
        switch ($Task) {
            "piper"  { Install-PiperAssets }
            "xvibe"  { Install-XVibe }
            "silero" { Install-Silero }
            "vosk"   { Install-VoskTts }
            "xtts"   { Install-Xtts }
        }
        Write-Host ("Готово: " + $Task) -ForegroundColor Green
    }
    catch {
        $Failures += ($Task + ": " + $_.Exception.Message)
        Write-Warning ("Не удалось установить " + $Task + ". Остальные движки будут обработаны отдельно.")
    }
}

Write-Host ""
Write-Host "Установка лаборатории завершена."
if ($Failures.Count -gt 0) {
    Write-Host "Ошибки:" -ForegroundColor Yellow
    $Failures | ForEach-Object { Write-Host (" - " + $_) -ForegroundColor Yellow }
    exit 1
}
Write-Host "Следующий шаг: запусти tools\tts-lab\Run-TtsLab.ps1" -ForegroundColor Green
