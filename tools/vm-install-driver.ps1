#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the test-signed NexScreen driver. Run INSIDE the test VM, never on a daily-use machine.
.DESCRIPTION
    1. Imports NexScreenTest.cer into LocalMachine\Root and LocalMachine\TrustedPublisher.
    2. Enables test signing. If it was off, the script stops here: reboot and run it again.
    3. pnputil /add-driver NexScreenVDD.inf /install
    4. nexscreen-vdd-ctl create
.PARAMETER PackageDir
    Folder with NexScreenVDD.inf/.dll/.cat, NexScreenTest.cer and nexscreen-vdd-ctl.exe.
#>
[CmdletBinding()]
param(
    [string]$PackageDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'out\driver')
)

$ErrorActionPreference = 'Stop'

$inf = Join-Path $PackageDir 'NexScreenVDD.inf'
$cer = Join-Path $PackageDir 'NexScreenTest.cer'
$ctl = Join-Path $PackageDir 'nexscreen-vdd-ctl.exe'
foreach ($f in $inf, $cer, $ctl, (Join-Path $PackageDir 'NexScreenVDD.cat')) {
    if (-not (Test-Path $f)) {
        throw "$f not found. Use -PackageDir to point to the signed package."
    }
}

# 1. Trust the test certificate
foreach ($store in 'Root', 'TrustedPublisher') {
    Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
}
Write-Host 'Test certificate imported into Root and TrustedPublisher.'

# 2. Test signing. SystemStartOptions reflects the current boot and is not localized, unlike bcdedit output.
$startOptions = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control').SystemStartOptions
if ($startOptions -notmatch 'TESTSIGNING') {
    $secureBoot = $false
    try { $secureBoot = Confirm-SecureBootUEFI } catch { }
    if ($secureBoot) {
        throw 'Secure Boot is on. Turn it off in the VM firmware settings; test signing does not work with it.'
    }
    bcdedit /set testsigning on
    if ($LASTEXITCODE -ne 0) {
        throw 'bcdedit /set testsigning on failed.'
    }
    Write-Host ''
    Write-Host 'Test signing enabled. Reboot the VM and run this script again.' -ForegroundColor Yellow
    exit 0
}
Write-Host 'Test signing is active.'

# 3. Driver package
pnputil /add-driver $inf /install
if ($LASTEXITCODE -ne 0) {
    throw "pnputil /add-driver failed (exit code $LASTEXITCODE)."
}

# 4. Software device that makes PnP load the driver
& $ctl create
if ($LASTEXITCODE -ne 0) {
    throw "nexscreen-vdd-ctl create failed (exit code $LASTEXITCODE)."
}

Write-Host ''
Write-Host 'Done. Check with: pnputil /enum-devices /class Display' -ForegroundColor Green
