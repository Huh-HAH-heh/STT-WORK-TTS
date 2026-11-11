[CmdletBinding()]
param(
    [string]$Text = "Привет! Рад тебя слышать. Кажется, мы наконец-то нашли время спокойно поговорить. Я могу удивляться, задавать вопросы и даже немного шутить. Проверим числа: семнадцать плюс двадцать пять — сорок два. Ну что, продолжим?",
    [string]$ReferenceWav,
    [switch]$NoPlayback
)

$ErrorActionPreference = "Continue"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$LabRoot = $PSScriptRoot
$OutputRoot = Join-Path $LabRoot "output"
$SessionName = Get-Date -Format "yyyyMMdd-HHmmss"
$SessionDir = Join-Path $OutputRoot $SessionName
$CacheRoot = Join-Path $LabRoot "cache"
$VoskModel = Join-Path $LabRoot "models\vosk-model-tts-ru-0.9-multi"
New-Item -ItemType Directory -Force -Path $SessionDir, $CacheRoot | Out-Null

$env:HF_HOME = Join-Path $CacheRoot "huggingface"
$env:TORCH_HOME = Join-Path $CacheRoot "torch"
$env:HF_HUB_CACHE = Join-Path $CacheRoot "huggingface\hub"
$env:TRANSFORMERS_CACHE = Join-Path $CacheRoot "huggingface\transformers"

if ($ReferenceWav) {
    if (-not (Test-Path $ReferenceWav)) { throw "Не найден WAV-образец: $ReferenceWav" }
    $ReferenceWav = (Resolve-Path $ReferenceWav).Path
}

$Cases = New-Object System.Collections.Generic.List[object]
$Failures = New-Object System.Collections.Generic.List[string]

function Add-Case {
    param([string]$Engine, [string]$Voice, [string]$Path)
    if (Test-Path $Path) {
        $Cases.Add([pscustomobject]@{ Engine = $Engine; Voice = $Voice; Path = $Path })
    }
}

function Invoke-PiperVoice {
    param([string]$Voice)
    $Exe = Join-Path $Root "assets\piper\piper.exe"
    $Model = Join-Path $LabRoot "models\piper\ru_RU-$Voice-medium.onnx"
    $Output = Join-Path $SessionDir "Piper_$Voice.wav"
    if (-not (Test-Path $Exe) -or -not (Test-Path $Model) -or -not (Test-Path "$Model.json")) {
        $Failures.Add("Piper " + $Voice + ": файлы не найдены. Запусти Install-TtsLab.ps1 -Engine piper.")
        return
    }
    try {
        $Info = New-Object System.Diagnostics.ProcessStartInfo
        $Info.FileName = $Exe
        $Q = [char]34
        $Info.WorkingDirectory = Split-Path $Exe -Parent
        $Info.UseShellExecute = $false
        $Info.RedirectStandardInput = $true
        $Info.CreateNoWindow = $true
        $Info.Arguments = "--model $Q$Model$Q --output_file $Q$Output$Q"
        $Process = New-Object System.Diagnostics.Process
        $Process.StartInfo = $Info
        if (-not $Process.Start()) { throw "Не удалось запустить Piper." }

        # Write UTF-8 bytes directly: console encodings on Windows may corrupt Cyrillic text.
        $InputBytes = [System.Text.Encoding]::UTF8.GetBytes($Text + [Environment]::NewLine)
        $InputStream = $Process.StandardInput.BaseStream
        $InputStream.Write($InputBytes, 0, $InputBytes.Length)
        $InputStream.Flush()
        $Process.StandardInput.Close()
        $Process.WaitForExit()
        if ($Process.ExitCode -ne 0 -or -not (Test-Path $Output)) { throw "Piper завершился с кодом $($Process.ExitCode)." }
        Add-Case "Piper" $Voice $Output
    }
    catch {
        $Failures.Add("Piper " + $Voice + ": " + $_.Exception.Message)
    }
}

function Invoke-PythonBatch {
    param([string]$Engine, [string]$Python, [string]$Script, [string[]]$Arguments)
    if (-not (Test-Path $Python)) {
        $Failures.Add($Engine + ": нет окружения. Запусти Install-TtsLab.ps1 -Engine " + $Engine + ".")
        return
    }
    if (-not (Test-Path $Script)) {
        $Failures.Add($Engine + ": не найден runner " + $Script)
        return
    }
    try {
        Write-Host ""
        Write-Host ("Генерация: " + $Engine) -ForegroundColor Cyan
        & $Python $Script @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Python runner завершился с кодом $LASTEXITCODE." }
    }
    catch {
        $Failures.Add($Engine + ": " + $_.Exception.Message)
    }
}

Write-Host "TTS LAB — одинаковый текст для всех голосов" -ForegroundColor Cyan
Write-Host ("Фраза: " + $Text)
Write-Host ("Результаты: " + $SessionDir)

foreach ($Voice in @("denis", "dmitri", "irina", "ruslan")) { Invoke-PiperVoice $Voice }

