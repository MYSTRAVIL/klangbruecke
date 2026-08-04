using Klangbruecke.Audio;
using NAudio.Wave;
using Xunit;

namespace Klangbruecke.Tests.Audio;

public sealed class AudioFormatBridgeTests
{
    [Fact]
    public void Differ_IsFalse_ForIdenticalFormats()
    {
        var a = new WaveFormat(48000, 16, 2);
        var b = new WaveFormat(48000, 16, 2);

        Assert.False(AudioFormatBridge.Differ(a, b));
    }

    [Fact]
    public void Differ_IsTrue_WhenSampleRateDiffers()
    {
        // The A2DP sink lands on 44.1 kHz while render endpoints sit at 48 kHz.
        var capture = new WaveFormat(44100, 16, 2);
        var render = new WaveFormat(48000, 16, 2);

        Assert.True(AudioFormatBridge.Differ(capture, render));
    }

    [Fact]
    public void Differ_IsTrue_WhenChannelCountDiffers()
    {
        Assert.True(AudioFormatBridge.Differ(new WaveFormat(48000, 16, 2), new WaveFormat(48000, 16, 1)));
    }

    [Fact]
    public void Differ_IsTrue_WhenBitDepthDiffers()
    {
        Assert.True(AudioFormatBridge.Differ(new WaveFormat(48000, 16, 2), new WaveFormat(48000, 24, 2)));
    }

    [Fact]
    public void Differ_IsTrue_WhenEncodingDiffers()
    {
        var pcm = new WaveFormat(48000, 32, 2);
        var ieeeFloat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        Assert.True(AudioFormatBridge.Differ(pcm, ieeeFloat));
    }

    [Fact]
    public void Differ_IsFalse_WhenOnlyTheExtensibleWrapperDiffers()
    {
        // The live shape: WasapiCapture.WaveFormat normalizes to IeeeFloat, AudioClient.MixFormat
        // stays WaveFormatExtensible. Comparing Encoding as given makes every pair "differ".
        var capture = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var render = new WaveFormatExtensible(48000, 32, 2);

        Assert.False(AudioFormatBridge.Differ(capture, render));
    }

    [Fact]
    public void Differ_IsFalse_ForTwoIdenticalExtensibleFormats()
    {
        Assert.False(AudioFormatBridge.Differ(new WaveFormatExtensible(48000, 32, 2), new WaveFormatExtensible(48000, 32, 2)));
    }

    [Fact]
    public void Differ_IsTrue_ForTheLiveEndpointPair()
    {
        // Extensible on both sides, as every real endpoint is: 44.1 kHz sink, 48 kHz render.
        var capture = new WaveFormatExtensible(44100, 32, 2);
        var render = new WaveFormatExtensible(48000, 32, 2);

        Assert.True(AudioFormatBridge.Differ(capture, render));
    }

    [Fact]
    public void Describe_NamesRateDepthChannelsAndEncoding()
    {
        string described = AudioFormatBridge.Describe(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));

        Assert.Contains("48000", described);
        Assert.Contains("32bit", described);
        Assert.Contains("2ch", described);
        Assert.Contains("IeeeFloat", described);
    }

    [Fact]
    public void Describe_NamesTheSubFormat_ForExtensibleFormats()
    {
        string described = AudioFormatBridge.Describe(new WaveFormatExtensible(48000, 32, 2));

        Assert.Contains("IEEE_FLOAT", described);
    }

    [Fact]
    public void Describe_SeparatesExtensiblePcmFromExtensibleFloat()
    {
        // Both report Encoding.Extensible. Without the subformat the log shows one string for two
        // different streams, and a reader comparing capture against render concludes they match.
        string pcm = AudioFormatBridge.Describe(new WaveFormatExtensible(48000, 16, 2));
        string ieeeFloat = AudioFormatBridge.Describe(new WaveFormatExtensible(48000, 32, 2));

        Assert.NotEqual(pcm, ieeeFloat);
        Assert.Contains("PCM", pcm);
    }
}
