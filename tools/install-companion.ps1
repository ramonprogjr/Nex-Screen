#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the NexScreen companion for the current user (no admin): a self-contained build in
    %LOCALAPPDATA%\Programs\NexScreen and a Start Menu shortcut "NexScreen" that runs it in the notification area.
.DESCRIPTION
    Builds windows\companion with dotnet publish (self-contained, single file, win-x64), so the installed copy needs
    no .NET runtime. "Start with Windows" stays off; the user turns it on from the tray menu.
    -Uninstall stops the installed companion and removes the folder, the shortcut and the HKCU Run value.
    Logs in %LOCALAPPDATA%\NexScreen\logs are kept.
.EXAMPLE
    .\tools\install-companion.ps1
    .\tools\install-companion.ps1 -NoLaunch
    .\tools\install-companion.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,

    # Do not start the companion after installing.
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'windows\companion\src\NexScreen.Companion\NexScreen.Companion.csproj'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\NexScreen'
$exe = Join-Path $installDir 'NexScreen.Companion.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'NexScreen.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'NexScreen'

function Stop-InstalledCompanion {
    $procs = @(Get-CimInstance Win32_Process -Filter "Name='NexScreen.Companion.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) })
    foreach ($p in $procs) {
        Write-Host "Stopping the installed companion (pid $($p.ProcessId))."
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($procs.Count -gt 0) { Start-Sleep -Seconds 1 }
}

if ($Uninstall) {
    Stop-InstalledCompanion
    if (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $runKey -Name $runValue
        Write-Host "Removed the HKCU Run value '$runValue'."
    }
    if (Test-Path $shortcut) {
        Remove-Item $shortcut -Force
        Write-Host "Removed $shortcut"
    }
    if (Test-Path $installDir) {
        Remove-Item $installDir -Recurse -Force
        Write-Host "Removed $installDir"
    }
    Write-Host 'NexScreen companion uninstalled. Logs are still in %LOCALAPPDATA%\NexScreen\logs.'
    exit 0
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet not found. Install the .NET 8 SDK (see PROJETO.md section 5).'
}

# Publish to a staging folder first so a failed build leaves the installed copy alone
$staging = Join-Path $env:TEMP 'nexscreen-companion-publish'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $staging --nologo -v minimal
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit code $LASTEXITCODE)."
}
if (-not (Test-Path (Join-Path $staging 'NexScreen.Companion.exe'))) {
    throw "Publish output not found in $staging."
}

Stop-InstalledCompanion
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Path (Join-Path $staging '*') -Destination $installDir -Recurse -Force
Remove-Item $staging -Recurse -Force
Write-Host "Installed to $installDir"

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.Arguments = '--tray'
$link.WorkingDirectory = $installDir
$link.IconLocation = "$exe,0"
$link.Description = 'NexScreen: usa o tablet como monitor estendido'
$link.Save()
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
Write-Host "Start Menu shortcut: $shortcut"

$current = (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue).$runValue
if ($current) {
    Write-Host "Start with Windows is on ($current)."
}
else {
    Write-Host 'Start with Windows is off. To turn it on: tray icon > "Iniciar com o Windows".'
}

if (-not $NoLaunch) {
    Start-Process -FilePath $exe -ArgumentList '--tray' -WorkingDirectory $installDir
    Write-Host 'NexScreen started in the notification area (next to the clock).'
}
