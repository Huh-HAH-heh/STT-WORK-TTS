[CmdletBinding()]
param(
    [string]$ServerPath = "llama-server.exe",
    [int]$Port = 8083,
    [int]$ContextSize = 2048,
    [string]$GpuLayers = "auto",
    [switch]$Background,
    [int]$StartupTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ModelPath = Join-Path $Root "models\intent\Qwen3-0.6B.Q4_K_M.gguf"
$Endpoint = "http://127.0.0.1:$Port"
$HealthUrl = "$Endpoint/health"

function Test-FastIntentServer {
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
        if (-not $pending.AsyncWaitHandle.WaitOne(500)) { return $false }
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

if (Test-FastIntentServer) {
    Write-Host "Быстрая модель уже запущена: $Endpoint" -ForegroundColor Green
    exit 0
}

if (Test-PortListening $Port) {
    Write-Host "Порт $Port уже занят; жду загрузки быстрой модели..." -ForegroundColor Yellow
    for ($Attempt = 0; $Attempt -lt 60; $Attempt++) {
        Start-Sleep -Seconds 2
        if (Test-FastIntentServer) {
            Write-Host "Быстрая модель готова: $Endpoint" -ForegroundColor Green
            exit 0
        }
    }
    throw "Порт $Port занят, но endpoint $HealthUrl не стал готовым. Не запускаю второй сервер."
}

if (-not (Test-Path $ModelPath)) {
    throw "Модель не найдена. Сначала запусти Install-FastIntentModel.ps1."
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
    "--alias", "stt-fast-intent",
    "--host", "127.0.0.1",
    "--port", $Port,
    "--ctx-size", $ContextSize,
    "--gpu-layers", $GpuLayers,
    "--fit", "on",
    "--jinja"
)

if (-not $Background) {
    Write-Host "Запуск локальной модели Qwen3-0.6B..." -ForegroundColor Cyan
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
$StdOutLog = Join-Path $LogDirectory "llama-fast-intent.stdout.log"
$StdErrLog = Join-Path $LogDirectory "llama-fast-intent.stderr.log"
Remove-Item $StdOutLog, $StdErrLog -Force -ErrorAction SilentlyContinue

$ArgumentLine = ($ServerArgs | ForEach-Object {
    '"' + ([string]$_).Replace('"', '\"') + '"'
}) -join " "

Write-Host "Запускаю Qwen3-0.6B в фоне..." -ForegroundColor Cyan
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
    if (Test-FastIntentServer) {
        Write-Host "Быстрая модель готова: $Endpoint" -ForegroundColor Green
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

throw "Qwen3-0.6B не стала отвечать за $StartupTimeoutSeconds секунд. Логи: $StdErrLog"
