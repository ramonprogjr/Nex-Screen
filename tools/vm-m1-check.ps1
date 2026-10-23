#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Automated part of the M1 acceptance checklist (PROJETO.md 6.6), run INSIDE the test VM after vm-install-driver.ps1.
.DESCRIPTION
    - Item 2: the NexScreen display adapter and monitor are present with no problem code.
    - Item 5: every advertised mode (1920x1200 and 1600x900 at 30/60 Hz) applies and reads back correctly.
    - Items 6/7: -Cycles N runs nexscreen-vdd-ctl remove/create N times, checking that the monitor disappears and
      comes back each time and that no new DriverFrameworks-UserMode errors were logged.
    Results are printed and saved to C:\ProgramData\NexScreen\m1-check.log.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File N:\tools\vm-m1-check.ps1 -Cycles 10
#>
[CmdletBinding()]
param(
    [int]$Cycles = 0
)

$ErrorActionPreference = 'Stop'
$logDir = Join-Path $env:ProgramData 'NexScreen'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'm1-check.log'
$script:failures = 0

function Write-Result([bool]$ok, [string]$message) {
    $line = '{0} {1}' -f $(if ($ok) { '[PASS]' } else { '[FAIL]' }), $message
    if (-not $ok) { $script:failures++ }
    Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    Add-Content -Path $log -Value $line
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class NxDisplay
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }

    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

    public static DEVMODE NewDevMode()
    {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        return dm;
    }
}
'@

function Get-DisplayDevice([string]$parent, [int]$index) {
    $dd = New-Object NxDisplay+DISPLAY_DEVICE
    $dd.cb = [Runtime.InteropServices.Marshal]::SizeOf($dd)
    # PowerShell turns $null into "" for string arguments; EnumDisplayDevices needs a real null to list adapters
    $device = if ($parent) { $parent } else { [NullString]::Value }
    if ([NxDisplay]::EnumDisplayDevices($device, $index, [ref]$dd, 0)) { return $dd }
    return $null
}

# Returns the output (\\.\DISPLAYn) driven by NexScreen: matched by adapter string or by its attached monitor
function Get-NexScreenOutput([switch]$Verbose) {
    for ($i = 0; ($dd = Get-DisplayDevice $null $i); $i++) {
        $monitors = @()
        for ($j = 0; ($mon = Get-DisplayDevice $dd.DeviceName $j); $j++) {
            $monitors += $mon
        }
        if ($Verbose) {
            $line = "  $($dd.DeviceName) adapter='$($dd.DeviceString)' flags=0x$('{0:X}' -f $dd.StateFlags) monitors=" +
                (($monitors | ForEach-Object { "'$($_.DeviceString)'" }) -join ',')
            Write-Host $line
            Add-Content -Path $log -Value $line
        }
        if ($dd.DeviceString -like '*NexScreen*' -or ($monitors | Where-Object { $_.DeviceString -like '*NexScreen*' })) {
            return $dd
        }
    }
    return $null
}

function Get-CurrentMode([string]$device) {
    $dm = [NxDisplay]::NewDevMode()
    if (-not [NxDisplay]::EnumDisplaySettings($device, [NxDisplay]::ENUM_CURRENT_SETTINGS, [ref]$dm)) { return $null }
    return $dm
}

function Get-NexScreenPnp {
    $adapter = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -eq 'NexScreen Virtual Display' -and $_.Status -eq 'OK' }
    $monitor = Get-CimInstance -Namespace root\wmi WmiMonitorID -ErrorAction SilentlyContinue |
        Where-Object { $_.Active -and (-join [char[]]($_.UserFriendlyName -ne 0)) -eq 'NexScreen' }
    return [pscustomobject]@{ Adapter = $adapter; Monitor = $monitor }
}

function Get-UmdfErrorCount {
    $filter = @{ LogName = 'Microsoft-Windows-DriverFrameworks-UserMode/Operational'; Level = 1, 2 }
    try { return @(Get-WinEvent -FilterHashtable $filter -ErrorAction Stop).Count } catch { return 0 }
}

