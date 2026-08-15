using Klangbruecke.Companion;
using Xunit;

namespace Klangbruecke.Tests.Companion;

public sealed class MediaProtocolTests
{
    [Fact]
    public void EncodeCommand_FramesTypeAndPayload()
    {
        var frame = MediaProtocol.EncodeCommand(MediaCommand.Next);
        // [len(4)][type(1)][json...]; len covers type+payload
        int len = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
        Assert.Equal(frame.Length - 4, len);
        Assert.Equal((byte)MessageType.Command, frame[4]);
    }

    [Fact]
    public void TryReadFrame_ReturnsFalse_WhenIncomplete()
    {
        var full = MediaProtocol.EncodeCommand(MediaCommand.Play);
        ReadOnlySpan<byte> partial = full.AsSpan(0, full.Length - 2);
        Assert.False(MediaProtocol.TryReadFrame(ref partial, out _, out _));
    }

    [Fact]
    public void TryReadFrame_ParsesOneFrame_AndAdvances()
    {
        var a = MediaProtocol.EncodeCommand(MediaCommand.Play);
        var b = MediaProtocol.EncodeCommand(MediaCommand.Pause);
        var combined = a.Concat(b).ToArray();
        ReadOnlySpan<byte> span = combined;
        Assert.True(MediaProtocol.TryReadFrame(ref span, out var t1, out _));
        Assert.Equal(MessageType.Command, t1);
        Assert.Equal(b.Length, span.Length); // advanced past frame a
    }

    [Fact]
    public void DecodeInbound_NowPlaying_ReplacesText()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"Al\",\"durationMs\":0,\"hasSession\":true}");
        var (snap, _) = MediaProtocol.DecodeInbound(MessageType.NowPlaying, payload, MediaSnapshot.Empty);
        Assert.Equal("T", snap.Title);
        Assert.True(snap.HasSession);
    }

    [Fact]
    public void DecodeInbound_PlaybackState_OnlyFlipsIsPlaying()
    {
        var prior = new MediaSnapshot("T", "A", "Al", false, true);
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"status\":\"playing\",\"positionMs\":0,\"timestampMs\":0,\"speed\":1.0}");
        var (snap, _) = MediaProtocol.DecodeInbound(MessageType.PlaybackState, payload, prior);
        Assert.True(snap.IsPlaying);
        Assert.Equal("T", snap.Title); // text unchanged
    }
}
