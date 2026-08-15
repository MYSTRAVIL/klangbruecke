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
