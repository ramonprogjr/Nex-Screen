#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the single Windows installer, out\installer\NexScreen-Setup-v<version>.exe (Inno Setup).
.DESCRIPTION
    1. Downloads the third-party payload into fresh folders under out\installer\downloads and verifies it:
       - Virtual Display Driver 25.7.23 (driver-only zip): pinned SHA256, and MttVDD.dll + mttvdd.cat must have a
         valid Authenticode signature from the SignPath Foundation;
       - Android SDK Platform-Tools (pinned version): pinned SHA256, and adb.exe + AdbWinApi.dll + AdbWinUsbApi.dll
         must have a valid Authenticode signature from Google LLC.
    2. Publishes the companion (self-contained, single file, win-x64), unless -CompanionDir points to a publish output.
    3. Builds nexscreen-vdd-ctl.exe with tools\build-driver.ps1, unless -SkipDriverBuild (then out\driver is reused).
    4. Stages everything in out\installer\stage and runs ISCC on windows\installer\NexScreen.iss.
    Needs Inno Setup 6 (winget install --id JRSoftware.InnoSetup -e), the .NET 8 SDK and, for step 3, VS 2022 + WDK.
.EXAMPLE
    .\tools\build-installer.ps1
    .\tools\build-installer.ps1 -Version v0.2.0
    .\tools\build-installer.ps1 -SkipDriverBuild -CompanionDir out\companion
#>
[CmdletBinding()]
param(
    # Version such as v0.2.0 or 0.2.0. Default: git describe --tags, or 0.0.0-dev without tags.
    [string]$Version,

    # Existing companion publish output (e.g. out\companion from CI). Default: dotnet publish here.
    [string]$CompanionDir,

    # Reuse out\driver\nexscreen-vdd-ctl.exe instead of running build-driver.ps1.
    [switch]$SkipDriverBuild,

    # Path to ISCC.exe. Default: the per-user or per-machine Inno Setup 6 install, then PATH.
    [string]$IsccPath,

    # Use the pinned Inno Setup in out\installer\innosetup, downloading, verifying and installing it (per user, no
    # admin) if needed. Used by CI so the build does not depend on what the runner image has.
    [switch]$InstallInnoSetup
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow with the progress bar in PowerShell 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'out\installer'
$downloads = Join-Path $outDir 'downloads'
$stage = Join-Path $outDir 'stage'
$iss = Join-Path $root 'windows\installer\NexScreen.iss'

# ---- Pinned third-party payload. To update: change the URL, download it once, verify it and paste the SHA256. ----
$Vdd = @{
    Name   = 'Virtual Display Driver 25.7.23'
    Url    = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip'
    Sha256 = 'E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A'
    Signer = 'CN=SignPath Foundation,'
}
$PlatformTools = @{
    Name   = 'Android SDK Platform-Tools r37.0.1'
    Url    = 'https://dl.google.com/android/repository/platform-tools_r37.0.1-win.zip'
    Sha256 = '45F4D63113E895EBDE0C90F194099A4676B6AC653BD28D54314A9E022BBC1A99'
    Signer = 'CN=Google LLC,'
}
$InnoSetup = @{
    Name   = 'Inno Setup 6.7.3'
    Url    = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
    Sha256 = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
    Signer = 'CN=Pyrsys B.V.,'
}

function Install-PinnedInnoSetup {
    $dir = Join-Path $outDir 'innosetup'
    $iscc = Join-Path $dir 'ISCC.exe'
    if (Test-Path $iscc) { return $iscc }
    $folder = Join-Path $downloads 'innosetup'
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
    New-Item -ItemType Directory -Force $folder | Out-Null
    $exe = Join-Path $folder 'innosetup-setup.exe'
    Write-Host "Downloading $($InnoSetup.Name): $($InnoSetup.Url)"
    Invoke-WebRequest -Uri $InnoSetup.Url -OutFile $exe -UseBasicParsing
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
    if ($hash -ne $InnoSetup.Sha256) {
        throw "SHA256 mismatch for $($InnoSetup.Name): expected $($InnoSetup.Sha256), got $hash."
    }
    Assert-Signed $exe $InnoSetup.Signer
    $p = Start-Process -FilePath $exe -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/NOICONS', "/DIR=`"$dir`"")
    if ($p.ExitCode -ne 0 -or -not (Test-Path $iscc)) {
        throw "Installing $($InnoSetup.Name) failed (exit code $($p.ExitCode))."
    }
    return $iscc
}

function Get-VerifiedZip([hashtable]$Package, [string]$Folder) {
    # Always a fresh, empty folder: nothing left over from an earlier (possibly tampered) download is reused
    if (Test-Path $Folder) { Remove-Item $Folder -Recurse -Force }
    New-Item -ItemType Directory -Force $Folder | Out-Null
    $zip = Join-Path $Folder 'package.zip'
    Write-Host "Downloading $($Package.Name): $($Package.Url)"
    Invoke-WebRequest -Uri $Package.Url -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $Package.Sha256) {
        throw "SHA256 mismatch for $($Package.Name): expected $($Package.Sha256), got $hash."
    }
    Write-Host "  SHA256 OK ($hash)"
    $extracted = Join-Path $Folder 'x'
    Expand-Archive -Path $zip -DestinationPath $extracted
    return $extracted
}

