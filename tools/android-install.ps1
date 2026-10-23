#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the NexScreen Android app, installs it on the tablet over USB and forwards the NexLink port.
.DESCRIPTION
    Runs gradlew assembleDebug in android\, installs the debug APK with adb install -r and runs
    adb reverse tcp:27183 tcp:27183 so the app reaches the Windows companion at 127.0.0.1:27183.
    The reverse mapping is lost when the cable is unplugged or adb restarts; run the script again
    (with -SkipBuild) in that case.
.EXAMPLE
    .\tools\android-install.ps1
    .\tools\android-install.ps1 -SkipBuild -Launch
    .\tools\android-install.ps1 -Serial R9XY106J8ZK
#>
[CmdletBinding()]
param(
    # Install the APK that is already built
    [switch]$SkipBuild,
    # Start the app after installing
    [switch]$Launch,
    # adb serial, required only when more than one device is connected
    [string]$Serial,
    [int]$Port = 27183
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$androidDir = Join-Path $root 'android'
$apk = Join-Path $androidDir 'app\build\outputs\apk\debug\app-debug.apk'
$package = 'app.nexscreen'

function Find-Jdk {
    $candidates = @()
    if ($env:JAVA_HOME) { $candidates += $env:JAVA_HOME }
    foreach ($pattern in @("$env:ProgramFiles\Microsoft\jdk-17*",
                           "$env:ProgramFiles\Eclipse Adoptium\jdk-17*",
                           "$env:ProgramFiles\Android\Android Studio\jbr")) {
        $candidates += @(Get-Item $pattern -ErrorAction SilentlyContinue | Sort-Object Name -Descending | ForEach-Object { $_.FullName })
    }
    foreach ($dir in $candidates) {
        if ($dir -and (Test-Path (Join-Path $dir 'bin\java.exe'))) { return $dir }
    }
    return $null
}

function Find-Sdk {
    foreach ($dir in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA 'Android\Sdk'))) {
        if ($dir -and (Test-Path (Join-Path $dir 'platforms'))) { return $dir }
    }
    return $null
}

function Find-Adb([string]$sdk) {
    if ($sdk) {
        $path = Join-Path $sdk 'platform-tools\adb.exe'
        if (Test-Path $path) { return $path }
    }
    $cmd = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $winget = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Recurse -Filter adb.exe -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($winget) { return $winget.FullName }
    return $null
}

$sdk = Find-Sdk
$adb = Find-Adb $sdk
if (-not $adb) {
    throw 'adb.exe not found. Install the Android SDK platform-tools (see docs\build-android.md).'
}
Write-Host "adb: $adb"

if (-not $SkipBuild) {
    $jdk = Find-Jdk
    if (-not $jdk) {
        throw 'JDK 17 not found. Install it with: winget install --id Microsoft.OpenJDK.17 -e'
    }
    if (-not $sdk) {
        throw "Android SDK not found in ANDROID_HOME or $env:LOCALAPPDATA\Android\Sdk (see docs\build-android.md)."
    }
    $env:JAVA_HOME = $jdk
    $env:ANDROID_HOME = $sdk
    Write-Host "JDK: $jdk"
    Write-Host "SDK: $sdk"

    # local.properties is machine-specific and gitignored; create it if missing
    $localProps = Join-Path $androidDir 'local.properties'
    if (-not (Test-Path $localProps)) {
        $sdkDir = $sdk.Replace('\', '/').Replace(':', '\:')
        [System.IO.File]::WriteAllText($localProps, "sdk.dir=$sdkDir`n", (New-Object System.Text.UTF8Encoding($false)))
    }

    Push-Location $androidDir
    try {
        & .\gradlew.bat assembleDebug --console=plain
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($exitCode -ne 0) {
        throw "gradlew assembleDebug failed (exit code $exitCode)."
    }
}

if (-not (Test-Path $apk)) {
    throw "APK not found: $apk. Run without -SkipBuild."
}

# Pick the device: exactly one in the 'device' state, or the one given by -Serial
$lines = @(& $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\S' })
$devices = @($lines | ForEach-Object {
    $parts = $_ -split '\s+'
    [pscustomobject]@{ Serial = $parts[0]; State = $parts[1] }
})
if ($Serial) {
    $devices = @($devices | Where-Object { $_.Serial -eq $Serial })
    if ($devices.Count -eq 0) {
        throw "Device $Serial is not connected. Check the cable and run 'adb devices'."
    }
}
if ($devices.Count -eq 0) {
    throw 'No Android device connected. Plug in the tablet with USB debugging enabled (see docs\build-android.md).'
}
$unauthorized = @($devices | Where-Object { $_.State -eq 'unauthorized' })
if ($unauthorized.Count -gt 0 -and @($devices | Where-Object { $_.State -eq 'device' }).Count -eq 0) {
    throw "Device $($unauthorized[0].Serial) is unauthorized. Unlock the tablet and accept the 'Allow USB debugging' prompt, then run this again."
}
$ready = @($devices | Where-Object { $_.State -eq 'device' })
if ($ready.Count -eq 0) {
    throw "Device $($devices[0].Serial) is '$($devices[0].State)'. Reconnect the cable or run 'adb kill-server'."
}
if ($ready.Count -gt 1) {
    throw "More than one device connected ($(($ready | ForEach-Object { $_.Serial }) -join ', ')). Pass -Serial."
}
$target = $ready[0].Serial
$model = (& $adb -s $target shell getprop ro.product.model | Out-String).Trim()
Write-Host "Device: $target ($model)"

& $adb -s $target install -r $apk
if ($LASTEXITCODE -ne 0) {
    throw "adb install failed (exit code $LASTEXITCODE)."
}

& $adb -s $target reverse "tcp:$Port" "tcp:$Port" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "adb reverse failed (exit code $LASTEXITCODE)."
}
Write-Host "Port forwarded: tablet 127.0.0.1:$Port -> PC 127.0.0.1:$Port"

if ($Launch) {
    & $adb -s $target shell am start -n "$package/.MainActivity" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start the app (exit code $LASTEXITCODE)."
    }
    Write-Host 'NexScreen started on the tablet.'
} else {
    Write-Host ''
    Write-Host 'Open NexScreen on the tablet, or run:'
    Write-Host "  `"$adb`" -s $target shell am start -n $package/.MainActivity"
}
Write-Host 'Then start the NexScreen companion on Windows. The app connects on its own.'
