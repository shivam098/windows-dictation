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

# Windows only lets the process that produced the last input change the foreground window, so a script started in the
# background can't rely on the new Chrome window being in front. Tap an unassigned key (F24) to get that right, bring the
# test window forward, and refuse to run unless it really is in front (otherwise commands would act on your windows).
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class LabWindow {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public static bool BringToFront(IntPtr h) {
    for (int i = 0; i < 5; i++) {
      keybd_event(0x87, 0, 0, UIntPtr.Zero); keybd_event(0x87, 0, 2, UIntPtr.Zero);
      ShowWindow(h, 9); SetForegroundWindow(h);
      System.Threading.Thread.Sleep(300);
      if (GetForegroundWindow() == h) return true;
    }
    return false;
  }
}
"@
function Get-LabWindow([string] $profileMarker) {
    for ($i = 0; $i -lt 20; $i++) {
        $browser = Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*$profileMarker*" -and $_.CommandLine -notlike '*--type=*' } | Select-Object -First 1
        if ($browser) {
            $p = Get-Process -Id $browser.ProcessId -ErrorAction SilentlyContinue
            if ($p -and $p.MainWindowHandle -ne 0) { return $p.MainWindowHandle }
        }
        Start-Sleep -Milliseconds 500
    }
    return [IntPtr]::Zero
}
$here = $PSScriptRoot
$page = ([Uri](Join-Path $here 'lab.html')).AbsoluteUri
$profileDir = Join-Path $env:TEMP 'vd-dictlab-profile'

# Start-Process joins arguments with spaces without quoting them, so quote paths (the profile may contain spaces).
Start-Process $Chrome -ArgumentList "`"--user-data-dir=$profileDir`" --no-first-run --no-default-browser-check --hide-crash-restore-bubble --new-window `"$page`"" | Out-Null
try {
    Start-Sleep -Seconds 6
    $lab = Get-LabWindow 'vd-dictlab-profile'
    if ($lab -eq [IntPtr]::Zero -or -not [LabWindow]::BringToFront($lab)) { throw 'The test window could not be brought to the front; nothing was run.' }
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
