<#
.SYNOPSIS
    Builds Klangbruecke, lays out an MSIX payload, packages and signs it.

.DESCRIPTION
    Uses the Windows SDK tools directly (makeappx / signtool) so no Visual Studio is required.

    MSIX packaging is load-bearing, not cosmetic: the phoneLineTransportManagement restricted
    capability only works with package identity. See docs/FINDINGS.md section 2.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path $PSScriptRoot -Parent
$project   = Join-Path $repoRoot 'src\Klangbruecke\Klangbruecke.csproj'
$layout    = Join-Path $repoRoot 'artifacts\layout'
$outputDir = Join-Path $repoRoot 'artifacts'
$msixPath  = Join-Path $outputDir 'Klangbruecke.msix'
$pfxPath   = Join-Path $PSScriptRoot 'KlangbrueckeDev.pfx'

function Find-SdkTool([string] $Name) {
    $tool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter $Name -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
    if (-not $tool) { throw "$Name not found. Install the Windows 10 SDK." }
    return $tool.FullName
}

$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = Find-SdkTool 'signtool.exe'

Write-Host "makeappx: $makeappx"
Write-Host "signtool: $signtool"
Write-Host ''

# --- build ---
Write-Host "Building ($Configuration)..."
dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $layout
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

# --- lay out package payload ---
Copy-Item (Join-Path $PSScriptRoot 'AppxManifest.xml') (Join-Path $layout 'AppxManifest.xml') -Force
$layoutImages = Join-Path $layout 'Images'
New-Item -ItemType Directory -Force -Path $layoutImages | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Images\*.png') $layoutImages -Force

# .pdb and .xml doc files have no business in a shipped package.
Get-ChildItem $layout -Include '*.pdb', '*.xml' -Recurse -File |
    Where-Object { $_.Name -ne 'AppxManifest.xml' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

# --- package ---
if (Test-Path $msixPath) { Remove-Item $msixPath -Force }
Write-Host ''
Write-Host 'Packaging...'
& $makeappx pack /d $layout /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }

# --- sign ---
if (-not (Test-Path $pfxPath)) {
    Write-Warning "No signing certificate at $pfxPath - package is UNSIGNED and will not install."
    Write-Warning 'Run packaging/New-DevCert.ps1 (elevated) first.'
    return
}

$passwordFile = "$pfxPath.password"
if (-not (Test-Path $passwordFile)) { throw "Password file missing: $passwordFile" }
$password = Get-Content $passwordFile -Raw

Write-Host ''
Write-Host 'Signing...'
& $signtool sign /fd SHA256 /a /f $pfxPath /p $password $msixPath
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }

Write-Host ''
Write-Host "Built: $msixPath"
Write-Host 'Install with:  Add-AppxPackage -Path .\artifacts\Klangbruecke.msix'
