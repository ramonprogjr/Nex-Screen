#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the NexScreen monitor and driver package from the test VM.
.PARAMETER DisableTestSigning
    Also runs bcdedit /set testsigning off (takes effect after a reboot).
#>
[CmdletBinding()]
param(
    [string]$PackageDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'out\driver'),
    [switch]$DisableTestSigning
)

$ErrorActionPreference = 'Stop'

$ctl = Join-Path $PackageDir 'nexscreen-vdd-ctl.exe'
if (Test-Path $ctl) {
    & $ctl remove
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "nexscreen-vdd-ctl remove failed (exit code $LASTEXITCODE); continuing with the driver package."
    }
} else {
    Write-Warning "$ctl not found; skipping device removal."
}

# Get-WindowsDriver returns the published oemXX.inf name without depending on the OS language
$packages = @(Get-WindowsDriver -Online | Where-Object { (Split-Path -Leaf $_.OriginalFileName) -ieq 'NexScreenVDD.inf' })
if ($packages.Count -eq 0) {
    Write-Host 'No NexScreenVDD driver package is installed.'
}
foreach ($pkg in $packages) {
    Write-Host "Removing $($pkg.Driver) (version $($pkg.Version))"
    pnputil /delete-driver $pkg.Driver /uninstall /force
    if ($LASTEXITCODE -ne 0) {
        throw "pnputil /delete-driver $($pkg.Driver) failed (exit code $LASTEXITCODE)."
    }
}

if ($DisableTestSigning) {
    bcdedit /set testsigning off
    Write-Host 'Test signing disabled; reboot to apply.'
}
