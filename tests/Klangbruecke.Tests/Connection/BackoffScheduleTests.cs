using Klangbruecke.Connection;
using Xunit;

namespace Klangbruecke.Tests.Connection;

public sealed class BackoffScheduleTests
{
    private static BackoffSchedule AfterAdvances(int advances)
    {
        BackoffSchedule backoff = new();
        for (int i = 0; i < advances; i++)
        {
            backoff.Advance();
        }

        return backoff;
    }

    [Fact]
    public void First_delay_is_two_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), new BackoffSchedule().CurrentDelay);
    }

    // Every row starts from a fresh instance and reads the delay the state machine would wait for
    // that many failures so far. The zero row is the one that matters most: it is what fails an
    // implementation that hands back the delay *after* advancing rather than the one now due.
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    [InlineData(4, 30)]
    public void Delays_follow_the_specified_sequence(int advances, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AfterAdvances(advances).CurrentDelay);
    }

    // Fifty, not just six. "Then 60 forever" is the part that carries a phone left switched off
    // overnight, and an implementation that walked off the end of the table - or kept doubling -
    // would still pass if six advances were the furthest anything looked.
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(50)]
    public void Sixth_and_later_delays_are_sixty_seconds(int advances)
    {
        Assert.Equal(TimeSpan.FromSeconds(60), AfterAdvances(advances).CurrentDelay);
    }

    [Fact]
    public void Reset_returns_to_the_first_delay()
    {
        BackoffSchedule backoff = AfterAdvances(3);

        backoff.Reset();

        Assert.Equal(TimeSpan.FromSeconds(2), backoff.CurrentDelay);
    }

    // Advances first: reset on an untouched instance would pass against a Reset() that does nothing.
    [Fact]
    public void Reset_returns_attempt_to_zero()
    {
        BackoffSchedule backoff = AfterAdvances(3);

        backoff.Reset();

        Assert.Equal(0, backoff.Attempt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Attempt_counts_advances(int advances)
    {
        Assert.Equal(advances, AfterAdvances(advances).Attempt);
    }
}
