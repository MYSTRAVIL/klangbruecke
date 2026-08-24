using Klangbruecke;
using Klangbruecke.Audio;
using Klangbruecke.Connection;
using Klangbruecke.Tests.Fakes;
using Xunit;

namespace Klangbruecke.Tests.Connection;

public sealed class SinkWedgeWatchdogTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = SinkWedgeWatchdog.StuckThreshold;

    private sealed class Harness
    {
        public bool Waiting;
        public FakeSinkEndpointStateProbe Probe = new();
        public FakeAppRestarter Restarter = new();
        public RecordingSettings Settings = new();
        public SinkWedgeWatchdog Watchdog;

        public Harness()
        {
            Watchdog = new SinkWedgeWatchdog(
                new FakeScheduler(Base),
                new ImmediateUiDispatcher(),
                () => Waiting,
                Probe,
                Restarter,
                Settings);
        }
    }

    // --- ShouldProbe: when the expensive read is even worth doing ---------------------------------

    [Fact]
    public void Does_not_probe_when_not_waiting_for_the_endpoint()
    {
        var h = new Harness { Waiting = false };
        Assert.False(h.Watchdog.ShouldProbe(Base));
    }

    [Fact]
    public void Does_not_probe_before_the_condition_has_held_past_the_threshold()
    {
        var h = new Harness { Waiting = true };

        Assert.False(h.Watchdog.ShouldProbe(Base)); // arms the clock
        Assert.False(h.Watchdog.ShouldProbe(Base + Threshold - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Probes_once_the_condition_has_held_past_the_threshold()
    {
        var h = new Harness { Waiting = true };

        h.Watchdog.ShouldProbe(Base); // arm
        Assert.True(h.Watchdog.ShouldProbe(Base + Threshold));
    }

    [Fact]
    public void The_clock_resets_when_the_condition_lapses_so_a_flap_does_not_accrue()
    {
        var h = new Harness { Waiting = true };

        h.Watchdog.ShouldProbe(Base); // arm at Base

        h.Waiting = false;
        h.Watchdog.ShouldProbe(Base + TimeSpan.FromMinutes(1)); // resets

        h.Waiting = true;
        Assert.False(h.Watchdog.ShouldProbe(Base + TimeSpan.FromMinutes(2))); // re-armed here, 0 elapsed
        Assert.True(h.Watchdog.ShouldProbe(Base + TimeSpan.FromMinutes(2) + Threshold));
    }

    // --- ApplyProbeResult: what to do with the answer --------------------------------------------

    [Fact]
    public void A_phantom_endpoint_while_waiting_and_within_budget_restarts_and_records_it()
    {
        var h = new Harness { Waiting = true };
        h.Watchdog.ShouldProbe(Base); // arm

        DateTimeOffset when = Base + Threshold;
        h.Watchdog.ApplyProbeResult(SinkEndpointCondition.Phantom, when);

        Assert.Equal(1, h.Restarter.RestartCount);
        Assert.Equal(new[] { when }, h.Settings.RecentAutoRestarts);
        Assert.Equal(1, h.Settings.SaveCount);
    }

    [Theory]
    [InlineData(SinkEndpointCondition.Unplugged)] // a live call (§14)
    [InlineData(SinkEndpointCondition.Active)]
    [InlineData(SinkEndpointCondition.Absent)]
    [InlineData(SinkEndpointCondition.Other)]
    public void A_non_phantom_endpoint_never_restarts(SinkEndpointCondition condition)
    {
        var h = new Harness { Waiting = true };
        h.Watchdog.ShouldProbe(Base); // arm

        h.Watchdog.ApplyProbeResult(condition, Base + Threshold);

        Assert.Equal(0, h.Restarter.RestartCount);
        Assert.Empty(h.Settings.RecentAutoRestarts);
        Assert.Equal(0, h.Settings.SaveCount);
    }

    [Fact]
    public void Does_not_restart_if_the_condition_recovered_while_the_probe_was_out()
    {
        var h = new Harness { Waiting = true };
        h.Watchdog.ShouldProbe(Base); // arm

        h.Waiting = false; // the endpoint arrived, or the phone left, while the probe ran

        h.Watchdog.ApplyProbeResult(SinkEndpointCondition.Phantom, Base + Threshold);

        Assert.Equal(0, h.Restarter.RestartCount);
    }

    [Fact]
    public void Does_not_restart_once_the_budget_for_the_hour_is_spent()
    {
        var h = new Harness { Waiting = true };
        h.Settings.RecentAutoRestarts = new()
        {
            Base.AddMinutes(-40),
            Base.AddMinutes(-20),
            Base.AddMinutes(-5),
        };

        h.Watchdog.ShouldProbe(Base); // arm
        h.Watchdog.ApplyProbeResult(SinkEndpointCondition.Phantom, Base);

        Assert.Equal(0, h.Restarter.RestartCount);
        // Left untouched; the manual tray item takes over from here.
        Assert.Equal(3, h.Settings.RecentAutoRestarts.Count);
    }

    [Fact]
    public void A_non_phantom_answer_rebases_the_clock_so_a_long_call_is_not_reprobed_every_tick()
    {
        var h = new Harness { Waiting = true };
        h.Watchdog.ShouldProbe(Base); // arm at Base

        h.Watchdog.ApplyProbeResult(SinkEndpointCondition.Unplugged, Base + Threshold); // rebases to here

        Assert.False(h.Watchdog.ShouldProbe(Base + Threshold + TimeSpan.FromMinutes(1)));
        Assert.True(h.Watchdog.ShouldProbe(Base + Threshold + Threshold));
    }
}
