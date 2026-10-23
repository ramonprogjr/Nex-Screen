#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the NexScreen companion (windows\NexScreen.sln) and runs it, passing the remaining arguments through.
.EXAMPLE
    .\tools\run-companion.ps1 --list
    .\tools\run-companion.ps1 --output primary --size 1920x1200
    .\tools\run-companion.ps1 --output primary --dump $env:TEMP\test.h264 --seconds 5
    .\tools\run-companion.ps1 -NoBuild -Configuration Debug -- --output \.\DISPLAY2
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CompanionArgs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'windows\NexScreen.sln'
$project = Join-Path $root 'windows\companion\src\NexScreen.Companion'
$exe = Join-Path $project "bin\$Configuration\net8.0-windows10.0.19041.0\win-x64\NexScreen.Companion.exe"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet not found. Install the .NET 8 SDK (see PROJETO.md section 5).'
}

if (-not $NoBuild) {
    & dotnet build $sln -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed (exit code $LASTEXITCODE)."
    }
}

if (-not (Test-Path $exe)) {
    throw "Companion not found at $exe. Run without -NoBuild."
}

# PowerShell may pass a literal '--' separator through; the companion does not expect it.
$passArgs = @($CompanionArgs | Where-Object { $_ -ne '--' })
Write-Host "Running: $exe $($passArgs -join ' ')"

# The companion is a GUI-subsystem exe (no console window in --tray mode) that attaches to this console, and
# PowerShell does not wait for GUI programs, so start it in this console and wait for it here. WaitForExit() is
# used instead of Start-Process -Wait, which would also wait for an adb server the companion may have started.
$quoted = @($passArgs | ForEach-Object {
    if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
})
$startArgs = @{ FilePath = $exe; NoNewWindow = $true; PassThru = $true }
if ($quoted.Count -gt 0) { $startArgs.ArgumentList = $quoted }
$proc = Start-Process @startArgs
$null = $proc.Handle  # keeps the exit code readable after exit
while (-not $proc.WaitForExit(200)) { }
exit $proc.ExitCode
