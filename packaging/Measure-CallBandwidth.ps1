<#
.SYNOPSIS
    Measures the audio bandwidth of incoming call audio to tell CVSD from mSBC.

.DESCRIPTION
    docs/FINDINGS.md section 11: outgoing voice through the bridge is muffled with harsh
    transient artifacts, and the microphone has been ruled out. The leading suspect is the SCO
    link negotiating CVSD (narrowband) instead of mSBC (wideband).

    SCO carries ONE codec in BOTH directions, so the incoming half answers the question about
    the outgoing half - and unlike the outgoing half, the incoming half is capturable here.
    This script WASAPI loopback-captures the default communications render endpoint (what you
    hear during a call), records for -Seconds, and reports where the spectrum stops:

      energy stopping around 3.4-4 kHz   -> CVSD narrowband
      meaningful content out to 7-8 kHz  -> mSBC wideband

    Two rolloff points get reported and only one decides. The "shoulder" (-20 dB below the
    passband) describes the timbre but is useless as a codec test, because speech tilts down on
    its own and crosses -20 dB around 5 kHz with no codec involved - judging on it calls mSBC
    narrowband. The "band edge" (-40 dB) is the discriminator: a codec cutoff is a cliff, and
    above 4 kHz a CVSD stream holds nothing but the upsampler's stopband.

    ETW would be the obvious tool and is a dead end: BTHPORT/BTHUSB/BthHFEnum use WPP tracing
    and Microsoft does not publish the TMF files to decode it. Hence measuring the audio.

    ############################################################################################
    #  THE RESULT IS ONLY MEANINGFUL OVER A *WIDEBAND* CALL.                                   #
    #                                                                                          #
    #  A normal cellular call is narrowband AT THE NETWORK LEVEL. It reads as ~4 kHz no        #
    #  matter what Bluetooth negotiated, so running this on a cellular call "confirms" CVSD    #
    #  whether or not CVSD is what happened. That is a false positive, not a measurement.      #
    #                                                                                          #
    #  Place the call over WhatsApp / Signal / Telegram / FaceTime Audio / Discord, from a     #
    #  SECOND phone to the bridged phone. Have the far end talk continuously - the script      #
    #  measures speech, and silence measures nothing.                                          #
    ############################################################################################

    Loopback hears everything rendered to that endpoint, so mute music, notifications and any
    other app first - they would add their own wideband energy and read as mSBC.

    Note the topology (FINDINGS section 11): the PC is the Hands-Free device, so no
    "Hands-Free" endpoint appears during a call. Incoming voice lands on the PC's ordinary
    default communications render endpoint. That is the correct thing to capture.

.PARAMETER Seconds
    Capture length. 10-15 s of continuous far-end speech is plenty.

.PARAMETER FftSize
    FFT length in samples, a power of two. 4096 at 48 kHz gives ~12 Hz bins over ~85 ms frames,
    which resolves the rolloff without smearing speech transients.

.PARAMETER Role
    Which default render endpoint to capture. Communications is the call path and the default.
    Use Multimedia to validate the script itself against known-wideband audio when the two
    defaults differ.

.PARAMETER DeviceName
    Capture a named render endpoint instead of the role default (substring, case-insensitive).
    Needed when the role default cannot be loopback-captured because another app holds it in
    exclusive mode - on this machine VoiceMeeter holds the beyerdynamic output that way, and
    WASAPI answers 0x8889000A. Point this at whatever endpoint you actually hear the call on.

.PARAMETER ListDevices
    Print the active render endpoints and exit, so -DeviceName has something to aim at.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File packaging\Measure-CallBandwidth.ps1 -Seconds 12

    The real measurement. Start it once the wideband call is connected and the far end is talking.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File packaging\Measure-CallBandwidth.ps1 -Role Multimedia

    Self-test with no phone involved: play music or a video and confirm it reports energy well
    above 4 kHz. If this reads narrowband, the script is broken, not the Bluetooth link.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File packaging\Measure-CallBandwidth.ps1 -ListDevices
    powershell.exe -ExecutionPolicy Bypass -File packaging\Measure-CallBandwidth.ps1 -DeviceName VoiceMeeter

    When the role default is locked in exclusive mode: list the endpoints, then aim at the one
    carrying the call.
