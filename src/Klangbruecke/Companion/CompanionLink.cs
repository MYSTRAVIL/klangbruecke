using Klangbruecke.Connection;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke.Companion;

/// <summary>
/// One controller for the phone-media remote: it wires the transport to the protocol to the SMTC
/// surface, and keeps the link up. Constructed the way <see cref="MusicHalf"/> is - seams in,
/// <see cref="BackoffSchedule"/> and <see cref="IScheduler"/> for the reconnect - so the whole of it
/// can be driven from a fake clock with no radio and no window.
///
/// The three flows it owns:
/// <list type="bullet">
/// <item>Inbound: a <see cref="ICompanionTransport.FrameReceived"/> is folded into the running
/// snapshot via <see cref="MediaProtocol.DecodeInbound"/> and published. The snapshot is kept here,
/// not on the wire, because a PlaybackState frame carries no text and must not blank the title.</item>
/// <item>Outbound: an <see cref="ISmtcPublisher.CommandRequested"/> - a button or media key - is
/// encoded and sent to the phone.</item>
/// <item>Recovery: on connect it sends <c>Hello</c>; on disconnect it clears the session (so the PC
/// stops showing a track the phone no longer has) and schedules a backoff reconnect.</item>
/// </list>
///
/// Single-threaded by contract, like the rest of <c>Connection/</c>: the tray app marshals every
/// callback onto the UI thread, which is what lets a class with this much wiring hold no locks. Every
/// callback guards-and-logs rather than throwing - a tray app that never shows a window also never
/// crashes one open.
/// </summary>
internal sealed class CompanionLink : IDisposable
{
    private readonly ICompanionTransport _transport;
    private readonly ISmtcPublisher _publisher;
    private readonly IScheduler _scheduler;
    private readonly string _pcName;

    private readonly BackoffSchedule _connectBackoff = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ArtCache _artCache = new();

    private IDisposable? _reconnect;
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private bool _disposed;

    // The advancing-seek-bar clock. On each PlaybackState we re-base to the phone's live position and,
    // while playing, a periodic tick pushes an interpolated position so the bar moves without the phone
    // ever streaming one. All touched only on the UI thread (marshaled inbound + UI-thread scheduler).
    private const int TickSeconds = 1;
    private IDisposable? _tick;
    private long _basePositionMs;
    private DateTimeOffset _baseAt;
    private double _speed;

    public CompanionLink(
        ICompanionTransport transport,
        ISmtcPublisher publisher,
        IScheduler scheduler,
        string? pcName = null)
    {
        _transport = transport;
        _publisher = publisher;
        _scheduler = scheduler;

        // The name the phone shows in its Hello handshake. Environment.MachineName is the honest
        // default; a caller may override it.
        _pcName = string.IsNullOrWhiteSpace(pcName) ? Environment.MachineName : pcName;

        _transport.FrameReceived += OnFrameReceived;
        _transport.Disconnected += OnDisconnected;
        _publisher.CommandRequested += OnCommandRequested;
        _publisher.SeekRequested += OnSeekRequested;
    }

    /// <summary>Connects for the first time. Failure is not fatal - it backs off and tries again.</summary>
    public async Task StartAsync() => await ConnectAsync();

    private async Task ConnectAsync()
    {
        // A pending reconnect is either the one that fired to bring us here or one from an older
        // drop; either way it has served its purpose.
        CancelReconnect();

        bool connected;

        try
        {
            connected = await _transport.TryConnectAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            // The shipping transport catches its own throws and returns false; this is a backstop for
            // the seam, so an escaping throw cannot leave the link with no timer and no event that
            // could ever move it again.
            Log.Error("The companion link's connect attempt threw.", ex);
            connected = false;
        }

        if (_disposed)
        {
            // Disposed while the radio was deciding. This answer describes a link nothing is holding.
            return;
        }

        if (!connected)
        {
            ScheduleReconnect();
            return;
        }

        _connectBackoff.Reset();

        // A fresh connection starts from a clean slate: the phone will send its own NowPlaying, and a
        // stale snapshot from the last session must not survive into this one.
        _snapshot = MediaSnapshot.Empty;

        await SendAsync(MediaProtocol.EncodeHello(MediaProtocol.ProtocolVersion, _pcName), "Hello");
    }

    private void OnFrameReceived(object? sender, byte[] frame)
    {
        try
        {
            if (frame is null || frame.Length == 0)
            {
                return;
            }

            var type = (MessageType)frame[0];
            var payload = new ReadOnlyMemory<byte>(frame, 1, frame.Length - 1);

            switch (type)
            {
                case MessageType.NowPlaying:
                    _snapshot = MediaProtocol.DecodeNowPlaying(payload, _snapshot);
                    ResolveArt();
                    _publisher.Publish(_snapshot);
                    break;

                case MessageType.PlaybackState:
                    OnPlaybackState(MediaProtocol.DecodePlaybackState(payload));
                    break;

                case MessageType.AlbumArt:
                    OnAlbumArt(payload);
                    break;

                // Hello / Command / RequestArt are never inbound on the PC side; nothing to fold.
            }
        }
        catch (Exception ex)
        {
            Log.Error("The companion link failed to handle an inbound frame.", ex);
        }
    }

