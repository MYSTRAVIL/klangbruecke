using Klangbruecke.Audio;
using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke.Connection;

/// <summary>
/// One controller for the music half: open the A2DP sink connection, and keep a route running over
/// it for as long as there is an endpoint to route.
///
/// Its shape is the answer to finding #2 and finding #3, which are the same fact seen twice: <b>the
/// capture endpoint is not the connection</b>. The endpoint arrives some unbounded time after the
/// connection reports Opened - 5 of 8 recorded launches looked once, found nothing and silently
/// routed no audio for the whole session - and a cellular call takes the endpoint away again without
/// closing the connection, which is why the predecessor app needed the phone re-picked from the tray
/// after every call. So the two facts get one state each. <see cref="MusicState.Linked"/> is
/// "connection open, no endpoint", the ordinary condition whenever the phone is not streaming, and
/// it never times out; <see cref="MusicState.Up"/> is "and audio is routing". The two transitions
/// between them are the whole point of this class, and neither of them touches Bluetooth.
///
/// Two backoffs, because there are two independent things that fail. The connect backoff is for a
/// radio that will not open a connection. The route backoff exists because
/// <c>AudioRouter.Start</c> returns <c>true</c> for a capture that died inside
/// <c>StartRecording</c> - measured, not hypothetical - so a retry that trusted the return value
/// would produce a <c>Linked -> Up -> Stopped -> Linked</c> loop at event speed. The bool is
/// advisory here; <c>IsRunning</c> and the stopped event are the truth.
///
/// <b>Single-threaded, and it subscribes to nothing.</b> Every input is a method call that
/// <c>ConnectionManager</c> has already marshalled onto the UI thread through <c>IUiDispatcher</c>,
/// which is what lets a class with this much state in it hold no locks. The one asynchronous seam -
/// <see cref="IAudioSinkService.ConnectAsync"/> - is guarded by a generation counter rather than a
/// lock, because the thing that can happen across that await is not a data race but a stale answer:
/// the phone can leave the room while the radio is still deciding.
/// </summary>
public sealed class MusicHalf
{
    /// <summary>
    /// How long the route has to keep running before the route backoff is forgiven.
    ///
    /// Ten seconds because the failure it exists for is immediate: a capture that cannot capture
    /// dies within a buffer or two of starting, so anything that survives an order of magnitude
    /// longer than that was a working route that something external ended. Measured with
    /// <see cref="IScheduler.Now"/> rather than a timer - the question is only ever asked at the
    /// moment the run ends, and a timer would need cancelling on four separate exits.
    /// </summary>
    private static readonly TimeSpan HealthyRouteRun = TimeSpan.FromSeconds(10);

    private readonly IAudioSinkService _sink;
    private readonly IAudioRouter _router;
    private readonly IAudioEndpointMonitor _endpoints;
    private readonly IScheduler _scheduler;

    private readonly BackoffSchedule _connectBackoff = new();
    private readonly BackoffSchedule _routeBackoff = new();

    private bool _switchedOn;
    private string? _phoneDeviceId;
    private string? _outputDeviceId;

    private IDisposable? _connectRetry;
    private DateTimeOffset? _connectRetryDueAt;

    private IDisposable? _routeRetry;

    /// <summary>
    /// The earliest moment a route may be started again, or null for "whenever you like".
    ///
    /// The timer above is what wakes the half up; this is what stops anything else from walking past
    /// the wait in the meantime. Both are needed: MMDevAPI delivers duplicate notifications - one
    /// cause produced five callbacks in every recorded run - and every one of them is an invitation
    /// to start a route that just failed.
    /// </summary>
    private DateTimeOffset? _routeNotBefore;

    /// <summary>When the current route started. Only meaningful while <see cref="MusicState.Up"/>.</summary>
    private DateTimeOffset _routeRunningSince;

    /// <summary>
    /// Bumped by every teardown, and read - never written - by every connect.
    ///
    /// A connect captures it before it announces itself and compares it after its await, discarding
    /// its own answer if it no longer matches: the connection that answer describes is one nothing
    /// is holding any more, and reporting it would resurrect a half the user just disconnected - or
    /// claim <see cref="MusicState.Linked"/> while a newer attempt is still in flight. The second
    /// case needs no separate bump, because a second connect can only start from
    /// <see cref="MusicState.Off"/> or <see cref="MusicState.Backoff"/>, and the only route to
    /// either while a connect is in flight goes through <see cref="TearDown"/>.
    ///
    /// Captured <em>before</em> <see cref="SetState"/> announces <see cref="MusicState.Connecting"/>,
    /// not after. A <see cref="Changed"/> handler that tears the half down re-entrantly bumps this,
    /// and a capture taken afterwards would match the teardown's own value - the guard would pass
    /// and the connect would land in <c>Linked</c> over a sink that had just been disconnected.
    /// </summary>
    private int _generation;

