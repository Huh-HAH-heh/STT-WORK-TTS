$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$AppArguments = @($args | ForEach-Object { [string]$_ })

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Invoke-SetupScript([string]$Path, [string[]]$Arguments = @()) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Path @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Подготовка завершилась с кодом $LASTEXITCODE: $Path"
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

$IntentDemo = $AppArguments -contains "--intent-demo"
$SttDemo = $AppArguments -contains "--stt-demo"
$ListVoices = $AppArguments -contains "--list-voices"
$SkipTts = $IntentDemo -or $SttDemo -or $ListVoices

$TtsEngine = "silero"
for ($i = 0; $i -lt $AppArguments.Count; $i++) {
    if ($AppArguments[$i] -in @("--tts", "--tts-engine") -and ($i + 1) -lt $AppArguments.Count) {
        $TtsEngine = $AppArguments[$i + 1].ToLowerInvariant()
        break
    }
}

if ($TtsEngine -eq "x-vibe") { $TtsEngine = "xvibe" }
if ($TtsEngine -notin @("silero", "xvibe", "piper", "sapi")) {
    throw "Неизвестный TTS '$TtsEngine'. Доступны silero и xvibe."
}

if ($ListVoices) {
    $Project = Join-Path $Root "src\STT.WorkTTS\STT.WorkTTS.csproj"
    & dotnet run --project $Project --configuration Release -- @AppArguments
    exit $LASTEXITCODE
}

Write-Host "STT Work TTS — проверка локальных компонентов" -ForegroundColor Cyan

$Project = Join-Path $Root "src\STT.WorkTTS\STT.WorkTTS.csproj"
if (-not (Test-Path $Project)) {
    $PortableExe = Join-Path $Root "STT.WorkTTS.exe"
    if (Test-Path $PortableExe) {
        & $PortableExe @AppArguments
        exit $LASTEXITCODE
    }
    throw "Не найден проект приложения: $Project"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Не найден .NET SDK. Установи .NET 10 SDK."
}

$SettingsPath = Join-Path $Root "src\STT.WorkTTS\appsettings.json"
$Settings = Get-Content -Raw -Path $SettingsPath | ConvertFrom-Json

if (-not $IntentDemo) {
    $VoskMarker = Join-Path $Root "assets\models\vosk-model-small-ru-0.22\conf\model.conf"
    if (-not (Test-Path $VoskMarker)) {
        Write-Step "Русская модель STT Vosk отсутствует — устанавливаю"
        Invoke-SetupScript (Join-Path $Root "tools\Install-VoskModel.ps1")
    }
    else {
        Write-Host "[OK] Vosk STT model"
    }
}

if ($Settings.Intent.Enabled) {
    $IntentModel = Join-Path $Root "models\intent\Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
    if (-not (Test-Path $IntentModel)) {
        Write-Step "Локальная модель Qwen отсутствует — скачиваю"
        Invoke-SetupScript (Join-Path $Root "tools\intent\Install-QwenIntentModel.ps1")
    }
    else {
        Write-Host "[OK] Qwen GGUF model"
    }

    $IntentEndpoint = [Uri]$Settings.Intent.Endpoint
    $HealthUrl = "$($IntentEndpoint.Scheme)://$($IntentEndpoint.Authority)/health"
    if (Test-HttpHealth $HealthUrl) {
        Write-Host "[OK] llama-server отвечает: $HealthUrl" -ForegroundColor Green
    }
    else {
        $ServerPath = Join-Path $env:USERPROFILE ".docker\bin\inference\llama-server.exe"
        $ServerCommand = Get-Command "llama-server.exe" -ErrorAction SilentlyContinue
        if ($ServerCommand) {
            $ServerPath = $ServerCommand.Source
        }
        if (-not (Test-Path $ServerPath)) {
            throw "Qwen-сервер не запущен, а llama-server.exe не найден. Ожидаемый путь: $ServerPath"
        }

        Write-Step "Qwen не запущен — поднимаю llama-server"
        Invoke-SetupScript (Join-Path $Root "tools\intent\Start-IntentModel.ps1") @(
            "-ServerPath", $ServerPath,
            "-Port", $IntentEndpoint.Port.ToString(),
            "-Background"
        )
    }
}
else {
    Write-Host "[OK] Intent router выключен в appsettings.json"
}

if (-not $SkipTts) {
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
    }
    elseif ($TtsEngine -eq "xvibe") {
        $TtsPython = Join-Path $Root "tools\tts-lab\vendor\xVibePocketTTS\.venv\Scripts\python.exe"
        $Imports = "import torch, soundfile, xvibe_pocket_tts"
        $InstallName = "xvibe"
    }
    else {
        $TtsPython = $null
        $InstallName = $null
    }

    if ($TtsEngine -in @("silero", "xvibe")) {
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

        $Worker = if ($TtsEngine -eq "silero") {
            Join-Path $Root "tools\tts-runtime\silero_worker.py"
        } else {
            Join-Path $Root "tools\tts-runtime\xvibe_worker.py"
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
}

Write-Host ""
Write-Host "Все обязательные компоненты проверены. Запускаю помощника." -ForegroundColor Green
& dotnet run --project $Project --configuration Release -- @AppArguments
exit $LASTEXITCODE
