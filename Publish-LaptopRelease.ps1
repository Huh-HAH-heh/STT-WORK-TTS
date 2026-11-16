[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag
)

$ErrorActionPreference = "Stop"
$Repository = "Huh-HAH-heh/STT-WORK-TTS"
$Branch = "feature/lightweight-offline"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Archive = Join-Path $Root "STT-WORK-TTS-Windows-x64.zip"
$Checksum = "$Archive.sha256"

if ($Tag -notmatch "^laptop-v\d+\.\d+\.\d+$") {
    throw "Tag format must be laptop-vMAJOR.MINOR.PATCH, for example laptop-v1.0.0."
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "Git is required."
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI is required. Install it with: winget install --id GitHub.cli"
}

$CurrentBranch = (& git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $CurrentBranch -ne $Branch) {
    throw "Switch to $Branch before publishing. Current branch: $CurrentBranch"
}

$Changes = & git status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw "Could not read Git working-tree status."
}
if ($Changes) {
    throw "Commit and push source changes before publishing. The working tree is not clean."
}

if (-not (Test-Path $Archive)) {
    throw "Portable ZIP not found. Run .\build_portable.ps1 first."
}
if (-not (Test-Path $Checksum)) {
    throw "SHA-256 file not found. Rebuild the archive with .\build_portable.ps1."
}

$ExpectedHash = ((Get-Content -Raw $Checksum).Trim() -split "\s+")[0].ToLowerInvariant()
$ActualHash = (Get-FileHash -Path $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ExpectedHash -ne $ActualHash) {
    throw "ZIP checksum mismatch. Rebuild the archive before publishing."
}

& gh auth status --hostname github.com
if ($LASTEXITCODE -ne 0) {
    throw "Sign in first by running: gh auth login"
}

Write-Host "Pushing $Branch..."
& git push origin $Branch
if ($LASTEXITCODE -ne 0) {
    throw "Could not push $Branch."
}

Write-Host "Publishing $Tag to GitHub Releases..."
$ReleaseArguments = @(
    "release", "create", $Tag,
    $Archive,
    $Checksum,
    "--repo", $Repository,
    "--target", $Branch,
    "--title", "STT Work TTS $Tag",
    "--generate-notes"
)
& gh @ReleaseArguments
if ($LASTEXITCODE -ne 0) {
    throw "GitHub Release creation failed."
}

Write-Host ""
Write-Host "Published release: https://github.com/$Repository/releases/tag/$Tag" -ForegroundColor Green
Write-Host "Portable ZIP and SHA-256 checksum are attached."
