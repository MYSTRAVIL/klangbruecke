using Klangbruecke.Audio;
using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke.Connection;

/// <summary>
/// The 30 s level-triggered drift correction: the spec's five checks in order, and the stall/
/// supersession bookkeeping that keeps a wedged pass from stopping the only backstop the app has.
///
/// <b>The timestamp shape of supersession.</b> Unlike the <see cref="GraceWindow"/>'s generation, a
/// pass is marked by <em>when</em> it started, because the reconcile also has to decide when a pass
/// has stalled - a bool set by a read that never returns would silently stop the backstop forever,
/// which is the predecessor app's defining bug rebuilt out of a mutex. A time can both identify the
/// current pass and say when it has been running too long to defer to.
///
/// <b>Single-threaded, subscribes to nothing, and never add <c>ConfigureAwait(false)</c>.</b> Every
/// await in a pass must resume on the thread the turn started on; that is what makes four machines
/// correct with no lock between them. See the "captured context" map in <c>ConnectionManagerTests</c>.
/// </summary>
internal sealed class Reconciler
{
    /// <summary>
    /// The drift correction. Level-triggered, because the events that should have told us are exactly
    /// the ones that go missing across sleep and resume - and an edge that never arrives is what
    /// leaves an app wrong forever, which is the predecessor app's defining bug and the reason this
    /// project exists.
    /// </summary>
    private static readonly TimeSpan ReconcilePeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a pass may be running before the next one stops deferring to it.
    /// </summary>
    /// <remarks>
    /// Deliberately shorter than <see cref="ReconcilePeriod"/> rather than equal to it. A pass starts
    /// a hair <em>after</em> the tick that launched it, so with the two the same the tick one period
    /// later would find the wedged pass a few microseconds short of the threshold and defer as well -
    /// recovery would cost two periods instead of one, on the real timer, and never in a test where
    /// virtual time lands exactly on the boundary. Five seconds of margin, on the same reasoning as
    /// the resume settle: long enough that a slow-but-live read is not abandoned, short enough that
    /// the tick that finds it does not have to be punctual.
    /// </remarks>
    private static readonly TimeSpan ReconcileStall = TimeSpan.FromSeconds(25);

    private readonly IScheduler _scheduler;
    private readonly ILinkMonitor _linkMonitor;
    private readonly IAudioSinkService _sink;
    private readonly LinkMachine _linkMachine;
    private readonly SuppressionLatch _latch;
    private readonly MusicHalf _music;
    private readonly CallsHalf _calls;
    private readonly GraceWindow _graceWindow;
    private readonly IConnectionCoordinator _coordinator;

    private IDisposable? _timer;

    /// <summary>
    /// When the pass currently running started, or null when none is.
    ///
    /// A pass has five awaits in it and the link read is a real round trip to a radio, so a forced
    /// pass - a phone picked, a resume, the setting coming back on - can land on top of the periodic
    /// one. Two interleaved passes would each decide against a link status the other is still acting
    /// on, and both would open a grace window against the same closed connection.
    ///
    /// A time rather than a bool, and the difference is the whole reason this app exists. A read that
    /// never completes would leave a bool set for the life of the process and silently stop the only
    /// backstop the app has - an app that is wrong forever with nothing to correct it, which is the
    /// predecessor's defining bug rebuilt out of a mutex. A pass still running after
    /// <see cref="ReconcileStall"/> has stopped being one to defer to.
    ///
    /// Deferring is only half the invariant; the abandoned pass must also stop acting when it finally
    /// answers. See <see cref="Superseded"/>.
    /// </summary>
    private DateTimeOffset? _reconcilingSince;

