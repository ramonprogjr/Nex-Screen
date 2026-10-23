#Requires -Version 5.1
<#
.SYNOPSIS
    Starts NexScreen in one step: links the tablet over USB (adb reverse), opens the app on it and runs the companion
    on the virtual monitor. Ctrl+C stops it.
.DESCRIPTION
    Needs the tablet connected by USB with USB debugging authorized, the NexScreen app installed
    (tools\android-install.ps1) and a virtual monitor in extend mode (the NexScreen driver, or the signed VDD bridge).
    Extra arguments go to the companion, e.g. --verbose or --no-input.
.EXAMPLE
    .\tools\start-nexscreen.ps1
    .\tools\start-nexscreen.ps1 -Build --verbose
#>
[CmdletBinding()]
param(
    [switch]$Build,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CompanionArgs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$port = 27183

function Find-Adb {
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\Google.PlatformTools_Microsoft.Winget.Source_8wekyb3d8bbwe\platform-tools\adb.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    throw 'adb not found. Install it with: winget install --id Google.PlatformTools -e'
}

$adb = Find-Adb

# Tablet connected and authorized?
$devices = @(& $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\S' })
if ($devices.Count -eq 0) {
    throw 'No tablet found. Connect the USB cable and enable USB debugging (docs\build-android.md).'
}
if ($devices -match '\sunauthorized') {
    throw 'The tablet has not authorized this PC. Unlock it and accept "Allow USB debugging".'
}
if ($devices.Count -gt 1) {
    throw "More than one Android device connected; leave only the tablet: $($devices -join '; ')"
}

& $adb reverse "tcp:$port" "tcp:$port" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'adb reverse failed.'
}
& $adb shell am start -n app.nexscreen/.MainActivity | Out-Null
Write-Host 'Tablet linked over USB and NexScreen app opened.'

# Only one companion can own the port
$running = @(Get-CimInstance Win32_Process -Filter "Name='NexScreen.Companion.exe'")
if ($running.Count -gt 0) {
    Write-Host "Stopping the companion that is already running (pid $($running.ProcessId -join ', '))."
    $running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Start-Sleep -Seconds 1
}

$runArgs = @()
if (-not $Build) { $runArgs += '-NoBuild' }
& (Join-Path $PSScriptRoot 'run-companion.ps1') @runArgs @CompanionArgs
exit $LASTEXITCODE
