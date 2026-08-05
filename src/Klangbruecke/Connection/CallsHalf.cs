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
/// with this much state in it hold no locks. The one asynchronous seam - enumerate, then register -
/// is guarded by a generation counter rather than a lock, because the thing that can happen across
/// those awaits is not a data race but a stale answer: the user can switch calls off while the radio
/// is still deciding.
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

    /// <summary>Unregisters. Deliberate intent only.</summary>
    public void OnPhoneDeselected() => Unregister();

    /// <summary>Unregisters. Deliberate intent only.</summary>
    public void OnDisabled() => Unregister();

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
                // run its timers. Enabled is asked as well as connectPermitted because Configure can
                // switch the half off without moving it out of Backoff.
                return connectPermitted && Enabled ? RegisterAsync() : Task.CompletedTask;

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
    /// </summary>
    private void ReconcileRegistration()
    {
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
    /// called from.
    /// </summary>
    private async Task RegisterAsync()
    {
        // Both captured before the state moves. SetState raises Changed, a handler is free to call
        // straight back in, and everything this method needs from the half must therefore already be
        // in hand - see the note on _generation for what a capture taken afterwards would let past.
        string? phoneDeviceId = _phoneDeviceId;
        int generation = _generation;

        CancelRetry();
        SetState(CallsState.Registering);

        bool registered;

        try
        {
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
            Log.Write(TransportMatcher.LevelFor(match.Outcome), match.Reason);

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

        if (generation != _generation)
        {
            return;
        }

        if (!registered)
        {
            ScheduleRetry();
            return;
        }

        _backoff.Reset();
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
    /// registration is genuinely asynchronous. Safe only because <see cref="RegisterAsync"/> catches
    /// everything it awaits: there is no path out of it that faults a task nobody is holding.
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
    /// The backoff schedule is deliberately not reset. How many times registering has failed is worth
    /// remembering across a switch flipped off and on again; the countdown belongs to the episode
    /// that started it and ends with it, which is what <see cref="CancelRetry"/> does.
    /// </summary>
    private void Unregister()
    {
        // Before anything else, so an answer already in flight is discarded rather than landing in a
        // half whose role has been released.
        _generation++;

        CancelRetry();
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
