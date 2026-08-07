<#
.SYNOPSIS
    Builds a signed Klangbruecke MSIX and publishes it as a GitHub release.

.DESCRIPTION
    The manual release path: you run it, it builds + signs locally (nothing leaves this machine but
    the finished artifacts), pushes the current commit to origin, and cuts a GitHub release with three
    things attached -

      Klangbruecke-<version>.msix   the signed package
      Klangbruecke.cer              the PUBLIC half of the signing certificate
      (release notes)               how to trust the cert and install

    Why the .cer ships beside the package: the MSIX is signed with a self-signed certificate
    (packaging/New-DevCert.ps1), which nothing trusts by default. Unlike a downloaded .exe, a
    self-signed MSIX has no "install anyway" prompt - Windows simply refuses until the certificate is
    in Trusted People. So every machine installs the .cer once, and the release notes below say how.
    If you ever move to a CA / Azure Trusted Signing certificate that chains to a trusted root, the
    .cer step goes away and this script's notes should change with it.

    Requires: gh (authenticated), git, and a signing certificate at packaging/KlangbrueckeDev.pfx.
    Run from anywhere; it operates on its own repo.

.PARAMETER Version
    Override the version. Default: the <Version> in Klangbruecke.csproj, which must match the Identity
    Version in AppxManifest.xml (the script refuses if they disagree - the project bumps both).

.PARAMETER Notes
    Changelog text to place above the install instructions in the release body. Optional.

.PARAMETER Draft
    Create the release as a draft, to review before it goes public.

.PARAMETER Prerelease
    Force the release to be marked prerelease. Default: on for 0.x versions, off from 1.0.

.PARAMETER Stable
    Force a full (non-prerelease) release, overriding the 0.x default.

.PARAMETER SkipBuild
    Reuse the existing artifacts\Klangbruecke.msix instead of rebuilding.

.PARAMETER Force
    Allow publishing with uncommitted changes in the working tree.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Notes = '',
    [switch] $Draft,
    [switch] $Prerelease,
    [switch] $Stable,
    [switch] $SkipBuild,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

if ($Prerelease -and $Stable) { throw 'Pass -Prerelease or -Stable, not both.' }

$repoRoot     = Split-Path $PSScriptRoot -Parent
$csprojPath   = Join-Path $repoRoot 'src\Klangbruecke\Klangbruecke.csproj'
$manifestPath = Join-Path $PSScriptRoot 'AppxManifest.xml'
$artifacts    = Join-Path $repoRoot 'artifacts'
$msixPath     = Join-Path $artifacts 'Klangbruecke.msix'

function Get-CsprojVersion {
    $m = [regex]::Match((Get-Content $csprojPath -Raw), '<Version>\s*([\d.]+)\s*</Version>')
    if (-not $m.Success) { throw "No <Version> found in $csprojPath" }
    return $m.Groups[1].Value
}

function Get-ManifestVersion {
    $m = [regex]::Match((Get-Content $manifestPath -Raw), '(?s)<Identity\b.*?Version="([\d.]+)"')
    if (-not $m.Success) { throw "No Identity Version found in $manifestPath" }
    return $m.Groups[1].Value
}

# --- resolve and cross-check the version ---
$csprojVersion   = Get-CsprojVersion
$manifestVersion = Get-ManifestVersion
if ($csprojVersion -ne $manifestVersion) {
    throw "Version mismatch: csproj is $csprojVersion, AppxManifest is $manifestVersion. " +
          'Bump both (docs/HANDOFF.md) before releasing.'
}

if (-not $Version) { $Version = $csprojVersion }
# Tag is semver-style: first three components, v-prefixed. 0.2.0.0 -> v0.2.0
$semver       = ($Version.Split('.')[0..2]) -join '.'
$tag          = "v$semver"
$isPrerelease = if ($Stable) { $false } elseif ($Prerelease) { $true } else { $Version.Split('.')[0] -eq '0' }

Write-Host "Version : $Version"
Write-Host "Tag     : $tag$(if ($isPrerelease) { '  (prerelease)' })"
Write-Host ''

