[CmdletBinding()]
param([switch]$Clean)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $Root

try {
    function Invoke-DownloadFile([string]$Uri, [string]$Destination, [string]$Label) {
        $Partial = "$Destination.download"
        Remove-Item $Partial -Force -ErrorAction SilentlyContinue
        Write-Host "Downloading $Label..."
        & curl.exe --location --fail --show-error --retry 4 --retry-delay 5 --connect-timeout 30 --max-time 1200 --output $Partial $Uri
        if ($LASTEXITCODE -ne 0) {
            $Code = $LASTEXITCODE
            Remove-Item $Partial -Force -ErrorAction SilentlyContinue
            throw "Download failed for $Label (curl exit $Code)."
        }
        if (-not (Test-Path $Partial) -or (Get-Item $Partial).Length -lt 1024) {
            Remove-Item $Partial -Force -ErrorAction SilentlyContinue
            throw "Downloaded file for $Label is missing or incomplete."
        }
        Move-Item -Path $Partial -Destination $Destination -Force
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET 10 SDK is required on the build computer."
    }

    $Assets = Join-Path $Root "assets"
    $ModelDir = Join-Path $Assets "models\vosk-model-small-ru-0.22"
    $PiperDir = Join-Path $Assets "piper"
    $VoiceDir = Join-Path $Assets "voices"
    $TempDir = Join-Path $Root ".build-temp"
    $PublishDir = Join-Path $Root "dist\STTWorkTTS"

    if ($Clean) {
        Remove-Item (Join-Path $Root "dist") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Force -Path $ModelDir, $PiperDir, $VoiceDir, $TempDir | Out-Null

    # The laptop profile uses the built-in command-example router.
    # RuBERT/ONNX training is optional and is not required for this package.

    if (-not (Test-Path (Join-Path $ModelDir "conf\model.conf"))) {
        $ModelZip = Join-Path $TempDir "vosk-model.zip"
        $ExtractDir = Join-Path $TempDir "vosk"
        Invoke-DownloadFile "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip" $ModelZip "Vosk Russian Small model"
        Expand-Archive -Path $ModelZip -DestinationPath $ExtractDir -Force
        $Extracted = Join-Path $ExtractDir "vosk-model-small-ru-0.22"
        if (-not (Test-Path $Extracted)) { throw "Unexpected Vosk model archive structure." }
        Copy-Item (Join-Path $Extracted "*") $ModelDir -Recurse -Force
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "tools\tts-lab\Install-TtsLab.ps1") -Engine piper
    if ($LASTEXITCODE -ne 0) { throw "Piper CLI and compatible Denis v1.0.0 assets could not be installed." }

    $VoiceModel = Join-Path $VoiceDir "ru_RU-denis-medium.onnx"
    $VoiceConfig = "$VoiceModel.json"
    if (-not (Test-Path (Join-Path $PiperDir "piper.exe")) -or -not (Test-Path $VoiceModel) -or -not (Test-Path $VoiceConfig)) {
        throw "Piper portable assets are missing after installation."
    }
    $VoiceConfigText = [System.IO.File]::ReadAllText($VoiceConfig, [System.Text.Encoding]::UTF8)
    if (($VoiceConfigText -notmatch '"piper_version"\s*:\s*"1\.0\.0"') -or ($VoiceConfigText -match '"vowel_clusters"\s*:')) {
        throw "Portable build requires a Piper v1.0.0 voice config compatible with the bundled legacy CLI."
    }

    Write-Host "Publishing self-contained .NET application..."
    Remove-Item $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
    $PublishArgs = @(
        "publish",
        ".\src\STT.WorkTTS\STT.WorkTTS.csproj",
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:PublishTrimmed=false",
        "--output", $PublishDir
    )
    & dotnet @PublishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    foreach ($SettingsFile in @("appsettings.json", "appsettings.laptop.json")) {
        if (-not (Test-Path (Join-Path $PublishDir $SettingsFile))) {
            throw "Published application is missing required profile settings: $SettingsFile"
        }
    }

    # This archive is a dedicated laptop distribution: plain Start.cmd must
    # select Piper by default, even when the user does not pass --profile laptop.
    Copy-Item (Join-Path $PublishDir "appsettings.laptop.json") (Join-Path $PublishDir "appsettings.json") -Force
    $PortableSettings = Get-Content -Raw -Path (Join-Path $PublishDir "appsettings.json") | ConvertFrom-Json
    if ($PortableSettings.Tts.Engine -ne "Piper" -or $PortableSettings.Generation.Enabled -or $PortableSettings.Intent.UseOnnx) {
        throw "Portable default settings must use Piper with generation and ONNX disabled."
    }

    # Copy models and native Piper DLLs beside the executable.
    $PublishAssets = Join-Path $PublishDir "assets"
    New-Item -ItemType Directory -Force -Path $PublishAssets | Out-Null
    Copy-Item (Join-Path $Assets "*") $PublishAssets -Recurse -Force

    # Include only startup/install scripts and lightweight TTS adapters.
    # Python environments, downloaded models and caches are created on first launch.
    $PortableToolFiles = @(
        "tools\Start-Assistant.ps1",
        "tools\Install-VoskModel.ps1",
        "tools\intent\Install-RuBertIntentModel.ps1",
        "tools\intent\dataset\commands.jsonl",
        "tools\intent\Install-QwenIntentModel.ps1",
        "tools\intent\Start-IntentModel.ps1",
        "tools\tts-lab\Install-TtsLab.ps1",
        "tools\tts-runtime\silero_worker.py",
        "tools\tts-runtime\xvibe_worker.py"
    )
    foreach ($RelativePath in $PortableToolFiles) {
        $SourcePath = Join-Path $Root $RelativePath
        if (-not (Test-Path $SourcePath)) {
            throw "Required portable startup file is missing: $SourcePath"
        }

        $DestinationPath = Join-Path $PublishDir $RelativePath
        $DestinationDirectory = Split-Path $DestinationPath -Parent
        New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
        Copy-Item $SourcePath $DestinationPath -Force
    }

    Copy-Item ".\Start.cmd" $PublishDir -Force
    Copy-Item ".\THIRD_PARTY_NOTICES.md" $PublishDir -Force
    Copy-Item ".\README.md" $PublishDir -Force

    $Archive = Join-Path $Root "STT-WORK-TTS-Windows-x64.zip"
    Remove-Item $Archive -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $Archive -CompressionLevel Optimal

    $ChecksumPath = "$Archive.sha256"
    $Hash = (Get-FileHash -Path $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$Hash  $(Split-Path -Leaf $Archive)" | Set-Content -Path $ChecksumPath -Encoding ascii

    $SizeMb = [math]::Round((Get-Item $Archive).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Portable archive created: $Archive"
    Write-Host "SHA-256 checksum created: $ChecksumPath"
    Write-Host "Archive size: $SizeMb MB"
    Write-Host "Extract the ZIP and run Start.cmd. Python is not required."
}
finally {
    Pop-Location
}
