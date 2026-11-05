<#
.SYNOPSIS
    Runs the dictation acceptance script against a fresh Chrome window with a local test page.
.DESCRIPTION
    Opens lab.html in a NEW Chrome window with a separate, temporary profile, runs dictation-lab.selftest.txt through
    VoiceDictation.exe --self-test (transcripts are scripted; no microphone), prints the results, and closes only that
    test window. Run it while you're not using the PC: Windows won't let a background process bring the test window to
    the front while you're typing elsewhere (the script then stops at the first failed check instead of typing).
.EXAMPLE
    .\tests\DictationLab\run.ps1 -Exe .\src\VoiceDictation.App\bin\Release\net10.0-windows\win-arm64\VoiceDictation.exe
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$page = ([Uri](Join-Path $here 'lab.html')).AbsoluteUri
$profileDir = Join-Path $env:TEMP 'vd-dictlab-profile'

# Start-Process joins arguments with spaces without quoting them, so quote paths (the profile may contain spaces).
Start-Process $Chrome -ArgumentList "`"--user-data-dir=$profileDir`" --no-first-run --no-default-browser-check --hide-crash-restore-bubble --new-window `"$page`"" | Out-Null
try {
    Start-Sleep -Seconds 6
    $out = Join-Path $env:TEMP 'vd-dictlab.out.txt'
    Remove-Item $out -ErrorAction SilentlyContinue
    $p = Start-Process $Exe -ArgumentList "--self-test `"$(Join-Path $here 'dictation-lab.selftest.txt')`" `"$out`"" -PassThru
    if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id; throw 'self-test timed out' }
    $lines = Get-Content $out
    $lines
    $pass = @($lines | Where-Object { $_ -match '^\s+PASS' }).Count
    $fail = @($lines | Where-Object { $_ -match '^\s+FAIL' }).Count
    Write-Host "`n$pass passed, $fail failed" -ForegroundColor $(if ($fail -eq 0 -and $pass -gt 0) { 'Green' } else { 'Red' })
}
finally {
    # Close only the test profile's Chrome: ask its windows to close first so the profile isn't marked as crashed.
    $test = @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like '*vd-dictlab-profile*' })
    foreach ($proc in $test) {
        $p = Get-Process -Id $proc.ProcessId -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne 0) { $p.CloseMainWindow() | Out-Null }
    }

    Start-Sleep -Seconds 2
    $test | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}
