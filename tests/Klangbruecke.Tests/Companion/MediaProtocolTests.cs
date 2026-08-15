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
    public void DecodeNowPlaying_ReplacesText()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"Al\",\"durationMs\":0,\"hasSession\":true}");
        var snap = MediaProtocol.DecodeNowPlaying(payload, MediaSnapshot.Empty);
        Assert.Equal("T", snap.Title);
        Assert.True(snap.HasSession);
    }

    [Fact]
    public void DecodePlaybackState_ReadsPlaying()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"status\":\"playing\",\"positionMs\":0,\"timestampMs\":0,\"speed\":1.0}");
        var u = MediaProtocol.DecodePlaybackState(payload);
        Assert.True(u.IsPlaying);
    }

    [Fact]
    public void EncodeRequestArt_FramesTypeAndHash()
    {
        var frame = MediaProtocol.EncodeRequestArt("abc123");
        Assert.Equal((byte)MessageType.RequestArt, frame[4]);
        int len = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
        Assert.Equal(frame.Length - 4, len);
        Assert.Contains("abc123", System.Text.Encoding.UTF8.GetString(frame, 5, frame.Length - 5));
    }

    [Fact]
    public void EncodeSeek_IsACommandFrame_WithPositionMs()
    {
        var frame = MediaProtocol.EncodeSeek(42000);
        Assert.Equal((byte)MessageType.Command, frame[4]);
        string json = System.Text.Encoding.UTF8.GetString(frame, 5, frame.Length - 5);
        Assert.Contains("\"command\":\"seek\"", json);
        Assert.Contains("\"positionMs\":42000", json);
    }

    [Fact]
    public void EncodeCommand_OmitsPositionMs()
    {
        // A non-seek Command stays byte-compatible with Phase 2: no positionMs field at all.
        var frame = MediaProtocol.EncodeCommand(MediaCommand.Next);
        string json = System.Text.Encoding.UTF8.GetString(frame, 5, frame.Length - 5);
        Assert.DoesNotContain("positionMs", json);
    }

    [Fact]
    public void DecodeNowPlaying_CarriesDurationAndArtHash_AndClearsStaleArt()
    {
        var prior = MediaSnapshot.Empty with { Art = new byte[] { 1, 2, 3 } };
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"Al\",\"durationMs\":200000,\"hasSession\":true,\"artHash\":\"h1\"}");
        var snap = MediaProtocol.DecodeNowPlaying(payload, prior);
        Assert.Equal(200000, snap.DurationMs);
        Assert.Equal("h1", snap.ArtHash);
        Assert.Null(snap.Art); // a new NowPlaying must not carry the previous track's image
    }

    [Fact]
    public void DecodePlaybackState_ReadsPositionSpeedAndPlaying()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"status\":\"playing\",\"positionMs\":30000,\"timestampMs\":123,\"speed\":1.0}");
        var u = MediaProtocol.DecodePlaybackState(payload);
        Assert.True(u.IsPlaying);
        Assert.Equal(30000, u.PositionMs);
        Assert.Equal(1.0, u.Speed);
    }

    [Fact]
    public void TryReadAlbumArt_SplitsHashFromJpeg()
    {
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 };
        var body = MediaProtocolTestHelper.BuildAlbumArtPayload("hash42", jpeg);
        Assert.True(MediaProtocol.TryReadAlbumArt(body, out string hash, out byte[] outJpeg));
        Assert.Equal("hash42", hash);
        Assert.Equal(jpeg, outJpeg);
    }

    [Fact]
    public void TryReadAlbumArt_RejectsTruncated()
    {
        Assert.False(MediaProtocol.TryReadAlbumArt(new byte[] { 0x00 }, out _, out _));
    }
}

internal static class MediaProtocolTestHelper
{
    // [2-byte BE hashLen][hash UTF-8][jpeg] - the AlbumArt payload the phone sends (no length prefix;
    // the outer frame already stripped it before TryReadAlbumArt sees the body).
    public static byte[] BuildAlbumArtPayload(string hash, byte[] jpeg)
    {
        byte[] h = System.Text.Encoding.UTF8.GetBytes(hash);
        var buf = new byte[2 + h.Length + jpeg.Length];
        buf[0] = (byte)(h.Length >> 8);
        buf[1] = (byte)h.Length;
        h.CopyTo(buf, 2);
        jpeg.CopyTo(buf, 2 + h.Length);
        return buf;
    }
}
