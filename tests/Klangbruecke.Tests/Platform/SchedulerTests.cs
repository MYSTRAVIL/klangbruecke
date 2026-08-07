using Klangbruecke.Tests.Fakes;
using Xunit;

namespace Klangbruecke.Tests.Platform;

/// <summary>
/// All against <see cref="FakeScheduler"/>. It is the seam the whole reconnect state machine's
/// timing is tested through - a 3 s grace window, a 2/4/8/16/30/60 s backoff, a 30 s reconcile loop
/// and a 5 s post-resume delay - so its own behaviour has to be pinned before anything leans on it.
/// <c>UiScheduler</c> is deliberately untested: it is a thin adapter over
/// <c>System.Windows.Forms.Timer</c> and exercising it would need a live message pump.
/// </summary>
public sealed class SchedulerTests
{
    // Fixed and offset-bearing, so nothing here can depend on the machine's clock or time zone.
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan Seconds(int seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Advance_before_delay_does_not_run_the_action()
    {
        var scheduler = new FakeScheduler(Start);
        int calls = 0;

        scheduler.Schedule(Seconds(2), () => calls++);
        scheduler.Advance(Seconds(1));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Advance_past_delay_runs_the_action_once()
    {
        var scheduler = new FakeScheduler(Start);
        int calls = 0;
        DateTimeOffset seenByTheCallback = default;

        scheduler.Schedule(Seconds(2), () =>
        {
            calls++;
            seenByTheCallback = scheduler.Now;
        });
        scheduler.Advance(Seconds(3));

        Assert.Equal(1, calls);

        // The callback sees the time it came due, not the end of the window it was collected in.
        // Downstream leans on this hardest: a backoff callback reads Now to work out its next delay
        // and a grace window stamps a disconnect with it, so a scheduler that ran everything at the
        // advance target would skew both by however far the test happened to advance.
        Assert.Equal(Start + Seconds(2), seenByTheCallback);
    }

    // The grace window and the backoff both schedule one-shots and then let a lot of virtual time
    // pass. A one-shot that re-fired per elapsed period would turn a single retry into a storm.
    [Fact]
    public void Advance_far_past_delay_still_runs_a_one_shot_once()
    {
        var scheduler = new FakeScheduler(Start);
        int calls = 0;

        scheduler.Schedule(Seconds(2), () => calls++);
        scheduler.Advance(Seconds(60));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Disposing_the_handle_cancels_a_pending_action()
    {
        var scheduler = new FakeScheduler(Start);
        int calls = 0;

        IDisposable handle = scheduler.Schedule(Seconds(2), () => calls++);
        handle.Dispose();
        scheduler.Advance(Seconds(60));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Periodic_fires_once_per_period()
    {
        var scheduler = new FakeScheduler(Start);
        int calls = 0;

        scheduler.SchedulePeriodic(Seconds(10), () => calls++);
        scheduler.Advance(Seconds(35));

        // 10, 20, 30 - not 35/10 rounded up, and not once for the whole span.
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Disposing_a_periodic_handle_stops_it()
    {
        var scheduler = new FakeScheduler(Start);
        int calls = 0;

        IDisposable handle = scheduler.SchedulePeriodic(Seconds(10), () => calls++);
        scheduler.Advance(Seconds(15));
        Assert.Equal(1, calls);

        handle.Dispose();
        scheduler.Advance(Seconds(100));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Now_advances_by_exactly_the_amount_advanced()
    {
        var scheduler = new FakeScheduler(Start);

        Assert.Equal(Start, scheduler.Now);

        scheduler.Advance(Seconds(7));

        // Exactly: a scheduler that only moved Now as far as the last callback it ran would make
        // every "nothing else happened for the rest of the window" assertion meaningless.
        Assert.Equal(Start + Seconds(7), scheduler.Now);
    }

    [Fact]
    public void Callbacks_fire_in_due_order_not_registration_order()
    {
        var scheduler = new FakeScheduler(Start);
        var order = new List<string>();

        scheduler.Schedule(Seconds(5), () => order.Add("late"));
        scheduler.Schedule(Seconds(1), () => order.Add("early"));
        scheduler.Advance(Seconds(10));

        // One Advance crossing both due times must still order them by when they came due. The
        // state machine has a grace window and a reconcile tick outstanding at the same moment, and
        // which one runs first decides whether a reconnect is attempted or pre-empted.
        Assert.Equal(new[] { "early", "late" }, order);
    }

    // Pins a deliberate choice: work scheduled from inside a callback sits out the Advance that is
    // running, however much virtual time is left in it, and runs on the next one. That makes every
    // Advance a bounded batch - a component that re-arms itself with no delay cannot spin inside
    // Advance and hang the suite - at the cost of needing one Advance per link of a chain.
    [Fact]
    public void An_action_scheduled_from_inside_a_callback_runs_on_a_later_advance()
    {
        var scheduler = new FakeScheduler(Start);
        int outerCalls = 0;
        int innerCalls = 0;

        scheduler.Schedule(Seconds(1), () =>
        {
            outerCalls++;
            scheduler.Schedule(TimeSpan.Zero, () => innerCalls++);
        });

        scheduler.Advance(Seconds(10));

        Assert.Equal(1, outerCalls);
        Assert.Equal(0, innerCalls);

        // Not lost either, and no further virtual time is needed to collect it.
        scheduler.Advance(TimeSpan.Zero);

        Assert.Equal(1, outerCalls);
        Assert.Equal(1, innerCalls);
    }

    // Not from the brief's table. IScheduler documents this rejection as part of the seam's contract,
    // so leaving it to review would have widened the interface with a rule nothing checks. It is also
    // load-bearing here rather than merely defensive: a non-positive period re-arms at the instant it
    // fired, and Advance would drain it forever - a hung suite, not a red one.
    [Fact]
    public void SchedulePeriodic_rejects_a_period_that_is_not_positive()
    {
        var scheduler = new FakeScheduler(Start);

        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.SchedulePeriodic(TimeSpan.Zero, () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.SchedulePeriodic(Seconds(-1), () => { }));
    }

    [Fact]
    public void PendingCount_drops_to_zero_after_a_one_shot_fires()
    {
        var scheduler = new FakeScheduler(Start);

        scheduler.Schedule(Seconds(2), () => { });
        Assert.Equal(1, scheduler.PendingCount);

        scheduler.Advance(Seconds(3));

        // Tests assert on PendingCount to show the state machine left no timer behind; that is only
        // worth anything if a fired one-shot stops being counted.
        Assert.Equal(0, scheduler.PendingCount);
    }
}
