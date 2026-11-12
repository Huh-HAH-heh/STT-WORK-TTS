[CmdletBinding()]
param(
    [string]$ServerPath = "llama-server.exe",
    [int]$Port = 8082,
    [int]$ContextSize = 4096,
    [string]$GpuLayers = "auto"
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ModelPath = Join-Path $Root "models\intent\Qwen3-4B-Instruct-2507-Q4_K_M.gguf"

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
    throw "Не найден llama-server.exe. Добавь его в PATH или передай -ServerPath с полным путём."
}

Write-Host "Запуск локальной модели Qwen3-4B..." -ForegroundColor Cyan
Write-Host "Endpoint: http://127.0.0.1:$Port/v1/chat/completions"
Write-Host "Чтобы завершить сервер, нажми Ctrl+C."
Write-Host ""

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

& $ResolvedServer @ServerArgs

if ($LASTEXITCODE -ne 0) {
    throw "llama-server завершился с кодом $LASTEXITCODE."
}
