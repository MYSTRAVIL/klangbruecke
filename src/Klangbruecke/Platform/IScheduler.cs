namespace Klangbruecke.Platform;

/// <summary>
/// The time seam. Everything in the reconnect state machine that waits - the grace window before a
/// disconnect is believed, the backoff between attempts, the reconcile tick, the settle delay after
/// a resume - asks this rather than a timer, so the tests can drive all of it from a fake clock
/// without sleeping.
///
/// Callbacks are delivered on the UI thread. The state machine is single-threaded on that thread by
/// contract, and this interface is where that guarantee is made: see <see cref="UiScheduler"/> for
/// why the production implementation is a WinForms timer and not a threadpool one.
/// </summary>
public interface IScheduler
{
    DateTimeOffset Now { get; }

    /// <summary>
    /// Runs the action once after the delay. Disposing the handle cancels it; disposing after it has
    /// already run is harmless. A delay of zero or less means "at the next opportunity", never
    /// inline - the action does not run before this method returns.
    /// </summary>
    IDisposable Schedule(TimeSpan delay, Action action);

    /// <summary>
    /// Runs the action every period until the handle is disposed.
    ///
    /// The period must be greater than zero: a non-positive one has no meaning here and would make
    /// a virtual-time scheduler spin forever rather than fail, so both implementations reject it
    /// with <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    IDisposable SchedulePeriodic(TimeSpan period, Action action);
}