    /// <summary>
    /// After a NowPlaying, attach cached art if we hold it, else ask the phone for it exactly once. A
    /// track with no artHash simply has no image; only a cache miss sends a RequestArt, so art is
    /// fetched once per track and never re-pushed for one the PC already has.
    /// </summary>
    private void ResolveArt()
    {
        string? hash = _snapshot.ArtHash;
        if (string.IsNullOrEmpty(hash))
        {
            return;
        }

        if (_artCache.TryGet(hash, out byte[] jpeg))
        {
            _snapshot = _snapshot with { Art = jpeg };
        }
        else
        {
            _ = SendAsync(MediaProtocol.EncodeRequestArt(hash), "RequestArt");
        }
    }

    private void OnAlbumArt(ReadOnlyMemory<byte> payload)
    {
        if (!MediaProtocol.TryReadAlbumArt(payload, out string hash, out byte[] jpeg))
        {
            Log.Warn("Dropped a malformed AlbumArt frame.");
            return;
        }

        _artCache.Put(hash, jpeg);

        // Only republish if this is the art for the track showing now; a late reply for a track we have
        // since moved past is cached for later but not shown over the current one.
        if (hash == _snapshot.ArtHash)
        {
            _snapshot = _snapshot with { Art = jpeg };
            _publisher.Publish(_snapshot);
        }
    }

    /// <summary>
    /// A PlaybackState carries the play flag (folded into the snapshot, so the title stands) and the
    /// timeline. We re-base the local clock from the phone's live position, push it once immediately,
    /// and while playing arm the tick that keeps the bar advancing; a pause pushes the frozen position
    /// once and disarms the tick.
    /// </summary>
    private void OnPlaybackState(PlaybackUpdate update)
    {
        _snapshot = _snapshot with { IsPlaying = update.IsPlaying };
        _publisher.Publish(_snapshot);

        _basePositionMs = update.PositionMs;
        _baseAt = _scheduler.Now;
        _speed = update.Speed;
        PushTimeline();

        if (update.IsPlaying)
        {
            ArmTick();
        }
        else
        {
            DisarmTick();
        }
    }

    private void PushTimeline()
    {
        long pos = TimelineMath.PositionAt(_basePositionMs, _scheduler.Now - _baseAt, _speed, _snapshot.DurationMs);
        _publisher.UpdateTimeline(new TimelineState(pos, _snapshot.DurationMs, _snapshot.IsPlaying, _speed));
    }

    private void ArmTick()
    {
        if (_tick is not null)
        {
            return; // already advancing
        }

        _tick = _scheduler.SchedulePeriodic(TimeSpan.FromSeconds(TickSeconds), PushTimeline);
    }

    private void DisarmTick()
    {
        _tick?.Dispose();
        _tick = null;
    }

    private void OnCommandRequested(object? sender, MediaCommand command)
        // Fire-and-forget, like the music half's retry: the send is genuinely asynchronous and
        // SendAsync catches everything, so there is no faulted task left for no one to observe.
        => _ = SendAsync(MediaProtocol.EncodeCommand(command), "command");

    private void OnSeekRequested(object? sender, long positionMs)
        => _ = SendAsync(MediaProtocol.EncodeSeek(positionMs), "seek");

    private void OnDisconnected(object? sender, EventArgs e)
    {
        // Stop the seek-bar clock: the phone is gone, so there is no position left to advance.
        DisarmTick();

        try
        {
            // Clear the surface before anything else: an SMTC session left showing the last track
            // after the phone has gone is the failure MediaSnapshot.Empty exists to prevent.
            _snapshot = MediaSnapshot.Empty;
            _publisher.Publish(MediaSnapshot.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("The companion link failed to clear the session on disconnect.", ex);
        }

        ScheduleReconnect();
    }

    private async Task SendAsync(byte[] frame, string what)
    {
        try
        {
            await _transport.SendAsync(frame, _cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error($"The companion link failed to send {what}.", ex);
        }
    }

    private void ScheduleReconnect()
    {
        if (_disposed)
        {
            return;
        }

        CancelReconnect();

        // The current step is what this failure waits; advancing after arming is what makes the first
        // wait 2 s rather than 4 - the same ordering the music half's connect backoff uses.
        TimeSpan delay = _connectBackoff.CurrentDelay;
        _connectBackoff.Advance();

        _reconnect = _scheduler.Schedule(delay, () =>
        {
            _reconnect = null;
            _ = ConnectAsync();
        });
    }

    private void CancelReconnect()
    {
        _reconnect?.Dispose();
        _reconnect = null;
    }

    /// <summary>
    /// Owns the seams it was handed. The manager (Task C2) disposes the link via
    /// <see cref="Teardown.Quietly"/>; the link passes that on to the transport and publisher the same
    /// way, so one failing dispose cannot skip the next.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _transport.FrameReceived -= OnFrameReceived;
        _transport.Disconnected -= OnDisconnected;
        _publisher.CommandRequested -= OnCommandRequested;
        _publisher.SeekRequested -= OnSeekRequested;

        CancelReconnect();
        DisarmTick();

        _cts.Cancel();
        _cts.Dispose();

        Teardown.Quietly(_transport.Dispose, "dispose the companion transport");
        Teardown.Quietly(_publisher.Dispose, "dispose the SMTC publisher");
    }
}
