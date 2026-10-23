#Requires -Version 5.1
<#
.SYNOPSIS
    Test-signs the driver package in out\driver: creates (or reuses) the "CN=NexScreen Test" certificate,
    exports the .cer, signs the .dll, generates the .cat with inf2cat and signs the .cat.
.PARAMETER Os
    inf2cat /os list. The INF targets NT$ARCH$.10.0...22000, so the default is Windows 11 21H2 x64.
.EXAMPLE
    .\tools\make-test-cert.ps1
#>
[CmdletBinding()]
param(
    [string]$Os = '10_CO_X64',
    [string]$WdkVersion = '10.0.26100.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'out\driver'
$subject = 'CN=NexScreen Test'

if (-not (Test-Path (Join-Path $outDir 'NexScreenVDD.inf'))) {
    throw "Package not found in $outDir. Run .\tools\build-driver.ps1 first."
}

$kitBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\$WdkVersion"
$signtool = Join-Path $kitBin 'x64\signtool.exe'
$inf2cat = Join-Path $kitBin 'x86\Inf2Cat.exe'
foreach ($tool in $signtool, $inf2cat) {
    if (-not (Test-Path $tool)) {
        throw "$tool not found. Install the SDK/WDK $WdkVersion (see PROJETO.md section 5)."
    }
}

# 1. Code signing certificate in CurrentUser\My (reused if it already exists)
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My -HashAlgorithm sha256 -NotAfter (Get-Date).AddYears(2)
    Write-Host "Created certificate $($cert.Thumbprint)"
} else {
    Write-Host "Reusing certificate $($cert.Thumbprint)"
}

# 2. Export the public certificate for the VM
$cerPath = Join-Path $outDir 'NexScreenTest.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null

function Invoke-Sign([string]$path) {
    & $signtool sign /fd sha256 /sha1 $cert.Thumbprint /s My $path
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed on $path"
    }
}

# 3. Sign the binary before building the catalog, then generate and sign the catalog
Invoke-Sign (Join-Path $outDir 'NexScreenVDD.dll')

$catPath = Join-Path $outDir 'NexScreenVDD.cat'
if (Test-Path $catPath) {
    Remove-Item $catPath -Force
}
& $inf2cat "/driver:$outDir" "/os:$Os" /uselocaltime
if ($LASTEXITCODE -ne 0) {
    throw 'inf2cat failed.'
}

Invoke-Sign $catPath
Invoke-Sign (Join-Path $outDir 'nexscreen-vdd-ctl.exe')

Write-Host ''
Write-Host "Signed package in $outDir"
Write-Host 'Copy out\driver and tools\vm-*.ps1 to the test VM and run vm-install-driver.ps1 as administrator.'
