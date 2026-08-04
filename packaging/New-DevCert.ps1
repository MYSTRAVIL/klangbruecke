<#
.SYNOPSIS
    Creates and trusts a self-signed certificate for sideloading Klangbruecke.

.DESCRIPTION
    Run once. Must be run elevated - installing into LocalMachine\TrustedPeople requires it.

    The subject MUST match the Publisher attribute in packaging/AppxManifest.xml exactly,
    or Windows will refuse to install the package.

    The .pfx is gitignored. Never commit it.
#>
[CmdletBinding()]
param(
    [string] $Subject  = 'CN=Klangbruecke Development',
    [string] $PfxPath  = (Join-Path $PSScriptRoot 'KlangbrueckeDev.pfx'),
    [int]    $ValidityYears = 5
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw 'Run this elevated - it installs a certificate into LocalMachine\TrustedPeople.'
}

# Re-running is normal (the script has failed part-way before, and certs expire). Without this,
# every run leaves another cert with the same subject behind and signing picks one at random.
$stale = @(Get-ChildItem 'Cert:\CurrentUser\My' | Where-Object { $_.Subject -eq $Subject })
if ($stale.Count -gt 0) {
    Write-Host "Removing $($stale.Count) existing '$Subject' certificate(s) from CurrentUser\My:"
    foreach ($old in $stale) {
        Write-Host "  $($old.Thumbprint)"
        Remove-Item -Path "Cert:\CurrentUser\My\$($old.Thumbprint)" -Force
    }
}

Write-Host "Creating self-signed certificate: $Subject"

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -FriendlyName 'Klangbruecke Development' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears($ValidityYears) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

Write-Host "  Thumbprint: $($cert.Thumbprint)"

# Random password; it never leaves this machine and the pfx is gitignored.
#
# Create().GetBytes() rather than the static Fill(): this script is run elevated, and an elevated
# shell on this machine is Windows PowerShell 5.1 on .NET Framework, where the static does not exist.
$bytes = [byte[]]::new(24)
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try   { $rng.GetBytes($bytes) }
finally { $rng.Dispose() }
$plain = [Convert]::ToBase64String($bytes)
$password = ConvertTo-SecureString -String $plain -Force -AsPlainText

Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $PfxPath -Password $password | Out-Null
Write-Host "  Exported: $PfxPath"

# Trust it, so the signed package will install.
Import-Certificate -FilePath (Export-Certificate -Cert $cert -FilePath (Join-Path $env:TEMP 'klangbruecke-dev.cer')).FullName `
                   -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Write-Host '  Installed into LocalMachine\TrustedPeople'

$passwordFile = Join-Path $PSScriptRoot 'KlangbrueckeDev.pfx.password'
Set-Content -Path $passwordFile -Value $plain -NoNewline
Write-Host "  Password written to: $passwordFile (gitignored)"

Write-Host ''
Write-Host 'Done. Now run packaging/Build-Msix.ps1'