$XvibePython = Join-Path $LabRoot "vendor\xVibePocketTTS\.venv\Scripts\python.exe"
$XvibeArgs = @(
    "--text", $Text, "--output-dir", $SessionDir, "--voices",
    "male_deep", "female_silky", "male_authoritative"
)
if ($ReferenceWav) { $XvibeArgs += @("--reference-wav", $ReferenceWav) }
Invoke-PythonBatch "xvibe" $XvibePython (Join-Path $PSScriptRoot "python\xvibe_batch.py") $XvibeArgs
foreach ($Voice in @("male_deep", "female_silky", "male_authoritative")) {
    Add-Case "xVibe" $Voice (Join-Path $SessionDir "xVibe_$Voice.wav")
}
if ($ReferenceWav) { Add-Case "xVibe" "cloned-reference" (Join-Path $SessionDir "xVibe_clone.wav") }

$SileroPython = Join-Path $LabRoot "envs\silero\Scripts\python.exe"
Invoke-PythonBatch "silero" $SileroPython (Join-Path $PSScriptRoot "python\silero_batch.py") @(
    "--text", $Text, "--output-dir", $SessionDir, "--voices",
    "aidar", "baya", "kseniya", "xenia", "eugene"
)
foreach ($Voice in @("aidar", "baya", "kseniya", "xenia", "eugene")) {
    Add-Case "Silero" $Voice (Join-Path $SessionDir "Silero_$Voice.wav")
}

$VoskPython = Join-Path $LabRoot "envs\vosk\Scripts\python.exe"
Invoke-PythonBatch "vosk" $VoskPython (Join-Path $PSScriptRoot "python\vosk_batch.py") @(
    "--text", $Text, "--output-dir", $SessionDir, "--model", $VoskModel, "--voices",
    "0", "1", "2", "3", "4"
)
foreach ($Voice in 0..4) { Add-Case "Vosk-TTS" ("speaker-" + $Voice) (Join-Path $SessionDir "Vosk_$Voice.wav") }

$XttsPython = Join-Path $LabRoot "envs\xtts\Scripts\python.exe"
$RunXtts = $false
if (Test-Path $XttsPython) {
    Write-Host ""
    Write-Host "XTTS-v2 использует Coqui Public Model License: только некоммерческое использование модели и её аудиовыхода." -ForegroundColor Yellow
    $Consent = Read-Host "Для личного/учебного некоммерческого теста принимаешь условия XTTS? (да/нет)"
    $RunXtts = $Consent -match "^(да|д|y|yes)$"
}
if ($RunXtts) {
    $env:COQUI_TOS_AGREED = "1"
    $env:TTS_HOME = Join-Path $CacheRoot "xtts"
    $XttsArgs = @(
        "--text", $Text, "--output-dir", $SessionDir, "--voices",
        "Ana Florence", "Andrew Chipper"
    )
    if ($ReferenceWav) { $XttsArgs += @("--speaker-wav", $ReferenceWav) }
    Invoke-PythonBatch "xtts" $XttsPython (Join-Path $PSScriptRoot "python\xtts_batch.py") $XttsArgs
    Add-Case "XTTS-v2" "Ana Florence" (Join-Path $SessionDir "XTTS_Ana_Florence.wav")
    Add-Case "XTTS-v2" "Andrew Chipper" (Join-Path $SessionDir "XTTS_Andrew_Chipper.wav")
    if ($ReferenceWav) { Add-Case "XTTS-v2" "cloned-reference" (Join-Path $SessionDir "XTTS_Cloned.wav") }
}
elseif (Test-Path $XttsPython) {
    Write-Host "XTTS пропущен: условия лицензии не приняты." -ForegroundColor Yellow
}
else {
    $Failures.Add("XTTS: нет окружения. Запусти Install-TtsLab.ps1 -Engine xtts.")
}

if ($Cases.Count -eq 0) {
    Write-Warning "Не создано ни одного WAV. Проверь ошибки установки выше."
    $Failures | ForEach-Object { Write-Host (" - " + $_) -ForegroundColor Yellow }
    exit 1
}

Write-Host ""
Write-Host ("Создано аудиофайлов: " + $Cases.Count) -ForegroundColor Green
$RatingsPath = Join-Path $LabRoot "ratings.csv"
foreach ($Case in $Cases) {
    Write-Host ""
    Write-Host "----------------------------------------" -ForegroundColor DarkGray
    Write-Host ($Case.Engine + " / " + $Case.Voice) -ForegroundColor Cyan
    Write-Host $Case.Path
    if ($NoPlayback) {
        continue
    }
    try {
        $Player = [System.Media.SoundPlayer]::new($Case.Path)
        $Player.Load()
        $Player.PlaySync()
        $Player.Dispose()
    }
    catch {
        Write-Warning "Не удалось воспроизвести WAV: $($_.Exception.Message)"
        continue
    }
    $Rating = Read-Host "Оценка 1-5 (Enter — пропустить)"
    if ($Rating -match "^[1-5]$") {
        $Notes = Read-Host "Комментарий (необязательно)"
        [pscustomobject]@{
            Date = (Get-Date).ToString("s")
            Engine = $Case.Engine
            Voice = $Case.Voice
            Rating = [int]$Rating
            Notes = $Notes
            Text = $Text
            File = $Case.Path
        } | Export-Csv -Path $RatingsPath -NoTypeInformation -Append -Encoding UTF8
    }
}
Write-Host ""
Write-Host ("Готово. WAV: " + $SessionDir)
if (Test-Path $RatingsPath) { Write-Host ("Оценки: " + $RatingsPath) }
if ($Failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Не удалось выполнить:" -ForegroundColor Yellow
    $Failures | ForEach-Object { Write-Host (" - " + $_) -ForegroundColor Yellow }
}
