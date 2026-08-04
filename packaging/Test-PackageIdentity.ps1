<#
.SYNOPSIS
    Verifies PackageIdentity's probe answers correctly in BOTH directions.

.DESCRIPTION
    PackageIdentity decides whether this app has MSIX package identity, which gates the calls half
    (phoneLineTransportManagement) and, since docs/FINDINGS.md section 8, guards the music half
    against a call that terminates an unpackaged process outright.

    The unit suite can only ever see one of the two answers: the test host is unpackaged, so it
    proves the probe returns 15700 with no identity. Nothing in the suite can prove it returns
    something else WITH identity - and that is the failure that matters, because a probe that always
    said "unpackaged" would silently disable both halves in the shipped MSIX while every test stayed
    green.

    This script covers that direction without building an MSIX, by borrowing an installed package's
    identity. Invoke-CommandInDesktopPackage launches a process with the identity of a package that
    is already on the machine, so the same P/Invoke can be run both ways and compared.

    Identity rides on the process token; trust level gates capabilities, not identity. So borrowing a
    full-trust desktop-bridge package answers the identity question for Klangbruecke's own MSIX too.

    Run this after ANY change to the DllImport in src/Klangbruecke/Platform/PackageIdentity.cs.
    Needs no elevation. Read-only: launches a child process, writes one temp file, changes nothing.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File packaging\Test-PackageIdentity.ps1
#>
[CmdletBinding()]
param(
    # Any installed package with an entry point works. Windows Terminal is a full-trust
    # desktop-bridge package, so the borrowed child can write its result back out.
    [string] $BorrowFrom = 'Microsoft.WindowsTerminal',

    # Set only when this script re-invokes itself as the borrowed-identity child.
    [string] $ChildResultPath
)

$ErrorActionPreference = 'Stop'

# APPMODEL_ERROR_NO_PACKAGE. ERROR_SUCCESS / ERROR_INSUFFICIENT_BUFFER both mean a package exists;
# the probe passes a zero length and a null buffer, so in practice it is always the latter.
$NoPackage         = 15700
$Success           = 0
$InsufficientBuffer = 122

# Mirrors the declaration in src/Klangbruecke/Platform/PackageIdentity.cs. If you change one, change
# both - a copy that drifts tests nothing.
function Invoke-IdentityProbe {
    if (-not ('Klangbruecke.IdentityProbe' -as [type])) {
        Add-Type -Language CSharp -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace Klangbruecke
{
    public static class IdentityProbe
    {
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);

        public static int Run()
        {
            uint length = 0;
            return GetCurrentPackageFullName(ref length, IntPtr.Zero);
        }
    }
}
"@
    }

    return [Klangbruecke.IdentityProbe]::Run()
}

# --- child mode: run under the borrowed identity, report back through a file ---------------------
# Invoke-CommandInDesktopPackage has no return channel and does not wait, so the result has to come
# back out of band.
if ($ChildResultPath) {
    try   { Set-Content -Path $ChildResultPath -Value (Invoke-IdentityProbe) }
    catch { Set-Content -Path $ChildResultPath -Value "THREW: $($_.Exception.Message)" }
    return
}

# --- parent mode ---------------------------------------------------------------------------------
# The Appx module does not load in PowerShell 7 (Operation is not supported on this platform), and
# Invoke-CommandInDesktopPackage lives in it. Hand off rather than fail.
if ($PSVersionTable.PSEdition -eq 'Core') {
    Write-Host 'Appx cmdlets need Windows PowerShell; relaunching there.'
    $winPs = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $winPs -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -BorrowFrom $BorrowFrom
    exit $LASTEXITCODE
}

$unpackaged = Invoke-IdentityProbe
Write-Host "unpackaged           probe=$unpackaged"

$package = Get-AppxPackage -Name $BorrowFrom | Select-Object -First 1
if (-not $package) {
    throw "Package '$BorrowFrom' is not installed. Pass -BorrowFrom with any installed package that has an entry point."
}

$appId = (Get-AppxPackageManifest -Package $package.PackageFullName).Package.Applications.Application.Id |
         Select-Object -First 1

$resultPath = Join-Path ([System.IO.Path]::GetTempPath()) "klangbruecke-identity-$PID.txt"
Remove-Item $resultPath -ErrorAction SilentlyContinue

Invoke-CommandInDesktopPackage `
    -PackageFamilyName $package.PackageFamilyName `
    -AppId $appId `
    -Command (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
    -Args "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -ChildResultPath `"$resultPath`"" `
    -PreventBreakaway

# The launch is asynchronous, and the child pays a JIT cost for Add-Type.
$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-Path $resultPath) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
}

if (-not (Test-Path $resultPath)) {
    throw "The borrowed-identity child never reported back. Identity was $($package.PackageFamilyName), AppId $appId."
}

$packaged = (Get-Content $resultPath -Raw).Trim()
Remove-Item $resultPath -ErrorAction SilentlyContinue
Write-Host "packaged ($($package.PackageFamilyName))  probe=$packaged"

$unpackagedOk = $unpackaged -eq $NoPackage
$packagedOk   = ($packaged -as [int]) -in @($Success, $InsufficientBuffer)

if (-not $unpackagedOk) {
    Write-Host "FAIL: expected $NoPackage unpackaged, got $unpackaged. The P/Invoke is not resolving." -ForegroundColor Red
}
if (-not $packagedOk) {
    Write-Host "FAIL: expected $Success or $InsufficientBuffer with identity, got $packaged." -ForegroundColor Red
    Write-Host '      PackageIdentity.IsPackaged would be false in the shipped MSIX - both halves off.' -ForegroundColor Red
}

if ($unpackagedOk -and $packagedOk) {
    Write-Host 'PASS: the probe distinguishes both directions.' -ForegroundColor Green
    exit 0
}

exit 1
