#Requires -Version 5.1
<#
.SYNOPSIS
    Builds NexScreenVDD.sln and collects the driver package in out\driver.
.EXAMPLE
    .\tools\build-driver.ps1
    .\tools\build-driver.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'driver\virtual-display\NexScreenVDD.sln'
$outDir = Join-Path $root 'out\driver'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found. Install Visual Studio 2022 (see PROJETO.md section 5)."
}
# Use the 64-bit MSBuild: the WDK 26100 InfVerif task only ships x64/arm64 binaries and fails under the x86 MSBuild
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw '64-bit MSBuild not found through vswhere.'
}
Write-Host "MSBuild: $msbuild"

$log = Join-Path $env:TEMP 'nexscreen-build.log'
& $msbuild $sln /m /nologo /v:minimal /t:Build "/p:Configuration=$Configuration" /p:Platform=x64 "/flp:LogFile=$log;Verbosity=minimal"
$exitCode = $LASTEXITCODE
# Some WDK tasks log errors without failing the build, so check the log as well as the exit code
$errors = @(Select-String -Path $log -Pattern ': (fatal )?error ' -ErrorAction SilentlyContinue)
if ($exitCode -ne 0 -or $errors.Count -gt 0) {
    throw "Build failed (exit code $exitCode, $($errors.Count) error line(s)). Log: $log"
}

# Collect the artifacts. The INF must be the stamped copy (DriverVer filled in), not the source one.
$buildDir = Join-Path $root "driver\virtual-display\x64\$Configuration"
$dll = Get-ChildItem $buildDir -Recurse -Filter 'NexScreenVDD.dll' | Select-Object -First 1
$exe = Get-ChildItem $buildDir -Recurse -Filter 'nexscreen-vdd-ctl.exe' | Select-Object -First 1
$inf = Get-ChildItem $buildDir -Recurse -Filter 'NexScreenVDD.inf' |
    Where-Object { (Get-Content $_.FullName -Raw) -match '(?m)^DriverVer\s*=\s*\d' } |
    Select-Object -First 1

foreach ($item in @(@{ Name = 'NexScreenVDD.dll'; File = $dll },
                    @{ Name = 'nexscreen-vdd-ctl.exe'; File = $exe },
                    @{ Name = 'NexScreenVDD.inf (stamped)'; File = $inf })) {
    if (-not $item.File) {
        throw "$($item.Name) not found under $buildDir."
    }
}

if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
}
New-Item -ItemType Directory -Force $outDir | Out-Null
Copy-Item $dll.FullName, $inf.FullName, $exe.FullName $outDir

Write-Host ''
Write-Host "Driver package in ${outDir}:"
Get-ChildItem $outDir | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host 'Next step: .\tools\make-test-cert.ps1'
