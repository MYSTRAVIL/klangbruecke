using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke.Connection;

/// <summary>
/// One controller for the calls half: claim the Bluetooth HFP hands-free role on the selected
/// phone's phone-line transport, and go on holding it.
///
/// <b>Registration is not link-scoped, and that is the whole shape of this class.</b> Holding the
/// role is what puts this PC in the phone's own call-audio picker; it is what makes the phone offer
/// the PC when it comes back into range, so it has to survive the phone leaving. That is why there
/// is deliberately no <c>OnLinkAbsent</c> here where <see cref="MusicHalf"/> has one, and why the
/// two methods that do release the role are named for intent rather than for events -
/// <see cref="OnPhoneDeselected"/> and <see cref="OnDisabled"/>. Every unregister/re-register round
/// trip makes the PC disappear and reappear in the handset's settings, so the cost of releasing it
/// speculatively is paid on a screen this app cannot see.
///
/// Grading is on <c>Registered</c> and never on <c>TransportConnected</c>.
/// <c>PhoneLineTransportDevice.ConnectAsync</c> returns False on this machine on every run,
/// including runs where a real cellular call routed audio in both directions (docs/FINDINGS.md §12).
/// The rule itself lives in <see cref="CallTransportResult.Claimed"/>; this class's part of it is to
/// read one field of the result and ignore the other.
///
/// <b>Single-threaded, and it subscribes to nothing.</b> Every input is a method call that
/// <c>ConnectionManager</c> has already marshalled onto the UI thread, which is what lets a class
/// with this much state in it hold no locks.
///
/// The one asynchronous seam - enumerate, then register - needs two guards, and they are not the
/// same guard. <see cref="_generation"/> discards a stale <em>answer</em>: the user can switch calls
/// off while the radio is still deciding, and the answer then describes a role nothing is holding.
/// <see cref="_inFlight"/> prevents a second <em>call</em>: a teardown sets
/// <see cref="CallsState.Off"/> while a registration is still awaiting, and the next level-triggered
/// link-present report would otherwise start a second <c>ConnectAsync</c> body over the same device.
/// Neither substitutes for the other - the generation counter has never guarded the call, only what
/// came back from it.
/// </summary>
public sealed class CallsHalf
{
    private readonly ICallTransportService _calls;
    private readonly IScheduler _scheduler;

    private readonly BackoffSchedule _backoff = new();

    private bool _switchedOn;
    private string? _phoneDeviceId;

    private IDisposable? _retry;
    private DateTimeOffset? _retryDueAt;

    /// <summary>
    /// True from before a registration announces itself until its awaits have finished, whatever
    /// they finished with.
    ///
    /// The state machine cannot promise this on its own. <see cref="OnLinkPresentAsync"/> starts only
    /// from <see cref="CallsState.Off"/> and <see cref="RegisterAsync"/> leaves Off before its first
    /// await, so the states alone look sufficient - but a teardown puts the half back in Off while a
    /// registration is still awaiting, and the next level-triggered report then walks straight in. On
    /// the real service that is two <c>ConnectAsync</c> bodies sharing one device field across
    /// awaits: the second begins by unregistering, which can release the role the first is about to
    /// claim, and the first's continuation then registers through the second's device. That is the
    /// unregister/re-register flap this whole class is built to avoid, arriving from the inside.
    ///
    /// The cost, stated plainly: a service call that never completes now also blocks new attempts
    /// rather than only stranding the state. That is the intended trade - the alternative to blocking
    /// is the overlap above - and a registration that never returns is a reconcile-side timeout
    /// question, which belongs to the manager and not here.
    ///
    /// The manager has since answered it, and the answer is no: see <c>ConnectionManager</c>'s
    /// reconcile, which explains why a teardown underneath a call that has not returned is a worse
    /// outcome than a half that goes on reporting Registering.
    /// </summary>
    private bool _inFlight;

    /// <summary>
    /// The phone the role is held on. Only meaningful while <see cref="CallsState.Up"/>.
    ///
    /// Without it the half can only ask "is a role held", never "on which phone", and a user who
    /// picks a different phone while it is Up leaves it satisfied forever with the role sitting on
    /// the handset they stopped using. Left stale on the way out rather than cleared at three
    /// separate exits: every entry to Up rewrites it and nothing outside Up reads it.
    /// </summary>
    private string? _registeredPhoneId;

