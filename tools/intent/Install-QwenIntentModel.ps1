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
$ModelUrl = "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/$ModelName"
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

Remove-Item $DownloadPath -Force -ErrorAction SilentlyContinue
Write-Host "Загрузка Qwen3-4B-Instruct-2507 Q4_K_M (~2.5 GB)..." -ForegroundColor Cyan
Invoke-WebRequest -UseBasicParsing -Uri $ModelUrl -OutFile $DownloadPath

$ActualHash = (Get-FileHash -Path $DownloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ActualHash -ne $ExpectedSha256) {
    Remove-Item $DownloadPath -Force -ErrorAction SilentlyContinue
    throw "SHA-256 не совпадает. Ожидался $ExpectedSha256, получен $ActualHash."
}

Move-Item -Path $DownloadPath -Destination $ModelPath -Force
Write-Host "Модель установлена, контрольная сумма совпадает." -ForegroundColor Green
Write-Host $ModelPath