    public MusicHalf(
        IAudioSinkService sink,
        IAudioRouter router,
        IAudioEndpointMonitor endpoints,
        IScheduler scheduler)
    {
        _sink = sink;
        _router = router;
        _endpoints = endpoints;
        _scheduler = scheduler;
    }

    /// <summary>Raised after any state change, on the calling thread. Once per actual change.</summary>
    public event EventHandler? Changed;

    public MusicState State { get; private set; } = MusicState.Off;

    /// <summary>
    /// The switch and a phone, together. A half with no phone picked has nothing to attempt, and
    /// reporting it as enabled would have the projection count it among the halves that are failing
    /// to deliver.
    /// </summary>
    public bool Enabled => _switchedOn && _phoneDeviceId is not null;

    /// <summary>
    /// How long until the next connect attempt, or null when none is scheduled.
    ///
    /// The connect backoff only. A route backing off does so from <see cref="MusicState.Linked"/>,
    /// which the projection reports as <c>Connected</c> - "waiting for phone audio" - so the number
    /// would have no reader; the one place the tray prints it is the <c>RetryBackoff</c> state, and
    /// that is reached only from <see cref="MusicState.Backoff"/>.
    ///
    /// Never negative. Timers do not run while the machine is suspended, so a retry can come due
    /// while nothing is there to fire it and the half can be found in <c>Backoff</c> minutes past
    /// its own deadline. "Overdue" is not something a countdown can say, and the tray would have to
    /// invent a reading for it.
    /// </summary>
    public TimeSpan? NextRetryIn
    {
        get
        {
            if (State != MusicState.Backoff || _connectRetryDueAt is not { } due)
            {
                return null;
            }

            TimeSpan remaining = due - _scheduler.Now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Has the scheduled connect retry come due? The gate on the reconcile's
    /// <see cref="MusicState.Backoff"/> backstop, so that a poll arriving mid-countdown does not
    /// become a second retry schedule.
    ///
    /// No recorded due time answers "yes". That is the null arm of the unwrap rather than a branch
    /// anyone can reach today - Backoff is only entered through <see cref="ScheduleConnectRetry"/>,
    /// which always records one - but it is the right default if it ever becomes reachable: nothing
    /// armed means nothing else is going to move the half, and the reconcile is all that is left.
    /// </summary>
    private bool ConnectRetryIsDue => _connectRetryDueAt is not { } due || _scheduler.Now >= due;

    /// <summary>
    /// The user's settings, as far as this half is concerned. Also the "phone deselected" input:
    /// switching music off, clearing the phone, or picking a different one are all the same event
    /// here - what this half was doing is no longer what was asked for.
    /// </summary>
    public void Configure(bool enabled, string? phoneDeviceId, string? outputDeviceId)
    {
        bool phoneChanged = phoneDeviceId != _phoneDeviceId;

        _switchedOn = enabled;
        _phoneDeviceId = phoneDeviceId;

        // Takes effect on the next route start rather than immediately. Restarting a working route
        // because a preference changed would interrupt music the user is listening to, and the
        // output the route is already using is the one they were listening on.
        _outputDeviceId = outputDeviceId;

        if (!Enabled || phoneChanged)
        {
            TearDown();
        }
    }

    /// <summary>
    /// The phone is in the room. Level-triggered: the reconcile poll says this every 30 s for as
    /// long as it is true, so everything except <see cref="MusicState.Off"/> ignores it - including
    /// <see cref="MusicState.Backoff"/>, whose whole purpose is to not attempt yet.
    /// </summary>
    public async Task OnLinkPresentAsync(bool connectPermitted)
    {
        if (State != MusicState.Off || !Enabled || !connectPermitted)
        {
            return;
        }

        await ConnectAsync();
    }

    /// <summary>The phone has left the room.</summary>
    public void OnLinkAbsent() => TearDown();

    /// <summary>A tray Disconnect, or auto-reconnect off after a drop. Same teardown, different cause.</summary>
    public void OnSuppressed() => TearDown();

    /// <summary>
    /// The connection object reported Closed.
    ///
    /// The route goes, and nothing else does. The manager owns the 3 s grace window that decides
    /// whether this was the phone leaving the room or only the audio profile being dropped, and it
    /// needs the half still holding what it had while that question is outstanding: reporting the
    /// half down immediately is a tray icon that flaps on every one-second dropout. Not a route
    /// failure either - we are the ones stopping it - so nothing backs off.
    /// </summary>
    public void OnConnectionClosed()
    {
        if (State is not (MusicState.Linked or MusicState.Up))
        {
            return;
        }

        _router.Stop();

        if (State == MusicState.Up)
        {
            LeaveUp(backOff: false);
        }
    }

    /// <summary>
    /// Something about the machine's audio endpoints changed - most likely nothing to do with us.
    /// Re-evaluates rather than acting on the notification, because most of them are about another
    /// endpoint entirely and MMDevAPI duplicates the ones that are not.
    /// </summary>
    public void OnEndpointsChanged()
    {
        // Nothing below Linked can act on the answer, and the answer is expensive: the real property
        // is a live full endpoint enumeration, measured at 152-282 ms on this machine, on the UI
        // thread. Read once, into a local, and only when it can change something.
        if (State is not (MusicState.Linked or MusicState.Up))
        {
            return;
        }

        bool present = _endpoints.SinkCaptureEndpointPresent;

        if (State == MusicState.Linked)
        {
            StartRouteIfDue(present);
        }
        else if (!present)
        {
            // Finding #3, arriving as an edge: a call has taken the capture endpoint. Stop the route
            // ourselves rather than waiting for it to notice, and do not back off - the endpoint
            // returning is what starts it again, and it should start immediately when it does.
            _router.Stop();
            LeaveUp(backOff: false);
        }
    }

    /// <summary>
    /// The route died on its own. <b>The single most important transition in this class:</b> it
    /// returns to <see cref="MusicState.Linked"/> and does not touch Bluetooth. A call invalidates
    /// the capture endpoint while the A2DP connection stays open, so reconnecting here would tear
    /// down a working link to fix something that was never broken - the predecessor app's defining
    /// bug.
    /// </summary>
    public void OnRouteStopped()
    {
        // Only from Up. The real router raises this from inside Start when the capture dies there,
        // and StartRouteIfDue has already seen IsRunning false and backed off for it; counting it
        // twice would double the wait on the first failure.
        if (State != MusicState.Up)
        {
            return;
        }

        LeaveUp(backOff: true);
    }

    /// <summary>
    /// The 30 s drift correction. Every read here is level-triggered, because the events that should
    /// have told us are exactly the ones that go missing across sleep and resume - an edge that never
    /// arrives is what leaves an app wrong forever.
    /// </summary>
    public Task ReconcileAsync(bool connectPermitted)
    {
        switch (State)
        {
            case MusicState.Up:
                bool present = _endpoints.SinkCaptureEndpointPresent;

                if (!present)
                {
                    _router.Stop();
                    LeaveUp(backOff: false);
                }
                else if (!_router.IsRunning)
                {
                    // A route that stopped without saying so. The stopped event is the fast path,
                    // not the only one.
                    LeaveUp(backOff: true);
                }

                return Task.CompletedTask;

            case MusicState.Linked:
                // Deliberately not gated on connectPermitted. Finishing what the user started is the
                // whole of the auto-reconnect-off carve-out, and without it finding #2 leaves those
                // users with a connection that never routes audio.
                StartRouteIfDue(_endpoints.SinkCaptureEndpointPresent);
                return Task.CompletedTask;

            case MusicState.Backoff:
                // The backstop for a retry that was never delivered - a suspended machine does not
                // run its timers, so the half can be found here long past its own deadline with
                // nothing armed that will ever fire. ConnectRetryIsDue is what keeps it a backstop
                // instead of a second retry schedule: this tick arrives every 30 s, so without the
                // gate every wait longer than that was unreachable and the sequence was really
                // 2/4/8/16/30/30/30 - while the tray went on showing a countdown that nothing was
                // waiting for. In the suspended case Now has jumped well past due, so the gate is
                // open exactly when it should be.
                //
                // Nothing else initiates from here. Being in Backoff already means a phone is picked
                // and the switch is on, so connect permission is the only other open question.
                return connectPermitted && ConnectRetryIsDue ? ConnectAsync() : Task.CompletedTask;

            default:
                return Task.CompletedTask;
        }
    }

    private async Task ConnectAsync()
    {
        string deviceId = _phoneDeviceId!;

        // Both captured before the state moves. SetState raises Changed, a handler is free to call
        // straight back in, and everything this method needs from the half must therefore already be
        // in hand - see the note on _generation for what a capture taken afterwards would let past.
        int generation = _generation;

        CancelConnectRetry();
        SetState(MusicState.Connecting);

        bool connected;

        try
        {
            connected = await _sink.ConnectAsync(deviceId);
        }
        catch (Exception ex)
        {
            // The shipping service catches its own throws and returns false, so this is a backstop
            // for the seam rather than for today's implementation. It matters because the
            // alternative is not a crash: an escaping throw would leave the half in Connecting with
            // no timer and no event that could ever move it again.
            Log.Error("The music half's connect attempt threw.", ex);
            connected = false;
        }

        if (generation != _generation)
        {
            // Torn down, or superseded by a newer attempt, while the radio was deciding. This answer
            // describes a connection nothing is holding.
            return;
        }

        if (!connected)
        {
            ScheduleConnectRetry();
            return;
        }

        _connectBackoff.Reset();
        SetState(MusicState.Linked);

        // Not Up. The endpoint may be minutes away - or may have been there all along, because its
        // lifetime is not this connection's: measured Active before this app connected at all, and
        // still Active after the app was killed (docs/FINDINGS.md section 4). An arrival that already
        // happened raises no notification, so asking now is the difference between routing audio and
        // waiting for the 30 s reconcile to notice.
        StartRouteIfDue(_endpoints.SinkCaptureEndpointPresent);
    }

    private void ScheduleConnectRetry()
    {
        CancelConnectRetry();

        // The current step is what this failure waits; advancing afterwards is what makes the first
        // wait 2 s rather than 4.
        TimeSpan delay = _connectBackoff.CurrentDelay;
        _connectBackoff.Advance();

        _connectRetryDueAt = _scheduler.Now + delay;

        // These two lines, in this order, and the order is the whole of it. SetState raises Changed;
        // a handler that tears the half down runs CancelConnectRetry, and with the handle not yet
        // assigned that cancellation finds nothing - leaving the arming line to hand a live timer to
        // a half that is already Off. It would fire a connect at a phone the user just disconnected,
        // or at a null device id if the teardown came from a deselect.
        _connectRetry = _scheduler.Schedule(delay, OnConnectRetryDue);
        SetState(MusicState.Backoff);
    }

    /// <summary>
    /// The connect backoff came due.
    ///
    /// Unguarded on purpose: reaching here means the half is still in <see cref="MusicState.Backoff"/>
    /// with a phone picked, because Backoff is left only through <see cref="ConnectAsync"/> and
    /// <see cref="TearDown"/> and both cancel this entry before they move. A "still in Backoff?"
    /// check would be a condition no input can make false.
    ///
    /// Fire and forget, because <see cref="IScheduler"/> hands out an <see cref="Action"/> and the
    /// connect is genuinely asynchronous. Safe only because <see cref="ConnectAsync"/> catches
    /// everything it awaits: there is no path out of it that faults a task nobody is holding.
    /// </summary>
    private void OnConnectRetryDue()
    {
        _connectRetry = null;
        _connectRetryDueAt = null;

        _ = ConnectAsync();
    }

    /// <summary>
    /// Starts the route if there is an endpoint to route and the route backoff has elapsed. The one
    /// place <see cref="IAudioRouter.Start"/> is called from, so no caller can add a route start and
    /// forget the wait.
    /// </summary>
    /// <remarks>
    /// Called only from <see cref="MusicState.Linked"/>, and it does not re-check that: all three
    /// call sites are either inside a Linked branch or have just entered Linked, so a guard here
    /// would be a condition no input can make false - dead code, which cannot be given an assertion.
    /// </remarks>
    private void StartRouteIfDue(bool endpointPresent)
    {
        if (!endpointPresent)
        {
            return;
        }

        if (_routeNotBefore is { } notBefore && _scheduler.Now < notBefore)
        {
            return;
        }

        _routeNotBefore = null;
        CancelRouteRetry();

        bool started = _router.Start(_outputDeviceId);

        // Both, and IsRunning is the one that decides. Start returns true for a capture that died
        // inside StartRecording - the capture thread dies asynchronously - and a half that believed
        // the bool would sit in Up over a route that is not running, waiting for a stopped event
        // that has already been raised.
        if (started && _router.IsRunning)
        {
            _routeRunningSince = _scheduler.Now;
            SetState(MusicState.Up);
            return;
        }

        ScheduleRouteRetry();
    }

    /// <summary>
    /// Leaves <see cref="MusicState.Up"/>, crediting a run that lasted long enough to call the route
    /// healthy.
    ///
    /// The credit is not conditional on how the run ended, because a route that played music for ten
    /// seconds and then lost its endpoint to a call has proved exactly what the reset is asking
    /// about. Charging the next failure for it would have an evening of calls back off to a minute
    /// between restarts.
    /// </summary>
    private void LeaveUp(bool backOff)
    {
        if (_scheduler.Now - _routeRunningSince >= HealthyRouteRun)
        {
            _routeBackoff.Reset();
        }

        if (backOff)
        {
            ScheduleRouteRetry();
        }

        SetState(MusicState.Linked);
    }

    private void ScheduleRouteRetry()
    {
        CancelRouteRetry();

        TimeSpan delay = _routeBackoff.CurrentDelay;
        _routeBackoff.Advance();

        _routeNotBefore = _scheduler.Now + delay;

        _routeRetry = _scheduler.Schedule(delay, () =>
        {
            _routeRetry = null;

            // The same re-evaluation a notification would have caused, and deliberately so: all that
            // has changed is that the wait is over, and whether there is anything to route is still
            // a question only the monitor can answer.
            OnEndpointsChanged();
        });
    }

    /// <summary>
    /// Everything off, back to <see cref="MusicState.Off"/>. The one exit that disconnects the sink.
    ///
    /// Every pending <em>wait</em> ends here; neither <em>schedule</em> is reset. The distinction is
    /// the point. How many times connecting has failed is worth remembering - the phone leaving the
    /// room is not evidence that the next attempt will work, and this runs on every range exit, so a
    /// schedule that reset here would never get past its first step for a phone that flaps. But a
    /// countdown belongs to the episode that started it, and that episode is over: a route gate left
    /// standing would meet the next connection and refuse to start audio over it for up to a minute,
    /// with the endpoint sitting right there and nothing armed to look again until the 30 s
    /// reconcile - finding #2's symptom, rebuilt out of parts that are each individually correct.
    /// </summary>
    private void TearDown()
    {
        // Before the state moves, so an answer already in flight is discarded rather than landing in
        // a half that has been shut down.
        _generation++;

        CancelConnectRetry();
        CancelRouteRetry();
        _routeNotBefore = null;

        if (State != MusicState.Off)
        {
            // Guarded, because Off is also where the app starts. Picking a phone for the first time
            // would otherwise disconnect a sink that was never connected and stop a route that was
            // never started - calls that do nothing except make the log describe events that did not
            // happen.
            _router.Stop();
            _sink.Disconnect();
        }

        SetState(MusicState.Off);
    }

    private void CancelConnectRetry()
    {
        _connectRetry?.Dispose();
        _connectRetry = null;
        _connectRetryDueAt = null;
    }

    private void CancelRouteRetry()
    {
        _routeRetry?.Dispose();
        _routeRetry = null;
    }

    /// <summary>
    /// The single place the state moves and the single place <see cref="Changed"/> is raised, so a
    /// subscriber that redraws the tray cannot be woken by a transition that did not happen.
    ///
    /// The event goes last within each transition, and "last" has to mean more than it sounds like.
    /// A handler does not only read: <see cref="Changed"/> fires on the calling thread, so a handler
    /// is free to call straight back into this half - the tray's Disconnect item is one keystroke
    /// from doing exactly that. So a caller of this method must finish its bookkeeping <em>before</em>
    /// the call, not after: anything it captures must be captured, and any timer it arms must be
    /// armed and reachable for cancellation. Two callers get that wrong by one line if they are
    /// rearranged - see <see cref="ConnectAsync"/> and <see cref="ScheduleConnectRetry"/>.
    /// </summary>
    private void SetState(MusicState next)
    {
        if (State == next)
        {
            return;
        }

        State = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
