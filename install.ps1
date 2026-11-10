$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "This script creates the self-contained offline Windows archive."
Write-Host "The build computer needs Python 3.12 x64 and internet access once."
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "build_portable.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Portable archive build failed."
}
