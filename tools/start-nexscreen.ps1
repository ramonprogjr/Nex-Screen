#Requires -Version 5.1
<#
.SYNOPSIS
    Developer path: rebuilds (with -Build) and runs the NexScreen companion in this console. Ctrl+C stops it.
.DESCRIPTION
    The companion links the tablet by itself: for an authorized tablet on USB or wireless debugging it runs
    adb reverse and opens the NexScreen app, and it reconnects paired wireless tablets. Needs the app installed
    (tools\android-install.ps1) and a virtual monitor (the NexScreen driver, or the signed VDD bridge).
    For everyday use, install it once with tools\install-companion.ps1 and use the Start Menu entry "NexScreen".
    Extra arguments go to the companion, e.g. --verbose, --no-input or --no-adb.
.EXAMPLE
    .\tools\start-nexscreen.ps1
    .\tools\start-nexscreen.ps1 -Build --verbose
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$Build,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CompanionArgs
)

$ErrorActionPreference = 'Stop'

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