    public Reconciler(
        IScheduler scheduler,
        ILinkMonitor linkMonitor,
        IAudioSinkService sink,
        LinkMachine linkMachine,
        SuppressionLatch latch,
        MusicHalf music,
        CallsHalf calls,
        GraceWindow graceWindow,
        IConnectionCoordinator coordinator)
    {
        _scheduler = scheduler;
        _linkMonitor = linkMonitor;
        _sink = sink;
        _linkMachine = linkMachine;
        _latch = latch;
        _music = music;
        _calls = calls;
        _graceWindow = graceWindow;
        _coordinator = coordinator;
    }

    /// <summary>Schedules the periodic tick. Call once, from the manager's Start.</summary>
    public void Start()
    {
        _timer = _scheduler.SchedulePeriodic(ReconcilePeriod, () => _ = RunAsync("tick"));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Has this pass been given up on and replaced?
    ///
    /// Asked after every await, and it is the same guard the halves spell <c>_generation</c>: what
    /// crosses an await is not a data race - the whole class is one thread - but a stale
    /// <em>answer</em>. Holding the marker is only half of it. A pass whose link read finally returns
    /// 45 s late would otherwise write that status into the link machine, feed it to the latch and
    /// run both halves against a state its replacement is halfway through establishing, which is
    /// exactly the interleaving <see cref="_reconcilingSince"/> exists to prevent.
    /// </summary>
    private bool Superseded(DateTimeOffset startedAt) => _coordinator.IsDisposed || _reconcilingSince != startedAt;

    /// <summary>
    /// Awaits one step of a pass and answers whether the pass is still the current one.
    ///
    /// A helper rather than the check written out four times, for the reason
    /// <c>MusicHalf.StartRouteIfDue</c> is the one place a route is started: written out, a fifth
    /// awaited step could be added and the guard forgotten, and the two arms would agree on every
    /// input the suite can produce - so neither could be broken without the other covering for it.
    /// Every await inside a pass except the link read, which has a value to hand back, goes through
    /// here.
    /// </summary>
    private async Task<bool> StillOurs(Task step, DateTimeOffset startedAt)
    {
        await step;

        return !Superseded(startedAt);
    }

    /// <param name="userAsked">
    /// True when this pass descends from the user naming a phone. It changes exactly one thing -
    /// check 2 - and see there for why.
    /// </param>
    public async Task RunAsync(string trigger, bool userAsked = false)
    {
        if (_coordinator.IsDisposed)
        {
            return;
        }

        if (_reconcilingSince is { } running && _scheduler.Now - running < ReconcileStall)
        {
            return;
        }

        DateTimeOffset startedAt = _scheduler.Now;
        _reconcilingSince = startedAt;

        try
        {
            // Three things in this method outlive an await on purpose, and they are the only three:
            // this snapshot, whose whole job is to be from before; startedAt, which is the token the
            // supersession check compares against; and the trigger string, which is a constant.
            // Everything else - permission most of all - is read at the point it is used. See the
            // note on ConnectHalvesAsync's first await for what a hoisted permission flag costs.
            Drift before = TakeDrift();

            // 1. The link, level-triggered. This is the backstop for a watcher edge that never
            // arrived, which is what sleep and resume do to WinRT device events.
            BluetoothLinkStatus status = await _linkMonitor.ReadLinkStatusAsync();

            if (Superseded(startedAt))
            {
                // The answer is older than the pass that replaced this one, and a link status from
                // 45 s ago is not a correction - it is drift, arriving in the machine whose job is to
                // remove it.
                return;
            }

            bool linkMoved = _linkMachine.OnLinkStatusRead(status);
            _latch.OnLinkState(_linkMachine.State);

            if (linkMoved && _linkMachine.State == LinkState.Absent)
            {
                // The backstop actually reaching the half it exists for. This poll is the only thing
                // that ever notices a range exit whose watcher edge never arrived, and until it said
                // so the music half went on believing in a phone that had left: measured in the
                // packaged 0.2.0.0 run, where a false watcher Added put the half in Backoff, the poll
                // corrected the link on its second tick, and the half went on opening the radio every
                // 60 s while the tray read Discovering. Worse than the wasted attempts is what
                // happens when the phone returns - OnLinkPresentAsync acts only from Off, so the
                // watcher's Added edge is refused and recovery waits on a 60 s retry that is never
                // reset without a success. That is the range-exit-and-return path, which is the
                // predecessor app's defining bug.
                //
                // <b>Edge-triggered, off the value LinkMachine already returns, and never
                // level-triggered.</b> The state alone is Absent on every tick for as long as the
                // phone is away, and a teardown on each of them bumps MusicHalf._generation and
                // cancels whatever the half had armed. That is not hypothetical: re-picking the same
                // phone resets the link machine to Absent while MusicHalf.Configure deliberately
                // leaves a half on the same phone alone, so the countdown the click just granted
                // sits under ticks that have nothing to report - see
                // A_poll_that_moves_nothing_does_not_stand_a_backing_off_half_down.
                //
                // The flag is also what keeps the poll debounce intact: MoveTo answers false for
                // Absent -> Absent and OnLinkStatusRead answers false from NoPhone, so this is
                // reachable only from Present, which is the one transition the debounce guards.
                //
                // Music only, for the reason OnDeviceRemoved gives: registration is not link-scoped.
                _music.OnLinkAbsent();
            }

            // After the teardown, so a half that has just gone Off does not pay for a 282 ms probe it
            // cannot act on.
            _coordinator.RefreshEndpointLevel();

            // 2. A consistency check between two seams - and deliberately not described as more than
            // that any more.
            //
            // It was written for "the connection object can go away without ever reporting Closed,
            // across a suspend most of all". <b>It cannot see that case</b>, and the honest version of
            // the comment is worth more than the reassuring one. <c>AudioSinkService.IsConnected</c>
            // answers from two fields that only this app writes - the connection reference and the
            // connected id - and a WinRT object killed underneath them leaves both set. The one
            // in-process caller that clears them is <c>MusicHalf.TearDown</c>, which lands on
            // <see cref="MusicState.Off"/> in the same call, so with the shipping sink this condition
            // is not reachable at all. Task 17 removed the last caller that could reach it: the tray's
            // own Disconnect, which stopped the sink without telling the half.
            //
            // It stays, as the seam guard it actually is. <see cref="IAudioSinkService"/> is an
            // interface, and an implementation whose IsConnected tracked the connection rather than
            // this app's bookkeeping would make this live again - which is the direction to fix it in
            // if the suspend case is ever measured. That fix needs a guarded tri-state in the shape of
            // <c>ICallTransportService.ReadRegistration</c>, never a bool: reading the live WinRT
            // State is an ABI call that can throw or fail to answer, and "could not tell" read as
            // "gone" tears down a working connection. What does back the suspend case up today is the
            // link status read above and the endpoint level below.
            //
            // The premise is pinned by
            // MusicHalfTests.Linked_and_Up_are_only_ever_held_over_a_connected_sink.
            if (!_sink.IsConnected && _music.State is MusicState.Linked or MusicState.Up)
            {
                if (userAsked)
                {
                    // The user has just named this phone, so there is nothing here to adjudicate: a
                    // half that still believes in a connection the sink no longer has is stale, not
                    // ambiguous. Opening a window would answer "the link is up, so the audio profile
                    // was dropped deliberately" and suppress the app three seconds after the click -
                    // and SelectPhone cancelling the previous window is what made that reachable.
                    //
                    // OnSuppressed is the teardown, not a claim about why: the half offers no
                    // "start again" input, and every other route out of Linked in this class ends at
                    // the same call. Standing it down here is what lets the link-present report below
                    // reconnect it inside this same pass, which is what the click asked for.
                    _music.OnSuppressed();
                }
                else
                {
                    _graceWindow.OnConnectionClosed();

                    // Nothing else this pass. What just opened is a question with a 3 s answer, and
                    // correcting the halves against a connection that is already gone would start a
                    // route over it in the meantime.
                    ReportDrift(before, trigger);
                    return;
                }
            }

            // 3, 4 and 5 - the capture endpoint, the route, and the registration - are each discharged
            // inside the half that owns them. Reading any of them here would be a second opinion that
            // could disagree with the machine acting on it.
            //
            // Permission is read per half and never hoisted into a local above these awaits. The
            // first of them can be a real ConnectAsync round trip to a radio, and the tray's
            // Disconnect during it sets the latch - which StillOurs cannot see, because nothing on
            // the Disconnect path touches _reconcilingSince, and which EnforceConnectPermission below
            // cannot repair, because it stands down only when the latch is *not* set. A hoisted flag
            // therefore claims the hands-free role seconds after the user disconnected, while the
            // tray reports Suppressed.
            if (!await StillOurs(_music.ReconcileAsync(_coordinator.ConnectPermitted), startedAt))
            {
                return;
            }

            if (!await StillOurs(_calls.ReconcileAsync(_coordinator.ConnectPermitted), startedAt))
            {
                return;
            }

            if (_linkMachine.State == LinkState.Present)
            {
                // Level-triggered, like everything else in the pass: both halves ignore this unless
                // they are Off, so saying it every 30 s costs nothing and saying it never is how an
                // app that missed one edge stays down for the rest of the session.
                if (!await StillOurs(_music.OnLinkPresentAsync(_coordinator.ConnectPermitted), startedAt))
                {
                    return;
                }

                // The last await in the pass, so everything after it is the tail - which is why no
                // test can catch a ConfigureAwait(false) here. See the map in ConnectionManagerTests
                // under "the captured context"; the prohibition still applies, it just has no tripwire.
                if (!await StillOurs(_calls.OnLinkPresentAsync(_coordinator.ConnectPermitted), startedAt))
                {
                    return;
                }
            }

            _coordinator.EnforceConnectPermission();
            ReportDrift(before, trigger);

            // Deliberately no timeout on a half stuck in Connecting or Registering, and CallsHalf's
            // note about "a reconcile-side timeout question" is answered here: no. The only lever
            // this class has is a teardown, and a teardown disposes the WinRT connection object
            // underneath an OpenAsync that has not returned - which is the class of call that takes
            // the process out rather than failing (FINDINGS.md section 8). Both seams' shipping
            // implementations catch their own throws and always complete, so "never completes" means
            // the radio stack is wedged, and the honest report for that is a tray that goes on saying
            // "connecting music" - visible, diagnosable, and not a crash.
        }
        finally
        {
            if (_reconcilingSince == startedAt)
            {
                // Only if it is still ours. A pass that was given up on and has now finally answered
                // must not clear the marker of the one that replaced it - the same reason the halves
                // capture a generation before their own awaits.
                _reconcilingSince = null;
            }
        }
    }

    /// <summary>
    /// Everything a pass can correct, in one value, so that "did this tick change anything?" is one
    /// comparison rather than five conditionals that can each forget to report.
    /// </summary>
    private readonly record struct Drift(
        LinkState Link,
        MusicState Music,
        CallsState Calls,
        SuppressionReason Suppression);

    private Drift TakeDrift() => new(_linkMachine.State, _music.State, _calls.State, _latch.Reason);

    /// <summary>
    /// One line, and only when something moved.
    ///
    /// At 30 s an unconditional line is 2,880 entries a day, every one of them synchronous file I/O
    /// under a lock on the UI thread - and a log where nothing stands out is one nobody reads when
    /// the reconnect they are hunting finally fails.
    /// </summary>
    private void ReportDrift(Drift before, string trigger)
    {
        Drift after = TakeDrift();

        if (after != before)
        {
            Log.Info(
                $"Reconcile ({trigger}) corrected drift: link {before.Link}->{after.Link}, "
                + $"music {before.Music}->{after.Music}, calls {before.Calls}->{after.Calls}, "
                + $"suppression {before.Suppression}->{after.Suppression}.");
        }

        _coordinator.Publish();
    }
}
