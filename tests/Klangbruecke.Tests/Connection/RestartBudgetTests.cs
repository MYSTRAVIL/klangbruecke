using Klangbruecke.Connection;
using Xunit;

namespace Klangbruecke.Tests.Connection;

public sealed class RestartBudgetTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Allows_when_nothing_is_on_record()
    {
        Assert.True(RestartBudget.Allows(Array.Empty<DateTimeOffset>(), Now));
    }

    [Fact]
    public void Allows_up_to_but_not_at_the_cap_within_the_window()
    {
        var two = new[] { Now.AddMinutes(-40), Now.AddMinutes(-10) };
        Assert.True(RestartBudget.Allows(two, Now));

        var three = new[] { Now.AddMinutes(-40), Now.AddMinutes(-20), Now.AddMinutes(-10) };
        Assert.False(RestartBudget.Allows(three, Now));
    }

    [Fact]
    public void Restarts_older_than_the_window_do_not_count()
    {
        // Three restarts, but two of them are more than an hour old: only one still counts, so a
        // fourth is allowed.
        var recent = new[] { Now.AddHours(-3), Now.AddHours(-2), Now.AddMinutes(-5) };
        Assert.True(RestartBudget.Allows(recent, Now));
    }

    [Fact]
    public void A_restart_dated_in_the_future_is_ignored()
    {
        // A clock moved backwards leaves a stamp later than now; it must age out rather than pin the
        // budget shut forever.
        var recent = new[] { Now.AddMinutes(5), Now.AddMinutes(10), Now.AddMinutes(30) };
        Assert.True(RestartBudget.Allows(recent, Now));
        Assert.Empty(RestartBudget.Prune(recent, Now));
    }

    [Fact]
    public void Record_appends_now_and_drops_the_out_of_window_entries()
    {
        var recent = new[] { Now.AddHours(-5), Now.AddMinutes(-30) };

        List<DateTimeOffset> after = RestartBudget.Record(recent, Now);

        Assert.Equal(new[] { Now.AddMinutes(-30), Now }, after);
    }

    [Fact]
    public void Prune_returns_the_surviving_entries_oldest_first()
    {
        var recent = new[] { Now.AddMinutes(-10), Now.AddMinutes(-50), Now.AddMinutes(-30) };

        Assert.Equal(
            new[] { Now.AddMinutes(-50), Now.AddMinutes(-30), Now.AddMinutes(-10) },
            RestartBudget.Prune(recent, Now));
    }

    [Fact]
    public void The_window_boundary_is_exclusive_so_exactly_one_hour_old_no_longer_counts()
    {
        var atBoundary = new[] { Now - RestartBudget.Window };
        Assert.Empty(RestartBudget.Prune(atBoundary, Now));
    }
}
