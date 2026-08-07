using Klangbruecke;
using Klangbruecke.Connection;
using Xunit;

namespace Klangbruecke.Tests;

public sealed class TrayIconPolicyTests
{
    // The three that carry a design decision, pinned by name so the note in TrayIconPolicy cannot be
    // quietly contradicted.

    // Connected is the only Active. It is the one state in which every enabled half is delivering; any
    // other state coloured Active would tell the user everything is fine when something is not.
    [Fact]
    public void Connected_IsTheOnlyActive()
    {
        Assert.Equal(TrayIconStatus.Active, TrayIconPolicy.For(ConnectionState.Connected));

        foreach (ConnectionState state in Enum.GetValues<ConnectionState>())
        {
            if (state != ConnectionState.Connected)
            {
                Assert.NotEqual(TrayIconStatus.Active, TrayIconPolicy.For(state));
            }
        }
    }

    // Degraded is Busy, not Active - "half of what you asked for" is not "working", and the amber mark
    // is the whole of the app's glance-level signal that a half has dropped.
    [Fact]
    public void Degraded_IsBusy_NotActive()
    {
        Assert.Equal(TrayIconStatus.Busy, TrayIconPolicy.For(ConnectionState.Degraded));
    }

    // Suppressed is Idle, not Busy: a deliberate Disconnect and a switched-off auto-reconnect are the
    // app dormant on purpose, and Busy would promise motion that is not coming.
    [Fact]
    public void Suppressed_IsIdle_NotBusy()
    {
        Assert.Equal(TrayIconStatus.Idle, TrayIconPolicy.For(ConnectionState.Suppressed));
    }

    [Theory]
    [InlineData(ConnectionState.Idle, TrayIconStatus.Idle)]
    [InlineData(ConnectionState.Discovering, TrayIconStatus.Busy)]
    [InlineData(ConnectionState.Connecting, TrayIconStatus.Busy)]
    [InlineData(ConnectionState.Connected, TrayIconStatus.Active)]
    [InlineData(ConnectionState.Degraded, TrayIconStatus.Busy)]
    [InlineData(ConnectionState.Suppressed, TrayIconStatus.Idle)]
    [InlineData(ConnectionState.RetryBackoff, TrayIconStatus.Busy)]
    public void MapsEachReportedState(ConnectionState state, TrayIconStatus expected)
    {
        Assert.Equal(expected, TrayIconPolicy.For(state));
    }

    // Total over the enum, including any state added later: the switch has a default arm, so this can
    // never throw. The list above is the intended mapping; this is the guarantee the view relies on -
    // it asks for an icon on every state change and can take neither a throw nor an undefined bucket.
    [Fact]
    public void For_IsTotal_OverEveryState()
    {
        foreach (ConnectionState state in Enum.GetValues<ConnectionState>())
        {
            TrayIconStatus status = TrayIconPolicy.For(state);
            Assert.True(Enum.IsDefined(status));
        }
    }

    // A value the enum does not define is not evidence of anything, and Idle is the safe reading -
    // show the dormant mark rather than a false Active. Mirrors ConnectionStateProjection's own
    // conservative default.
    [Fact]
    public void For_UndefinedState_IsIdle()
    {
        Assert.Equal(TrayIconStatus.Idle, TrayIconPolicy.For((ConnectionState)999));
    }
}
