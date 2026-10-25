#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    End-to-end test of NexScreen-Setup, run INSIDE the test VM as administrator. Never on a machine you use.
.DESCRIPTION
    Cycle A (always):
      1. Silent install (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /AUTOSTART).
      2. Checks: files in Program Files\NexScreen, adb runs, vdd_settings.xml, Start Menu shortcut, Add/Remove
         Programs entry, HKCU Run value (same format as the tray's "Iniciar com o Windows"), adb folder on the
         system PATH, one Root\MttVDD device working, its package in the driver store, the "VDD by MTT" monitor.
      3. Silent reinstall over it: still exactly one Root\MttVDD device (the existing device is reused).
      4. Starts the companion like the shortcut does and creates HKCU\Software\NexScreen.
      5. Silent uninstall, then checks that everything above is gone and no NexScreen/adb process is left.
    Cycle B (-Extended):
      1. Puts a user-made C:\VirtualDisplayDriver\vdd_settings.xml in place, installs without /AUTOSTART: the user
         file is backed up, ours is installed and no Run value is written.
      2. Uninstalls with /KEEPVDD: the device, the driver package and vdd_settings.xml stay.
      3. Installs again: the existing device is reused (still one).
      4. Uninstalls: the device and package are gone and the user's vdd_settings.xml is restored.
    Results go to the console and C:\ProgramData\NexScreen\installer-test\result.log, Inno Setup logs next to it.
    The exit code is the number of failed checks.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File N:\tools\vm-test-installer.ps1
    powershell -ExecutionPolicy Bypass -File N:\tools\vm-test-installer.ps1 -Extended
    powershell -ExecutionPolicy Bypass -File N:\tools\vm-test-installer.ps1 -Setup N:\out\installer\NexScreen-Setup-v0.3.0.exe
#>
[CmdletBinding()]
param(
    # Default: the newest N:\out\installer\NexScreen-Setup-v*.exe (or out\installer next to this script).
    [string]$Setup,

    # Also run cycle B (existing vdd_settings.xml, /KEEPVDD, reinstall on an existing device).
    [switch]$Extended
)

$ErrorActionPreference = 'Stop'
$appDir = Join-Path $env:ProgramFiles 'NexScreen'
$appExe = Join-Path $appDir 'NexScreen.Companion.exe'
$adbDir = Join-Path $appDir 'platform-tools'
$uninstaller = Join-Path $appDir 'unins000.exe'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\NexScreen.lnk'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C1DC1EFF-E67A-473D-A1EF-6304C039DDA1}_is1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$envKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
$vddSettings = 'C:\VirtualDisplayDriver\vdd_settings.xml'
$vddBackup = "$vddSettings.nexscreen-backup"
$hwid = 'Root\MttVDD'
$expectedRunValue = '"{0}" --tray' -f $appExe

$logDir = Join-Path $env:ProgramData 'NexScreen\installer-test'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'result.log'
Set-Content -Path $log -Value "NexScreen installer test $(Get-Date -Format s)"
$script:failures = 0
$script:step = 0

function Write-Result([bool]$ok, [string]$message) {
    $line = '{0} {1}' -f $(if ($ok) { '[PASS]' } else { '[FAIL]' }), $message
    if (-not $ok) { $script:failures++ }
    Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    Add-Content -Path $log -Value $line
}

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "== $message" -ForegroundColor Cyan
    Add-Content -Path $log -Value "== $message"
}

# ---- Helpers ----

function Get-VddDevices {
    @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object {
        $ids = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data
        $ids -and ($ids | Where-Object { $_ -ieq $hwid })
    })
}

function Get-VddPackages {
    # Get-WindowsDriver returns the published oemNN.inf without depending on the OS language
    @(Get-WindowsDriver -Online | Where-Object { (Split-Path -Leaf $_.OriginalFileName) -ieq 'mttvdd.inf' })
}

function Get-MonitorNames {
    @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction SilentlyContinue | ForEach-Object {
        -join ($_.UserFriendlyName | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ })
    })
}

function Wait-Until([scriptblock]$condition, [int]$seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return [bool](& $condition)
}

function Get-SystemPath {
    (Get-ItemProperty -Path $envKey -Name Path).Path
}

