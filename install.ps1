$ErrorActionPreference = "Stop"

Write-Host "Creating Python 3.12 virtual environment..."
py -3.12 -m venv .venv

$python = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
if (-not (Test-Path $python)) {
    throw "Python 3.12 was not found. Install Python 3.12 and run this script again."
}

Write-Host "Updating pip..."
& $python -m pip install --upgrade pip

Write-Host "Installing STT and audio dependencies..."
& $python -m pip install -e .

Write-Host "Installing Qwen3-TTS..."
& $python -m pip install qwen-tts

Write-Host ""
Write-Host "Installation completed."
Write-Host "Activate with: .\.venv\Scripts\Activate.ps1"
Write-Host "Then run: python -m stt_work_tts"