Push-Location $repoRoot
try {
    # --- preflight ---
    & gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'gh is not authenticated. Run: gh auth login' }

    if (& git tag --list $tag) { throw "Tag $tag already exists. Bump the version, or delete the tag to re-cut." }

    if (-not $Force -and (& git status --porcelain)) {
        throw 'Working tree has uncommitted changes. Commit them, or pass -Force to release anyway.'
    }

    $headSha = (& git rev-parse --short HEAD).Trim()
    $branch  = (& git rev-parse --abbrev-ref HEAD).Trim()
    Write-Host "Releasing commit $headSha on $branch"
    Write-Host ''

    # --- build + sign ---
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot 'Build-Msix.ps1') -Configuration Release
        if ($LASTEXITCODE -ne 0) { throw "Build-Msix.ps1 failed with exit code $LASTEXITCODE" }
    }
    if (-not (Test-Path $msixPath)) { throw "No package at $msixPath. Run without -SkipBuild." }

    # A release of an unsigned package is dead on arrival - it cannot install. Build-Msix only warns
    # and returns when the cert is missing, so catch it here rather than shipping the warning.
    $sig = Get-AuthenticodeSignature $msixPath
    if (-not $sig.SignerCertificate) {
        throw "Package at $msixPath is unsigned. Run packaging/New-DevCert.ps1 (elevated), then rebuild."
    }

    # --- assemble the release assets ---
    $msixAsset = Join-Path $artifacts "Klangbruecke-$semver.msix"
    Copy-Item $msixPath $msixAsset -Force

    # The public certificate, taken from the signature itself so it is exactly what signed the package
    # (not merely what the pfx happens to hold). Trusting this .cer is what lets the .msix install.
    $cerAsset = Join-Path $artifacts 'Klangbruecke.cer'
    Export-Certificate -Cert $sig.SignerCertificate -FilePath $cerAsset -Type CERT | Out-Null
    Write-Host "Public certificate exported: $cerAsset"

    # --- release notes ---
    # Six backticks -> three literal backticks: in a double-quoted here-string a backtick is the escape
    # character, so a fenced code block needs them doubled.
    $installNotes = @"
## Install

Klangbruecke is a sideloaded MSIX signed with a self-signed certificate, so Windows will not install
it until that certificate is trusted. There is no "install anyway" prompt for MSIX - this step is
required, and it is a one-time thing per machine.

**1. Trust the certificate** (one time, from an **elevated** PowerShell):

``````powershell
Import-Certificate -FilePath .\Klangbruecke.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
``````

**2. Install (or upgrade) the app** - from **Windows PowerShell**, not PowerShell 7 (the Appx module
does not load in pwsh):

``````powershell
Add-AppxPackage -Path .\Klangbruecke-$semver.msix
``````

Klangbruecke then starts automatically and lives in the system tray. Windows 10 build 19041 or later
is required. See the [README](https://github.com/MYSTRAVIL/klangbruecke#readme) for what it does and
how to test music and calls.
"@

    $body = if ($Notes) { "$Notes`n`n$installNotes" } else { $installNotes }
    $notesFile = Join-Path $artifacts 'release-notes.md'
    Set-Content -Path $notesFile -Value $body -Encoding utf8

    # --- publish ---
    # gh tags the release at $headSha, which must exist on the remote, so push the branch first. Both
    # this and the tag are release actions the operator is triggering on purpose.
    Write-Host "Pushing $branch to origin..."
    & git push origin HEAD
    if ($LASTEXITCODE -ne 0) { throw "git push failed with exit code $LASTEXITCODE" }

    $ghArgs = @(
        'release', 'create', $tag,
        $msixAsset, $cerAsset,
        '--title', "Klangbruecke $tag",
        '--notes-file', $notesFile,
        '--target', $headSha
    )
    if ($Draft)        { $ghArgs += '--draft' }
    if ($isPrerelease) { $ghArgs += '--prerelease' }

    Write-Host ''
    Write-Host "Creating release $tag ..."
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE" }

    Write-Host ''
    Write-Host "Released $tag. If it looks wrong: gh release delete $tag --cleanup-tag"
}
finally {
    Pop-Location
}