#>
[CmdletBinding()]
param(
    [ValidateRange(2, 120)]
    [int] $Seconds = 12,

    [ValidateSet(1024, 2048, 4096, 8192, 16384)]
    [int] $FftSize = 4096,

    [ValidateSet('Communications', 'Multimedia', 'Console')]
    [string] $Role = 'Communications',

    [string] $DeviceName,

    [switch] $ListDevices
)

$ErrorActionPreference = 'Stop'

# This machine is German-locale, where the default formatting renders 3500 Hz as "3.500 Hz" - which
# an English reader parses as 3.5 Hz, i.e. the one number the whole script exists to report becomes
# ambiguous. Pin the culture rather than hand-formatting every value.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

# --- thresholds, all in one place ----------------------------------------------------------------
# Peak below this is digital silence or dither: loopback returns zero-filled buffers when nothing
# is rendering, and zeros would otherwise analyse as a perfectly flat "narrowband" spectrum.
$SilencePeakDb   = -66.0
# Quiet enough that the noise floor competes with the signal; results get reported but flagged.
$QuietRmsDb      = -55.0
# Two rolloff points, because one number cannot do both jobs.
#   -20 dB is the spectral shoulder. Reported because it describes the timbre, but it is NOT a
#   codec test: speech tilts down ~6-12 dB per octave on its own, so wideband speech crosses -20 dB
#   somewhere around 5 kHz with no codec involved. Judging on it would call mSBC narrowband.
#   -40 dB is the band edge. A codec cutoff is a cliff, not a slope - above 4 kHz a CVSD stream
#   contains nothing but the upsampler's stopband, typically 60 dB down or more. Nothing a talker
#   does produces a 40 dB cliff, so this is the discriminator.
$ShoulderDropDb  = 20.0
$CliffDropDb     = 40.0
# Passband reference window - speech energy both codecs carry, so it anchors the comparison.
$PassbandLoHz    = 300.0
$PassbandHiHz    = 3000.0
# Verdict boundaries. CVSD dies just under 4 kHz; mSBC runs to 7-8 kHz. Between the two is a
# result that deserves to be called inconclusive rather than rounded to the nearest hypothesis.
$NarrowbandMaxHz = 4500.0
$WidebandMinHz   = 6000.0

$repoRoot = Split-Path $PSScriptRoot -Parent
$naudio   = Join-Path $repoRoot 'artifacts\layout'

$dllPaths = foreach ($dll in 'NAudio.Core.dll', 'NAudio.Wasapi.dll') {
    $path = Join-Path $naudio $dll
    if (-not (Test-Path $path)) {
        throw "$dll not found at $path. Run packaging/Build-Msix.ps1 first - it produces artifacts\layout."
    }
    Add-Type -Path $path
    $path
}

# NAudio.Core is a netstandard2.0 assembly. Referencing it from a .NET 8/9 compilation drags in
# the netstandard facade (WaveFormatEncoding is an enum, and Enum itself fails to bind without it),
# and naming any reference explicitly turns off Add-Type's implicit set - so the framework
# assemblies the C# below uses have to be listed too. Simple names; Add-Type resolves them.
$references = @($dllPaths) +
              @('netstandard', 'System.Runtime', 'System.Collections', 'System.Threading', 'System.Threading.Thread')

