#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Creates a Hyper-V Gen 2 test VM with Secure Boot off, or copies out\driver into an existing one.
.DESCRIPTION
    Requires Hyper-V, which is only available on Windows 11 Pro/Enterprise/Education (not Home).
.EXAMPLE
    .\tools\new-test-vm.ps1 -IsoPath D:\iso\Win11_x64.iso
    # after Windows is installed in the VM:
    .\tools\new-test-vm.ps1 -CopyPackage
#>
[CmdletBinding(DefaultParameterSetName = 'Create')]
param(
    [string]$Name = 'NexScreen-Test',

    [Parameter(Mandatory, ParameterSetName = 'Create')]
    [string]$IsoPath,
    [Parameter(ParameterSetName = 'Create')]
    [int]$MemoryGB = 4,
    [Parameter(ParameterSetName = 'Create')]
    [int]$DiskGB = 64,
    [Parameter(ParameterSetName = 'Create')]
    [string]$SwitchName = 'Default Switch',

    [Parameter(Mandatory, ParameterSetName = 'Copy')]
    [switch]$CopyPackage,
    [Parameter(ParameterSetName = 'Copy')]
    [string]$GuestDir = 'C:\NexScreen'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command New-VM -ErrorAction SilentlyContinue)) {
    throw 'Hyper-V PowerShell module not found. Enable Microsoft-Hyper-V-All (Windows Pro or higher) and reboot.'
}

if ($PSCmdlet.ParameterSetName -eq 'Copy') {
    # Needs the VM running with "Guest Service Interface" enabled (done at creation below)
    $files = @(Get-ChildItem (Join-Path $root 'out\driver') -File) +
             @(Get-Item (Join-Path $root 'tools\vm-install-driver.ps1'), (Join-Path $root 'tools\vm-uninstall-driver.ps1'))
    foreach ($f in $files) {
        $dest = if ($f.Extension -eq '.ps1') { "$GuestDir\tools\$($f.Name)" } else { "$GuestDir\out\driver\$($f.Name)" }
        Copy-VMFile -Name $Name -SourcePath $f.FullName -DestinationPath $dest -FileSource Host -CreateFullPath -Force
        Write-Host "Copied $($f.Name) -> $dest"
    }
    Write-Host "In the VM (admin): powershell -ExecutionPolicy Bypass -File $GuestDir\tools\vm-install-driver.ps1"
    return
}

if (-not (Test-Path $IsoPath)) {
    throw "ISO not found: $IsoPath"
}
if (Get-VM -Name $Name -ErrorAction SilentlyContinue) {
    throw "A VM named '$Name' already exists."
}

$vhdDir = (Get-VMHost).VirtualHardDiskPath
$vhdPath = Join-Path $vhdDir "$Name.vhdx"

New-VM -Name $Name -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) `
    -NewVHDPath $vhdPath -NewVHDSizeBytes ($DiskGB * 1GB) -SwitchName $SwitchName | Out-Null
Set-VMProcessor -VMName $Name -Count 2
Set-VMFirmware -VMName $Name -EnableSecureBoot Off
# Windows 11 setup requires a TPM
Set-VMKeyProtector -VMName $Name -NewLocalKeyProtector
Enable-VMTPM -VMName $Name
Enable-VMIntegrationService -VMName $Name -Name 'Guest Service Interface'

$dvd = Add-VMDvdDrive -VMName $Name -Path $IsoPath -Passthru
Set-VMFirmware -VMName $Name -FirstBootDevice $dvd

Write-Host "VM '$Name' created (Gen 2, Secure Boot off, ${MemoryGB} GB, ${DiskGB} GB disk)."
Write-Host "Start it with: Start-VM $Name; vmconnect localhost $Name"
Write-Host 'After installing Windows: .\tools\new-test-vm.ps1 -CopyPackage'