    /// <summary>
    /// The last match reason written to the log, so an unchanging one is written once.
    ///
    /// A pairing that is permanently <c>Ambiguous</c> or <c>SoleCandidate</c> produces the same Warn
    /// on every attempt, and the attempts do not stop - that is a line every 60 seconds, forever,
    /// against the project's rule that only a change gets logged. Never cleared: a reason that
    /// changes is news, and after a success the next failure's reason differs from the success's, so
    /// the recovery-then-relapse case reports itself without a reset.
    /// </summary>
    private string? _lastMatchReason;

    /// <summary>
    /// Bumped by every release of the role, and read - never written - by every registration.
    ///
    /// A registration captures it before it announces itself and compares it after each await,
    /// discarding its own answer if it no longer matches. Captured <em>before</em>
    /// <see cref="SetState"/> announces <see cref="CallsState.Registering"/>, not after: a
    /// <see cref="Changed"/> handler that shuts the half down re-entrantly bumps this, and a capture
    /// taken afterwards would match the teardown's own value - the guard would pass, and the half
    /// would report <see cref="CallsState.Up"/> over a role the user had just released.
    /// </summary>
    private int _generation;

    public CallsHalf(ICallTransportService calls, IScheduler scheduler)
    {
        _calls = calls;
        _scheduler = scheduler;
    }

    /// <summary>Raised after any state change, on the calling thread. Once per actual change.</summary>
    public event EventHandler? Changed;

    public CallsState State { get; private set; } = CallsState.Off;

    /// <summary>
    /// The switch and a phone, together. A half with no phone picked has nothing to attempt, and
    /// reporting it as enabled would have the projection count it among the halves that are failing
    /// to deliver.
    /// </summary>
    public bool Enabled => _switchedOn && _phoneDeviceId is not null;

