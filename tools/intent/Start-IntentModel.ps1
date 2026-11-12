[CmdletBinding()]
param(
    [string]$ServerPath = "llama-server.exe",
    [int]$Port = 8082,
    [int]$ContextSize = 4096,
    [string]$GpuLayers = "auto",
    [switch]$Background,
    [int]$StartupTimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ModelPath = Join-Path $Root "models\intent\Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
$Endpoint = "http://127.0.0.1:$Port"
$HealthUrl = "$Endpoint/health"

function Test-IntentServer {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $HealthUrl -TimeoutSec 2
        return ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300)
    }
    catch {
        return $false
    }
}

function Test-PortListening([int]$CheckPort) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $pending = $client.BeginConnect("127.0.0.1", $CheckPort, $null, $null)
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

if (Test-IntentServer) {
    Write-Host "Qwen уже запущен: $Endpoint" -ForegroundColor Green
    exit 0
}

if (Test-PortListening $Port) {
    Write-Host "Порт $Port уже занят; проверяю, не загружается ли модель..." -ForegroundColor Yellow
    for ($Attempt = 0; $Attempt -lt 90; $Attempt++) {
        Start-Sleep -Seconds 2
        if (Test-IntentServer) {
            Write-Host "Qwen готов: $Endpoint" -ForegroundColor Green
            exit 0
        }
    }
    throw "Порт $Port занят, но endpoint $HealthUrl не стал готовым. Не запускаю второй сервер."
}

if (-not (Test-Path $ModelPath)) {
    throw "Модель не найдена. Сначала запусти Install-QwenIntentModel.ps1."
}

$ServerCommand = Get-Command $ServerPath -ErrorAction SilentlyContinue
if ($ServerCommand) {
    $ResolvedServer = $ServerCommand.Source
}
elseif (Test-Path $ServerPath) {
    $ResolvedServer = (Resolve-Path $ServerPath).Path
}
else {
    throw "Не найден llama-server.exe: $ServerPath"
}

$ServerArgs = @(
    "--model", $ModelPath,
    "--alias", "stt-intent",
    "--host", "127.0.0.1",
    "--port", $Port,
    "--ctx-size", $ContextSize,
    "--gpu-layers", $GpuLayers,
    "--fit", "on",
    "--jinja"
)

if (-not $Background) {
    Write-Host "Запуск локальной модели Qwen3-4B..." -ForegroundColor Cyan
    Write-Host "Endpoint: $Endpoint/v1/chat/completions"
    Write-Host "Чтобы завершить сервер, нажми Ctrl+C."
    Write-Host ""
    & $ResolvedServer @ServerArgs
    if ($LASTEXITCODE -ne 0) {
        throw "llama-server завершился с кодом $LASTEXITCODE."
    }
    exit 0
}

$LogDirectory = Join-Path $Root "data\logs"
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$StdOutLog = Join-Path $LogDirectory "llama-server.stdout.log"
$StdErrLog = Join-Path $LogDirectory "llama-server.stderr.log"
Remove-Item $StdOutLog, $StdErrLog -Force -ErrorAction SilentlyContinue

$ArgumentLine = ($ServerArgs | ForEach-Object {
    '"' + ([string]$_).Replace('"', '\"') + '"'
}) -join " "

Write-Host "Qwen не запущен. Поднимаю llama-server..." -ForegroundColor Cyan
$StartParameters = @{
    FilePath = $ResolvedServer
    ArgumentList = $ArgumentLine
    WorkingDirectory = (Split-Path $ResolvedServer -Parent)
    WindowStyle = "Hidden"
    PassThru = $true
    RedirectStandardOutput = $StdOutLog
    RedirectStandardError = $StdErrLog
}
$Process = Start-Process @StartParameters

for ($Second = 0; $Second -lt $StartupTimeoutSeconds; $Second += 2) {
    Start-Sleep -Seconds 2

    if (Test-IntentServer) {
        Write-Host "Qwen готов: $Endpoint" -ForegroundColor Green
        Write-Host "PID: $($Process.Id)"
        exit 0
    }

    try { $Process.Refresh() } catch { }
    if ($Process.HasExited) {
        $Details = if (Test-Path $StdErrLog) {
            (Get-Content $StdErrLog -Tail 40 -ErrorAction SilentlyContinue) -join [Environment]::NewLine
        } else { "Лог сервера не создан." }
        throw ("llama-server завершился при запуске (код {0}).{1}{2}" -f $Process.ExitCode, [Environment]::NewLine, $Details)
    }
}

throw "llama-server не стал отвечать за $StartupTimeoutSeconds секунд. Логи: $StdErrLog"