function Assert-Signed([string]$Path, [string]$SignerPrefix) {
    $sig = Get-AuthenticodeSignature -FilePath $Path
    if ($sig.Status -ne 'Valid') {
        throw "Authenticode signature of $Path is $($sig.Status): $($sig.StatusMessage)"
    }
    $subject = $sig.SignerCertificate.Subject
    if (-not $subject.StartsWith($SignerPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected signer for ${Path}: $subject"
    }
    Write-Host "  Signature OK: $(Split-Path -Leaf $Path) ($($subject.Split(',')[0]))"
}

function Find-Iscc {
    if ($IsccPath) {
        if (-not (Test-Path $IsccPath)) { throw "ISCC not found at $IsccPath." }
        return $IsccPath
    }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'ISCC.exe (Inno Setup 6) not found. Install it with: winget install --id JRSoftware.InnoSetup -e'
}

# ---- Version ----
if (-not $Version) {
    # PowerShell 5.1 turns git's stderr into a terminating error under 'Stop', so allow it here
    try {
        $Version = (& git -C $root describe --tags --match 'v*' 2>$null)
        if ($LASTEXITCODE -ne 0) { $Version = $null }
    }
    catch {
        $Version = $null
    }
    if (-not $Version) { $Version = '0.0.0-dev' }
}
$Version = $Version.Trim() -replace '^v', ''
if ($Version -notmatch '^\d+\.\d+\.\d+([-+.][0-9A-Za-z.+-]*)?$') {
    throw "Invalid version '$Version' (expected e.g. v0.2.0)."
}
$fileVersion = ($Version -replace '^(\d+)\.(\d+)\.(\d+).*$', '$1.$2.$3') + '.0'
Write-Host "NexScreen $Version (file version $fileVersion)"

$iscc = if ($InstallInnoSetup) { Install-PinnedInnoSetup } else { Find-Iscc }
Write-Host "ISCC: $iscc"

# ---- 1. Third-party payload ----
$vddDir = Get-VerifiedZip $Vdd (Join-Path $downloads 'vdd')
$vddFiles = Join-Path $vddDir 'VirtualDisplayDriver'
foreach ($f in 'MttVDD.inf', 'MttVDD.dll', 'mttvdd.cat') {
    if (-not (Test-Path (Join-Path $vddFiles $f))) { throw "$f missing from the VDD package." }
}
Assert-Signed (Join-Path $vddFiles 'MttVDD.dll') $Vdd.Signer
Assert-Signed (Join-Path $vddFiles 'mttvdd.cat') $Vdd.Signer
if ((Get-Content (Join-Path $vddFiles 'MttVDD.inf') -Raw) -notmatch 'Root\\MttVDD') {
    throw 'MttVDD.inf does not list the Root\MttVDD hardware ID.'
}

$ptDir = Get-VerifiedZip $PlatformTools (Join-Path $downloads 'platform-tools')
$ptFiles = Join-Path $ptDir 'platform-tools'
$adbFiles = 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll'
foreach ($f in $adbFiles + 'NOTICE.txt', 'source.properties') {
    if (-not (Test-Path (Join-Path $ptFiles $f))) { throw "$f missing from the Platform-Tools package." }
}
foreach ($f in $adbFiles) {
    Assert-Signed (Join-Path $ptFiles $f) $PlatformTools.Signer
}

# ---- 2. Companion ----
if ($CompanionDir) {
    $companionOut = (Resolve-Path $CompanionDir).Path
}
else {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet not found. Install the .NET 8 SDK (see PROJETO.md section 5).'
    }
    $companionOut = Join-Path $outDir 'companion'
    if (Test-Path $companionOut) { Remove-Item $companionOut -Recurse -Force }
    $project = Join-Path $root 'windows\companion\src\NexScreen.Companion\NexScreen.Companion.csproj'
    # Same flags as .github/workflows/release.yml
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
        -o $companionOut --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }
}
if (-not (Test-Path (Join-Path $companionOut 'NexScreen.Companion.exe'))) {
    throw "NexScreen.Companion.exe not found in $companionOut."
}

# ---- 3. nexscreen-vdd-ctl ----
if (-not $SkipDriverBuild) {
    & (Join-Path $PSScriptRoot 'build-driver.ps1') -Configuration Release
}
$ctl = Join-Path $root 'out\driver\nexscreen-vdd-ctl.exe'
if (-not (Test-Path $ctl)) { throw "$ctl not found. Run .\tools\build-driver.ps1 first." }

# ---- 4. Stage and compile ----
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
foreach ($d in 'companion', 'platform-tools', 'vdd') {
    New-Item -ItemType Directory -Force (Join-Path $stage $d) | Out-Null
}
Get-ChildItem $companionOut -Exclude '*.pdb', 'README.md', 'LICENSE' |
    Copy-Item -Destination (Join-Path $stage 'companion') -Recurse
Copy-Item $ctl $stage
foreach ($f in $adbFiles + 'NOTICE.txt', 'source.properties') {
    Copy-Item (Join-Path $ptFiles $f) (Join-Path $stage 'platform-tools')
}
foreach ($f in 'MttVDD.inf', 'MttVDD.dll', 'mttvdd.cat') {
    Copy-Item (Join-Path $vddFiles $f) (Join-Path $stage 'vdd')
}

& $iscc /Qp "/DAppVersion=$Version" "/DFileVersion=$fileVersion" "/DStageDir=$stage" "/DOutputDir=$outDir" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)." }

$setup = Join-Path $outDir "NexScreen-Setup-v$Version.exe"
if (-not (Test-Path $setup)) { throw "$setup was not produced." }
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
Write-Host ''
Write-Host "Installer: $setup" -ForegroundColor Green
Write-Host "SHA256:    $hash"