# Capture and FFT run in C# rather than PowerShell for two reasons: DataAvailable fires on the
# WASAPI capture thread, which Register-ObjectEvent will not pump reliably while the main
# runspace is asleep; and a 12 s capture is half a million samples, which a PowerShell loop
# would still be chewing on minutes later.
if (-not ('Klangbruecke.LoopbackSpectrum' -as [type])) {
    Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Klangbruecke
{
    public sealed class SpectrumResult
    {
        public int SampleRate;
        public int Channels;
        public int BitsPerSample;
        public string Encoding;
        public long SampleCount;
        public double PeakAbs;
        public double RmsDb;
        public int FramesTotal;
        public int FramesUsed;
        public double BinHz;
        public double[] Power;      // averaged linear power per bin, index 0 = DC
    }

    public static class LoopbackSpectrum
    {
        // Speech has gaps. A frame more than this far below the loudest frame is a gap, and
        // averaging gaps in would mix the endpoint noise floor into the high bands and blur
        // exactly the rolloff we are trying to find.
        private const double ActiveFrameFloorDb = -25.0;

        public static SpectrumResult Measure(MMDevice device, int seconds, int fftSize)
        {
            var capture = new WasapiLoopbackCapture(device);
            var format = capture.WaveFormat;
            int channels = format.Channels;
            int sampleRate = format.SampleRate;

            // Loopback yields the device mix format, normally 32-bit float but reported as
            // WAVE_FORMAT_EXTENSIBLE. Resolve it to a plain encoding rather than assuming.
            var extensible = format as WaveFormatExtensible;
            if (extensible != null)
            {
                try { format = extensible.ToStandardWaveFormat(); }
                catch { /* leave it Extensible; the check below then rejects it by name */ }
            }
            var encoding = format.Encoding;
            int bits = format.BitsPerSample;

            bool supported =
                (encoding == WaveFormatEncoding.IeeeFloat && bits == 32) ||
                (encoding == WaveFormatEncoding.Pcm && (bits == 16 || bits == 24 || bits == 32));
            if (!supported)
            {
                capture.Dispose();
                throw new NotSupportedException(
                    "Unhandled capture format: " + encoding + " " + bits + "-bit. Expected IEEE float 32 or PCM 16/24/32.");
            }

            // Pre-size for the whole capture so the callback thread never reallocates mid-record.
            var samples = new List<float>(sampleRate * (seconds + 1));
            int bytesPerSample = bits / 8;

            capture.DataAvailable += delegate(object sender, WaveInEventArgs e)
            {
                int frames = e.BytesRecorded / (bytesPerSample * channels);
                for (int f = 0; f < frames; f++)
                {
                    double sum = 0.0;
                    for (int c = 0; c < channels; c++)
                    {
                        sum += ReadSample(e.Buffer, (f * channels + c) * bytesPerSample, encoding, bits);
                    }
                    samples.Add((float)(sum / channels));   // downmix: one voice, not a stereo image
                }
            };

            Exception failure = null;
            var stopped = new ManualResetEventSlim(false);
            capture.RecordingStopped += delegate(object sender, StoppedEventArgs e)
            {
                failure = e.Exception;
                stopped.Set();
            };

            capture.StartRecording();
            Thread.Sleep(seconds * 1000);
            capture.StopRecording();
            stopped.Wait(5000);
            capture.Dispose();
            if (failure != null) { throw failure; }

            var result = new SpectrumResult();
            result.SampleRate = sampleRate;
            result.Channels = channels;
            result.BitsPerSample = bits;
            result.Encoding = encoding.ToString();
            result.SampleCount = samples.Count;
            result.BinHz = (double)sampleRate / fftSize;
            result.Power = new double[fftSize / 2];

            int n = samples.Count;
            double peak = 0.0, sumSq = 0.0;
            for (int i = 0; i < n; i++)
            {
                float v = samples[i];
                double a = Math.Abs(v);
                if (a > peak) { peak = a; }
                sumSq += (double)v * v;
            }
            result.PeakAbs = peak;
            double rms = n > 0 ? Math.Sqrt(sumSq / n) : 0.0;
            result.RmsDb = rms > 0 ? 20.0 * Math.Log10(rms) : double.NegativeInfinity;

            // Pass 1: level of every candidate frame, so pass 2 can keep only the loud ones.
            int hop = fftSize / 2;               // 50% overlap - Hann sums to unity, nothing is missed
            var starts = new List<int>();
            var levels = new List<double>();
            double loudest = 0.0;
            for (int start = 0; start + fftSize <= n; start += hop)
            {
                double s2 = 0.0;
                for (int i = 0; i < fftSize; i++) { double v = samples[start + i]; s2 += v * v; }
                double frameRms = Math.Sqrt(s2 / fftSize);
                starts.Add(start);
                levels.Add(frameRms);
                if (frameRms > loudest) { loudest = frameRms; }
            }
            result.FramesTotal = starts.Count;

            // Pass 2: Hann-windowed FFT of each kept frame, power averaged bin by bin. Averaging
            // many frames is what turns a noisy speech spectrum into a readable rolloff.
            var window = new float[fftSize];
            for (int i = 0; i < fftSize; i++) { window[i] = (float)FastFourierTransform.HannWindow(i, fftSize); }

            int m = (int)Math.Round(Math.Log(fftSize, 2));
            double gate = loudest * Math.Pow(10.0, ActiveFrameFloorDb / 20.0);
            var buffer = new Complex[fftSize];
            int used = 0;

            for (int f = 0; f < starts.Count; f++)
            {
                if (levels[f] < gate) { continue; }
                int start = starts[f];
                for (int i = 0; i < fftSize; i++)
                {
                    buffer[i].X = samples[start + i] * window[i];
                    buffer[i].Y = 0.0f;
                }
                FastFourierTransform.FFT(true, m, buffer);
                for (int bin = 0; bin < result.Power.Length; bin++)
                {
                    double re = buffer[bin].X, im = buffer[bin].Y;
                    result.Power[bin] += re * re + im * im;
                }
                used++;
            }

            if (used > 0)
            {
                for (int bin = 0; bin < result.Power.Length; bin++) { result.Power[bin] /= used; }
            }
            result.FramesUsed = used;
            return result;
        }

        private static double ReadSample(byte[] buffer, int offset, WaveFormatEncoding encoding, int bits)
        {
            if (encoding == WaveFormatEncoding.IeeeFloat) { return BitConverter.ToSingle(buffer, offset); }
            if (bits == 16) { return BitConverter.ToInt16(buffer, offset) / 32768.0; }
            if (bits == 24)
            {
                int v = (buffer[offset + 2] << 16) | (buffer[offset + 1] << 8) | buffer[offset];
                if ((v & 0x800000) != 0) { v |= unchecked((int)0xFF000000); }
                return v / 8388608.0;
            }
            return BitConverter.ToInt32(buffer, offset) / 2147483648.0;
        }
    }
}
'@
}

