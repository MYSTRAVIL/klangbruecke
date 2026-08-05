namespace Klangbruecke.Platform;

/// <summary>
/// The machine woke up.
///
/// One event, one fact, and deliberately nothing else. PC sleep/resume is one of the two transitions
/// that defined the predecessor app's failure - it came back from sleep believing it was still
/// connected, with nothing to correct it - so the state machine needs to be told. Making that a seam
/// is what lets the whole resume path be tested by a fake instead of by suspending the dev machine.
///
/// <b>Nothing here waits.</b> The consumer does not act on <see cref="Resumed"/> immediately: it
/// delays about 5 s and then forces a reconcile, because the Bluetooth stack is not back at the moment
/// this fires and an instant attempt only burns the first backoff step for nothing. That delay belongs
/// to <c>ConnectionManager</c> and <c>IScheduler</c>. This seam reports the wake, not what to do about
/// it.
///
/// <b>Nothing here marshals.</b> Implementations raise <see cref="Resumed"/> on whatever thread the OS
/// notification arrived on - for <see cref="PowerNotifier"/> that is the dedicated <c>SystemEvents</c>
/// window thread, never the UI thread. <c>ConnectionManager</c> posts every inbound event through
/// <c>IUiDispatcher</c> before touching state, which is what makes it single-threaded by contract. Do
/// not add a second marshalling layer here; <c>ILinkMonitor</c> states the same contract for the same
/// reason.
/// </summary>
public interface IPowerNotifier : IDisposable
{
    /// <summary>The machine has resumed from sleep or hibernation.</summary>
    event EventHandler? Resumed;

    /// <summary>
    /// Begin listening. Until this is called nothing is subscribed and no <see cref="Resumed"/> can
    /// arrive.
    /// </summary>
    void Start();
}
