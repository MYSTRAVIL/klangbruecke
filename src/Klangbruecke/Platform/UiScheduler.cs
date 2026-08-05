namespace Klangbruecke.Platform;

/// <summary>
/// Delivers every callback on the thread that constructed it. UI thread only.
///
/// Built on <see cref="System.Windows.Forms.Timer"/>, which posts WM_TIMER through the message pump
/// and so raises Tick on the thread that started it - no marshalling needed inside this class, and
/// no lock anywhere in the state machine it drives. <see cref="System.Threading.Timer"/> would be
/// the obvious swap and is the one thing that must not happen here: its callbacks arrive on the
/// threadpool, which would quietly turn the whole single-threaded design into a racy one with no
/// test to show for it.
///
/// Precisely: Tick arrives on the thread that *started* the timer, which is the thread that called
/// Schedule. Construct and use this from the UI thread only - Stage 1 requires that anyway. Called
/// from a thread with no message pump, nothing would ever fire, which is why there is no marshalling
/// here to make it look safe.
///
/// Untested by design: it is a thin adapter whose behaviour is the pump's, and exercising it would
/// need a live message loop. FakeScheduler carries the tested semantics.
/// </summary>
public sealed class UiScheduler : IScheduler, IDisposable
{
    // Every live handle, so Dispose can stop timers that are still armed. A tray app tears down its
    // state machine while a 60 s backoff is outstanding on nearly every exit; a surviving timer
    // would tick into a torn-down object.
    private readonly List<Handle> _live = new();
    private bool _disposed;

    public DateTimeOffset Now => DateTimeOffset.Now;

    public IDisposable Schedule(TimeSpan delay, Action action) => Start(delay, action, repeat: false);

    public IDisposable SchedulePeriodic(TimeSpan period, Action action)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "The period must be greater than zero.");
        }

        return Start(period, action, repeat: true);
    }

    private IDisposable Start(TimeSpan interval, Action action, bool repeat)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_disposed)
        {
            // Matches ControlUiDispatcher: work handed to a torn-down surface is dropped, not
            // thrown at the caller. Shutdown races here are ordinary, not faults.
            return new Handle(this, timer: null, action, repeat);
        }

        var timer = new System.Windows.Forms.Timer { Interval = ToInterval(interval) };
        var handle = new Handle(this, timer, action, repeat);

        timer.Tick += handle.OnTick;
        _live.Add(handle);
        timer.Start();

        return handle;
    }

    /// <summary>
    /// Interval is milliseconds and must be at least 1, so a zero or negative delay becomes "next
    /// tick" rather than an exception - the same reading of a non-positive delay that
    /// <c>FakeScheduler</c> gives it. Ceiling, not truncation: a sub-millisecond delay must not
    /// round down to zero and throw.
    /// </summary>
    private static int ToInterval(TimeSpan interval) =>
        (int)Math.Clamp(Math.Ceiling(interval.TotalMilliseconds), 1, int.MaxValue);

    /// <summary>Stops every outstanding timer. Nothing scheduled through this fires afterwards.</summary>
    public void Dispose()
    {
        _disposed = true;

        // Copied: disposing a handle removes it from _live.
        foreach (Handle handle in _live.ToArray())
        {
            handle.Dispose();
        }

        _live.Clear();
    }

    private void Forget(Handle handle) => _live.Remove(handle);

    private sealed class Handle : IDisposable
    {
        private readonly UiScheduler _owner;
        private readonly System.Windows.Forms.Timer? _timer;
        private readonly Action _action;
        private readonly bool _repeat;
        private bool _disposed;

        public Handle(UiScheduler owner, System.Windows.Forms.Timer? timer, Action action, bool repeat)
        {
            _owner = owner;
            _timer = timer;
            _action = action;
            _repeat = repeat;
        }

        public void OnTick(object? sender, EventArgs e)
        {
            if (!_repeat)
            {
                // Before the action, not after: a one-shot whose action throws - or which schedules
                // more work, or disposes this handle - must already be dead either way, or it fires
                // again on the next tick.
                Dispose();
            }

            _action();
        }

        public void Dispose()
        {
            // Idempotent in its own right rather than by leaning on WinForms tolerating a Stop on a
            // disposed timer: callers dispose a handle they already cancelled all the time, and a
            // one-shot disposes itself from OnTick.
            if (_disposed || _timer is null)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;

            // Disposing a WinForms timer from inside its own Tick is safe: the pump has already
            // finished with the message by the time the handler runs.
            _timer.Dispose();
            _owner.Forget(this);
        }
    }
}
