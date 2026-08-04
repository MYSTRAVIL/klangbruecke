<#
.SYNOPSIS
    Watches for the HFP call-audio endpoints and reports the codec they negotiated.

.DESCRIPTION
    Start this BEFORE placing a call, then make the call. The Hands-Free endpoints only exist
    while the SCO link is up, so there is no way to inspect them after the fact.

    The sample rate is the codec:
      8000 Hz  = CVSD  (narrowband, ~4 kHz audio - muffled, with slope-overload distortion)
      16000 Hz = mSBC  (wideband)

    Windows advertises wideband support in
    HKLM:\SYSTEM\CurrentControlSet\Control\Bluetooth\Audio\Hfp\HandsFree, but advertising is
    not negotiating - the phone and the link both get a say.

    Ctrl+C to stop, or it exits on its own after -Seconds.
#>
[CmdletBinding()]
param(
    [int] $Seconds = 300,
    [int] $PollMs  = 400
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$naudio   = Join-Path $repoRoot 'artifacts\layout'

foreach ($dll in 'NAudio.Core.dll', 'NAudio.Wasapi.dll') {
    $path = Join-Path $naudio $dll
    if (-not (Test-Path $path)) {
        throw "$dll not found at $path. Run packaging/Build-Msix.ps1 first - it produces artifacts\layout."
    }
    Add-Type -Path $path
}

$enumerator = New-Object NAudio.CoreAudioApi.MMDeviceEnumerator

function Get-HfpEndpoints {
    # DeviceState.All: an endpoint that exists but is not Active is itself the useful answer -
    # it means the transport is registered but no SCO link is up.
    $enumerator.EnumerateAudioEndPoints(
        [NAudio.CoreAudioApi.DataFlow]::All,
        [NAudio.CoreAudioApi.DeviceState]::All) |
        Where-Object { $_.FriendlyName -match 'Hands-Free|Handsfree|HF Audio' }
}

Write-Host 'Watching for HFP call audio. Place the call now.' -ForegroundColor Cyan
Write-Host 'Ctrl+C to stop.'
Write-Host ''

$found = Get-HfpEndpoints
if (-not $found) {
    Write-Host 'No Hands-Free endpoints present yet (expected until the transport connects).'
} else {
    Write-Host "Present but idle: $($found.Count) endpoint(s)."
}
Write-Host ''

$seen     = @{}
$deadline = (Get-Date).AddSeconds($Seconds)
$reported = $false

while ((Get-Date) -lt $deadline) {
    foreach ($d in Get-HfpEndpoints) {
        $rate = $null
        if ($d.State -eq [NAudio.CoreAudioApi.DeviceState]::Active) {
            # MixFormat throws while the endpoint is transitioning; a miss here is not an error,
            # the next poll picks it up.
            try { $rate = $d.AudioClient.MixFormat.SampleRate } catch { }
        }

        $key = '{0}|{1}|{2}' -f $d.ID, $d.State, $rate
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true

        $stamp = (Get-Date).ToString('HH:mm:ss.fff')

        if ($null -eq $rate) {
            Write-Host "$stamp  $($d.State.ToString().PadRight(10)) $($d.FriendlyName)"
            continue
        }

        $codec = switch ($rate) {
            8000    { 'CVSD narrowband'; break }
            16000   { 'mSBC wideband';   break }
            default { "unexpected ($rate Hz)" }
        }
        $colour = if ($rate -eq 16000) { 'Green' } else { 'Yellow' }

        Write-Host "$stamp  ACTIVE     $($d.FriendlyName)" -ForegroundColor $colour
        Write-Host "           $rate Hz  ->  $codec" -ForegroundColor $colour
        $reported = $true
    }

    Start-Sleep -Milliseconds $PollMs
}

Write-Host ''
if ($reported) {
    Write-Host 'Done. 8000 Hz means the link fell back to CVSD despite Windows offering mSBC.'
} else {
    Write-Host 'No endpoint ever went Active - no SCO link was established while this ran.'
}