    /// <summary>
    /// How long until the next registration attempt, or null when none is scheduled.
    ///
    /// Never negative. Timers do not run while the machine is suspended, so a retry can come due with
    /// nothing there to fire it and the half can be found in <see cref="CallsState.Backoff"/> minutes
    /// past its own deadline. "Overdue" is not something a countdown can say, and the tray would have
    /// to invent a reading for it.
    /// </summary>
    public TimeSpan? NextRetryIn
    {
        get
        {
            if (State != CallsState.Backoff || _retryDueAt is not { } due)
            {
                return null;
            }

            TimeSpan remaining = due - _scheduler.Now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Has the scheduled retry come due? The gate on the reconcile's <see cref="CallsState.Backoff"/>
    /// backstop, so that a poll arriving mid-countdown does not become a second retry schedule.
    ///
    /// No recorded due time answers "yes". That is the null arm of the unwrap rather than a branch
    /// anyone can reach today - Backoff is only entered through <see cref="ScheduleRetry"/>, which
    /// always records one - but it is the right default if it ever becomes reachable: nothing armed
    /// means nothing else is going to move the half, and the reconcile is all that is left.
    /// </summary>
    private bool RetryIsDue => _retryDueAt is not { } due || _scheduler.Now >= due;

    /// <summary>
    /// The user's settings, as far as this half is concerned - and nothing else. It records; it never
    /// registers and never unregisters.
    ///
    /// The deliberate opposite of <see cref="MusicHalf.Configure"/>, which tears down. Settings
    /// arrive here for reasons that have nothing to do with calls, and the two mistakes are not the
    /// same size: an A2DP connection dropped in error costs a reconnect the app can do by itself,
    /// while a hands-free role dropped in error costs the user the PC's entry in their phone's
    /// call-audio picker and cannot be put back without a round trip that flaps it again. So
    /// releasing the role is reserved for <see cref="OnPhoneDeselected"/> and
    /// <see cref="OnDisabled"/>, the two inputs that mean the user decided - and the manager calls
    /// one of them when a settings change really is a decision rather than a re-push.
    ///
    /// What it does change is what the half attempts <em>next</em>: <see cref="Enabled"/> and the
    /// phone id are read at the moment of each attempt, so a half switched off here starts nothing
    /// more, and one pointed at a different phone registers that one.
    /// </summary>
    public void Configure(bool enabled, string? phoneDeviceId)
    {
        _switchedOn = enabled;
        _phoneDeviceId = phoneDeviceId;
    }

    /// <summary>
    /// The phone is in the room. Level-triggered: the reconcile poll says this every 30 s for as long
    /// as it is true, so everything except <see cref="CallsState.Off"/> ignores it - including
    /// <see cref="CallsState.Backoff"/>, whose whole purpose is to not attempt yet.
    ///
    /// Registration does not need the phone present to survive, but it does need it present to be
    /// claimed, which is why this is the input that starts one.
    /// </summary>
    public Task OnLinkPresentAsync(bool connectPermitted)
    {
        if (State != CallsState.Off || !Enabled || !connectPermitted)
        {
            return Task.CompletedTask;
        }

        return RegisterAsync();
    }

    /// <summary>
    /// Unregisters. Deliberate intent only.
    ///
    /// Resets the backoff, unlike <see cref="OnDisabled"/>: a different phone is a different failure
    /// history, and making the newly-picked phone serve out the old one's 60 s penalty punishes the
    /// user for the action they took precisely because the first phone was not working.
    /// </summary>
    public void OnPhoneDeselected() => Unregister(resetBackoff: true);

    /// <summary>
    /// Unregisters. Deliberate intent only.
    ///
    /// Keeps the backoff, unlike <see cref="OnPhoneDeselected"/>: the same phone and the same radio
    /// are still there afterwards, and flipping a switch off and on again has repaired nothing.
    /// Forgetting an hour of failures here is how a permanently broken pairing gets retried every two
    /// seconds for as long as somebody keeps toggling.
    /// </summary>
    public void OnDisabled() => Unregister(resetBackoff: false);

    /// <summary>
    /// The 30 s drift correction. Level-triggered, because the events that should have told us are
    /// exactly the ones that go missing across sleep and resume - an edge that never arrives is what
    /// leaves an app wrong forever.
    /// </summary>
    public Task ReconcileAsync(bool connectPermitted)
    {
        switch (State)
        {
            case CallsState.Up:
                ReconcileRegistration();
                return Task.CompletedTask;

            case CallsState.Backoff:
                // The backstop for a retry that was never delivered - a suspended machine does not
                // run its timers, so the half can be found here minutes past its own deadline with
                // nothing armed that will ever fire. RetryIsDue is what keeps it a backstop instead
                // of a second retry schedule: this tick arrives every 30 s, so without the gate every
                // wait longer than that was unreachable and the sequence was really 2/4/8/16/30/30/30
                // - with the tray showing a countdown that nothing was waiting for. In the suspended
                // case Now has jumped well past due, so the gate is open exactly when it should be.
                //
                // Enabled is asked as well as connectPermitted because Configure can switch the half
                // off without moving it out of Backoff.
                return connectPermitted && Enabled && RetryIsDue ? RegisterAsync() : Task.CompletedTask;

            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Is the role still held? Asked only from <see cref="CallsState.Up"/>, where it is the one
    /// question worth asking and the only state that can act on the answer.
    ///
    /// <b>Only <see cref="RegistrationStatus.NotRegistered"/> counts as lost, and this is deliberately
    /// the opposite of <c>LinkMachine.OnLinkStatusRead</c>, where a failed read counts as
    /// disconnected.</b> Both are right, for asymmetric reasons. For the link, guessing pessimistically
    /// means "keep looking for the phone rather than going dormant", and the cost of being wrong is a
    /// rediscovery nobody sees. For registration, guessing pessimistically means unregister and
    /// register again, and the cost of being wrong is the PC vanishing from and reappearing in the
    /// phone's call-audio-device list - the exact harm the absent <c>OnLinkAbsent</c> exists to
    /// prevent. So <see cref="RegistrationStatus.Unknown"/> is treated as what it is, no information:
    /// nothing moves, nothing is logged, and the next tick asks again.
    ///
    /// There are two kinds of drift, and the wrong phone is checked first because it needs no ABI
    /// call to detect and no answer from one could change it.
    /// </summary>
    private void ReconcileRegistration()
    {
        if (!string.Equals(_registeredPhoneId, _phoneDeviceId, StringComparison.Ordinal))
        {
            // The user picked a different phone and only the settings heard about it. This is the one
            // release that is not <see cref="OnPhoneDeselected"/>'s, and it is not the flap the
            // missing OnLinkAbsent guards against: that rule is about releasing a role nobody asked
            // to release, and this is the user asking. Left alone, the role sits on a handset they
            // have stopped using and goes on offering this PC there.
            Log.Warn("The hands-free role is held on a phone that is no longer the selected one. Moving it.");

            // Explicitly, unlike the branch below: here the role really is still held, and the new
            // phone cannot be registered while the old registration stands.
            _calls.Disconnect();

            // Through the backoff rather than straight into a registration, so that a settings change
            // that keeps failing cannot spin at reconcile speed. The schedule was reset on the way
            // into Up, so this waits 2 s.
            ScheduleRetry();
            return;
        }

        if (_calls.ReadRegistration() != RegistrationStatus.NotRegistered)
        {
            return;
        }

        // No Disconnect first. The role is already gone - that is what this branch just measured -
        // so releasing it would be the flap with none of the benefit.
        Log.Warn("The hands-free role is no longer held. Registering again.");
        ScheduleRetry();
    }

    /// <summary>
    /// Enumerate, correlate, claim. The one place <see cref="ICallTransportService.ConnectAsync"/> is
    /// called from - which is also why the <see cref="_inFlight"/> check lives here rather than in
    /// the three inputs that lead to it. Put at the door, no input can be added that starts a second
    /// registration and forgets to ask; put in the callers, the two arms would agree on every input
    /// and neither could be broken without the other covering for it.
    /// </summary>
    private async Task RegisterAsync()
    {
        if (_inFlight)
        {
            // In practice only OnLinkPresentAsync reaches this, because Off is the only state a
            // teardown can leave a flight running in and the only state that input starts from. It is
            // still checked for all three: what makes the overlap unsafe is the service, not the
            // caller.
            return;
        }

        // Both captured before the state moves. SetState raises Changed, a handler is free to call
        // straight back in, and everything this method needs from the half must therefore already be
        // in hand - see the note on _generation for what a capture taken afterwards would let past.
        string? phoneDeviceId = _phoneDeviceId;
        int generation = _generation;

        bool registered;

        try
        {
            // Inside the try, all three of them, and this is the whole of what the finally below is
            // worth. SetState raises Changed on the calling thread; a subscriber that throws would
            // propagate out of this method without ever entering a try placed after these lines, and
            // the flag would be wedged true for the life of the process. Neither OnDisabled nor
            // OnPhoneDeselected clears it - they reset the state and bump the generation - so every
            // later attempt would return at the door, silently, with the tray reporting Off and calls
            // never registering again.
            _inFlight = true;

            CancelRetry();
            SetState(CallsState.Registering);

            IReadOnlyList<TransportCandidate> candidates = await _calls.FindTransportsAsync();

            if (generation != _generation)
            {
                // Released, or superseded, while the enumeration ran. Registering now would claim a
                // role nothing is holding on behalf of a half that has been shut down.
                return;
            }

            TransportMatchResult match = TransportMatcher.Match(candidates, phoneDeviceId);

            // The one line that says why. "Ambiguous" and "NoCandidates" are two different facts
            // about the user's pairing and both arrive here as a half that quietly backs off; the
            // level follows the outcome, which is what TransportMatchOutcome's own summary promises.
            //
            // Once per distinct reason. The attempts do not stop, so a pairing that is permanently
            // ambiguous would otherwise write the identical warning for as long as the app runs.
            if (!string.Equals(match.Reason, _lastMatchReason, StringComparison.Ordinal))
            {
                _lastMatchReason = match.Reason;
                Log.Write(TransportMatcher.LevelFor(match.Outcome), match.Reason);
            }

            if (match.Match is not { } transport)
            {
                // NoCandidates or Ambiguous. The matcher connects nothing when it cannot tell two
                // phones apart, and second-guessing it here is the wrong-phone bug it exists to
                // prevent: a hands-free role on someone else's handset looks like a working app until
                // their phone rings through this PC.
                ScheduleRetry();
                return;
            }

            CallTransportResult result = await _calls.ConnectAsync(transport.Id);

            // Registered, and only Registered. result.TransportConnected is False on every run on
            // this machine including the ones where calls demonstrably worked - see
            // CallTransportResult.Claimed, which owns the rule, and docs/FINDINGS.md §12.
            registered = result.Registered;
        }
        catch (Exception ex)
        {
            // The shipping service catches its own throws and returns a result, so this is a backstop
            // for the seam rather than for today's implementation. It matters because the alternative
            // is not a crash: an escaping throw would leave the half in Registering with no timer and
            // no event that could ever move it again.
            Log.Error("The calls half's registration attempt threw.", ex);
            registered = false;
        }
        finally
        {
            // In a finally, not on the way out: a throw the catch above did not expect - one raised
            // by the catch's own logging, say - would otherwise wedge the flag true and leave the
            // half unable to register again for the life of the process. The catch does not cover a
            // throwing Changed subscriber either, because SetState runs before the first await and
            // the catch is entered by the awaits' failures; the finally covers both.
            _inFlight = false;
        }

        if (generation != _generation)
        {
            return;
        }

        if (!registered)
        {
            ScheduleRetry();
            return;
        }

        // Both before the announce. A Changed handler that reconciles re-entrantly would otherwise
        // find Up with no phone recorded against it and read that as the wrong phone.
        _backoff.Reset();
        _registeredPhoneId = phoneDeviceId;

        SetState(CallsState.Up);
    }

    private void ScheduleRetry()
    {
        CancelRetry();

        // The current step is what this failure waits; advancing afterwards is what makes the first
        // wait 2 s rather than 4.
        TimeSpan delay = _backoff.CurrentDelay;
        _backoff.Advance();

        _retryDueAt = _scheduler.Now + delay;

        // These two lines, in this order, and the order is the whole of it. SetState raises Changed;
        // a handler that shuts the half down runs CancelRetry, and with the handle not yet assigned
        // that cancellation finds nothing - leaving the arming line to hand a live timer to a half
        // that is already Off. It would register a phone the user just disconnected.
        _retry = _scheduler.Schedule(delay, OnRetryDue);
        SetState(CallsState.Backoff);
    }

    /// <summary>
    /// The backoff came due.
    ///
    /// Reaching here means the half is still in <see cref="CallsState.Backoff"/>, because Backoff is
    /// left only through <see cref="RegisterAsync"/> and <see cref="Unregister"/> and both cancel
    /// this entry before they move. What it does <em>not</em> mean is that the half is still wanted:
    /// <see cref="Configure"/> can switch it off without moving it, so the settings are re-read here.
    /// Standing down releases nothing, because Backoff is only ever reached with the role unheld.
    ///
    /// Fire and forget, because <see cref="IScheduler"/> hands out an <see cref="Action"/> and the
    /// registration is genuinely asynchronous. Safe for the service seam, which is the part that
    /// actually fails: <see cref="RegisterAsync"/> catches everything it <em>awaits</em>. It is not a
    /// blanket guarantee, and saying so would be worse than saying nothing - <see cref="SetState"/>
    /// and <see cref="ScheduleRetry"/> run outside that try, so a <see cref="Changed"/> subscriber
    /// that throws faults a task nobody is holding and the throw surfaces at
    /// <c>TaskScheduler.UnobservedTaskException</c> instead of at a caller. Deliberately not wrapped:
    /// a subscriber that throws is a defect in the subscriber, and a catch here would be one nothing
    /// can reach through the public surface - the untestable swallow this project forbids.
    /// </summary>
    private void OnRetryDue()
    {
        _retry = null;
        _retryDueAt = null;

        if (!Enabled)
        {
            SetState(CallsState.Off);
            return;
        }

        _ = RegisterAsync();
    }

    /// <summary>
    /// Let the role go, and stop trying to get it back. The one exit that calls
    /// <see cref="ICallTransportService.Disconnect"/>.
    ///
    /// Unconditional, with no "were we up?" guard, which is where this differs from
    /// <c>MusicHalf.TearDown</c>. This class's own belief about whether the role is held is the one
    /// thing that can be stale - a registration whose answer was discarded mid-flight leaves the
    /// service holding a role this half never recorded - and the service already checks whether there
    /// is anything to release before releasing it. Skipping the call to avoid a no-op would be
    /// precisely the case where it was not one.
    ///
    /// The countdown always ends here - it belongs to the episode that started it - but the
    /// <em>schedule</em> depends on which input called, and the two are not the same question. See
    /// <see cref="OnPhoneDeselected"/> and <see cref="OnDisabled"/>, which take opposite answers for
    /// reasons that are opposite too: one changes the phone, the other does not.
    /// </summary>
    private void Unregister(bool resetBackoff)
    {
        // Before anything else, so an answer already in flight is discarded rather than landing in a
        // half whose role has been released.
        _generation++;

        CancelRetry();

        if (resetBackoff)
        {
            _backoff.Reset();
        }

        _calls.Disconnect();

        SetState(CallsState.Off);
    }

    private void CancelRetry()
    {
        _retry?.Dispose();
        _retry = null;
        _retryDueAt = null;
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
    /// rearranged - see <see cref="RegisterAsync"/> and <see cref="ScheduleRetry"/>.
    /// </summary>
    private void SetState(CallsState next)
    {
        if (State == next)
        {
            return;
        }

        State = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
