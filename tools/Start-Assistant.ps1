$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$AppArguments = @($args | ForEach-Object { [string]$_ })

$Project = Join-Path $Root "src\STT.WorkTTS\STT.WorkTTS.csproj"
$PortableExe = Join-Path $Root "STT.WorkTTS.exe"
$UsePortable = $false

if (-not (Test-Path $Project)) {
    if (Test-Path $PortableExe) {
        $UsePortable = $true
    }
    else {
        throw "Не найден ни исходный проект, ни portable EXE в $Root."
    }
}

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Invoke-SetupScript([string]$Path, [string[]]$Arguments = @()) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Path @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Подготовка завершилась с кодом \${LASTEXITCODE}: $Path"
    }
}

function Test-HttpHealth([string]$Url) {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 2
        return ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300)
    }
    catch {
        return $false
    }
}

function Test-TcpPort([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $pending = $client.BeginConnect("127.0.0.1", $Port, $null, $null)
        if (-not $pending.AsyncWaitHandle.WaitOne(500)) {
            return $false
        }
        $client.EndConnect($pending)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

$IntentDemo = $AppArguments -contains "--intent-demo"
$SttDemo = $AppArguments -contains "--stt-demo"
$ListVoices = $AppArguments -contains "--list-voices"
$SkipTts = $IntentDemo -or $SttDemo -or $ListVoices

$TtsEngine = "silero"
$TtsEngineExplicit = $false
for ($i = 0; $i -lt $AppArguments.Count; $i++) {
    if ($AppArguments[$i] -in @("--tts", "--tts-engine") -and ($i + 1) -lt $AppArguments.Count) {
        $TtsEngine = $AppArguments[$i + 1].ToLowerInvariant()
        $TtsEngineExplicit = $true
        break
    }
}

if ($TtsEngine -eq "x-vibe") { $TtsEngine = "xvibe" }
if ($TtsEngine -notin @("silero", "xvibe", "piper", "sapi")) {
    throw "Неизвестный TTS '$TtsEngine'. Доступны silero и xvibe."
}

if ($UsePortable) {
    $SettingsPath = Join-Path $Root "appsettings.json"
}
else {
    $SettingsPath = Join-Path $Root "src\STT.WorkTTS\appsettings.json"
}

if (-not (Test-Path $SettingsPath)) {
    throw "Не найден файл настроек: $SettingsPath"
}

if ($ListVoices) {
    if ($UsePortable) {
        & $PortableExe @AppArguments
    }
    else {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw "Не найден .NET SDK. Установи .NET 10 SDK."
        }
        & dotnet run --project $Project --configuration Release -- @AppArguments
    }
    exit $LASTEXITCODE
}

Write-Host "STT Work TTS — проверка локальных компонентов" -ForegroundColor Cyan

if (-not $UsePortable -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Не найден .NET SDK. Установи .NET 10 SDK."
}

$Settings = Get-Content -Raw -Path $SettingsPath | ConvertFrom-Json
if (-not $TtsEngineExplicit) {
    $TtsEngine = ([string]$Settings.Tts.Engine).ToLowerInvariant()
    if ($TtsEngine -eq "x-vibe") { $TtsEngine = "xvibe" }
}
if ($TtsEngine -notin @("silero", "xvibe", "piper", "sapi")) {
    throw "Неизвестный TTS в appsettings.json: $TtsEngine"
}

if (-not $IntentDemo) {
    $VoskModelDirectory = Join-Path $Root "assets\models\vosk-model-small-ru-0.22"
    $VoskMarker = Join-Path $VoskModelDirectory "conf\model.conf"
    $VoskAcousticModel = Join-Path $VoskModelDirectory "am\final.mdl"
    $VoskValid = (Test-Path $VoskMarker) -and (Test-Path $VoskAcousticModel)
    if ($VoskValid) {
        $VoskValid = (Get-Item $VoskAcousticModel).Length -gt 1000000
    }

    if (-not $VoskValid) {
        Write-Step "Русская модель STT Vosk отсутствует — устанавливаю"
        Invoke-SetupScript (Join-Path $Root "tools\Install-VoskModel.ps1")
    }
    else {
        Write-Host "[OK] Vosk STT model"
    }
}

if ($Settings.Intent.Enabled) {
    Write-Step "Проверяю компактную модель Qwen3-0.6B для команд"
    Invoke-SetupScript (Join-Path $Root "tools\\intent\\Install-FastIntentModel.ps1")

    $IntentEndpoint = [Uri]$Settings.Intent.Endpoint
    $HealthUrl = "$($IntentEndpoint.Scheme)://$($IntentEndpoint.Authority)/health"
    if (Test-HttpHealth $HealthUrl) {
        Write-Host "[OK] Быстрая модель отвечает: $HealthUrl" -ForegroundColor Green
    }
    elseif (Test-TcpPort $IntentEndpoint.Port) {
        Write-Step "Порт быстрой модели занят, жду её загрузки"
        $ServerReady = $false
        for ($Attempt = 0; $Attempt -lt 60; $Attempt++) {
            Start-Sleep -Seconds 2
            if (Test-HttpHealth $HealthUrl) {
                $ServerReady = $true
                break
            }
        }
        if (-not $ServerReady) {
            throw "Порт $($IntentEndpoint.Port) занят, но быстрая модель не стала готовой. Проверь data\\logs."
        }
        Write-Host "[OK] Быстрая модель готова: $HealthUrl" -ForegroundColor Green
    }
    else {
        $ServerPath = Join-Path $env:USERPROFILE ".docker\\bin\\inference\\llama-server.exe"
        $ServerCommand = Get-Command "llama-server.exe" -ErrorAction SilentlyContinue
        if ($ServerCommand) { $ServerPath = $ServerCommand.Source }
        if (-not (Test-Path $ServerPath)) {
            throw "Не найден llama-server.exe. Ожидаемый путь: $ServerPath"
        }

        Write-Step "Запускаю быструю модель Qwen3-0.6B"
        Invoke-SetupScript (Join-Path $Root "tools\\intent\\Start-FastIntentModel.ps1") @(
            "-ServerPath", $ServerPath,
            "-Port", $IntentEndpoint.Port.ToString(),
            "-Background"
        )
    }
}
else {
    Write-Host "[OK] Intent router выключен в appsettings.json"
}

if ($Settings.Generation.Enabled) {
    $GenerationModelPath = Join-Path $Root ([string]$Settings.Generation.ModelPath)
    if (Test-Path $GenerationModelPath) {
        Write-Host "[OK] Веса Qwen 4B уже установлены; сервер запустится по требованию." -ForegroundColor Green
    }
    else {
        Write-Host "[INFO] Qwen 4B пока не скачивается. Загрузка начнётся только при первом сложном запросе или генерации текста." -ForegroundColor Cyan
    }
}

if (-not $SkipTts) {
    if ($TtsEngine -in @("silero", "xvibe")) {
        $PythonLauncher = Get-Command py -ErrorAction SilentlyContinue
        if (-not $PythonLauncher) {
            throw "Для Silero/xVibe нужен Python 3.12 с py.exe. Установи Python 3.12 и повтори запуск."
        }

        & py -3.12 --version
        if ($LASTEXITCODE -ne 0) {
            throw "Python 3.12 не найден. Установи Python 3.12 x64 и включи py launcher."
        }

        if ($TtsEngine -eq "silero") {
            $TtsPython = Join-Path $Root "tools\tts-lab\envs\silero\Scripts\python.exe"
            $Imports = "import torch, soundfile, omegaconf"
            $InstallName = "silero"
            $Worker = Join-Path $Root "tools\tts-runtime\silero_worker.py"
        }
        else {
            $TtsPython = Join-Path $Root "tools\tts-lab\vendor\xVibePocketTTS\.venv\Scripts\python.exe"
            $Imports = "import torch, soundfile, xvibe_pocket_tts"
            $InstallName = "xvibe"
            $Worker = Join-Path $Root "tools\tts-runtime\xvibe_worker.py"
        }

        $TtsReady = $false
        if (Test-Path $TtsPython) {
            & $TtsPython -c $Imports *> $null
            $TtsReady = ($LASTEXITCODE -eq 0)
        }

        if (-not $TtsReady) {
            Write-Step "TTS $TtsEngine не установлен или повреждён — готовлю окружение"
            Invoke-SetupScript (Join-Path $Root "tools\tts-lab\Install-TtsLab.ps1") @("-Engine", $InstallName)

            if (-not (Test-Path $TtsPython)) {
                throw "Установка $TtsEngine завершилась, но Python окружение не появилось: $TtsPython"
            }
            & $TtsPython -c $Imports
            if ($LASTEXITCODE -ne 0) {
                throw "Не удалось проверить Python-зависимости $TtsEngine."
            }
        }
        else {
            Write-Host "[OK] Python TTS environment: $TtsEngine"
        }

        if (-not (Test-Path $Worker)) {
            throw "Не найден адаптер TTS: $Worker"
        }
    }
    elseif ($TtsEngine -eq "piper") {
        $PiperExe = Join-Path $Root "assets\piper\piper.exe"
        $PiperVoice = Join-Path $Root "assets\voices\ru_RU-denis-medium.onnx"
        if (-not (Test-Path $PiperExe) -or -not (Test-Path $PiperVoice)) {
            Write-Step "Piper отсутствует — устанавливаю ресурсы"
            Invoke-SetupScript (Join-Path $Root "tools\tts-lab\Install-TtsLab.ps1") @("-Engine", "piper")
        }
    }
    elseif ($TtsEngine -eq "sapi") {
        Write-Host "[INFO] Используется установленный в Windows SAPI voice."
    }
}

Write-Host ""
Write-Host "Все обязательные компоненты проверены. Запускаю помощника." -ForegroundColor Green
if ($UsePortable) {
    & $PortableExe @AppArguments
}
else {
    & dotnet run --project $Project --configuration Release -- @AppArguments
}
exit $LASTEXITCODE