# --- helpers -------------------------------------------------------------------------------------

# Mean power per bin, not the sum: the bands below 4 kHz are 1 kHz wide and the ones above are
# 2 kHz, so summing would hand the wide bands a free 3 dB and flatter the high end.
function Get-BandPower {
    param([double[]] $Power, [double] $BinHz, [double] $LoHz, [double] $HiHz)

    $first = [int][math]::Ceiling($LoHz / $BinHz)
    $last  = [int][math]::Floor($HiHz / $BinHz)
    if ($first -lt 1) { $first = 1 }                                  # skip DC
    if ($last -gt $Power.Length - 1) { $last = $Power.Length - 1 }
    if ($last -lt $first) { return $null }                            # band sits above Nyquist

    $sum = 0.0
    for ($i = $first; $i -le $last; $i++) { $sum += $Power[$i] }
    return $sum / ($last - $first + 1)
}

function Format-Bar {
    param([double] $Db, [double] $FloorDb = -60.0, [int] $Width = 26)

    $fraction = ($Db - $FloorDb) / (0.0 - $FloorDb)
    if ($fraction -lt 0) { $fraction = 0 }
    if ($fraction -gt 1) { $fraction = 1 }
    $filled = [int][math]::Round($Width * $fraction)
    ('#' * $filled).PadRight($Width, '.')
}

$enumerator = New-Object NAudio.CoreAudioApi.MMDeviceEnumerator

function Get-ActiveRenderEndpoint {
    $enumerator.EnumerateAudioEndPoints(
        [NAudio.CoreAudioApi.DataFlow]::Render,
        [NAudio.CoreAudioApi.DeviceState]::Active)
}

if ($ListDevices) {
    Write-Host 'Active render endpoints:'
    foreach ($d in Get-ActiveRenderEndpoint) {
        Write-Host "  $($d.FriendlyName)"
    }
    Write-Host ''
    Write-Host 'Pass any distinctive part of a name to -DeviceName.'
    exit 0
}

# --- the caveat, before anything else ------------------------------------------------------------
Write-Host ''
Write-Host '  READ THIS FIRST - it decides whether the number below means anything.' -ForegroundColor Yellow
Write-Host ''
Write-Host '  A normal CELLULAR call is narrowband at the network level. It reads as ~4 kHz'    -ForegroundColor Yellow
Write-Host '  no matter what the Bluetooth link negotiated, so measuring one "confirms" CVSD'  -ForegroundColor Yellow
Write-Host '  whether or not CVSD is what happened.'                                           -ForegroundColor Yellow
Write-Host ''
Write-Host '  Only a WIDEBAND call is conclusive: WhatsApp, Signal, Telegram, FaceTime Audio'  -ForegroundColor Yellow
Write-Host '  or Discord, placed from your second phone to the bridged phone.'                 -ForegroundColor Yellow
Write-Host ''
Write-Host '  Also: the far end must be talking continuously, and nothing else may be playing' -ForegroundColor Yellow
Write-Host '  to this endpoint - loopback hears music and notifications as wideband too.'      -ForegroundColor Yellow
Write-Host ''

