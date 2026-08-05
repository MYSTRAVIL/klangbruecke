using Klangbruecke.Diagnostics;
using Microsoft.Win32;

namespace Klangbruecke.Platform;

/// <summary>
/// <see cref="SystemEvents.PowerModeChanged"/>, filtered down to the one transition this app cares
/// about.
///
/// It does one thing: raise <see cref="Resumed"/> for <see cref="PowerModes.Resume"/> and for nothing
/// else. It does not wait, does not reconcile and does not know what a Bluetooth link is - the 5 s
/// settle before the forced reconcile lives in <c>ConnectionManager</c>, on <c>IScheduler</c>, where
/// it can be tested on a hand-cranked clock. See <see cref="IPowerNotifier"/>.
///
/// <b>Nothing here marshals.</b> <c>SystemEvents</c> raises on its own dedicated window thread - not
/// the UI thread and not the managed threadpool - and <see cref="OnPowerModeChanged"/> re-raises on
/// that same thread. <c>ConnectionManager</c> posts every inbound event through <c>IUiDispatcher</c>
/// before touching state, which is what makes it single-threaded by contract. A second marshalling
/// layer here would put an extra hop in front of every wake and buy nothing. <c>LinkMonitor</c> says
/// the same thing about its watcher callbacks, for the same reason.
///
/// <b>The subscription is static, and that is the whole hazard.</b>
/// <c>SystemEvents.PowerModeChanged</c> holds the handler in a plain field - not a weak reference -
/// so it roots this object for the life of the process. An instance that is dropped without
/// <see cref="Dispose"/> is not collected and keeps being handed resumes long after the app believes
/// it is gone, firing into whatever it is still wired to. Everything below - the
/// <see cref="_subscribed"/> flag, the refusal to start after disposal - exists to make sure exactly
/// one subscription is taken and exactly one is given back.
/// </summary>
public sealed class PowerNotifier : IPowerNotifier
{
    // Not just tidiness on either side.
    //
    // SystemEvents.RemoveEventHandler removes one entry per call, so a second Start would take a
    // subscription that a single Dispose could not give back - a permanent leak, and two Resumed per
    // wake in the meantime, which downstream becomes two settles and two forced reconciles.
    //
    // Neither field is volatile, unlike LinkMonitor's. Both are written only by the thread that calls
    // Start/Dispose - the UI thread - and neither is read on the SystemEvents thread: the callback
    // does not consult them. See the note in Dispose on what that deliberately does not close.
    private bool _subscribed;
    private bool _disposed;

    public event EventHandler? Resumed;

    public void Start()
    {
        // Refused rather than tolerated, and this is the one method here that refuses anything.
        // Dispose's idempotence guard returns early on a second call, so a subscription taken after
        // the first Dispose would never be removed - and unlike a DeviceWatcher, this one is rooted by
        // a static event, so nothing can ever collect it. Nothing downstream can detect that, and the
        // caller doing it has a real defect worth surfacing. Same reasoning as LinkMonitor.Watch.
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_subscribed)
        {
            return;
        }

        _subscribed = true;

        // First touch of SystemEvents in a process starts its hidden message window on a dedicated
        // background thread. That is the point - a WM_POWERBROADCAST has to land somewhere - and it
        // is why this is behind Start rather than the constructor: constructing a notifier should not
        // spin up an OS message pump.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>
    /// The notification, filtered. Everything that is not a resume is dropped here.
    ///
    /// Public, and the seam this class is tested through - the event it really listens to is static
    /// and is raised only by an actual machine suspend, which a suite that runs in two seconds cannot
    /// arrange. <c>PowerModeChangedEventArgs</c> has a public constructor, so all three
    /// <see cref="PowerModes"/> values can be delivered by hand. Same precedent as
    /// <c>LinkMonitor.OnCandidateAdded</c>: call it only as a test.
    /// </summary>
    public void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Resume only. Suspend is the actively wrong one - acting on it would schedule a settle and a
        // reconcile against a machine on its way down - and StatusChange is the noisy one, fired on
        // every AC/battery transition, which on a laptop is often and means nothing here.
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        // Logged here rather than left to the subscriber, for the same reason LinkMonitor logs its
        // edges: reconnect-after-resume is one of the two paths CLAUDE.md names as historically
        // fragile, and the log is the only instrument for it after the fact. A log that records only
        // what the state machine concluded cannot tell "the resume never arrived" from "it arrived and
        // nothing acted on it" - two different bugs, in two different components.
        Log.Info("The machine resumed from sleep.");

        // Raised on the SystemEvents thread. ConnectionManager posts through IUiDispatcher; do not add
        // a second marshalling layer here.
        //
        // `this` as the sender, not whatever SystemEvents passed. Subscribers identify the source they
        // registered with, and a consumer holding more than one seam would otherwise have no way to
        // tell which one spoke.
        Resumed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_subscribed)
        {
            _subscribed = false;

            // The one line that matters in this class. Without it the handler stays in the static
            // list forever: the object is never collected and every later resume is still delivered
            // to it.
            //
            // What this deliberately does not close: SystemEvents raises on its own thread, so a
            // notification can already be in flight when this runs, and no check-then-act here could
            // prevent that - the check would race the raise just as this does. A late Resumed is
            // handled where it can be: ConnectionManager posts it and its own teardown decides.
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }
}
