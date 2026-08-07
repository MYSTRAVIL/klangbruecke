using Klangbruecke.Platform;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// A scheduler on a hand-cranked clock. <see cref="Advance"/> is what every Stage 1 timing test is
/// driven by, so nothing has to sleep or allocate a real timer to test a 60 s backoff.
///
/// Public, in Fakes, and not <c>file</c>-scoped: several later components share it.
/// </summary>
public sealed class FakeScheduler : IScheduler
{
    private readonly List<Entry> _entries = new();
    private DateTimeOffset _now;
    private long _sequence;

    // Bumped by each Advance. An entry created while a drain is running carries the current value,
    // which is how the drain tells "was already pending" from "was scheduled by a callback just
    // now" - see the comment on Advance.
    private long _epoch;

    public FakeScheduler(DateTimeOffset start) => _now = start;

    public DateTimeOffset Now => _now;

    /// <summary>Scheduled actions that have neither fired nor been cancelled. Periodics stay counted.</summary>
    public int PendingCount => _entries.Count;

    public IDisposable Schedule(TimeSpan delay, Action action) => Add(delay, action, period: null);

    public IDisposable SchedulePeriodic(TimeSpan period, Action action)
    {
        if (period <= TimeSpan.Zero)
        {
            // Guards the drain loop below as much as the caller: a non-positive period would come
            // due again the instant it re-armed and Advance would never return.
            throw new ArgumentOutOfRangeException(nameof(period), period, "The period must be greater than zero.");
        }

        return Add(period, action, period);
    }

    private IDisposable Add(TimeSpan delay, Action action, TimeSpan? period)
    {
        ArgumentNullException.ThrowIfNull(action);

        var entry = new Entry(this, _now + delay, action, period, _sequence++, _epoch);
        _entries.Add(entry);

        return entry;
    }

    /// <summary>
    /// Advances the clock, firing every due callback in due order.
    ///
    /// Work scheduled from inside a callback sits out the Advance that is running, however much
    /// virtual time is left in it, and comes due on the next one. That is a deliberate trade: it
    /// makes each Advance a bounded batch, so a component that re-arms itself with no delay fails a
    /// test instead of spinning inside Advance and hanging the suite. The cost is that a chain of
    /// callbacks needs one Advance per link.
    ///
    /// A periodic re-arms in place rather than being re-added, so it keeps its epoch and can still
    /// fire several times within one Advance - which is the whole point of a 30 s reconcile tick.
    ///
    /// What that costs, stated because callbacks read Now: sitting an entry out means the drain ends
    /// with Now at the target, so the next Advance can fire it well past its due time. A 2 s one-shot
    /// scheduled from a callback at Start+1 during Advance(60) runs with Now = Start+60, not
    /// Start+3, and a further Advance(0) collects it no earlier - the skew is already banked. The
    /// invariant that does hold, and the only one to rely on: Now never runs backwards, and a
    /// callback never observes a Now earlier than its own due time. Anything needing an exact
    /// interval must schedule it before the Advance that should collect it.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), by, "Time does not run backwards.");
        }

        DateTimeOffset target = _now + by;
        long epoch = ++_epoch;

        while (NextDue(target, epoch) is { } due)
        {
            // The callback sees the time it was scheduled for, not the end of the window. Never
            // backwards: an entry can be due before now if it was given a zero or negative delay.
            if (due.DueAt > _now)
            {
                _now = due.DueAt;
            }

            if (due.Period is { } period)
            {
                due.DueAt += period;
            }
            else
            {
                // Off the books before it runs, so PendingCount is honest inside the callback and a
                // handle the callback disposes is a no-op rather than a second cancellation.
                due.Dispose();
            }

            due.Run();
        }

        _now = target;
    }

    /// <summary>
    /// The earliest entry that is due, in registration order among entries sharing a due time, and
    /// ignoring anything scheduled since this drain began.
    /// </summary>
    private Entry? NextDue(DateTimeOffset target, long epoch)
    {
        Entry? best = null;

        foreach (Entry entry in _entries)
        {
            if (entry.Epoch >= epoch || entry.DueAt > target)
            {
                continue;
            }

            if (best is null || entry.DueAt < best.DueAt || (entry.DueAt == best.DueAt && entry.Sequence < best.Sequence))
            {
                best = entry;
            }
        }

        return best;
    }

    private void Remove(Entry entry) => _entries.Remove(entry);

    private sealed class Entry : IDisposable
    {
        private readonly FakeScheduler _owner;
        private readonly Action _action;

        public Entry(
            FakeScheduler owner,
            DateTimeOffset dueAt,
            Action action,
            TimeSpan? period,
            long sequence,
            long epoch)
        {
            _owner = owner;
            _action = action;
            DueAt = dueAt;
            Period = period;
            Sequence = sequence;
            Epoch = epoch;
        }

        public DateTimeOffset DueAt { get; set; }

        public TimeSpan? Period { get; }

        public long Sequence { get; }

        public long Epoch { get; }

        public void Run() => _action();

        /// <summary>Cancellation is removal, so a cancelled entry cannot be selected or counted.</summary>
        public void Dispose() => _owner.Remove(this);
    }
}