# --- endpoint ------------------------------------------------------------------------------------
if ($DeviceName) {
    $device = Get-ActiveRenderEndpoint | Where-Object { $_.FriendlyName -like "*$DeviceName*" } | Select-Object -First 1
    if (-not $device) {
        throw "No active render endpoint matches '$DeviceName'. Run with -ListDevices to see the names."
    }
    $source = "matched -DeviceName '$DeviceName'"
} else {
    $device = $enumerator.GetDefaultAudioEndpoint(
        [NAudio.CoreAudioApi.DataFlow]::Render,
        [NAudio.CoreAudioApi.Role]::$Role)
    $source = "default $($Role.ToLower()) render"
}

Write-Host "Endpoint  : $($device.FriendlyName)" -ForegroundColor Cyan
Write-Host "Source    : $source, state $($device.State)"
$mix = $device.AudioClient.MixFormat
Write-Host "Mix format: $($mix.SampleRate) Hz, $($mix.Channels) ch"
Write-Host ''
Write-Host "Capturing $Seconds seconds..." -ForegroundColor Cyan

try {
    $result = [Klangbruecke.LoopbackSpectrum]::Measure($device, $Seconds, $FftSize)
} catch {
    # AUDCLNT_E_DEVICE_IN_USE. Loopback is a shared-mode client, so it loses to any app holding the
    # endpoint exclusively - VoiceMeeter does exactly that to its hardware outputs. Nothing about
    # the endpoint's own properties reveals this, so the failure has to be explained here.
    if ("$($_.Exception.Message)" -match '0x8889000A') {
        Write-Host ''
        Write-Host "CANNOT CAPTURE '$($device.FriendlyName)' - another app owns it in exclusive mode." -ForegroundColor Red
        Write-Host ''
        Write-Host '  WASAPI returned AUDCLNT_E_DEVICE_IN_USE (0x8889000A). Loopback runs in shared mode'
        Write-Host '  and cannot attach to an endpoint held exclusively. VoiceMeeter does this to the'
        Write-Host '  hardware outputs it drives, which on this machine includes the beyerdynamic adapter.'
        Write-Host ''
        Write-Host '  Options:'
        Write-Host '    - capture the endpoint the call audio is mixed into instead:'
        Write-Host '        -ListDevices, then e.g. -DeviceName VoiceMeeter'
        Write-Host '    - or close the owning app and re-run.'
        Write-Host ''
        Write-Host '  Either endpoint answers the codec question as long as the call audio passes'
        Write-Host '  through it unresampled - a virtual cable at 48 kHz does not add bandwidth that'
        Write-Host '  the SCO link did not deliver.'
        exit 4
    }
    throw
}

$nyquist = $result.SampleRate / 2.0
Write-Host ("Captured  : {0:n0} samples at {1} Hz ({2}, {3}-bit, {4} ch), {5} of {6} FFT frames above the speech gate" -f `
    $result.SampleCount, $result.SampleRate, $result.Encoding, $result.BitsPerSample, $result.Channels, `
    $result.FramesUsed, $result.FramesTotal)
Write-Host ''

# --- did we actually hear anything? --------------------------------------------------------------
# Silence must never produce a codec verdict. Zero-filled buffers have a flat, empty spectrum that
# an unguarded rolloff search would happily report as narrowband.
$peakDb = if ($result.PeakAbs -gt 0) { 20 * [math]::Log10($result.PeakAbs) } else { [double]::NegativeInfinity }

if ($result.FramesUsed -eq 0 -or $peakDb -lt $SilencePeakDb) {
    Write-Host 'NO AUDIO CAPTURED - no codec verdict.' -ForegroundColor Red
    Write-Host ''
    Write-Host "  Peak level was $(if ([double]::IsNegativeInfinity($peakDb)) { 'digital silence' } else { '{0:n1} dBFS' -f $peakDb }), which is nothing."
    Write-Host '  Loopback only hears what is actually being rendered to this endpoint. It cannot'
    Write-Host '  hear a call that is routed elsewhere, and it hears nothing at all while nobody talks.'
    Write-Host ''
    Write-Host '  Check, in order:'
    Write-Host "    - is the call connected and is the far end speaking right now?"
    Write-Host "    - is '$($device.FriendlyName)' really where you hear the call? If not, change the"
    Write-Host '      default communications playback device in Sound settings, or pass -Role Multimedia.'
    Write-Host '    - self-test: play music and re-run. If that also reads silent, the capture path is broken.'
    exit 2
}

