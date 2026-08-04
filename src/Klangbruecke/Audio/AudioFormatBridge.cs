using NAudio.Wave;

namespace Klangbruecke.Audio;

public static class AudioFormatBridge
{
    /// <summary>
    /// True when the capture format cannot be handed to a shared-mode render client as-is.
    /// Shared mode requires an exact match with the endpoint's mix format; a mismatch throws
    /// at Init, which presents as silence rather than an obvious error.
    /// </summary>
    public static bool RequiresResampling(WaveFormat capture, WaveFormat output)
        => capture.SampleRate != output.SampleRate
        || capture.Channels != output.Channels
        || capture.BitsPerSample != output.BitsPerSample
        || capture.Encoding != output.Encoding;

    public static string Describe(WaveFormat format)
        => $"{format.SampleRate}Hz {format.BitsPerSample}bit {format.Channels}ch {format.Encoding}";
}