function Test-PathHasAdb {
    @((Get-SystemPath) -split ';' | Where-Object { $_.TrimEnd('\') -ieq $adbDir }).Count -gt 0
}

function Get-NexScreenProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='NexScreen.Companion.exe' OR Name='adb.exe'" | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase)
    })
}

function Invoke-Setup([string[]]$extraArgs, [string]$name) {
    $script:step++
    $setupLog = Join-Path $logDir ('{0:00}-{1}.log' -f $script:step, $name)
    $setupArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$setupLog`"") + $extraArgs
    Write-Host "Running $localSetup $($setupArgs -join ' ')"
    $p = Start-Process -FilePath $localSetup -ArgumentList $setupArgs -Wait -PassThru
    Write-Result ($p.ExitCode -eq 0) "Setup ($name) exit code $($p.ExitCode) (log: $setupLog)"
}

function Invoke-Uninstall([string[]]$extraArgs, [string]$name) {
    $script:step++
    $uninstallLog = Join-Path $logDir ('{0:00}-{1}.log' -f $script:step, $name)
    if (-not (Test-Path $uninstaller)) {
        Write-Result $false "Uninstaller $uninstaller not found"
        return
    }
    $setupArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$uninstallLog`"") + $extraArgs
    Write-Host "Running $uninstaller $($setupArgs -join ' ')"
    $p = Start-Process -FilePath $uninstaller -ArgumentList $setupArgs -Wait -PassThru
    # The uninstaller runs a second copy from %TEMP%; wait until it has removed itself
    $done = Wait-Until { -not (Test-Path $uninstaller) -and -not (Get-Process -Name '_iu*' -ErrorAction SilentlyContinue) } 180
    Write-Result ($p.ExitCode -eq 0 -and $done) "Uninstall ($name) exit code $($p.ExitCode), finished: $done (log: $uninstallLog)"
}