# --- band breakdown ------------------------------------------------------------------------------
$bands = @(
    @{ Name = '0-1 kHz';  Lo = 0;    Hi = 1000 }
    @{ Name = '1-2 kHz';  Lo = 1000; Hi = 2000 }
    @{ Name = '2-3 kHz';  Lo = 2000; Hi = 3000 }
    @{ Name = '3-4 kHz';  Lo = 3000; Hi = 4000 }
    @{ Name = '4-6 kHz';  Lo = 4000; Hi = 6000 }
    @{ Name = '6-8 kHz';  Lo = 6000; Hi = 8000 }
    @{ Name = '8 kHz+';   Lo = 8000; Hi = $nyquist }
)

$measured = foreach ($band in $bands) {
    $power = Get-BandPower -Power $result.Power -BinHz $result.BinHz -LoHz $band.Lo -HiHz $band.Hi
    [pscustomobject]@{ Name = $band.Name; Power = $power }
}

$peakPower = ($measured | Where-Object { $null -ne $_.Power } | Measure-Object -Property Power -Maximum).Maximum

Write-Host 'Band energy, dB relative to the loudest band:'
foreach ($band in $measured) {
    if ($null -eq $band.Power) {
        Write-Host ("  {0,-9}  {1,10}   (above this endpoint's {2:n0} Hz Nyquist limit)" -f $band.Name, 'n/a', $nyquist)
        continue
    }
    $db = if ($band.Power -gt 0) { 10 * [math]::Log10($band.Power / $peakPower) } else { -99.0 }
    $colour = if ($db -ge -25) { 'Green' } elseif ($db -ge -40) { 'Yellow' } else { 'DarkGray' }
    Write-Host ("  {0,-9}  {1,7:n1} dB  [{2}]" -f $band.Name, $db, (Format-Bar -Db $db)) -ForegroundColor $colour
}
Write-Host ''

# --- rolloff -------------------------------------------------------------------------------------
# Bucket the bins into 100 Hz steps first. A raw bin scan trips over single-bin nulls in speech;
# 100 Hz is coarse enough to be stable and fine enough to place a 4 kHz vs 7 kHz edge.
$bucketHz = 100.0
$buckets  = [int][math]::Floor($nyquist / $bucketHz)
$level    = New-Object 'double[]' ($buckets + 1)
for ($b = 0; $b -le $buckets; $b++) {
    $p = Get-BandPower -Power $result.Power -BinHz $result.BinHz -LoHz ($b * $bucketHz) -HiHz (($b + 1) * $bucketHz)
    $level[$b] = if ($null -ne $p -and $p -gt 0) { 10 * [math]::Log10($p) } else { -200.0 }
}

$passbandPower = Get-BandPower -Power $result.Power -BinHz $result.BinHz -LoHz $PassbandLoHz -HiHz $PassbandHiHz
$passbandDb    = 10 * [math]::Log10($passbandPower)

# Walk up from the top of the passband and take the first SUSTAINED drop - three consecutive
# buckets under the threshold - so one dip between formants does not read as the band edge.
function Find-Rolloff {
    param([double] $DropDb)

    $threshold = $passbandDb - $DropDb
    for ($b = [int]($PassbandHiHz / $bucketHz); $b -le $buckets - 2; $b++) {
        if ($level[$b] -lt $threshold -and $level[$b + 1] -lt $threshold -and $level[$b + 2] -lt $threshold) {
            return $b * $bucketHz
        }
    }
    return $null
}

$shoulderHz = Find-Rolloff -DropDb $ShoulderDropDb
$cliffHz    = Find-Rolloff -DropDb $CliffDropDb

function Format-Rolloff {
    param($Hz)
    if ($null -eq $Hz) { return ('none below {0:n0} Hz' -f $nyquist) }
    return ('~{0:n0} Hz' -f $Hz)
}

