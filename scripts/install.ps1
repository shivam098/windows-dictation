<#
.SYNOPSIS
    Builds and installs Voice Dictation for the current user. No administrator rights are needed.

.DESCRIPTION
    1. Uses the .NET 10 SDK (installs it into %LOCALAPPDATA%\Microsoft\dotnet if missing).
    2. Publishes a self-contained build for this machine's architecture.
    3. Downloads the speech model from Hugging Face and verifies its SHA-256 hash (reuses Voice Assistant's copy if present).
    4. Installs to %LOCALAPPDATA%\Programs\VoiceDictation and adds a Start menu shortcut.
    5. Starts the app (look for the microphone icon in the system tray).

.EXAMPLE
    .\scripts\install.ps1
.EXAMPLE
    .\scripts\install.ps1 -Model ggml-base.en-q8_0.bin -StartAtSignIn
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\VoiceDictation'),

    # small.en-q8_0: better accuracy (~0.5 s per phrase on Snapdragon X). base.en-q8_0: faster (~0.2 s), less accurate.
    [ValidateSet('ggml-small.en-q8_0.bin', 'ggml-base.en-q8_0.bin', 'ggml-base.en.bin')]
    [string] $Model = 'ggml-small.en-q8_0.bin',

    [switch] $StartAtSignIn,

    [switch] $NoLaunch
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# SHA-256 values from https://huggingface.co/api/models/ggerganov/whisper.cpp/tree/main (LFS oid).
$ModelHashes = @{
    'ggml-small.en-q8_0.bin' = '67a179f608ea6114bd3fdb9060e762b588a3fb3bd00c4387971be4d177958067'
    'ggml-base.en-q8_0.bin'  = 'a4d4a0768075e13cfd7e19df3ae2dbc4a68d37d36a7dad45e8410c9a34f8c87e'
    'ggml-base.en.bin'       = 'a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002'
}
# https://huggingface.co/api/models/ggml-org/whisper-vad/tree/main
$VadHash = '2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'src\VoiceDictation.App\VoiceDictation.App.csproj'
$DataDir = Join-Path $env:LOCALAPPDATA 'VoiceDictation'
$SharedModelsDir = Join-Path $env:LOCALAPPDATA 'VoiceAssistant\models'
$ModelsDir = Join-Path $DataDir 'models'
$SettingsFile = Join-Path $env:APPDATA 'VoiceDictation\settings.json'
$Exe = Join-Path $InstallDir 'VoiceDictation.exe'
$Shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Voice Dictation.lnk'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# Only ever replace a folder that is empty or is an existing Voice Dictation install.
function Assert-SafeInstallDir {
    if (-not (Test-Path $InstallDir)) { return }
    $isInstall = Test-Path (Join-Path $InstallDir 'VoiceDictation.exe')
    $isEmpty = -not (Get-ChildItem $InstallDir -Force | Select-Object -First 1)
    if (-not ($isInstall -or $isEmpty)) {
        throw "Refusing to use '$InstallDir': it is not empty and is not a Voice Dictation install. Choose an empty or new folder."
    }
}

function Get-Rid {
    $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    switch ($arch) {
        'ARM64' { 'win-arm64' }
        'AMD64' { 'win-x64' }
        default { throw "Unsupported processor architecture '$arch'." }
    }
}

function Get-DotNet {
    $candidates = @(
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks -match '^10\.') { return $candidate }
    }

    Write-Step 'Installing the .NET 10 SDK for the current user (no admin needed)'
    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    $arch = if ((Get-Rid) -eq 'win-arm64') { 'arm64' } else { 'x64' }
    & $installer -Channel 10.0 -Architecture $arch -InstallDir (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet') | Out-Null
    return (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
}

function Install-Model {
    New-Item -ItemType Directory -Force $ModelsDir | Out-Null
    Install-VerifiedFile "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$Model" (Join-Path $ModelsDir $Model) $ModelHashes[$Model] "speech model $Model"
    # Silero voice-activity model: tells speech from music/noise and splits phrases that background sound merged.
    Install-VerifiedFile 'https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin' (Join-Path $ModelsDir 'ggml-silero-v6.2.0.bin') $VadHash 'voice-activity model'
}

function Install-VerifiedFile([string] $Url, [string] $Target, [string] $Expected, [string] $Label) {
    if ((Test-Path $Target) -and (Get-FileHash $Target -Algorithm SHA256).Hash -eq $Expected) {
        Write-Step "$Label already present and verified"
        return
    }

    # Voice Assistant's models are reused as they are (the app looks there too).
    $shared = Join-Path $SharedModelsDir (Split-Path $Target -Leaf)
    if ((Test-Path $shared) -and (Get-FileHash $shared -Algorithm SHA256).Hash -eq $Expected) {
        Write-Step "$Label found in Voice Assistant's models; reusing it"
        return
    }

    Write-Step "Downloading $Label (one time)"
    $partial = "$Target.partial"
    Invoke-WebRequest $Url -OutFile $partial -UseBasicParsing
    $actual = (Get-FileHash $partial -Algorithm SHA256).Hash
    if ($actual -ne $Expected) {
        Remove-Item $partial -Force
        throw "Hash mismatch for $Label (expected $Expected, got $actual). Download discarded."
    }

    Move-Item -Force $partial $Target
}

function Set-ModelSetting {
    New-Item -ItemType Directory -Force (Split-Path $SettingsFile) | Out-Null
    if (Test-Path $SettingsFile) {
        $settings = Get-Content $SettingsFile -Raw | ConvertFrom-Json
        if ($settings.PSObject.Properties.Name -contains 'modelFile') {
            if ($settings.modelFile -eq $Model) { return }
            $settings.modelFile = $Model
        }
        else {
            $settings | Add-Member -NotePropertyName modelFile -NotePropertyValue $Model
        }
    }
    else {
        $settings = [pscustomobject]@{ modelFile = $Model }
    }

    $settings | ConvertTo-Json -Depth 10 | Set-Content $SettingsFile -Encoding UTF8
}

$rid = Get-Rid
Assert-SafeInstallDir
$dotnet = Get-DotNet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Write-Step "Building Voice Dictation ($rid, self-contained)"
$staging = Join-Path $env:TEMP "VoiceDictation-publish-$([guid]::NewGuid().ToString('N'))"
& $dotnet publish $Project -c Release -r $rid --self-contained true -o $staging -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Install-Model
Set-ModelSetting

$running = Get-Process VoiceDictation -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Step 'Stopping the running copy'
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

Write-Step "Installing to $InstallDir"
# The stopped process can hold its native DLLs for a moment; retry instead of failing half-way.
for ($attempt = 1; (Test-Path $InstallDir); $attempt++) {
    try { Remove-Item $InstallDir -Recurse -Force -ErrorAction Stop }
    catch {
        if ($attempt -ge 10) { throw }
        Start-Sleep -Milliseconds 500
    }
}
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $staging '*') $InstallDir -Recurse -Force
Remove-Item $staging -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($Shortcut)
$link.TargetPath = $Exe
$link.WorkingDirectory = $InstallDir
$link.IconLocation = "$Exe,0"
$link.Description = 'Hold Ctrl+Win and speak to type into any app.'
$link.Save()

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($StartAtSignIn) {
    Set-ItemProperty $runKey -Name VoiceDictation -Value "`"$Exe`""
    Write-Step 'Voice Dictation will start when you sign in'
}

if (-not $NoLaunch) {
    Write-Step 'Starting Voice Dictation'
    Start-Process $Exe
}

Write-Host ''
Write-Host 'Installed. Look for the microphone icon in the system tray.' -ForegroundColor Green
Write-Host '  Hold Ctrl+Win, speak, release -> typed where your cursor is (Ctrl+Win+Space = hands-free, Esc = cancel)'
Write-Host '  Alt+Shift+Z pastes the last dictation; Alt+Shift+H switches to Hindi + English'
if (Get-Process VoiceAssistant -ErrorAction SilentlyContinue) {
    Write-Warning 'Voice Assistant is running and also uses Ctrl+Win, so text would be typed twice. Exit one of them.'
}
