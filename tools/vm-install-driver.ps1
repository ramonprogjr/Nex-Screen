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

foreach ($name in 'NexScreenVDD.inf', 'NexScreenVDD.dll', 'NexScreenVDD.cat', 'NexScreenTest.cer', 'nexscreen-vdd-ctl.exe') {
    if (-not (Test-Path (Join-Path $PackageDir $name))) {
        throw "$name not found in $PackageDir. Use -PackageDir to point to the signed package."
    }
}

# Work from a local copy: VM shared folders (\\VBoxSvr\...) are not reliable for pnputil and the device tool
$localDir = Join-Path $env:ProgramData 'NexScreen\driver'
New-Item -ItemType Directory -Force $localDir | Out-Null
Copy-Item (Join-Path $PackageDir '*') $localDir -Force
$inf = Join-Path $localDir 'NexScreenVDD.inf'
$cer = Join-Path $localDir 'NexScreenTest.cer'
$ctl = Join-Path $localDir 'nexscreen-vdd-ctl.exe'

# 1. Trust the test certificate. certutil instead of Import-Certificate, which reports "access denied" for
#    LocalMachine stores even when the import succeeds.
foreach ($store in 'Root', 'TrustedPublisher') {
    certutil -f -addstore $store $cer | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "certutil -addstore $store failed (exit code $LASTEXITCODE)."
    }
}
Write-Host 'Test certificate added to Root and TrustedPublisher.'

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
