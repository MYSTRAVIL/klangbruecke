using NAudio.Dmo;
using NAudio.Wave;

namespace Klangbruecke.Audio;

/// <summary>
/// Renders endpoint formats for the log. Purely diagnostic: nothing here gates the audio path.
///
/// Shared-mode WASAPI converts between the capture and render formats itself - NAudio initializes
/// the client with <c>SrcDefaultQuality | AutoConvertPcm</c> - so a difference is worth recording,
/// not correcting.
/// </summary>
public static class AudioFormatBridge
{
    /// <summary>True when the two formats are not the same stream shape.</summary>
    /// <remarks>
    /// Both sides are normalized first. <c>WasapiCapture.WaveFormat</c> already returns a standard
    /// format while <c>AudioClient.MixFormat</c> returns the raw <c>WaveFormatExtensible</c>, so
    /// comparing <c>Encoding</c> as given reports IeeeFloat against Extensible for every pair on
    /// the machine - including a device against itself.
    /// </remarks>
    public static bool Differ(WaveFormat capture, WaveFormat render)
    {
        WaveFormat a = capture.AsStandardWaveFormat();
        WaveFormat b = render.AsStandardWaveFormat();

        return a.SampleRate != b.SampleRate
            || a.Channels != b.Channels
            || a.BitsPerSample != b.BitsPerSample
            || a.Encoding != b.Encoding;
    }

    /// <summary>
    /// Names the subformat for extensible formats. Every real endpoint on this machine is
    /// extensible, so printing the tag alone renders both halves of a pair "Extensible" and a
    /// reader comparing them concludes the formats match.
    /// </summary>
    public static string Describe(WaveFormat format)
    {
        string encoding = format is WaveFormatExtensible extensible
            ? $"{format.Encoding}/{AudioMediaSubtypes.GetAudioSubtypeName(extensible.SubFormat)}"
            : format.Encoding.ToString();

        return $"{format.SampleRate}Hz {format.BitsPerSample}bit {format.Channels}ch {encoding}";
    }
}
