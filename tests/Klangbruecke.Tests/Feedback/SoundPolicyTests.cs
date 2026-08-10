using Klangbruecke.Connection;
using Klangbruecke.Feedback;
using Xunit;

namespace Klangbruecke.Tests.Feedback;

public sealed class SoundPolicyTests
{
    [Fact]
    public void Entering_Connected_plays_Connected()
    {
        Assert.Equal(SoundEvent.Connected, SoundPolicy.For(ConnectionState.Connecting, ConnectionState.Connected));
        Assert.Equal(SoundEvent.Connected, SoundPolicy.For(ConnectionState.Degraded, ConnectionState.Connected));
    }

    [Fact]
    public void Staying_Connected_is_silent()
    {
        Assert.Null(SoundPolicy.For(ConnectionState.Connected, ConnectionState.Connected));
    }

    [Fact]
    public void A_half_dropping_from_full_plays_Degraded()
    {
        Assert.Equal(SoundEvent.Degraded, SoundPolicy.For(ConnectionState.Connected, ConnectionState.Degraded));
    }

    [Fact]
    public void A_partial_initial_connect_is_not_Degraded()
    {
        Assert.Null(SoundPolicy.For(ConnectionState.Connecting, ConnectionState.Degraded));
        Assert.Null(SoundPolicy.For(ConnectionState.Discovering, ConnectionState.Degraded));
    }

    [Theory]
    [InlineData(ConnectionState.Connected, ConnectionState.Idle)]
    [InlineData(ConnectionState.Connected, ConnectionState.Discovering)]
    [InlineData(ConnectionState.Connected, ConnectionState.RetryBackoff)]
    [InlineData(ConnectionState.Connected, ConnectionState.Suppressed)]
    [InlineData(ConnectionState.Degraded, ConnectionState.Discovering)]
    public void Losing_the_bridge_plays_Disconnected(ConnectionState previous, ConnectionState next)
    {
        Assert.Equal(SoundEvent.Disconnected, SoundPolicy.For(previous, next));
    }

    [Theory]
    [InlineData(ConnectionState.Idle, ConnectionState.Discovering)]
    [InlineData(ConnectionState.Discovering, ConnectionState.Connecting)]
    [InlineData(ConnectionState.RetryBackoff, ConnectionState.Connecting)]
    [InlineData(ConnectionState.Idle, ConnectionState.Idle)]
    public void Churn_below_a_live_bridge_is_silent(ConnectionState previous, ConnectionState next)
    {
        Assert.Null(SoundPolicy.For(previous, next));
    }
}
