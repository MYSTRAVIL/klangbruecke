using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke.Connection;

/// <summary>
/// The 3 s window that decides, when the audio connection closes, whether the phone dropped the
/// profile deliberately (the ACL link is still up - suppress) or left the room (the link is gone -
/// range exit). Nothing is decided until it elapses, which is what keeps a one-second dropout from
/// flapping the tray.
///
/// <b>The generation shape of supersession.</b> A window is superseded by discrete events - another
/// window being armed, or the phone selection changing - so a counter is enough; it needs no time to
/// compare against, unlike the <see cref="Reconciler"/>'s stall timestamp. The generation is captured
/// when the question is asked (the window is armed) and read by the answer (the elapsed callback):
/// what crosses the await is a stale answer, not a data race.
///
/// <b>Single-threaded, subscribes to nothing, and never add <c>ConfigureAwait(false)</c>.</b> Every
/// input is a method call the manager has already marshalled onto the UI thread, and the one await -
/// the link status read in <see cref="OnElapsedAsync"/> - must resume on that thread, because its
/// answer drives the suppression latch. A <c>ConfigureAwait(false)</c> here would drive it from a
/// radio's worker thread. The manager holds no lock and this is why.
/// </summary>
internal sealed class GraceWindow
{
    /// <summary>
    /// How long to wait before believing a closed audio connection.
    ///
    /// The connection closing is the same event for two opposite causes: the phone dropped the audio
    /// profile deliberately (the ACL link stays up, and reconnecting would fight the user), or the
    /// phone left the room. Three seconds is long enough for the radio to settle and short enough
    /// that a real range exit is not left looking connected - and, more to the point, it is what
    /// keeps a one-second dropout from flapping the tray, because nothing is decided until it
    /// elapses.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly IScheduler _scheduler;
    private readonly ILinkMonitor _linkMonitor;
    private readonly LinkMachine _linkMachine;
    private readonly SuppressionLatch _latch;
    private readonly MusicHalf _music;
    private readonly IConnectionCoordinator _coordinator;

    private IDisposable? _timer;

    /// <summary>
    /// Bumped by every window that is armed, and read - never written - by the window that finally
    /// answers. What crosses the await is a stale answer, not a data race.
    /// </summary>
    private int _generation;

    public GraceWindow(
        IScheduler scheduler,
        ILinkMonitor linkMonitor,
        LinkMachine linkMachine,
        SuppressionLatch latch,
        MusicHalf music,
        IConnectionCoordinator coordinator)
    {
        _scheduler = scheduler;
        _linkMonitor = linkMonitor;
        _linkMachine = linkMachine;
        _latch = latch;
        _music = music;
        _coordinator = coordinator;
    }

    /// <summary>
    /// Voids an outstanding window, because the question it is going to answer is about a phone the
    /// user has just changed their mind about.
    ///
    /// Both matter. Bumping the generation alone would leave the armed timer standing, and
    /// <see cref="OnConnectionClosed"/> declines to arm a window while one is armed - so the next
    /// Closed would get no window at all. Disposing alone would leave a window that has already fired
    /// and is waiting on its read free to come back and decide.
    /// </summary>
    public void Cancel()
    {
        _timer?.Dispose();
        _timer = null;
        _generation++;
    }

    /// <summary>
    /// The audio connection reported Closed - or the reconcile found it gone without one.
    ///
    /// Nothing is decided here. The half drops its route, because there is nothing to route, and
    /// keeps everything else: which of the two causes this was is a question only a link status read
    /// can answer, and asking it immediately gets the wrong answer for a radio that has not settled.
    /// </summary>
    public void OnConnectionClosed()
    {
        _music.OnConnectionClosed();

        if (_timer is null)
        {
            // One window at a time. A connection that reports Closed twice, or a reconcile that finds
            // it gone on two ticks running, would otherwise arm two windows that each read the link
            // and decide again.
            //
            // "At a time" only covers the wait, though. The handle is dropped the moment the window
            // fires, so a second Closed arriving while the first window's link read is still
            // outstanding arms a second window on top of it - which is why the generation is taken
            // here, at the moment the question is asked.
            int generation = ++_generation;

            _timer = _scheduler.Schedule(Window, () =>
            {
                _timer = null;
                _ = OnElapsedAsync(generation);
            });
        }

        _coordinator.Publish();
    }

    private async Task OnElapsedAsync(int generation)
    {
        if (_coordinator.IsDisposed)
        {
            return;
        }

        BluetoothLinkStatus status = await _linkMonitor.ReadLinkStatusAsync();

        if (Superseded(generation))
        {
            // A newer Closed has asked the same question since, and this answer predates it. Acting
            // on it is worse here than anywhere else in the class: this is the one read that decides
            // deliberate-versus-out-of-range, so a stale one either latches a suppression nobody
            // asked for - the app then sitting next to a phone it refuses to reconnect to, which is
            // the predecessor's defining bug reached from a new direction - or records an absence
            // that expires a suppression the user did ask for.
            return;
        }

        if (status == BluetoothLinkStatus.Connected)
        {
            // The ACL link is alive and only the audio profile went, which is what the phone dropping
            // this PC looks like. Reconnecting would fight the user, once every backoff step, for as
            // long as they left the phone in the room.
            Log.Info("The audio connection closed with the Bluetooth link still up: treating it as deliberate.");
            _coordinator.SuppressDeliberately("The phone dropped the audio connection.");
        }
        else
        {
            // Disconnected, or a read that could not answer - and Unknown belongs here rather than
            // with the branch above for the same reason LinkMachine collapses it to Absent: guessing
            // the link is up leaves the app dormant next to a phone that walked out of the building.
            Log.Info("The audio connection closed and the phone is not reachable: treating it as a range exit.");

            // Immediately, not through the poll debounce. That debounce exists because one failed
            // read is indistinguishable from the phone leaving; here two independent observations
            // agree, which is the definite kind - the same kind as a watcher removal.
            _linkMachine.OnDeviceRemoved();
            _latch.OnLinkState(_linkMachine.State);

            _music.OnLinkAbsent();
            _coordinator.Report("The phone is out of range.");
        }

        _coordinator.EnforceConnectPermission();
        _coordinator.Publish();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private bool Superseded(int generation) => _coordinator.IsDisposed || _generation != generation;
}
