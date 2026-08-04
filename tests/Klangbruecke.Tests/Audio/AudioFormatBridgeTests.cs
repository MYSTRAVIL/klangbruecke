using Klangbruecke.Audio;
using NAudio.Wave;
using Xunit;

namespace Klangbruecke.Tests.Audio;

public sealed class AudioFormatBridgeTests
{
    [Fact]
    public void RequiresResampling_IsFalse_ForIdenticalFormats()
    {
        var a = new WaveFormat(48000, 16, 2);
        var b = new WaveFormat(48000, 16, 2);

        Assert.False(AudioFormatBridge.RequiresResampling(a, b));
    }

    [Fact]
    public void RequiresResampling_IsTrue_WhenSampleRateDiffers()
    {
        // The A2DP sink commonly lands on 44.1 kHz while render endpoints sit at 48 kHz.
        var capture = new WaveFormat(44100, 16, 2);
        var output = new WaveFormat(48000, 16, 2);

        Assert.True(AudioFormatBridge.RequiresResampling(capture, output));
    }

    [Fact]
    public void RequiresResampling_IsTrue_WhenChannelCountDiffers()
    {
        Assert.True(AudioFormatBridge.RequiresResampling(new WaveFormat(48000, 16, 2), new WaveFormat(48000, 16, 1)));
    }

    [Fact]
    public void RequiresResampling_IsTrue_WhenBitDepthDiffers()
    {
        Assert.True(AudioFormatBridge.RequiresResampling(new WaveFormat(48000, 16, 2), new WaveFormat(48000, 24, 2)));
    }

    [Fact]
    public void RequiresResampling_IsTrue_WhenEncodingDiffers()
    {
        var pcm = new WaveFormat(48000, 32, 2);
        var ieeeFloat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        Assert.True(AudioFormatBridge.RequiresResampling(pcm, ieeeFloat));
    }

    [Fact]
    public void Describe_NamesRateDepthChannelsAndEncoding()
    {
        string described = AudioFormatBridge.Describe(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));

        Assert.Contains("48000", described);
        Assert.Contains("2ch", described);
        Assert.Contains("IeeeFloat", described);
    }
}
