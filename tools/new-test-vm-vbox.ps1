#Requires -Version 5.1
<#
.SYNOPSIS
    Creates a VirtualBox test VM for the driver (for Windows editions without Hyper-V, such as Home).
.DESCRIPTION
    EFI firmware with Secure Boot off (needed for test signing), TPM 2.0 (needed by Windows 11 setup), the Windows 11
    ISO attached, and the repository shared read-only as drive N: in the guest. The share needs the VirtualBox Guest
    Additions installed in the guest (Devices > Insert Guest Additions CD image).
.EXAMPLE
    .\tools\new-test-vm-vbox.ps1 -IsoPath "$env:USERPROFILE\Downloads\Win11_25H2_BrazilianPortuguese_x64.iso"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$IsoPath,
    [string]$Name = 'NexScreen-Test',
    [int]$MemoryMB = 4096,
    [int]$Cpus = 2,
    [int]$DiskGB = 64
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$vbox = Join-Path $env:ProgramFiles 'Oracle\VirtualBox\VBoxManage.exe'
if (-not (Test-Path $vbox)) {
    throw 'VBoxManage.exe not found. Install VirtualBox: winget install Oracle.VirtualBox'
}
if (-not (Test-Path $IsoPath)) {
    throw "ISO not found: $IsoPath"
}

function Invoke-VBox {
    & $vbox @args
    if ($LASTEXITCODE -ne 0) {
        throw "VBoxManage $($args -join ' ') failed (exit code $LASTEXITCODE)."
    }
}

$existing = & $vbox list vms
if ($existing -match "^`"$([regex]::Escape($Name))`"") {
    throw "A VM named '$Name' already exists."
}

Invoke-VBox createvm --name $Name --ostype Windows11_64 --register
Invoke-VBox modifyvm $Name --memory $MemoryMB --cpus $Cpus --vram 128 --graphicscontroller vboxsvga `
    --firmware efi --tpm-type 2.0 --nic1 nat --clipboard-mode bidirectional

$vmDir = Split-Path -Parent ((& $vbox showvminfo $Name --machinereadable | Select-String '^CfgFile=').Line -replace '^CfgFile="(.*)"$', '$1')
$disk = Join-Path $vmDir "$Name.vdi"
Invoke-VBox createmedium disk --filename $disk --size ($DiskGB * 1024) --format VDI

Invoke-VBox storagectl $Name --name SATA --add sata --controller IntelAhci --portcount 2
Invoke-VBox storageattach $Name --storagectl SATA --port 0 --device 0 --type hdd --medium $disk
Invoke-VBox storageattach $Name --storagectl SATA --port 1 --device 0 --type dvddrive --medium $IsoPath

# Repository shared read-only; appears as N: in the guest once Guest Additions are installed
Invoke-VBox sharedfolder add $Name --name nexscreen --hostpath $root --readonly --automount --auto-mount-point N:

Write-Host ''
Write-Host "VM '$Name' created (EFI, TPM 2.0, Secure Boot off, $MemoryMB MB, $DiskGB GB)."
Write-Host "Start it with: & '$vbox' startvm $Name"
Write-Host 'After installing Windows, install the Guest Additions, then in the guest (admin):'
Write-Host '  powershell -ExecutionPolicy Bypass -File N:\tools\vm-install-driver.ps1 -PackageDir N:\out\driver'
