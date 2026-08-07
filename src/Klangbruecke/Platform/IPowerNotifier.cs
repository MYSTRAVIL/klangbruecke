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
/// <b>Do not add a marshalling layer here</b> - but not for the reason <c>ILinkMonitor</c> gives.
/// <c>ILinkMonitor</c> does not marshal because nothing does, and <c>ConnectionManager</c> is what
/// makes the hop. <see cref="PowerNotifier"/> does not marshal because <c>SystemEvents</c>
/// <b>already has</b>, before the handler is entered.
///
/// The real rule, measured rather than assumed: <c>SystemEvents</c> captures whatever
/// <see cref="System.Threading.SynchronizationContext"/> was current on the thread that called
/// <see cref="Start"/>, and dispatches through it. So <see cref="Resumed"/> arrives on <b>whichever
/// context was current when <see cref="Start"/> ran</b> - not on a fixed thread. Three consequences,
/// and the third is the one that bites:
///
/// <list type="number">
/// <item>Called after the WinForms context is installed, <see cref="Resumed"/> lands on the <b>UI
/// thread</b>.</item>
/// <item>The dispatch is <c>Send</c>, not <c>Post</c> - measured: one <c>Send</c>, zero <c>Post</c>.
/// Under WinForms that is a <b>blocking</b> cross-thread <c>Control.Invoke</c>, so the OS notification
/// thread stalls behind a busy UI thread. Do not do slow work in a <see cref="Resumed"/> handler.</item>
/// <item>With no context installed, <c>SystemEvents</c> captures a plain
/// <see cref="System.Threading.SynchronizationContext"/> whose <c>Send</c> runs inline - i.e. on the
/// <c>SystemEvents</c> window thread. Because <see cref="Start"/> is deliberately not called from the
/// constructor, <b>the delivery thread depends on where startup calls it</b> - and the test is
/// whether the WinForms context has been installed by then, <b>not</b> whether <c>Application.Run</c>
/// has been entered. This sentence used to say the latter, and it was false for this app: the context
/// is installed by the first <c>Control</c> constructed, which is <c>ControlUiDispatcher</c>'s
/// marshalling control in <c>Program.Main</c>, and <c>ConnectionManager.Start</c> is reached from
/// <c>TrayContext</c>'s constructor. Both run before <c>Application.Run</c> is entered and both are
/// on the UI thread with the context already current, so this app gets consequence 1 above. The
/// installation itself is pinned by
/// <c>UiDispatcherTests.Control_InstallsTheWinFormsSynchronizationContextOnTheThreadThatBuildsIt</c> -
/// which is all it pins, so do not lean on it for anything downstream of the capture.</item>
/// </list>
///
/// Either way <c>ConnectionManager</c> posts every inbound event through <c>IUiDispatcher</c> before
/// touching state, which is what makes it single-threaded by contract regardless of which of the two
/// it got. That is why the ambiguity above is tolerable rather than a bug - but do not write code here
/// or downstream that assumes a particular one.
///
/// <b><see cref="Start"/> and <see cref="IDisposable.Dispose"/> must be called on the same thread</b>,
/// and implementations are not required to be thread-safe across them. This is a real requirement, not
/// a nicety: <see cref="PowerNotifier"/> tracks whether it is subscribed in ordinary non-volatile
/// fields, so a <see cref="IDisposable.Dispose"/> raced from another thread can miss the unsubscribe -
/// and because the underlying subscription is static and strongly rooted, a missed unsubscribe is a
/// leak for the life of the process, not a transient glitch.
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
