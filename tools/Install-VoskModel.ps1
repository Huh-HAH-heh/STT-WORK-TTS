param(
    [string]$ModelName = "vosk-model-small-ru-0.22"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Assets = Join-Path $Root "assets"
$ModelDirectory = Join-Path $Assets ("models\" + $ModelName)
$Marker = Join-Path $ModelDirectory "conf\model.conf"
$AcousticModel = Join-Path $ModelDirectory "am\final.mdl"
$TempDirectory = Join-Path $Root ".build-temp"
$PartialArchive = Join-Path $TempDirectory ($ModelName + ".zip.download")
$Archive = Join-Path $TempDirectory ($ModelName + ".zip")
$ExtractDirectory = Join-Path $TempDirectory ($ModelName + "-extract")
$Url = "https://alphacephei.com/vosk/models/$ModelName.zip"

if ((Test-Path $Marker) -and (Test-Path $AcousticModel) -and (Get-Item $AcousticModel).Length -gt 1000000) {
    Write-Host "Vosk model is already installed: $ModelDirectory" -ForegroundColor Green
    exit 0
}

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
    throw "curl.exe was not found. Install Windows curl or run this script on Windows 10/11."
}

New-Item -ItemType Directory -Force -Path (Split-Path $ModelDirectory -Parent), $TempDirectory | Out-Null

if (-not (Test-Path $PartialArchive)) {
    New-Item -ItemType File -Path $PartialArchive -Force | Out-Null
}

$Downloaded = $false
for ($Attempt = 1; $Attempt -le 8; $Attempt++) {
    Write-Host ("Downloading Vosk Russian STT model (attempt {0}/8)..." -f $Attempt)
    $CurlArguments = @(
        "--location", "--fail", "--show-error",
        "--connect-timeout", "30",
        "--retry", "2", "--retry-delay", "3",
        "--progress-bar",
        "--continue-at", "-",
        "--output", $PartialArchive,
        $Url
    )

    & curl.exe @CurlArguments
    $ExitCode = $LASTEXITCODE

    if ($ExitCode -eq 0) {
        $Downloaded = $true
        break
    }

    if ($ExitCode -eq 33) {
        Write-Warning "The server refused to resume the partial archive; restarting the download."
        Remove-Item $PartialArchive -Force -ErrorAction SilentlyContinue
        New-Item -ItemType File -Path $PartialArchive -Force | Out-Null
    }

    if ($Attempt -lt 8) {
        Start-Sleep -Seconds ([Math]::Min(3 * $Attempt, 20))
    }
}

if (-not $Downloaded) {
    $Size = if (Test-Path $PartialArchive) {
        [Math]::Round((Get-Item $PartialArchive).Length / 1MB, 1)
    } else { 0 }
    throw "Vosk download failed. Partial download: $PartialArchive ($Size MB). Run this script again to resume."
}

Move-Item -Path $PartialArchive -Destination $Archive -Force
Remove-Item $ExtractDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $ExtractDirectory | Out-Null

Write-Host "Extracting model..."
Expand-Archive -Path $Archive -DestinationPath $ExtractDirectory -Force
$ExtractedModel = Join-Path $ExtractDirectory $ModelName
if (-not (Test-Path (Join-Path $ExtractedModel "conf\model.conf"))) {
    throw "Archive does not contain the expected Vosk model structure: $ExtractedModel"
}

New-Item -ItemType Directory -Force -Path $ModelDirectory | Out-Null
Copy-Item (Join-Path $ExtractedModel "*") $ModelDirectory -Recurse -Force

if (-not (Test-Path $Marker)) {
    throw "Vosk model extraction did not finish correctly."
}

Remove-Item $ExtractDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $Archive -Force -ErrorAction SilentlyContinue
Write-Host "Vosk STT model installed: $ModelDirectory" -ForegroundColor Green
