$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$Venv = Join-Path $PSScriptRoot ".training-venv"
$Python = Join-Path $Venv "Scripts\python.exe"
$Requirements = Join-Path $PSScriptRoot "requirements-training.txt"

if (-not (Get-Command py -ErrorAction SilentlyContinue)) {
    throw "Python Launcher py.exe is required. Install Python 3.12 x64 on the main PC."
}

if (-not (Test-Path $Python)) {
    Write-Host "Creating an isolated training environment..." -ForegroundColor Cyan
    py -3.12 -m venv $Venv
    if ($LASTEXITCODE -ne 0) { throw "Could not create the training environment." }
}

Write-Host "Installing training dependencies (PyTorch and Transformers)..." -ForegroundColor Cyan
& $Python -m pip install --upgrade pip
if ($LASTEXITCODE -ne 0) { throw "pip upgrade failed." }
& $Python -m pip install -r $Requirements
if ($LASTEXITCODE -ne 0) { throw "Training dependency installation failed." }

Push-Location $Root
try {
    Write-Host "Fine-tuning RuBERT-tiny2 and exporting ONNX INT8..." -ForegroundColor Cyan
    & $Python "tools/intent/Train-RuBertIntent.py" --dataset "tools/intent/dataset/commands.jsonl" --output "assets/intent" --epochs 8
    if ($LASTEXITCODE -ne 0) { throw "RuBERT training or ONNX export failed." }
}
finally {
    Pop-Location
}
