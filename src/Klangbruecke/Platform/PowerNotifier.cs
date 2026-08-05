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
/// <b>Nothing here marshals, because <c>SystemEvents</c> already did.</b> An earlier draft of this
/// file claimed the handler arrives on a dedicated <c>SystemEvents</c> thread, never the UI thread.
/// That is false, so the correction is stated with the measurement behind it.
///
/// <c>SystemEvents</c> does own a window thread, and that is where a notification <em>originates</em> -
/// but it is not necessarily where the handler runs. Each subscription captures the
/// <see cref="System.Threading.SynchronizationContext"/> current on the thread that called
/// <see cref="Start"/> - kept in the internal <c>SystemEventInvokeInfo._syncContext</c> - and
/// dispatches through <c>SynchronizationContext.Send</c>. Whether that is a hop or not depends
/// entirely on what was captured, and bullet three below is the case where it is not one. Measured on
/// this machine, .NET 8 / SDK 8.0.417:
///
/// <list type="bullet">
/// <item>Subscribing under a custom context and reading the private field back: the captured context
/// <b>is</b> the one that was current at <c>+=</c> time.</item>
/// <item>Driving the internal <c>Invoke</c> from a background thread: <b>one <c>Send</c>, zero
/// <c>Post</c></b>. The dispatch blocks the raising thread until the handler returns, so under a
/// WinForms context this is a blocking <c>Control.Invoke</c> and a slow <see cref="Resumed"/> handler
/// stalls the OS notification thread. Keep this path short.</item>
/// <item>Subscribing with no ambient context: a plain <c>SynchronizationContext</c> is captured, whose
/// <c>Send</c> runs inline - i.e. on the <c>SystemEvents</c> window thread.</item>
/// </list>
///
/// So the delivery thread is <b>whichever context was current when <see cref="Start"/> ran</b>, and
/// since <see cref="Start"/> is deliberately not called from the constructor, that depends on where
/// startup wires it - specifically, on whether the WinForms context has been installed by then.
/// <b>Not on whether <c>Application.Run</c> has been entered</b>; an earlier version of this sentence
/// said exactly that, and it was false for this app. The context is installed by the first
/// <c>Control</c> constructed, which in <c>Program.Main</c> is <c>ControlUiDispatcher</c>'s
/// marshalling control, and <c>ConnectionManager.Start</c> is reached from <c>TrayContext</c>'s
/// constructor - both of them before <c>Application.Run</c>, and resumes land on the UI thread
/// regardless. See <c>UiDispatcherTests.Control_InstallsTheWinFormsSynchronizationContextOnTheThreadThatBuildsIt</c>.
/// <see cref="OnPowerModeChanged"/> re-raises on whatever thread it was entered on and
/// adds nothing. <c>ConnectionManager</c> posts every inbound event through <c>IUiDispatcher</c>
/// before touching state, which is what makes it single-threaded by contract either way - so a second
/// marshalling layer here would add a hop and buy nothing. <c>LinkMonitor</c> reaches the same
/// instruction by the opposite route: nothing has marshalled for it at all.
///
/// <see cref="Start"/> and <see cref="Dispose"/> must be called on the same thread. See
/// <see cref="IPowerNotifier"/>, and the field comment below for what that buys.
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
    // Neither field is volatile, unlike LinkMonitor's, and that rests entirely on the same-thread
    // caller contract stated on IPowerNotifier: Start and Dispose are called on one thread, and
    // OnPowerModeChanged never consults either field, so there is no cross-thread read to publish.
    //
    // If that contract is ever broken the failure is not a torn read, it is a missed unsubscribe - and
    // because the subscription is static and strongly rooted, a missed unsubscribe leaks for the life
    // of the process. Whoever wants to tear down off-thread must make these volatile (or lock) first.
    // See the note in Dispose for the one race this deliberately does not close even so.
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

        // Raised on whatever thread this method was entered on, which is whichever
        // SynchronizationContext was current when Start() ran - the UI thread if Start came after the
        // WinForms context was installed, the SystemEvents window thread if it came before. Not a
        // fixed thread, and deliberately not re-marshalled: SystemEvents.Send has already made the
        // hop, and ConnectionManager posts through IUiDispatcher regardless. Do not add a second
        // marshalling layer here. See the class comment for the measurement.
        //
        // Keep whatever runs downstream of this short. The dispatch that got here is Send, not Post,
        // so the OS notification thread is blocked until every handler returns.
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
            // What this deliberately does not close: a notification can already be in flight when
            // this runs, and no check-then-act here could prevent that - the check would race the
            // raise just as this does. The raise originates on the SystemEvents window thread
            // whatever thread it is finally dispatched to, and it snapshots the handler list before
            // it dispatches, so even a Send that lands on this very thread can be one that was taken
            // before this line ran. A late Resumed is handled where it can be: ConnectionManager
            // posts it and its own teardown decides.
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }
}
