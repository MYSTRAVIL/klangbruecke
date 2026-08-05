using Klangbruecke.Connection;
using Klangbruecke.Tests.Fakes;
using Xunit;

namespace Klangbruecke.Tests.Connection;

public sealed class MusicHalfTests
{
    private const string PhoneId = "phone-1";
    private const string OtherPhoneId = "phone-2";
    private const string OutputId = "output-1";

    /// <summary>The smallest step the timing tests use to straddle a due time.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    // --- Off ---------------------------------------------------------------------------------

    [Fact]
    public async Task Disabled_stays_Off_when_the_link_arrives()
    {
        Harness half = new(enabled: false);

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(MusicState.Off, half.Half.State);
        Assert.Empty(half.Sink.ConnectCalls);
    }

    [Fact]
    public async Task Connect_not_permitted_leaves_Off_on_link_present()
    {
        Harness half = new();

        await half.Half.OnLinkPresentAsync(connectPermitted: false);

        Assert.Equal(MusicState.Off, half.Half.State);
        Assert.Empty(half.Sink.ConnectCalls);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, PhoneId)]
    [InlineData(true, null)]
    public void Enabled_needs_both_the_switch_and_a_phone(bool enabled, string? phoneDeviceId)
    {
        Harness half = new(enabled, phoneDeviceId);

        Assert.False(half.Half.Enabled);
    }

    [Fact]
    public void Enabled_is_true_with_the_switch_on_and_a_phone_picked()
    {
        Assert.True(new Harness().Half.Enabled);
    }

    // --- Connecting --------------------------------------------------------------------------

    [Fact]
    public async Task Link_present_starts_a_connect()
    {
        Harness half = new();
        half.Sink.DeferConnect = true;

        Task connecting = half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(MusicState.Connecting, half.Half.State);
        Assert.Equal(new[] { PhoneId }, half.Sink.ConnectCalls);

        half.Sink.CompleteConnect(true);
        await connecting;
    }

    /// <summary>
    /// Finding #2, and the reason this half has five states rather than four: the capture endpoint
    /// has not been seen yet, and assuming it arrived with the connection is what left 5 of 8
    /// recorded launches silently routing nothing.
    /// </summary>
    [Fact]
    public async Task Successful_connect_moves_to_Linked_not_Up()
    {
        Harness half = new();

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(MusicState.Linked, half.Half.State);
        Assert.Empty(half.Router.StartCalls);
    }

    /// <summary>
    /// The other half of finding #2. Measured: the endpoint tracks the phone's own A2DP link, so it
    /// is usually <c>Active</c> before this app connects at all - and an arrival that has already
    /// happened raises no notification. Waiting for one would mean waiting for the 30 s reconcile.
    /// </summary>
    [Fact]
    public async Task Connecting_with_the_endpoint_already_present_reaches_Up()
    {
        Harness half = new();
        half.Endpoints.SinkCaptureEndpointPresent = true;

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(MusicState.Up, half.Half.State);
        Assert.Equal(new string?[] { OutputId }, half.Router.StartCalls);
    }

    [Fact]
    public async Task Repeated_link_present_reports_do_not_start_a_second_connect()
    {
        Harness half = new();
        await half.ReachLinkedAsync();

        // The reconcile poll is level-triggered: it says "the phone is there" every 30 s for as long
        // as it is there.
        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Single(half.Sink.ConnectCalls);
        Assert.Equal(MusicState.Linked, half.Half.State);
    }

    // --- Connect backoff ---------------------------------------------------------------------

    [Fact]
    public async Task Failed_connect_moves_to_Backoff_and_schedules_a_retry()
    {
        Harness half = new();
        half.Sink.ConnectResult = false;

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(MusicState.Backoff, half.Half.State);
        Assert.Equal(Seconds(2), half.Half.NextRetryIn);
        Assert.Equal(1, half.Scheduler.PendingCount);
    }

    /// <summary>
    /// The spec's row reads "returned false <em>or threw</em>". The shipping service catches its own
    /// throws, so this covers the seam rather than today's implementation - which is the point: a
    /// throw that escaped would otherwise leave the half in <c>Connecting</c> forever, with no timer
    /// and no event that can ever move it.
    /// </summary>
    [Fact]
    public async Task Connect_that_throws_moves_to_Backoff_and_schedules_a_retry()
    {
        Harness half = new();
        half.Sink.ConnectThrows = new InvalidOperationException("the radio said no");

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(MusicState.Backoff, half.Half.State);
        Assert.Equal(Seconds(2), half.Half.NextRetryIn);
        Assert.Equal(1, half.Scheduler.PendingCount);
    }

    [Fact]
    public async Task Backoff_retries_on_the_2_4_8_sequence()
    {
        Harness half = new();
        half.Sink.ConnectResult = false;

        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        Assert.Single(half.Sink.ConnectCalls);

        foreach (TimeSpan delay in new[] { Seconds(2), Seconds(4), Seconds(8) })
        {
            int before = half.Sink.ConnectCalls.Count;

            half.Scheduler.Advance(delay - Tick);
            Assert.Equal(before, half.Sink.ConnectCalls.Count);

            half.Scheduler.Advance(Tick);
            Assert.Equal(before + 1, half.Sink.ConnectCalls.Count);
        }

        Assert.Equal(4, half.Sink.ConnectCalls.Count);
        Assert.Equal(MusicState.Backoff, half.Half.State);
    }

    [Fact]
    public async Task Backoff_resets_after_a_successful_connect()
    {
        Harness half = new();
        half.Sink.ConnectResult = false;

        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        half.Scheduler.Advance(Seconds(2));
        Assert.Equal(Seconds(4), half.Half.NextRetryIn);

        half.Sink.ConnectResult = true;
        half.Scheduler.Advance(Seconds(4));
        Assert.Equal(MusicState.Linked, half.Half.State);

        // Round the loop again. The next failure has to start the sequence from the beginning rather
        // than from where the last run of failures left it.
        half.Half.OnLinkAbsent();
        half.Sink.ConnectResult = false;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(Seconds(2), half.Half.NextRetryIn);
    }

    /// <summary>
    /// The phone leaving the room is not evidence that connecting to it will work next time, and the
    /// halves are torn down on every range exit - so a backoff that reset there would be a backoff
    /// that never got past 2 s for a phone that flaps. Reset belongs to success only.
    /// </summary>
    [Fact]
    public async Task Link_absent_does_not_reset_the_connect_backoff()
    {
        Harness half = new();
        half.Sink.ConnectResult = false;

        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        half.Scheduler.Advance(Seconds(2));
        Assert.Equal(Seconds(4), half.Half.NextRetryIn);

        half.Half.OnLinkAbsent();
        Assert.Equal(0, half.Scheduler.PendingCount);

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(Seconds(8), half.Half.NextRetryIn);
    }

    [Fact]
    public async Task Link_present_while_backing_off_does_not_jump_the_queue()
    {
        Harness half = new();
        half.Sink.ConnectResult = false;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Single(half.Sink.ConnectCalls);
        Assert.Equal(1, half.Scheduler.PendingCount);
    }

    // --- The route ---------------------------------------------------------------------------

    [Fact]
    public async Task Endpoint_appearing_while_Linked_starts_the_route()
    {
        Harness half = new();
        await half.ReachLinkedAsync();

        half.SetEndpoint(true);

        Assert.Equal(new string?[] { OutputId }, half.Router.StartCalls);
    }

    [Fact]
    public async Task Endpoint_appearing_while_Linked_moves_to_Up()
    {
        Harness half = new();
        await half.ReachLinkedAsync();

        half.SetEndpoint(true);

        Assert.Equal(MusicState.Up, half.Half.State);
    }

    [Fact]
    public async Task Route_start_failure_keeps_Linked_and_advances_the_route_backoff()
    {
        Harness half = new();
        half.Router.StartResult = false;
        await half.ReachLinkedAsync();

        half.SetEndpoint(true);

        Assert.Equal(MusicState.Linked, half.Half.State);
        Assert.Single(half.Router.StartCalls);

        // It does not spin. MMDevAPI sends duplicates - one cause produced five callbacks in every
        // recorded run - and each one arriving inside the backoff window must start nothing.
        half.Half.OnEndpointsChanged();
        half.Half.OnEndpointsChanged();
        Assert.Single(half.Router.StartCalls);

        half.Scheduler.Advance(Seconds(2) - Tick);
        Assert.Single(half.Router.StartCalls);

        half.Scheduler.Advance(Tick);
        Assert.Equal(2, half.Router.StartCalls.Count);
    }

    /// <summary>
    /// The measured lie: <c>AudioRouter.Start</c> returns true for a capture that died inside
    /// <c>StartRecording</c>. Retry logic that trusts the return value loops at event speed, so the
    /// bool is advisory and <c>IsRunning</c> is the truth.
    /// </summary>
    [Fact]
    public async Task Route_that_lies_about_starting_keeps_Linked_and_advances_the_route_backoff()
    {
        Harness half = new();
        half.Router.StartLeavesItRunning = false;
        await half.ReachLinkedAsync();

        half.SetEndpoint(true);

        Assert.Equal(MusicState.Linked, half.Half.State);
        Assert.Single(half.Router.StartCalls);

        half.Scheduler.Advance(Seconds(2) - Tick);
        Assert.Single(half.Router.StartCalls);

        half.Scheduler.Advance(Tick);
        Assert.Equal(2, half.Router.StartCalls.Count);
    }

    [Fact]
    public async Task Route_stopped_while_Up_returns_to_Linked()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Router.Die();

        Assert.Equal(MusicState.Linked, half.Half.State);
    }

    /// <summary>
    /// Finding #3, and the predecessor app's defining bug. A cellular call invalidates the capture
    /// endpoint without closing the A2DP connection: the route dies and the Bluetooth link is
    /// perfectly fine. Reconnecting it here is what made the old app need the phone re-picked from
    /// the tray after every call.
    /// </summary>
    [Fact]
    public async Task Route_stopped_while_Up_does_not_reconnect_bluetooth()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Router.Die();

        Assert.Single(half.Sink.ConnectCalls);
        Assert.Equal(0, half.Sink.DisconnectCount);

        // And not later either, when the route backoff comes due: what is retried is the route.
        half.Scheduler.Advance(Seconds(2));

        Assert.Single(half.Sink.ConnectCalls);
        Assert.Equal(0, half.Sink.DisconnectCount);
        Assert.Equal(2, half.Router.StartCalls.Count);
    }

    [Fact]
    public async Task Route_restarts_when_the_endpoint_returns_after_a_call()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Router.Die();
        half.SetEndpoint(false);
        Assert.Equal(MusicState.Linked, half.Half.State);

        // The call itself. Nothing to start while the endpoint is gone, and the route retry coming
        // due in the middle of it must not change that.
        half.Scheduler.Advance(TimeSpan.FromMinutes(3));
        Assert.Single(half.Router.StartCalls);

        half.SetEndpoint(true);

        Assert.Equal(2, half.Router.StartCalls.Count);
        Assert.Equal(MusicState.Up, half.Half.State);
        Assert.Single(half.Sink.ConnectCalls);
    }

    [Fact]
    public async Task Repeated_immediate_route_stops_advance_the_route_backoff()
    {
        Harness half = new();
        await half.ReachUpAsync();

        int started = 1;

        foreach (TimeSpan delay in new[] { Seconds(2), Seconds(4), Seconds(8), Seconds(16), Seconds(30) })
        {
            half.Router.Die();
            Assert.Equal(MusicState.Linked, half.Half.State);

            half.Scheduler.Advance(delay - Tick);
            Assert.Equal(started, half.Router.StartCalls.Count);

            half.Scheduler.Advance(Tick);
            Assert.Equal(++started, half.Router.StartCalls.Count);
            Assert.Equal(MusicState.Up, half.Half.State);
        }

        Assert.Equal(6, half.Router.StartCalls.Count);
    }

    [Fact]
    public async Task Route_backoff_resets_after_ten_seconds_of_running()
    {
        Harness half = new();
        await half.ReachUpAsync();

        // Two failures, so the schedule is demonstrably past its first step.
        half.Router.Die();
        half.Scheduler.Advance(Seconds(2));
        half.Router.Die();
        half.Scheduler.Advance(Seconds(4));
        Assert.Equal(MusicState.Up, half.Half.State);
        Assert.Equal(3, half.Router.StartCalls.Count);

        half.Scheduler.Advance(Seconds(10));
        half.Router.Die();

        half.Scheduler.Advance(Seconds(2) - Tick);
        Assert.Equal(3, half.Router.StartCalls.Count);

        half.Scheduler.Advance(Tick);
        Assert.Equal(4, half.Router.StartCalls.Count);
    }

    /// <summary>
    /// A run long enough to count as healthy is healthy however it ended, and the ordinary way it
    /// ends on this machine is a call taking the endpoint away. Charging the next stop for a route
    /// that had been playing music for ten seconds would make an evening of calls back off to a
    /// minute between restarts.
    /// </summary>
    [Fact]
    public async Task A_call_that_interrupts_a_healthy_run_still_resets_the_route_backoff()
    {
        Harness half = new();
        half.Router.StartResult = false;
        await half.ReachLinkedAsync();

        half.SetEndpoint(true);
        half.Router.StartResult = true;
        half.Scheduler.Advance(Seconds(2));
        Assert.Equal(MusicState.Up, half.Half.State);
        Assert.Equal(2, half.Router.StartCalls.Count);

        half.Scheduler.Advance(Seconds(10));

        half.SetEndpoint(false);
        Assert.Equal(MusicState.Linked, half.Half.State);
        half.SetEndpoint(true);
        Assert.Equal(MusicState.Up, half.Half.State);
        Assert.Equal(3, half.Router.StartCalls.Count);

        half.Router.Die();

        half.Scheduler.Advance(Seconds(2) - Tick);
        Assert.Equal(3, half.Router.StartCalls.Count);

        half.Scheduler.Advance(Tick);
        Assert.Equal(4, half.Router.StartCalls.Count);
    }

    /// <summary>
    /// The state a phone that is simply not streaming sits in, all day. It is also what a call looks
    /// like. Timing out of it would tear down a working connection every time the user paused.
    /// </summary>
    [Fact]
    public async Task Linked_does_not_time_out()
    {
        Harness half = new();
        await half.ReachLinkedAsync();
        int changed = half.ChangedCount;

        half.Scheduler.Advance(TimeSpan.FromHours(1));

        Assert.Equal(MusicState.Linked, half.Half.State);
        Assert.Equal(changed, half.ChangedCount);
        Assert.Single(half.Sink.ConnectCalls);
        Assert.Equal(0, half.Sink.DisconnectCount);
        Assert.Equal(0, half.Router.StopCount);
    }

    [Fact]
    public async Task Endpoint_vanishing_while_Up_stops_the_route()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.SetEndpoint(false);

        Assert.Equal(MusicState.Linked, half.Half.State);
        Assert.Equal(1, half.Router.StopCount);
        Assert.Single(half.Sink.ConnectCalls);

        // Not a route failure: we stopped it, so nothing is counting down. The endpoint coming back
        // is what starts it again.
        Assert.Equal(0, half.Scheduler.PendingCount);
    }

    // --- Teardown ----------------------------------------------------------------------------

    [Fact]
    public async Task Link_absent_stops_the_router_and_disconnects_the_sink()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Half.OnLinkAbsent();

        Assert.Equal(MusicState.Off, half.Half.State);
        Assert.Equal(1, half.Router.StopCount);
        Assert.Equal(1, half.Sink.DisconnectCount);
    }

    [Fact]
    public async Task Suppressed_stops_the_router_and_disconnects_the_sink()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Half.OnSuppressed();

        Assert.Equal(MusicState.Off, half.Half.State);
        Assert.Equal(1, half.Router.StopCount);
        Assert.Equal(1, half.Sink.DisconnectCount);
    }

    [Theory]
    [InlineData(false, PhoneId)]
    [InlineData(true, null)]
    [InlineData(true, OtherPhoneId)]
    public async Task Losing_the_phone_or_the_switch_stops_the_router_and_disconnects_the_sink(
        bool enabled,
        string? phoneDeviceId)
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Half.Configure(enabled, phoneDeviceId, OutputId);

        Assert.Equal(MusicState.Off, half.Half.State);
        Assert.Equal(1, half.Router.StopCount);
        Assert.Equal(1, half.Sink.DisconnectCount);
    }

    /// <summary>
    /// Picking a phone is the first thing that ever happens, and there is nothing to tear down yet.
    /// An unconditional teardown here would disconnect a sink that was never connected and stop a
    /// route that was never started - harmless calls that make the log lie about what happened.
    /// </summary>
    [Fact]
    public void Configuring_a_phone_from_Off_does_not_disconnect_anything()
    {
        Harness half = new(enabled: false, phoneDeviceId: null, outputDeviceId: null);

        half.Half.Configure(enabled: true, PhoneId, OutputId);

        Assert.Equal(MusicState.Off, half.Half.State);
        Assert.Equal(0, half.Sink.DisconnectCount);
        Assert.Equal(0, half.Router.StopCount);
        Assert.Equal(0, half.ChangedCount);
        Assert.True(half.Half.Enabled);
    }

    /// <summary>
    /// No <c>Disconnect</c> and no <c>Off</c>. The manager owns the 3 s grace window that decides
    /// whether a closed connection was the phone leaving the room or the audio profile being
    /// dropped, and reporting the half down before that answer arrives is a tray icon that flaps on
    /// every one-second dropout.
    /// </summary>
    [Fact]
    public async Task Connection_closed_stops_the_route_and_waits_in_Linked()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Half.OnConnectionClosed();

        Assert.Equal(MusicState.Linked, half.Half.State);
        Assert.Equal(1, half.Router.StopCount);
        Assert.Equal(0, half.Sink.DisconnectCount);
        Assert.Equal(0, half.Scheduler.PendingCount);
    }

    [Fact]
    public async Task A_connect_that_completes_after_a_teardown_does_not_reach_Linked()
    {
        Harness half = new();
        half.Sink.DeferConnect = true;
        Task connecting = half.Half.OnLinkPresentAsync(connectPermitted: true);

        half.Half.OnLinkAbsent();
        Assert.Equal(MusicState.Off, half.Half.State);

        half.Sink.CompleteConnect(true);
        await connecting;

        Assert.Equal(MusicState.Off, half.Half.State);
        Assert.Equal(0, half.Scheduler.PendingCount);
    }

    /// <summary>
    /// The same guard, one turn later: the phone left and came back inside the first connect's
    /// lifetime, so the stale answer arrives with a newer attempt already in flight. It describes a
    /// connection nothing is holding.
    /// </summary>
    [Fact]
    public async Task A_connect_that_a_newer_one_superseded_does_not_reach_Linked()
    {
        Harness half = new();
        half.Sink.DeferConnect = true;
        Task first = half.Half.OnLinkPresentAsync(connectPermitted: true);

        half.Half.OnLinkAbsent();
        Task second = half.Half.OnLinkPresentAsync(connectPermitted: true);
        Assert.Equal(MusicState.Connecting, half.Half.State);
        Assert.Equal(2, half.Sink.ConnectCalls.Count);

        half.Sink.CompleteConnect(true);
        await first;
        Assert.Equal(MusicState.Connecting, half.Half.State);

        half.Sink.CompleteConnect(false);
        await second;
        Assert.Equal(MusicState.Backoff, half.Half.State);
    }

    // --- Reconcile ---------------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_moves_Up_to_Linked_when_the_endpoint_vanished()
    {
        Harness half = new();
        await half.ReachUpAsync();

        // No notification arrived. This is the drift the 30 s poll exists to correct.
        half.Endpoints.SinkCaptureEndpointPresent = false;
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(MusicState.Linked, half.Half.State);
        Assert.Equal(1, half.Router.StopCount);
    }

    [Fact]
    public async Task Reconcile_moves_Up_to_Linked_when_the_router_stopped_silently()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Router.DieSilently();
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(MusicState.Linked, half.Half.State);

        // A silent death is still a death: the route backs off exactly as it would have on an event.
        half.Scheduler.Advance(Seconds(2) - Tick);
        Assert.Single(half.Router.StartCalls);

        half.Scheduler.Advance(Tick);
        Assert.Equal(2, half.Router.StartCalls.Count);
    }

    [Fact]
    public async Task Reconcile_reconnects_from_Backoff_when_permitted()
    {
        Harness half = new();
        half.Sink.ConnectResult = false;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        half.Sink.ConnectResult = true;
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(MusicState.Linked, half.Half.State);
        Assert.Equal(2, half.Sink.ConnectCalls.Count);
        Assert.Equal(0, half.Scheduler.PendingCount);
    }

    [Fact]
    public async Task Reconcile_does_not_connect_when_connect_is_not_permitted()
    {
        Harness half = new();
        half.Sink.ConnectResult = false;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        await half.Half.ReconcileAsync(connectPermitted: false);

        Assert.Equal(MusicState.Backoff, half.Half.State);
        Assert.Single(half.Sink.ConnectCalls);
    }

    /// <summary>
    /// The whole of the auto-reconnect-off carve-out: the app may not start anything by itself, but
    /// it may finish what the user started. Without this, finding #2 leaves these users with a
    /// connection that never routes audio in most launches.
    /// </summary>
    [Fact]
    public async Task Connect_not_permitted_still_starts_the_route_from_Linked()
    {
        Harness half = new();
        await half.ReachLinkedAsync();

        half.Endpoints.SinkCaptureEndpointPresent = true;
        await half.Half.ReconcileAsync(connectPermitted: false);

        Assert.Equal(MusicState.Up, half.Half.State);
        Assert.Equal(new string?[] { OutputId }, half.Router.StartCalls);
    }

    /// <summary>
    /// One read per call, and none at all from a state that could not act on the answer. The real
    /// property is a live full endpoint enumeration - 152-282 ms, on the UI thread - and MMDevAPI
    /// delivers duplicates, so a second read in the same pass is a second appointment with a
    /// quarter-second stall for an answer that cannot have changed.
    /// </summary>
    [Fact]
    public async Task Endpoint_presence_is_read_once_per_call_and_never_from_Off()
    {
        Harness half = new();

        half.Half.OnEndpointsChanged();
        await half.Half.ReconcileAsync(connectPermitted: true);
        Assert.Equal(0, half.Endpoints.PresenceReads);

        await half.ReachLinkedAsync();

        int reads = half.Endpoints.PresenceReads;
        half.Half.OnEndpointsChanged();
        Assert.Equal(reads + 1, half.Endpoints.PresenceReads);

        reads = half.Endpoints.PresenceReads;
        await half.Half.ReconcileAsync(connectPermitted: true);
        Assert.Equal(reads + 1, half.Endpoints.PresenceReads);

        half.SetEndpoint(true);

        reads = half.Endpoints.PresenceReads;
        await half.Half.ReconcileAsync(connectPermitted: true);
        Assert.Equal(reads + 1, half.Endpoints.PresenceReads);
    }

    // --- NextRetryIn and Changed -------------------------------------------------------------

    [Fact]
    public async Task NextRetryIn_is_null_unless_a_connect_retry_is_pending()
    {
        Harness half = new();
        Assert.Null(half.Half.NextRetryIn);

        await half.ReachLinkedAsync();
        Assert.Null(half.Half.NextRetryIn);

        half.SetEndpoint(true);
        Assert.Null(half.Half.NextRetryIn);
    }

    [Fact]
    public async Task NextRetryIn_counts_down_to_the_scheduled_connect_retry()
    {
        Harness half = new();
        half.Sink.ConnectResult = false;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(Seconds(2), half.Half.NextRetryIn);

        half.Scheduler.Advance(TimeSpan.FromMilliseconds(1500));

        Assert.Equal(TimeSpan.FromMilliseconds(500), half.Half.NextRetryIn);
    }

    /// <summary>
    /// The route backoff is deliberately not reported. A route counting down happens in
    /// <c>Linked</c>, which the projection reports as <c>Connected</c> - "waiting for phone audio" -
    /// so the number would have no reader, and the one place the tray does print it
    /// (<c>RetryBackoff</c>) is reached only from <c>Backoff</c>.
    /// </summary>
    [Fact]
    public async Task NextRetryIn_ignores_a_pending_route_retry()
    {
        Harness half = new();
        half.Router.StartResult = false;
        await half.ReachLinkedAsync();

        half.SetEndpoint(true);

        Assert.Equal(MusicState.Linked, half.Half.State);
        Assert.Equal(1, half.Scheduler.PendingCount);
        Assert.Null(half.Half.NextRetryIn);
    }

    [Fact]
    public async Task Changed_fires_once_per_state_change()
    {
        Harness half = new();
        half.Sink.DeferConnect = true;

        Task connecting = half.Half.OnLinkPresentAsync(connectPermitted: true);
        Assert.Equal(1, half.ChangedCount);

        half.Sink.CompleteConnect(true);
        await connecting;
        Assert.Equal(2, half.ChangedCount);

        // Three notifications, one change. MMDevAPI reports every endpoint on the machine and
        // duplicates what it reports.
        half.Endpoints.SinkCaptureEndpointPresent = true;
        half.Half.OnEndpointsChanged();
        half.Half.OnEndpointsChanged();
        half.Half.OnEndpointsChanged();

        Assert.Equal(3, half.ChangedCount);
        Assert.Equal(MusicState.Up, half.Half.State);
        Assert.Single(half.Router.StartCalls);
    }

    [Fact]
    public async Task Changed_does_not_fire_when_nothing_changed()
    {
        Harness half = new();
        await half.ReachLinkedAsync();
        int changed = half.ChangedCount;

        half.Half.OnEndpointsChanged();
        half.Half.OnRouteStopped();
        half.Half.Configure(enabled: true, PhoneId, OutputId);
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(changed, half.ChangedCount);
        Assert.Equal(MusicState.Linked, half.Half.State);
        Assert.Equal(0, half.Router.StopCount);
        Assert.Equal(0, half.Sink.DisconnectCount);

        // Nor did any of them arm a timer. A stopped event for a route that was never running is
        // the one of these that could quietly start a route backoff counting down.
        Assert.Equal(0, half.Scheduler.PendingCount);
    }

    /// <summary>
    /// The half plus its four doubles, wired the way <c>ConnectionManager</c> will wire them.
    ///
    /// The one thing worth noticing is the <c>Stopped</c> subscription: <see cref="MusicHalf"/>
    /// subscribes to nothing itself. Every inbound event reaches it as a method call, already
    /// marshalled onto the UI thread by the manager, which is what makes a component with no locks
    /// in it correct.
    /// </summary>
    private sealed class Harness
    {
        public Harness(
            bool enabled = true,
            string? phoneDeviceId = PhoneId,
            string? outputDeviceId = OutputId)
        {
            Half = new MusicHalf(Sink, Router, Endpoints, Scheduler);
            Half.Changed += (_, _) => ChangedCount++;
            Router.Stopped += (_, _) => Half.OnRouteStopped();

            Half.Configure(enabled, phoneDeviceId, outputDeviceId);
        }

        public FakeAudioSinkService Sink { get; } = new();

        public FakeAudioRouter Router { get; } = new();

        public FakeEndpointMonitor Endpoints { get; } = new();

        public FakeScheduler Scheduler { get; } =
            new(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

        public MusicHalf Half { get; }

        public int ChangedCount { get; private set; }

        /// <summary>A connect that succeeds, with the endpoint not there yet - the usual arrival.</summary>
        public async Task ReachLinkedAsync()
        {
            await Half.OnLinkPresentAsync(connectPermitted: true);
            Assert.Equal(MusicState.Linked, Half.State);
        }

        public async Task ReachUpAsync()
        {
            await ReachLinkedAsync();
            SetEndpoint(true);
            Assert.Equal(MusicState.Up, Half.State);
        }

        /// <summary>
        /// What the manager does with an <c>EndpointsChanged</c> callback: the level the monitor now
        /// reports, then the marshalled notification that something moved.
        /// </summary>
        public void SetEndpoint(bool present)
        {
            Endpoints.SinkCaptureEndpointPresent = present;
            Half.OnEndpointsChanged();
        }
    }
}