function Test-Installed([bool]$expectRunValue) {
    foreach ($rel in 'NexScreen.Companion.exe', 'nexscreen-vdd-ctl.exe', 'unins000.exe', 'LICENSE.txt',
                     'THIRD_PARTY_NOTICES.txt', 'driver-THIRD_PARTY.md', 'platform-tools\adb.exe',
                     'platform-tools\AdbWinApi.dll', 'platform-tools\AdbWinUsbApi.dll', 'platform-tools\NOTICE.txt',
                     'vdd\MttVDD.inf', 'vdd\MttVDD.dll', 'vdd\mttvdd.cat') {
        Write-Result (Test-Path (Join-Path $appDir $rel)) "File $rel installed"
    }

    $adbVersion = (& (Join-Path $adbDir 'adb.exe') version 2>&1 | Out-String)
    Write-Result ($LASTEXITCODE -eq 0 -and $adbVersion -match 'Android Debug Bridge') "adb.exe runs ($(($adbVersion -split "`n")[0].Trim()))"

    $xmlOk = (Test-Path $vddSettings) -and ((Get-Content $vddSettings -Raw) -match '<width>1728</width>')
    Write-Result $xmlOk "$vddSettings is NexScreen's (1728x1080)"

    if (Test-Path $shortcut) {
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($shortcut)
        Write-Result ($lnk.TargetPath -ieq $appExe) "Shortcut target $($lnk.TargetPath)"
        $expectedArgs = '--tray --adb "{0}"' -f (Join-Path $adbDir 'adb.exe')
        Write-Result ($lnk.Arguments -eq $expectedArgs) "Shortcut arguments $($lnk.Arguments)"
    }
    else {
        Write-Result $false "Shortcut $shortcut exists"
    }

    $arp = Get-ItemProperty -Path $uninstallKey -ErrorAction SilentlyContinue
    Write-Result ($arp -and $arp.DisplayName -eq 'NexScreen' -and $arp.Publisher -eq 'Ramon Pereira') "Add/Remove Programs entry ($($arp.DisplayName) $($arp.DisplayVersion), $($arp.Publisher))"

    $run = (Get-ItemProperty -Path $runKey -Name NexScreen -ErrorAction SilentlyContinue).NexScreen
    if ($expectRunValue) {
        Write-Result ($run -eq $expectedRunValue) "HKCU Run value NexScreen = $run"
    }
    else {
        Write-Result (-not $run) "No HKCU Run value without /AUTOSTART ($run)"
    }

    Write-Result (Test-PathHasAdb) "System PATH contains $adbDir"

    $devices = Get-VddDevices
    Write-Result ($devices.Count -eq 1) "Exactly one $hwid device ($($devices.Count))"
    Write-Result (@($devices | Where-Object { $_.Status -eq 'OK' }).Count -eq 1) "$hwid device status: $($devices.Status -join ', ')"
    & (Join-Path $appDir 'nexscreen-vdd-ctl.exe') status --hwid $hwid | ForEach-Object { Write-Host "  $_" }
    Write-Result ($LASTEXITCODE -eq 0) "nexscreen-vdd-ctl status --hwid $hwid exit code $LASTEXITCODE"
    Write-Result ((Get-VddPackages).Count -ge 1) 'mttvdd.inf is in the driver store'

    $monitorOk = Wait-Until { @(Get-MonitorNames | Where-Object { $_ -match 'VDD' }).Count -ge 1 } 30
    Write-Result $monitorOk "VDD monitor present (monitors: $((Get-MonitorNames) -join ', '))"
}

function Test-Removed([bool]$vddRemoved) {
    Write-Result (-not (Test-Path $appDir)) "$appDir removed"
    Write-Result (-not (Test-Path $shortcut)) 'Shortcut removed'
    Write-Result (-not (Test-Path $uninstallKey)) 'Add/Remove Programs entry removed'
    $run = (Get-ItemProperty -Path $runKey -Name NexScreen -ErrorAction SilentlyContinue).NexScreen
    Write-Result (-not $run) "HKCU Run value removed ($run)"
    Write-Result (-not (Test-Path 'HKCU:\Software\NexScreen')) 'HKCU\Software\NexScreen removed'
    Write-Result (-not (Test-PathHasAdb)) 'adb folder removed from the system PATH'
    Write-Result ((Get-NexScreenProcesses).Count -eq 0) 'No companion/adb process left from Program Files\NexScreen'

    $devices = Get-VddDevices
    $packages = Get-VddPackages
    if ($vddRemoved) {
        Write-Result ($devices.Count -eq 0) "No $hwid device left ($($devices.Count))"
        Write-Result ($packages.Count -eq 0) "mttvdd.inf removed from the driver store ($(($packages | ForEach-Object Driver) -join ', '))"
        $gone = Wait-Until { @(Get-MonitorNames | Where-Object { $_ -match 'VDD' }).Count -eq 0 } 30
        Write-Result $gone "VDD monitor gone (monitors: $((Get-MonitorNames) -join ', '))"
    }
    else {
        Write-Result ($devices.Count -eq 1) "/KEEPVDD: $hwid device kept ($($devices.Count))"
        Write-Result ($packages.Count -ge 1) '/KEEPVDD: mttvdd.inf kept in the driver store'
        Write-Result ((Test-Path $vddSettings) -and ((Get-Content $vddSettings -Raw) -match '<width>1728</width>')) '/KEEPVDD: vdd_settings.xml kept'
    }
}

# ---- Setup file ----
if (-not $Setup) {
    $candidates = @('N:\out\installer', (Join-Path (Split-Path -Parent $PSScriptRoot) 'out\installer')) |
        Where-Object { Test-Path $_ } |
        ForEach-Object { Get-ChildItem $_ -Filter 'NexScreen-Setup-v*.exe' } |
        Sort-Object LastWriteTime -Descending
    if (-not $candidates) { throw 'No NexScreen-Setup-v*.exe found in N:\out\installer. Pass -Setup.' }
    $Setup = $candidates[0].FullName
}
# Run a local copy: the shared folder is read-only and slower
$localSetup = Join-Path $logDir (Split-Path -Leaf $Setup)
Copy-Item $Setup $localSetup -Force
Write-Host "Setup: $Setup"
Add-Content -Path $log -Value "Setup: $Setup ($((Get-FileHash $localSetup).Hash))"

# ---- Preconditions ----
Write-Step 'Preconditions'
$clean = -not (Test-Path $appDir) -and (Get-VddDevices).Count -eq 0 -and (Get-VddPackages).Count -eq 0 -and -not (Test-Path $vddSettings)
Write-Result $clean 'Clean machine: no NexScreen install, no Root\MttVDD device or package, no vdd_settings.xml'
if (-not $clean) {
    Write-Warning 'The VM is not clean; results may be misleading (restore the snapshot first).'
}
$monitorsBefore = Get-MonitorNames
Add-Content -Path $log -Value "Monitors before: $($monitorsBefore -join ', ')"

# ---- Cycle A ----
Write-Step 'A1. Silent install with /AUTOSTART'
Invoke-Setup @('/AUTOSTART') 'install'
Test-Installed $true

Write-Step 'A2. Silent reinstall over the existing install (device must be reused)'
Invoke-Setup @('/AUTOSTART') 'reinstall'
Write-Result ((Get-VddDevices).Count -eq 1) "Still exactly one $hwid device ($((Get-VddDevices).Count))"

Write-Step 'A3. Start the companion like the shortcut and add the per-user settings key'
Start-Process -FilePath $appExe -ArgumentList '--tray', '--adb', "`"$(Join-Path $adbDir 'adb.exe')`"" -WorkingDirectory $appDir
$running = Wait-Until { @(Get-Process -Name 'NexScreen.Companion' -ErrorAction SilentlyContinue).Count -ge 1 } 15
Write-Result $running 'Companion is running in the tray'
New-Item -Path 'HKCU:\Software\NexScreen' -Force | Out-Null
New-ItemProperty -Path 'HKCU:\Software\NexScreen' -Name 'InstallerTest' -Value 1 -PropertyType DWord -Force | Out-Null
Start-Sleep -Seconds 5

Write-Step 'A4. Silent uninstall'
Invoke-Uninstall @() 'uninstall'
Test-Removed $true
Write-Result (-not (Test-Path 'C:\VirtualDisplayDriver')) 'C:\VirtualDisplayDriver removed (it did not exist before)'

# ---- Cycle B ----
if ($Extended) {
    Write-Step 'B1. Install over a user-made vdd_settings.xml, without /AUTOSTART'
    New-Item -ItemType Directory -Force (Split-Path $vddSettings) | Out-Null
    $userXml = "<?xml version='1.0' encoding='utf-8'?>`r`n<vdd_settings><!-- installer-test user file --></vdd_settings>`r`n"
    Set-Content -Path $vddSettings -Value $userXml -NoNewline -Encoding UTF8
    $userHash = (Get-FileHash $vddSettings).Hash
    Invoke-Setup @() 'install-over-user-settings'
    Test-Installed $false
    Write-Result ((Test-Path $vddBackup) -and (Get-FileHash $vddBackup).Hash -eq $userHash) "User file backed up to $vddBackup"

    Write-Step 'B2. Uninstall with /KEEPVDD'
    Invoke-Uninstall @('/KEEPVDD') 'uninstall-keepvdd'
    Test-Removed $false

    Write-Step 'B3. Install again on the kept device'
    Invoke-Setup @() 'install-on-existing-device'
    Write-Result ((Get-VddDevices).Count -eq 1) "Existing device reused: exactly one $hwid device ($((Get-VddDevices).Count))"

    Write-Step 'B4. Uninstall (removes the virtual monitor)'
    Invoke-Uninstall @() 'uninstall-final'
    Test-Removed $true
    $restored = (Test-Path $vddSettings) -and (Get-FileHash $vddSettings).Hash -eq $userHash -and -not (Test-Path $vddBackup)
    Write-Result $restored 'User vdd_settings.xml restored'

    # Leave the VM as it was
    Remove-Item 'C:\VirtualDisplayDriver' -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
$summary = if ($script:failures -eq 0) { 'ALL CHECKS PASSED' } else { "$($script:failures) CHECK(S) FAILED" }
Write-Host $summary -ForegroundColor $(if ($script:failures -eq 0) { 'Green' } else { 'Red' })
Add-Content -Path $log -Value $summary
Write-Host "Log: $log"
exit $script:failures