function Wait-Until([scriptblock]$condition, [int]$timeoutSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

Set-Content -Path $log -Value "NexScreen M1 check $(Get-Date -Format s)"

# --- Item 2: device present -------------------------------------------------------------------------------------
$pnp = Get-NexScreenPnp
Write-Result ($null -ne $pnp.Adapter) 'Adapter "NexScreen Virtual Display" present with status OK'
Write-Result ($null -ne $pnp.Monitor) 'Active monitor with EDID name "NexScreen"'

$output = Get-NexScreenOutput
Write-Result ($null -ne $output) "NexScreen output found ($($output.DeviceName))"
if (-not $output) {
    Write-Host 'Display outputs seen:'
    Get-NexScreenOutput -Verbose | Out-Null
    exit 1
}
$attached = ($output.StateFlags -band [NxDisplay]::DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) -ne 0
Write-Result $attached 'NexScreen output is part of the desktop (extend mode)'

# --- Item 5: modes ----------------------------------------------------------------------------------------------
$advertised = @()
for ($m = 0; ; $m++) {
    $dm = [NxDisplay]::NewDevMode()
    if (-not [NxDisplay]::EnumDisplaySettings($output.DeviceName, $m, [ref]$dm)) { break }
    $advertised += '{0}x{1}@{2}' -f $dm.dmPelsWidth, $dm.dmPelsHeight, $dm.dmDisplayFrequency
}
$advertised = $advertised | Sort-Object -Unique
Write-Host "Advertised modes: $($advertised -join ', ')"
Add-Content -Path $log -Value "Advertised modes: $($advertised -join ', ')"

$expected = @(@(1920, 1200, 60), @(1920, 1200, 30), @(1600, 900, 60), @(1600, 900, 30))
foreach ($mode in $expected + ,@(1920, 1200, 60)) {
    $w, $h, $hz = $mode
    $dm = [NxDisplay]::NewDevMode()
    $dm.dmPelsWidth = $w; $dm.dmPelsHeight = $h; $dm.dmDisplayFrequency = $hz
    $dm.dmFields = [NxDisplay]::DM_PELSWIDTH -bor [NxDisplay]::DM_PELSHEIGHT -bor [NxDisplay]::DM_DISPLAYFREQUENCY
    $rc = [NxDisplay]::ChangeDisplaySettingsEx($output.DeviceName, [ref]$dm, [IntPtr]::Zero, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds 2
    $cur = Get-CurrentMode $output.DeviceName
    $ok = ($rc -eq 0) -and $cur -and $cur.dmPelsWidth -eq $w -and $cur.dmPelsHeight -eq $h -and $cur.dmDisplayFrequency -eq $hz
    Write-Result $ok ("Mode {0}x{1}@{2}: ChangeDisplaySettingsEx={3}, current={4}x{5}@{6}" -f `
        $w, $h, $hz, $rc, $cur.dmPelsWidth, $cur.dmPelsHeight, $cur.dmDisplayFrequency)
}

# --- Items 6/7: remove/create cycles ----------------------------------------------------------------------------
if ($Cycles -gt 0) {
    $ctl = Join-Path $env:ProgramData 'NexScreen\driver\nexscreen-vdd-ctl.exe'
    if (-not (Test-Path $ctl)) { throw "$ctl not found; run vm-install-driver.ps1 first." }
    # The UMDF operational log is disabled by default; without it the error check below would see nothing
    wevtutil set-log 'Microsoft-Windows-DriverFrameworks-UserMode/Operational' /enabled:true
    $errorsBefore = Get-UmdfErrorCount

    for ($c = 1; $c -le $Cycles; $c++) {
        & $ctl remove | Out-Null
        $removed = ($LASTEXITCODE -eq 0) -and (Wait-Until { -not (Get-NexScreenPnp).Monitor })
        $dwmAlive = $null -ne (Get-Process dwm -ErrorAction SilentlyContinue)
        Write-Result ($removed -and $dwmAlive) "Cycle ${c}: remove (monitor gone, dwm running)"

        & $ctl create | Out-Null
        $back = ($LASTEXITCODE -eq 0) -and (Wait-Until { (Get-NexScreenPnp).Monitor })
        Write-Result $back "Cycle ${c}: create (monitor back)"
    }

    $newErrors = (Get-UmdfErrorCount) - $errorsBefore
    Write-Result ($newErrors -le 0) "No new DriverFrameworks-UserMode errors after $Cycles cycles ($newErrors new)"
}

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host "ALL CHECKS PASSED. Log: $log" -ForegroundColor Green
} else {
    Write-Host "$($script:failures) CHECK(S) FAILED. Log: $log" -ForegroundColor Red
}
exit $script:failures
