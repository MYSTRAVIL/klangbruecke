<#
.SYNOPSIS
  Generates the three event chimes (connect/disconnect/degraded) as 16-bit mono PCM WAVs into
  src/Klangbruecke/Assets. Re-run to change the tones; the .wav files are committed and embedded.
#>
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$rate = 44100
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Klangbruecke\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function Write-Wav([string]$path, [double[]]$freqs, [double]$segMs) {
    $amp = 0.22; $fade = [int]($rate * 0.015)
    $samples = New-Object System.Collections.Generic.List[int16]
    foreach ($f in $freqs) {
        $n = [int]($rate * $segMs / 1000.0)
        for ($i = 0; $i -lt $n; $i++) {
            $env = 1.0
            if ($i -lt $fade) { $env = $i / $fade }
            elseif ($i -gt ($n - $fade)) { $env = ($n - $i) / $fade }
            $v = [math]::Sin(2 * [math]::PI * $f * $i / $rate) * $amp * $env
            $samples.Add([int16]([math]::Round($v * 32767)))
        }
    }
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $dataLen = $samples.Count * 2
    $bw.Write([char[]]'RIFF'); $bw.Write([int](36 + $dataLen)); $bw.Write([char[]]'WAVE')
    $bw.Write([char[]]'fmt '); $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
    $bw.Write([int]$rate); $bw.Write([int]($rate * 2)); $bw.Write([int16]2); $bw.Write([int16]16)
    $bw.Write([char[]]'data'); $bw.Write([int]$dataLen)
    foreach ($s in $samples) { $bw.Write([int16]$s) }
    $bw.Flush(); [System.IO.File]::WriteAllBytes($path, $ms.ToArray()); $bw.Dispose(); $ms.Dispose()
    Write-Host "wrote $path ($($samples.Count) samples)"
}

# Warm mid-range tones with a gentle 15 ms attack/release - lower and smoother than the first cut,
# which read as piercing (660-880 Hz). G4->C5 rising for connect, C5->G4 falling for disconnect, a low
# E4 for degraded.
Write-Wav (Join-Path $assets 'connect.wav')    @(392, 523) 140
Write-Wav (Join-Path $assets 'disconnect.wav') @(523, 392) 140
Write-Wav (Join-Path $assets 'degraded.wav')   @(330)      220