Write-Host ("Passband  : {0:n0}-{1:n0} Hz reference" -f $PassbandLoHz, $PassbandHiHz)
Write-Host ("Shoulder  : {0}  (-{1:n0} dB - descriptive; speech tilts down here on its own)" -f (Format-Rolloff $shoulderHz), $ShoulderDropDb)
Write-Host ("Band edge : {0}  (-{1:n0} dB - this is what the verdict uses)" -f (Format-Rolloff $cliffHz), $CliffDropDb) -ForegroundColor Cyan
Write-Host ("Level     : peak {0:n1} dBFS, RMS {1:n1} dBFS" -f $peakDb, $result.RmsDb)
Write-Host ''

# --- verdict -------------------------------------------------------------------------------------
if ($result.RmsDb -lt $QuietRmsDb) {
    Write-Host 'WARNING: the capture is very quiet. The endpoint noise floor competes with the signal,' -ForegroundColor Yellow
    Write-Host '         so the rolloff above may be measuring hiss. Raise the volume and re-run.'      -ForegroundColor Yellow
    Write-Host ''
}

# A 40 dB cliff can only be seen if there is 40 dB to see. The top octave of the capture carries
# nothing real on a voice call, so it doubles as a noise-floor probe: if the passband does not sit
# far enough above it, "no band edge found" would mean "could not have found one" - not "wideband".
$topOctavePower = Get-BandPower -Power $result.Power -BinHz $result.BinHz -LoHz ($nyquist * 0.85) -HiHz $nyquist
$dynamicRangeDb = if ($null -ne $topOctavePower -and $topOctavePower -gt 0) { $passbandDb - (10 * [math]::Log10($topOctavePower)) } else { [double]::PositiveInfinity }

if ($null -eq $cliffHz -and $dynamicRangeDb -lt ($CliffDropDb + 5)) {
    Write-Host ("VERDICT: INCONCLUSIVE - only {0:n0} dB of usable range above the noise floor." -f $dynamicRangeDb) -ForegroundColor Yellow
    Write-Host ("         A codec band edge is a {0:n0} dB drop and this capture cannot resolve one, so" -f $CliffDropDb)
    Write-Host '         "no band edge" here means "could not tell", not "wideband". Raise the level and re-run.'
    exit 3
}

if ($result.SampleRate -le 8000) {
    # An 8 kHz endpoint cannot represent anything above 4 kHz, so the spectrum above is moot -
    # the format alone is the answer, the same signal Watch-HfpAudio.ps1 reads off the endpoint.
    Write-Host 'VERDICT: CVSD narrowband - the endpoint itself runs at 8 kHz.' -ForegroundColor Yellow
    Write-Host '         Nothing above 4 kHz can exist here regardless of what the spectrum shows.'
    exit 1
}

$effective = if ($null -eq $cliffHz) { $nyquist } else { $cliffHz }

if ($effective -lt $NarrowbandMaxHz) {
    Write-Host ("VERDICT: NARROWBAND - content stops dead at ~{0:n0} Hz." -f $effective) -ForegroundColor Yellow
    Write-Host '         Consistent with CVSD on the SCO link (FINDINGS section 11).'
    Write-Host '         Conclusive ONLY if this was a wideband call. Over cellular it proves nothing,'
    Write-Host '         because the network was narrowband before Bluetooth ever saw the audio.'
    exit 1
}

if ($effective -ge $WidebandMinHz) {
    Write-Host ("VERDICT: WIDEBAND - content runs out to ~{0:n0} Hz." -f $effective) -ForegroundColor Green
    Write-Host '         The link is carrying mSBC, so the SCO codec is not the cause of the'
    Write-Host '         degradation and FINDINGS section 11 needs a different hypothesis.'
    Write-Host '         (If this was music or a video rather than a call, it only proves the tool works.)'
    exit 0
}

Write-Host ("VERDICT: INCONCLUSIVE - rolloff at ~{0:n0} Hz sits between the two codecs." -f $effective) -ForegroundColor Yellow
Write-Host ("         CVSD would stop below {0:n0} Hz, mSBC would run past {1:n0} Hz." -f $NarrowbandMaxHz, $WidebandMinHz)
Write-Host '         Usually means the far end was quiet, something else was playing to this endpoint,'
Write-Host '         or the source itself was band-limited. Re-run with continuous speech.'
exit 3
