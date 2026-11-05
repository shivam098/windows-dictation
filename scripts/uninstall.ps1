<#
.SYNOPSIS
    Removes Voice Dictation for the current user.

.PARAMETER RemoveData
    Also deletes downloaded speech models, settings, and logs.
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\VoiceDictation'),
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'

if ((Test-Path $InstallDir) -and -not (Test-Path (Join-Path $InstallDir 'VoiceDictation.exe'))) {
    throw "'$InstallDir' does not contain VoiceDictation.exe; refusing to delete it."
}

$running = Get-Process VoiceDictation -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name VoiceDictation -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Voice Dictation.lnk') -ErrorAction SilentlyContinue
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }

if ($RemoveData) {
    Remove-Item (Join-Path $env:LOCALAPPDATA 'VoiceDictation') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:APPDATA 'VoiceDictation') -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Voice Dictation removed.' -ForegroundColor Green
if (-not $RemoveData) {
    Write-Host 'Speech models and settings were kept. Use -RemoveData to delete them too.'
}
