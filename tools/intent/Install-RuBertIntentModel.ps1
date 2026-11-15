$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ModelDirectory = Join-Path $Root "assets\intent"
$RequiredFiles = @(
    "rubert-tiny2-intent-int8.onnx",
    "vocab.txt",
    "labels.json"
)

$Missing = @($RequiredFiles | Where-Object { -not (Test-Path (Join-Path $ModelDirectory $_)) })
if ($Missing.Count -gt 0) {
    throw ("RuBERT-tiny2 is not trained yet. Missing: {0}. On the main PC run .\tools\intent\Train-RuBertIntent.ps1, then copy the complete assets\intent folder to this PC." -f ($Missing -join ", "))
}

$Model = Join-Path $ModelDirectory "rubert-tiny2-intent-int8.onnx"
if ((Get-Item $Model).Length -lt 1000000) {
    throw "ONNX model file is suspiciously small or incomplete: $Model"
}

$Labels = Get-Content (Join-Path $ModelDirectory "labels.json") -Raw | ConvertFrom-Json
if ($Labels.Count -lt 5) {
    throw "labels.json contains too few intent labels."
}

Write-Host "RuBERT-tiny2 ONNX INT8 model is ready." -ForegroundColor Green
Write-Host ("Model size: {0:N1} MB" -f ((Get-Item $Model).Length / 1MB))
Write-Host ("Intent labels: {0}" -f $Labels.Count)
