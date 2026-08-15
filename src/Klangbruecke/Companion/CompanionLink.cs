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

    private IDisposable? _reconnect;
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private bool _disposed;

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
                    _publisher.Publish(_snapshot);
                    break;

                case MessageType.PlaybackState:
                    _snapshot = _snapshot with
                    {
                        IsPlaying = MediaProtocol.DecodePlaybackState(payload).IsPlaying,
                    };
                    _publisher.Publish(_snapshot);
                    break;

                // AlbumArt (Task 5) and the seek timeline (Task 6) fold in here next; Hello / Command /
                // RequestArt are never inbound on the PC side.
            }
        }
        catch (Exception ex)
        {
            Log.Error("The companion link failed to handle an inbound frame.", ex);
        }
    }

    private void OnCommandRequested(object? sender, MediaCommand command)
        // Fire-and-forget, like the music half's retry: the send is genuinely asynchronous and
        // SendAsync catches everything, so there is no faulted task left for no one to observe.
        => _ = SendAsync(MediaProtocol.EncodeCommand(command), "command");

    private void OnDisconnected(object? sender, EventArgs e)
    {
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

        CancelReconnect();

        _cts.Cancel();
        _cts.Dispose();

        Teardown.Quietly(_transport.Dispose, "dispose the companion transport");
        Teardown.Quietly(_publisher.Dispose, "dispose the SMTC publisher");
    }
}
