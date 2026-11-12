[CmdletBinding()]
param([string]$ModelDirectory = "")

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($ModelDirectory)) {
    $ModelDirectory = Join-Path $Root "models\intent"
}

$ModelName = "Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
$ModelPath = Join-Path $ModelDirectory $ModelName
$DownloadPath = "$ModelPath.download"

# Pin the exact Hugging Face revision whose SHA-256 is recorded below.
$Revision = "3e772c15a5192f387bcdfc1d24341034bed9c22f"
$ModelUrl = "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/$Revision/$ModelName"
$ExpectedSha256 = "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597"

New-Item -ItemType Directory -Force -Path $ModelDirectory | Out-Null

if (Test-Path $ModelPath) {
    $ExistingHash = (Get-FileHash -Path $ModelPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ExistingHash -eq $ExpectedSha256) {
        Write-Host "Модель уже установлена и SHA-256 совпадает." -ForegroundColor Green
        Write-Host $ModelPath
        exit 0
    }

    Write-Warning "Найден существующий GGUF с другим SHA-256. Он будет скачан заново."
    Remove-Item $ModelPath -Force
}

$CurlCommand = Get-Command "curl.exe" -ErrorAction SilentlyContinue
if (-not $CurlCommand) {
    throw "Не найден curl.exe. Обнови Windows или установи современный curl для Windows, затем повтори запуск."
}
$CurlPath = $CurlCommand.Source

Write-Host "Загрузка Qwen3-4B-Instruct-2507 Q4_K_M (~2.5 GB)..." -ForegroundColor Cyan
if (Test-Path $DownloadPath) {
    $PartialSize = (Get-Item $DownloadPath).Length
    if ($PartialSize -gt 0) {
        Write-Host ("Найден частичный файл: {0:N0} MB. Попробую продолжить его загрузку." -f ($PartialSize / 1MB)) -ForegroundColor Yellow
    }
}

$DownloadComplete = $false
$MaxAttempts = 12

for ($Attempt = 1; $Attempt -le $MaxAttempts; $Attempt++) {
    $CurlArguments = @(
        "--location",
        "--fail",
        "--show-error",
        "--connect-timeout", "30",
        "--speed-time", "60",
        "--speed-limit", "1024",
        "--progress-bar",
        "--output", $DownloadPath
    )

    if ((Test-Path $DownloadPath) -and (Get-Item $DownloadPath).Length -gt 0) {
        $CurlArguments += @("--continue-at", "-")
    }

    $CurlArguments += $ModelUrl

    Write-Host ("Попытка {0} из {1}..." -f $Attempt, $MaxAttempts) -ForegroundColor Cyan
    & $CurlPath @CurlArguments
    $CurlExitCode = $LASTEXITCODE

    if ($CurlExitCode -eq 0) {
        $DownloadComplete = $true
        break
    }

    if ($CurlExitCode -eq 33 -and (Test-Path $DownloadPath)) {
        Write-Warning "Сервер не принял продолжение загрузки. Удаляю неполный файл и начну заново."
        Remove-Item $DownloadPath -Force -ErrorAction SilentlyContinue
    }
    elseif (Test-Path $DownloadPath) {
        $PartialSize = (Get-Item $DownloadPath).Length
        Write-Warning ("Соединение оборвалось (curl exit {0}). Сохранено {1:N0} MB." -f $CurlExitCode, ($PartialSize / 1MB))
    }
    else {
        Write-Warning ("Не удалось подключиться (curl exit {0})." -f $CurlExitCode)
    }

    if ($Attempt -lt $MaxAttempts) {
        $Delay = [Math]::Min(30, 5 * $Attempt)
        Write-Host ("Повтор через {0} сек. Частичный файл будет сохранён." -f $Delay) -ForegroundColor Yellow
        Start-Sleep -Seconds $Delay
    }
}

if (-not $DownloadComplete) {
    $PartialSize = if (Test-Path $DownloadPath) { (Get-Item $DownloadPath).Length } else { 0 }
    throw ("Загрузка не завершилась после {0} попыток. Частичный файл сохранён ({1:N0} MB): {2}{3}Запусти этот скрипт ещё раз — он продолжит загрузку." -f $MaxAttempts, ($PartialSize / 1MB), $DownloadPath, [Environment]::NewLine)
}

Write-Host "Проверка SHA-256..." -ForegroundColor Cyan
$ActualHash = (Get-FileHash -Path $DownloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ActualHash -ne $ExpectedSha256) {
    Remove-Item $DownloadPath -Force -ErrorAction SilentlyContinue
    throw "SHA-256 не совпадает. Ожидался $ExpectedSha256, получен $ActualHash. Файл удалён."
}

Move-Item -Path $DownloadPath -Destination $ModelPath -Force
Write-Host "Модель установлена, контрольная сумма совпадает." -ForegroundColor Green
Write-Host $ModelPath
