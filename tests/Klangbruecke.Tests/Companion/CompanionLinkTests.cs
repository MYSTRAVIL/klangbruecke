using System.Text;
using Klangbruecke.Companion;
using Klangbruecke.Tests.Fakes;
using Xunit;

namespace Klangbruecke.Tests.Companion;

public sealed class CompanionLinkTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static FakeScheduler NewScheduler() => new(Start);

    /// <summary>Builds a decoded frame (type + payload, no length) the way the transport delivers one.</summary>
    private static byte[] FrameOf(MessageType type, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[1 + payload.Length];
        frame[0] = (byte)type;
        payload.CopyTo(frame, 1);
        return frame;
    }

    /// <summary>An AlbumArt frame as the transport delivers one: [type][2-byte BE hashLen][hash][jpeg], no length prefix.</summary>
    private static byte[] AlbumArtFrameOf(string hash, byte[] jpeg)
    {
        byte[] h = Encoding.UTF8.GetBytes(hash);
        var frame = new byte[1 + 2 + h.Length + jpeg.Length];
        frame[0] = (byte)MessageType.AlbumArt;
        frame[1] = (byte)(h.Length >> 8);
        frame[2] = (byte)h.Length;
        h.CopyTo(frame, 3);
        jpeg.CopyTo(frame, 3 + h.Length);
        return frame;
    }

    private const string NowPlayingH1 =
        "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":200000,\"hasSession\":true,\"artHash\":\"h1\"}";

    [Fact]
    public async Task NowPlayingWithNewArtHash_RequestsArt()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, NewScheduler());
        await link.StartAsync();
        t.Raise(FrameOf(MessageType.NowPlaying, NowPlayingH1));
        Assert.Contains(t.Sent, f => f[4] == (byte)MessageType.RequestArt);
    }

    [Fact]
    public async Task AlbumArtFrame_AttachesBytesToSnapshotAndRepublishes()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, NewScheduler());
        await link.StartAsync();
        t.Raise(FrameOf(MessageType.NowPlaying, NowPlayingH1));
        t.Raise(AlbumArtFrameOf("h1", new byte[] { 9, 8, 7 }));
        Assert.Equal(new byte[] { 9, 8, 7 }, p.Published.Last().Art);
    }

    [Fact]
    public async Task ArtHashAlreadyCached_DoesNotRequestAgain_AndServesFromCache()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, NewScheduler());
        await link.StartAsync();
        t.Raise(FrameOf(MessageType.NowPlaying, NowPlayingH1));
        t.Raise(AlbumArtFrameOf("h1", new byte[] { 1 }));
        int before = t.Sent.Count(f => f[4] == (byte)MessageType.RequestArt);

        // Same track again (e.g. a metadata refresh): art is cached, no second request, served from cache.
        t.Raise(FrameOf(MessageType.NowPlaying, NowPlayingH1));
        Assert.Equal(before, t.Sent.Count(f => f[4] == (byte)MessageType.RequestArt));
        Assert.Equal(new byte[] { 1 }, p.Published.Last().Art);
    }

    [Fact]
    public async Task NowPlayingWithoutArtHash_DoesNotRequestArt()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, NewScheduler());
        await link.StartAsync();
        t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":0,\"hasSession\":true}"));
        Assert.DoesNotContain(t.Sent, f => f[4] == (byte)MessageType.RequestArt);
    }

    [Fact]
    public async Task OnConnect_SendsHello()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, NewScheduler());
        await link.StartAsync();
        Assert.Contains(t.Sent, f => f[4] == (byte)MessageType.Hello);
    }

    [Fact]
    public async Task NowPlayingFrame_PublishesSnapshot()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, NewScheduler());
        await link.StartAsync();
        t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":0,\"hasSession\":true}"));
        Assert.Equal("T", p.Published.Last().Title);
    }

    [Fact]
    public async Task SmtcCommand_SendsCommandFrame()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, NewScheduler());
        await link.StartAsync();
        p.RaiseCommand(MediaCommand.Next);
        Assert.Contains(t.Sent, f => f[4] == (byte)MessageType.Command);
    }

    [Fact]
    public async Task OnDisconnect_ClearsSession()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, NewScheduler());
        await link.StartAsync();
        t.RaiseDisconnected();
        Assert.False(p.Published.Last().HasSession);
    }

    /// <summary>
    /// PlaybackState carries no text, so a play/pause that arrived after a NowPlaying must leave the
    /// title standing - the fold is the same one <c>MediaProtocol.DecodeInbound</c> proves, but here
    /// it is proved through the link's own accumulated snapshot.
    /// </summary>
    [Fact]
    public async Task PlaybackStateFrame_KeepsPriorTextAndFlipsPlaying()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, NewScheduler());
        await link.StartAsync();

        t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":0,\"hasSession\":true}"));
        t.Raise(FrameOf(MessageType.PlaybackState, "{\"status\":\"playing\",\"positionMs\":0,\"timestampMs\":0,\"speed\":1.0}"));

        MediaSnapshot last = p.Published.Last();
        Assert.Equal("T", last.Title);
        Assert.True(last.IsPlaying);
    }

    /// <summary>
    /// A dropped link backs off and reconnects on the same 2/4/8 schedule the music half uses, driven
    /// by the injected scheduler rather than a real timer.
    /// </summary>
    [Fact]
    public async Task Disconnect_SchedulesAReconnect()
    {
        var scheduler = NewScheduler();
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, scheduler);
        await link.StartAsync();
        Assert.Equal(1, t.ConnectAttempts);

        t.RaiseDisconnected();
        scheduler.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(2, t.ConnectAttempts);
    }

    /// <summary>A failed initial connect backs off too, rather than giving up silently.</summary>
    [Fact]
    public async Task FailedConnect_SchedulesAReconnect()
    {
        var scheduler = NewScheduler();
        var t = new FakeCompanionTransport { NextConnectResult = false };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, scheduler);
        await link.StartAsync();
        Assert.Equal(1, t.ConnectAttempts);

        t.NextConnectResult = true;
        scheduler.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(2, t.ConnectAttempts);
    }
}
