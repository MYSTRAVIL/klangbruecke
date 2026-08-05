using Klangbruecke.Audio;
using Klangbruecke.Bluetooth;
using Klangbruecke.Connection;
using Klangbruecke.Diagnostics;
using Klangbruecke.Tests.Diagnostics;
using Klangbruecke.Tests.Fakes;
using Xunit;

// System.Windows.Forms.LinkState is a public enum, and UseWindowsForms + ImplicitUsings puts it in
// every file of this project via a global using - so an unqualified LinkState here is CS0104,
// ambiguous. The alias picks ours, exactly as LinkMachineTests and SuppressionLatchTests do.
using LinkState = Klangbruecke.Connection.LinkState;

namespace Klangbruecke.Tests.Connection;

/// <summary>
/// The manager, driven entirely through its seams: a hand-cranked clock, a watcher that fires when a
/// test says so, and a link status read that answers whatever the test set. Nothing here sleeps,
/// allocates a timer, or needs a phone.
///
/// The device ids are the real ones from this machine's pairing, for the same reason
/// <c>CallsHalfTests</c> uses them: the transport correlation is address-based, and a synthetic id
/// would let a matcher bug pass.
/// </summary>
public sealed class ConnectionManagerTests : IDisposable
{
    // A2DP selector, 110a, ...\SNK. This is what the settings store as the selected phone.
    private const string PhoneId =
        @"\\?\BTHENUM#{0000110a-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&C01C6A90E174_C00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\SNK";

    // A second phone, as the settings would store it: the A2DP shape carrying a different address.
    private const string OtherPhoneId =
        @"\\?\BTHENUM#{0000110a-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&D8C0A63F1B22_C00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\SNK";

    // Phone-line selector, 111f, ...\service. Same phone as PhoneId, same address, different shape.
    private const string TransportId =
        @"\\?\BTHENUM#{0000111f-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&C01C6A90E174_C00000000#{bd41df2d-addd-4fc9-a194-b9881d2a2efa}\service";

    private const string OtherTransportId =
        @"\\?\BTHENUM#{0000111f-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&D8C0A63F1B22_C00000000#{bd41df2d-addd-4fc9-a194-b9881d2a2efa}\service";

    private const string OutputId = @"{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}";
    private const string OtherOutputId = @"{0.0.0.00000000}.{66666666-7777-8888-9999-aaaaaaaaaaaa}";

    private static readonly TransportCandidate PhoneTransport = new(TransportId, "MYSTRAPIX9");
    private static readonly TransportCandidate OtherTransport = new(OtherTransportId, "The other phone");

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a test will wait for the threadpool to hand the endpoint probe's answer back.
    ///
    /// <b>A failure budget, not a timing assertion.</b> Nothing waits for any part of it on a passing
    /// run - the handoff is a field read on a fake and completes in microseconds - so the only thing
    /// this number decides is how long a genuinely broken build takes to say so. It is therefore set
    /// far beyond any plausible scheduling hiccup rather than near it: a machine busy enough to lose
    /// a five-second race would turn a correct implementation red, and a test that fails when the
    /// build is fine is worse than one that takes half a minute to report a build that is not.
    ///
    /// The cost is real and worth stating: a mutant that keeps the probe on the calling thread is
    /// killed by this timeout rather than by an immediate assert, so a mutation sweep pays it once
    /// per such run.
    /// </summary>
    private static readonly TimeSpan HandoffBudget = TimeSpan.FromSeconds(30);

    private readonly ILog _originalLog = Log.Current;
    private readonly RecordingLog _log = new();

    public ConnectionManagerTests() => Log.Current = _log;

    public void Dispose() => Log.Current = _originalLog;

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    // --- start ---------------------------------------------------------------------------------

    [Fact]
    public void Start_with_no_saved_phone_is_Idle()
    {
        using Harness h = new(phoneDeviceId: null);

        Assert.Equal(ConnectionState.Idle, h.Manager.State);
        Assert.Equal("no phone selected", h.Manager.Detail);
        Assert.Empty(h.Link.WatchCalls);
        Assert.Empty(h.Sink.ConnectCalls);
    }

    [Fact]
    public void Start_with_a_saved_phone_begins_watching()
    {
        using Harness h = new();

        Assert.Equal(new[] { PhoneId }, h.Link.WatchCalls);
        Assert.Equal(ConnectionState.Discovering, h.Manager.State);

        // Watching, not connecting. Nothing has seen the phone yet, and selection is an intent.
        Assert.Empty(h.Sink.ConnectCalls);
        Assert.True(h.Endpoints.Started);
        Assert.True(h.Power.Started);
    }

    /// <summary>
    /// Start is wired from a constructor that Task 17 owns, and a second call would double every
    /// subscription: two watchers, two reconcile timers, two of every inbound event.
    /// </summary>
    [Fact]
    public void Start_is_idempotent()
    {
        using Harness h = new();

        h.Manager.Start();

        Assert.Equal(new[] { PhoneId }, h.Link.WatchCalls);
        Assert.Equal(1, h.Scheduler.PendingCount);
    }

    [Fact]
    public void Device_appearing_connects_both_halves()
    {
        using Harness h = new();

        h.Link.RaiseAppeared();

        Assert.Equal(new[] { PhoneId }, h.Sink.ConnectCalls);
        Assert.Equal(new[] { TransportId }, h.Calls.ConnectCalls);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    // --- selection -----------------------------------------------------------------------------

    /// <summary>
    /// The id is the user's answer to "which phone", not a record of what happened to connect, so it
    /// is written before anything is attempted - the packaged build has to be able to come back to it
    /// after a reboot even when this attempt fails (docs/FINDINGS.md section 8).
    /// </summary>
    [Fact]
    public void Selecting_a_phone_saves_it_before_connecting()
    {
        using Harness h = new(phoneDeviceId: null);

        int connectsAtFirstSave = -1;
        string? idAtFirstSave = "not saved";
        h.Settings.OnSave = () =>
        {
            if (connectsAtFirstSave >= 0)
            {
                return;
            }

            connectsAtFirstSave = h.Sink.ConnectCalls.Count;
            idAtFirstSave = h.Settings.PhoneDeviceId;
        };

        h.Manager.SelectPhone(PhoneId);

        Assert.Equal(PhoneId, idAtFirstSave);
        Assert.Equal(0, connectsAtFirstSave);

        Assert.Equal(new[] { PhoneId }, h.Sink.ConnectCalls);
        Assert.Equal(new[] { PhoneId }, h.Link.WatchCalls);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    /// <summary>
    /// The one release that is neither a Disconnect nor a switch: the role would otherwise sit on a
    /// handset the user has stopped using and go on offering this PC there.
    /// </summary>
    [Fact]
    public void Selecting_a_different_phone_moves_the_hands_free_role()
    {
        using Harness h = new();
        h.ReachConnected();
        h.Calls.Transports = new[] { PhoneTransport, OtherTransport };

        h.Manager.SelectPhone(OtherPhoneId);

        Assert.Equal(1, h.Calls.DisconnectCount);
        Assert.Equal(1, h.Sink.DisconnectCount);
        Assert.Equal(new[] { PhoneId, OtherPhoneId }, h.Sink.ConnectCalls);
        Assert.Equal(new[] { TransportId, OtherTransportId }, h.Calls.ConnectCalls);
    }

    /// <summary>
    /// Re-picking the phone that is already connected must not flap anything. Every
    /// unregister/re-register round trip makes this PC disappear and reappear in the handset's own
    /// call-audio picker, on a screen this app cannot see.
    /// </summary>
    [Fact]
    public void Reselecting_the_same_phone_does_not_flap_the_hands_free_role()
    {
        using Harness h = new();
        h.ReachConnected();

        h.Manager.SelectPhone(PhoneId);

        Assert.Equal(0, h.Calls.DisconnectCount);
        Assert.Equal(0, h.Sink.DisconnectCount);
        Assert.Single(h.Sink.ConnectCalls);
        Assert.Single(h.Calls.ConnectCalls);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    [Fact]
    public void Deselecting_the_phone_unregisters_calls_and_stops_the_router()
    {
        using Harness h = new();
        h.ReachRouting();

        int saves = h.Settings.SaveCount;

        h.Manager.DeselectPhone();

        Assert.Equal(1, h.Calls.DisconnectCount);
        Assert.Equal(1, h.Sink.DisconnectCount);
        Assert.True(h.Router.StopCount >= 1, "the route was left running over a phone nobody selected");
        Assert.False(h.Router.IsRunning);
        Assert.Equal(1, h.Link.StopWatchingCount);
        Assert.Equal(ConnectionState.Idle, h.Manager.State);

        // Written, not just held. An in-memory clear that never reaches the file comes back as the
        // old phone on the next start, which is the "it forgot my setting" bug this app exists to end.
        Assert.Null(h.Settings.PhoneDeviceId);
        Assert.Equal(saves + 1, h.Settings.SaveCount);
    }

    [Fact]
    public void Selecting_an_output_restarts_the_route_without_touching_bluetooth()
    {
        using Harness h = new();
        h.ReachRouting();

        int saves = h.Settings.SaveCount;

        h.Manager.SelectOutput(OtherOutputId);

        Assert.Equal(new string?[] { OutputId, OtherOutputId }, h.Router.StartCalls);
        Assert.Single(h.Sink.ConnectCalls);
        Assert.Equal(0, h.Sink.DisconnectCount);

        Assert.Equal(OtherOutputId, h.Settings.OutputDeviceId);
        Assert.Equal(saves + 1, h.Settings.SaveCount);
    }

    /// <summary>
    /// A re-point that cannot open the new endpoint leaves the half believing it is routing audio
    /// over a route that is not running. Without this the tray reads "music and calls up" over
    /// silence until the reconcile notices, up to 30 s later.
    /// </summary>
    /// <param name="startResult">
    /// Both ways a start can fail, because they are not the same failure. The false is the ordinary
    /// one; the true is the measured lie - <c>AudioRouter.Start</c> returns true for a capture that
    /// died inside <c>StartRecording</c>, because the capture thread dies asynchronously - and a
    /// caller that believed the bool would leave the half claiming Up over silence.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selecting_an_output_that_cannot_start_returns_the_half_to_waiting(bool startResult)
    {
        using Harness h = new();
        h.ReachRouting();
        h.Router.StartResult = startResult;
        h.Router.StartLeavesItRunning = false;

        h.Manager.SelectOutput(OtherOutputId);

        Assert.False(h.Router.IsRunning);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
        Assert.Equal("waiting for phone audio", h.Manager.Detail);
    }

    [Fact]
    public void ListOutputDevices_comes_from_the_router()
    {
        using Harness h = new();
        h.Router.Outputs = new[] { new AudioOutputDevice(OutputId, "Speakers"), new AudioOutputDevice(OtherOutputId, "Headset") };

        Assert.Equal(h.Router.Outputs, h.Manager.ListOutputDevices());
    }

    [Fact]
    public async Task FindPhonesAsync_comes_from_the_sink()
    {
        using Harness h = new();
        h.Sink.Devices = new[] { new PhoneDevice(PhoneId, "MYSTRAPIX9") };

        Assert.Equal(h.Sink.Devices, await h.Manager.FindPhonesAsync());
    }

    /// <summary>
    /// A selection changes state synchronously - the latch is cleared, the link machine is reset -
    /// and handing the announcement to the pass is not enough: a pass that started under the stall
    /// threshold ago returns before it publishes anything. So reselecting the same phone while
    /// suppressed cleared the latch and repainted nothing, and the tray went on saying the app was
    /// disconnected until a tick that could be half a minute away.
    /// </summary>
    [Fact]
    public void Reselecting_the_same_phone_repaints_even_while_a_pass_is_in_flight()
    {
        using Harness h = new();
        h.ReachConnected();

        h.Manager.RequestDisconnect();
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);

        // A pass wedged on its link read, so the one the click starts defers to it.
        h.Link.DeferRead = true;
        h.Scheduler.Advance(Seconds(30));
        Assert.Equal(1, h.Link.ReadCount);

        h.Manager.SelectPhone(PhoneId);

        // The pass really did decline - this is the condition the bug needed, not an assumption.
        Assert.Equal(1, h.Link.ReadCount);

        // And the tray was told anyway.
        Assert.Equal(ConnectionState.Discovering, h.Manager.State);
    }

    // --- the grace window ----------------------------------------------------------------------

    /// <summary>
    /// The ACL link is alive and only the audio profile went, which is what the phone dropping this
    /// PC looks like. Reconnecting would fight the user.
    /// </summary>
    [Fact]
    public void Connection_closed_with_the_link_still_up_suppresses_after_the_grace_window()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Grace);

        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
        Assert.Equal("disconnected until the phone leaves and returns", h.Manager.Detail);
        Assert.Equal(1, h.Sink.DisconnectCount);
        Assert.Equal(1, h.Calls.DisconnectCount);
    }

    [Fact]
    public void Connection_closed_with_the_link_gone_goes_to_Discovering()
    {
        using Harness h = new();
        h.ReachRouting();
        h.Link.Status = BluetoothLinkStatus.Disconnected;

        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Grace);

        Assert.Equal(ConnectionState.Discovering, h.Manager.State);

        // Out of range is not a decision, so nothing is latched and the return trip reconnects.
        Assert.Equal(0, h.Calls.DisconnectCount);
    }

    /// <summary>
    /// A read that could not answer is not evidence the link is up. Guessing the other way leaves the
    /// app suppressed next to a phone that walked out of the building.
    /// </summary>
    [Fact]
    public void Connection_closed_with_an_unknown_link_goes_to_Discovering()
    {
        using Harness h = new();
        h.ReachRouting();
        h.Link.Status = BluetoothLinkStatus.Unknown;

        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Grace);

        Assert.Equal(ConnectionState.Discovering, h.Manager.State);
    }

    /// <summary>
    /// The window exists so a one-second dropout does not flap the tray. The route stops immediately -
    /// there is nothing to route - but nothing is decided, nothing is disconnected, and the reported
    /// state does not move.
    /// </summary>
    [Fact]
    public void No_action_is_taken_before_the_grace_window_elapses()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Seconds(2.9));

        Assert.Equal(ConnectionState.Connected, h.Manager.State);
        Assert.Equal(0, h.Sink.DisconnectCount);
        Assert.Equal(0, h.Calls.DisconnectCount);
        Assert.Equal(0, h.Link.ReadCount);
    }

    /// <summary>
    /// A connection that reports Closed twice, or a reconcile that finds it gone on two ticks running,
    /// must not leave two windows armed - each one reads the link and decides again.
    /// </summary>
    [Fact]
    public void A_second_connection_closed_does_not_stack_grace_windows()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Grace);

        Assert.Equal(1, h.Link.ReadCount);
        Assert.Equal(1, h.Sink.DisconnectCount);
    }

    /// <summary>
    /// The window's handle is dropped the moment it fires, so a second Closed arriving while the
    /// first window's link read is still outstanding arms a second window on top of it. The late
    /// answer must decide nothing.
    ///
    /// This is the worst read in the class to be stale on. It is the one that separates a deliberate
    /// disconnect from a range exit, and getting it wrong does not merely mislabel: recording an
    /// absence that never happened arms the very expiry that undoes the suppression the user asked
    /// for, one reconcile tick later.
    /// </summary>
    [Fact]
    public void A_grace_window_that_was_superseded_does_not_decide()
    {
        using Harness h = new();
        h.ReachRouting();

        // Window one asks, and gets no answer.
        h.Link.DeferRead = true;
        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Grace);
        Assert.Equal(1, h.Link.ReadCount);

        // Window two asks again, and the link is up: the phone dropped the audio profile.
        h.Link.DeferRead = false;
        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Grace);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
        Assert.Equal("The phone dropped the audio connection.", h.Status[^1].Text);

        // And now window one answers, with the opposite verdict.
        h.Link.CompleteRead(BluetoothLinkStatus.Disconnected);

        Assert.Equal("The phone dropped the audio connection.", h.Status[^1].Text);

        // The damage a stale "out of range" does is not immediate: it records an absence, and the
        // next Connected poll then reads that as the phone having left and returned, which expires
        // the deliberate suppression and reconnects the phone the user just disconnected.
        h.Scheduler.Advance(Seconds(95));

        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
        Assert.Single(h.Sink.ConnectCalls);
    }

    /// <summary>
    /// A window's question is about the phone that was selected when it opened. Picking a phone -
    /// even the same one - is the most explicit "connect to this" the app has, and a window that
    /// opened a moment earlier must not come back three seconds later and suppress it.
    /// </summary>
    [Fact]
    public void Picking_a_phone_voids_a_grace_window_that_is_already_open()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Seconds(1));

        h.Manager.SelectPhone(PhoneId);

        // The window would have fired here, read a link that is still up, and called it deliberate.
        h.Scheduler.Advance(Seconds(2));

        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    /// <summary>
    /// Voiding it means cancelling the wait, not only disowning the answer. A window whose handle is
    /// left armed goes on blocking the next one - <c>OnConnectionClosed</c> declines to arm while one
    /// is armed - so the next real disconnect would get no decision at all until the reconcile
    /// noticed, which is the drift the grace window exists to answer promptly.
    /// </summary>
    [Fact]
    public void A_disconnect_after_picking_a_phone_still_gets_its_own_window()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Seconds(1));

        h.Manager.SelectPhone(PhoneId);

        // A second disconnect, while the first window's original deadline has still not arrived.
        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Grace);

        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
    }

    /// <summary>
    /// And a window that has already asked - fired, and waiting on the radio - is past the reach of
    /// any timer. Only the generation can stop that one coming back and suppressing the selection the
    /// user made while it was waiting.
    /// </summary>
    [Fact]
    public void Picking_a_phone_voids_a_grace_window_that_has_already_asked()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Link.DeferRead = true;
        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Grace);
        Assert.Equal(1, h.Link.ReadCount);

        h.Link.DeferRead = false;
        h.Manager.SelectPhone(PhoneId);

        // The window's read answers at last, with a link that is up - which it would call deliberate.
        h.Link.CompleteRead(BluetoothLinkStatus.Connected);

        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    /// <summary>
    /// The single most important transition in the app: a call takes the capture endpoint away while
    /// the A2DP connection stays open, and reconnecting there is the predecessor app's defining bug -
    /// the phone had to be re-picked from the tray after every call.
    /// </summary>
    [Fact]
    public void A_route_that_dies_does_not_touch_bluetooth()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Router.Die();

        Assert.Single(h.Sink.ConnectCalls);
        Assert.Equal(0, h.Sink.DisconnectCount);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
        Assert.Equal("waiting for phone audio", h.Manager.Detail);
    }

    /// <summary>
    /// The other half of the same asymmetry: music goes, registration stays. Holding the hands-free
    /// role is what puts this PC in the phone's own call-audio picker, so it has to survive the phone
    /// walking out - releasing it here costs the user the entry on a screen this app cannot see.
    /// </summary>
    [Fact]
    public void The_phone_leaving_the_room_tears_music_down_and_leaves_calls_registered()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Link.RaiseRemoved();

        Assert.Equal(1, h.Sink.DisconnectCount);
        Assert.False(h.Router.IsRunning);
        Assert.Equal(0, h.Calls.DisconnectCount);
        Assert.Equal(ConnectionState.Discovering, h.Manager.State);
    }

    /// <summary>
    /// Finding #2, and the case that produced it: the capture endpoint's lifetime is not this app's
    /// connection's (docs/FINDINGS.md section 4), and it was measured Active before this app opened a
    /// connection at all. One route, not two -
    /// a second start would tear the first down and restart it, which is a gap in the audio for
    /// exactly the users whose endpoint was already there.
    /// </summary>
    [Fact]
    public void An_endpoint_that_is_already_present_starts_exactly_one_route()
    {
        using Harness h = new();
        h.SetEndpointPresent(true);

        h.Link.RaiseAppeared();

        Assert.Equal(new string?[] { OutputId }, h.Router.StartCalls);
        Assert.True(h.Router.IsRunning);
    }

    /// <summary>
    /// The preference has to reach the half as well as the live route, or the next start after any
    /// drop - a call ending, a range exit and return - goes back to the output the user changed away
    /// from, with nothing to say why.
    /// </summary>
    [Fact]
    public void The_chosen_output_is_used_by_the_next_route_start_too()
    {
        using Harness h = new();
        h.ReachRouting();
        h.Manager.SelectOutput(OtherOutputId);

        // The route dies and the half brings it back on its own backoff. The manager is not involved.
        h.Router.Die();
        h.Scheduler.Advance(Seconds(2));

        Assert.Equal(OtherOutputId, h.Router.StartCalls[^1]);
        Assert.True(h.Router.IsRunning);
    }

    // --- suppression ---------------------------------------------------------------------------

    [Fact]
    public void Tray_disconnect_suppresses()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Manager.RequestDisconnect();

        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
        Assert.Equal("disconnected until the phone leaves and returns", h.Manager.Detail);
        Assert.Equal(1, h.Sink.DisconnectCount);
        Assert.Equal(1, h.Calls.DisconnectCount);
        Assert.True(h.Router.StopCount >= 1);
    }

    [Fact]
    public void Suppressed_re_arms_after_the_link_drops_and_returns()
    {
        using Harness h = new();
        h.ReachConnected();
        h.Manager.RequestDisconnect();

        h.Link.RaiseRemoved();
        h.Link.RaiseAppeared();

        Assert.Equal(ConnectionState.Connected, h.Manager.State);
        Assert.Equal(2, h.Sink.ConnectCalls.Count);
    }

    /// <summary>
    /// The reconcile reports a level every 30 s, not an edge, so a latch that expired on any Present
    /// report would undo a deliberate Disconnect about a minute after the user asked for it.
    /// </summary>
    [Fact]
    public void Suppressed_does_not_re_arm_while_the_phone_stays_in_range()
    {
        using Harness h = new();
        h.ReachConnected();
        h.Manager.RequestDisconnect();

        h.Scheduler.Advance(Seconds(95));

        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
        Assert.Single(h.Sink.ConnectCalls);
    }

    /// <summary>
    /// A deliberate disconnect expires when the phone leaves and returns. The click grant must not
    /// outlive it, or auto-reconnect off would be undone by a selection the user made minutes ago and
    /// a Disconnect they made since.
    /// </summary>
    [Fact]
    public void A_tray_disconnect_ends_a_click_initiated_grant()
    {
        using Harness h = new(phoneDeviceId: null, autoReconnect: false);
        h.Sink.ConnectResult = false;
        h.Manager.SelectPhone(PhoneId);
        Assert.Single(h.Sink.ConnectCalls);

        h.Manager.RequestDisconnect();

        h.Link.RaiseRemoved();
        h.Link.RaiseAppeared();

        Assert.Single(h.Sink.ConnectCalls);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
    }

    // --- the reconcile -------------------------------------------------------------------------

    [Fact]
    public void Reconcile_runs_every_thirty_seconds()
    {
        using Harness h = new();

        // Straddled, so the period is pinned from below as well as above: a count after 95 s alone is
        // satisfied by anything from 24 s to 31 s.
        h.Scheduler.Advance(Seconds(29.9));
        Assert.Equal(0, h.Link.ReadCount);

        h.Scheduler.Advance(Seconds(0.2));
        Assert.Equal(1, h.Link.ReadCount);

        h.Scheduler.Advance(Seconds(64.9));
        Assert.Equal(3, h.Link.ReadCount);
    }

    /// <summary>
    /// At 30 s an unconditional line is 2,880 entries a day, and every one of them is synchronous file
    /// I/O under a lock on the UI thread.
    /// </summary>
    [Fact]
    public void Reconcile_writes_no_log_line_when_nothing_changed()
    {
        using Harness h = new();
        h.ReachRouting();
        _log.Entries.Clear();

        h.Scheduler.Advance(Seconds(95));

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Reconcile_logs_when_something_changed()
    {
        using Harness h = new();
        h.ReachRouting();
        _log.Entries.Clear();

        // A route that stopped without saying so. Only a level read finds this one, and it is the one
        // drift in the five checks that no other component logs for itself.
        h.Router.DieSilently();

        // And it stays down, so the condition is still there on the second and third ticks. That is
        // what makes the brief's row - "one entry, not three" - a real distinction: a single tick
        // cannot tell a line written per correction from a line written per tick, and a drift that
        // repairs itself would produce a second honest line for the repair.
        h.Router.StartResult = false;

        h.Scheduler.Advance(Seconds(95));

        Assert.Single(_log.Entries);
    }

    /// <summary>
    /// Check 2 of the five: the connection object can go away without ever reporting Closed - across a
    /// suspend, most of all - and the level read is the only thing that would ever notice.
    /// </summary>
    [Fact]
    public void Reconcile_that_finds_the_connection_gone_opens_the_grace_window()
    {
        using Harness h = new();
        h.ReachRouting();

        // Not PublishState: this is the connection vanishing with no event at all.
        h.Sink.Disconnect();

        h.Scheduler.Advance(Seconds(30));
        Assert.Equal(ConnectionState.Connected, h.Manager.State);

        h.Scheduler.Advance(Grace);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
    }

    /// <summary>
    /// A pass has four awaits in it and the link read is a real round trip to the radio, so a forced
    /// pass - a phone picked, a resume, the setting coming back on - can land on top of the periodic
    /// one. Two interleaved passes would each decide against a link status the other is still acting
    /// on, and both would open a grace window against the same closed connection.
    ///
    /// Nothing is lost by declining: the pass in flight resumes against whatever the settings say
    /// when it comes back, which is the new phone.
    /// </summary>
    [Fact]
    public void A_forced_reconcile_does_not_run_on_top_of_one_already_in_flight()
    {
        using Harness h = new();
        h.Calls.Transports = new[] { PhoneTransport, OtherTransport };
        h.Link.DeferRead = true;

        h.Power.RaiseResumed();
        h.Scheduler.Advance(Seconds(5));
        Assert.Equal(1, h.Link.ReadCount);

        h.Manager.SelectPhone(OtherPhoneId);
        Assert.Equal(1, h.Link.ReadCount);

        h.Link.CompleteRead(BluetoothLinkStatus.Connected);

        Assert.Equal(new[] { OtherPhoneId }, h.Sink.ConnectCalls);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    /// <summary>
    /// And it defers to a pass in flight, not to one that has stopped answering. A read that never
    /// completes would otherwise hold the guard for the life of the process and stop the only
    /// backstop the app has - which is the predecessor's defining bug rebuilt out of a mutex.
    /// </summary>
    [Fact]
    public void A_reconcile_that_never_answers_does_not_stop_the_loop()
    {
        using Harness h = new();
        h.Link.DeferRead = true;

        h.Scheduler.Advance(Seconds(30));
        Assert.Equal(1, h.Link.ReadCount);

        // The tick at 60 s finds the pass from 30 s still waiting, and 30 seconds is well past the
        // 25 s after which a pass stops being one to defer to.
        h.Scheduler.Advance(Seconds(30));
        Assert.Equal(2, h.Link.ReadCount);
    }

    /// <summary>
    /// And a pass that was given up on and has now finally answered must not hand its replacement's
    /// place away on the way out. The overlap this whole guard exists to prevent would then happen
    /// anyway, in the one case where two passes really are running at once.
    /// </summary>
    [Fact]
    public void A_reconcile_that_was_given_up_on_does_not_release_its_replacement()
    {
        using Harness h = new();
        h.Link.DeferRead = true;

        h.Scheduler.Advance(Seconds(30));
        h.Scheduler.Advance(Seconds(30));
        Assert.Equal(2, h.Link.ReadCount);

        // The first pass answers at last, while the second is still waiting on its own read.
        h.Link.CompleteRead(BluetoothLinkStatus.Connected);

        h.Power.RaiseResumed();
        h.Scheduler.Advance(Seconds(5));

        Assert.Equal(2, h.Link.ReadCount);
    }

    /// <summary>
    /// Holding the marker is only half of it. A pass that finally answers 45 s late must also stop
    /// <em>acting</em>: its link status is 45 seconds old, and writing it into the machine whose job
    /// is to remove drift - then running both halves against a state its replacement is halfway
    /// through establishing - is exactly the interleaving the guard is documented as preventing.
    /// </summary>
    [Fact]
    public void A_reconcile_that_was_given_up_on_does_not_act_on_its_stale_answer()
    {
        using Harness h = new();

        // Pass A starts at 30 s and hangs.
        h.Link.DeferRead = true;
        h.Scheduler.Advance(Seconds(30));

        // Pass B, at 60 s, answers at once and brings both halves up.
        h.Link.DeferRead = false;
        h.Scheduler.Advance(Seconds(30));
        Assert.Equal(ConnectionState.Connected, h.Manager.State);

        // And then the connection goes away with no event at all, which is what check 2 exists to
        // catch - so a pass that goes on running has something to find.
        h.Sink.Disconnect();

        int reads = h.Link.ReadCount;
        int connects = h.Sink.ConnectCalls.Count;

        // A's read comes back at last, carrying a status from before the phone was even found. It
        // must decide nothing: not the link status it is holding, and not the checks below it.
        h.Link.CompleteRead(BluetoothLinkStatus.Disconnected);

        Assert.Equal(0, h.Router.StopCount);
        Assert.Equal(connects, h.Sink.ConnectCalls.Count);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);

        // A grace window opened by the stale pass would read the link again three seconds later.
        h.Scheduler.Advance(Grace);
        Assert.Equal(reads, h.Link.ReadCount);
    }

    /// <summary>
    /// The link read is not the only place a pass gives up the thread. A connect attempt inside
    /// <c>MusicHalf.ReconcileAsync</c> awaits a radio too, and a pass superseded there must stop just
    /// as flatly - otherwise it goes on to drive the calls half with a permission flag and a link
    /// state it computed a minute ago.
    /// </summary>
    [Fact]
    public void A_reconcile_superseded_inside_a_half_stops_there()
    {
        using Harness h = new();

        // Pass A gets as far as opening the connection and hands off the thread there.
        h.Sink.DeferConnect = true;
        h.Scheduler.Advance(Seconds(30));
        Assert.Equal(1, h.Sink.PendingConnects);

        // Pass B, a period later, runs to completion and takes the marker with it.
        h.Scheduler.Advance(Seconds(30));

        _log.Entries.Clear();

        // A's connect answers at last. Its own idea of "before" is a minute old and describes a
        // world with no link and no halves - reported now, it would credit this pass with every
        // correction B made and put a line in the log that describes a tick that did not happen.
        h.Sink.CompleteConnect(true);

        Assert.Empty(_log.Entries);
    }

    /// <summary>
    /// The threshold is deliberately shorter than the tick that has to clear it. On a real timer a
    /// pass starts a hair after the tick that launched it, so a threshold of one whole period would
    /// have the next tick miss it by microseconds and defer again - recovery costing two periods
    /// instead of one, invisibly, and never in a test where virtual time lands on the boundary.
    /// </summary>
    [Fact]
    public void The_stall_threshold_is_shorter_than_the_tick_that_clears_it()
    {
        using Harness h = new();
        h.Link.DeferRead = true;

        h.Scheduler.Advance(Seconds(30));
        Assert.Equal(1, h.Link.ReadCount);

        // A forced pass 24.9 s into the wedged one still defers to it.
        h.Scheduler.Advance(Seconds(19.9));
        h.Power.RaiseResumed();
        h.Scheduler.Advance(Seconds(5));
        Assert.Equal(1, h.Link.ReadCount);

        // 29.9 s in - still short of a whole period, and still before the 60 s tick - it does not.
        h.Power.RaiseResumed();
        h.Scheduler.Advance(Seconds(5));
        Assert.Equal(2, h.Link.ReadCount);
    }

    // --- the poll as the music half's backstop ---------------------------------------------------
    //
    // The poll exists precisely because a watcher edge can never arrive, so a poll that corrects the
    // link and tells nobody is the backstop failing at the one job it has. Measured in the packaged
    // 0.2.0.0 run: a false watcher Added for a phone that was not in the room put the music half in
    // Backoff, the poll corrected Present -> Absent on its second tick, the tray read Discovering -
    // and the half went on opening the radio every 60 s for as long as the app ran.

    /// <summary>
    /// The correction reaching the half. <b>The damage is not the wasted attempts</b> - it is that
    /// <c>OnLinkPresentAsync</c> acts only from <c>Off</c>, so a half left in Backoff refuses the
    /// watcher's Added edge when the phone actually comes back, and recovery then waits on a 60 s
    /// retry that is never reset without a success. That is range-exit-and-return, which is the
    /// predecessor app's defining bug.
    /// </summary>
    [Fact]
    public void A_polled_range_exit_stands_the_music_half_down_and_disarms_its_retry()
    {
        using Harness h = new(enableCalls: false);
        h.Sink.ConnectResult = false;

        // The watcher says the phone is there and the radio disagrees, which is the measured case.
        h.Link.RaiseAppeared();
        h.Link.Status = BluetoothLinkStatus.Disconnected;
        Assert.Equal(ConnectionState.RetryBackoff, h.Manager.State);

        // Two polls, because one failed read is not a range exit.
        h.Scheduler.Advance(Seconds(60));

        Assert.Equal(1, h.Sink.DisconnectCount);
        Assert.Equal(ConnectionState.Discovering, h.Manager.State);

        // Off, not Backoff, and this is the assertion that matters: nothing is armed to open the
        // radio again while the phone is gone. The state above reads Discovering either way, which
        // is exactly how the projection and the behaviour came to disagree.
        int connects = h.Sink.ConnectCalls.Count;
        h.Scheduler.Advance(Seconds(600));
        Assert.Equal(connects, h.Sink.ConnectCalls.Count);
    }

    /// <summary>
    /// And the debounce is not bypassed on the way. <c>ILinkMonitor.ReadLinkStatusAsync</c> collapses
    /// every failed read to <c>Unknown</c>, so one non-Connected poll is indistinguishable from a
    /// transient hiccup - and tearing down on it stops the router and disconnects the sink mid-song.
    /// </summary>
    [Fact]
    public void One_non_connected_poll_does_not_tear_the_music_half_down()
    {
        using Harness h = new(enableCalls: false);
        h.ReachRouting();

        h.Link.Status = BluetoothLinkStatus.Unknown;
        h.Scheduler.Advance(Seconds(30));

        Assert.Equal(0, h.Sink.DisconnectCount);
        Assert.True(h.Router.IsRunning);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);

        // And a Connected read starts the run over rather than leaving it half finished, so the next
        // failed read is the first of a new run and not the second of the old one.
        h.Link.Status = BluetoothLinkStatus.Connected;
        h.Scheduler.Advance(Seconds(30));

        h.Link.Status = BluetoothLinkStatus.Unknown;
        h.Scheduler.Advance(Seconds(30));

        Assert.Equal(0, h.Sink.DisconnectCount);
        Assert.True(h.Router.IsRunning);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    /// <summary>
    /// <b>Edge-triggered, and this is the assertion that says so.</b> Every other tick in a range
    /// exit reads <c>Absent</c> too, and a teardown on the level rather than on the transition would
    /// bump <c>MusicHalf._generation</c> and cancel the half's timers every 30 s - discarding a
    /// click-granted attempt that a later pass started.
    ///
    /// The state below is staged, not contrived: re-picking the same phone is the one input that
    /// resets the link machine to <c>Absent</c> while deliberately leaving the half alone -
    /// <c>MusicHalf.Configure</c> tears down only on a phone change - so the countdown the click just
    /// granted is sitting there with the link reading Absent and no transition to report.
    /// </summary>
    [Fact]
    public void A_poll_that_moves_nothing_does_not_stand_a_backing_off_half_down()
    {
        using Harness h = new(enableCalls: false);
        h.Sink.ConnectResult = false;

        h.Link.RaiseAppeared();
        Assert.Equal(ConnectionState.RetryBackoff, h.Manager.State);
        Assert.Single(h.Sink.ConnectCalls);

        // The radio stops answering, and the user re-picks the same phone. The pass that click
        // starts reads Absent - and moves nothing, because the machine was already Absent.
        h.Link.Status = BluetoothLinkStatus.Disconnected;
        h.Manager.SelectPhone(PhoneId);

        Assert.Equal(0, h.Sink.DisconnectCount);

        // The countdown is still armed, which is the whole of it.
        h.Scheduler.Advance(Seconds(2));
        Assert.Equal(2, h.Sink.ConnectCalls.Count);
    }

    /// <summary>
    /// Music only. There is deliberately no symmetric call for the calls half: registration is not
    /// link-scoped, holding it is what puts this PC in the phone's own call-audio picker, and every
    /// unregister/re-register round trip makes the PC vanish from and reappear in that list - see
    /// <c>CallsHalf</c>'s missing <c>OnLinkAbsent</c>, and <c>OnDeviceRemoved</c>, which this matches.
    /// </summary>
    [Fact]
    public void A_polled_range_exit_leaves_the_calls_half_registered()
    {
        using Harness h = new();
        h.ReachRouting();
        Assert.Equal(new[] { TransportId }, h.Calls.ConnectCalls);

        h.Link.Status = BluetoothLinkStatus.Disconnected;
        h.Scheduler.Advance(Seconds(60));

        // Music is down.
        Assert.Equal(1, h.Sink.DisconnectCount);
        Assert.False(h.Router.IsRunning);

        // The role is not, and it was neither released nor re-registered.
        Assert.Equal(0, h.Calls.DisconnectCount);
        Assert.Equal(new[] { TransportId }, h.Calls.ConnectCalls);

        // And the phone comes back to a half that can take it. Off is the one state
        // OnLinkPresentAsync acts from, which is the whole reason the teardown had to happen.
        h.Link.Status = BluetoothLinkStatus.Connected;
        h.Scheduler.Advance(Seconds(30));

        Assert.Equal(2, h.Sink.ConnectCalls.Count);
        Assert.Equal(new[] { TransportId }, h.Calls.ConnectCalls);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    // --- resume --------------------------------------------------------------------------------

    /// <summary>
    /// The Bluetooth stack is not back at the moment the event fires, and an immediate attempt only
    /// burns the first backoff step for nothing.
    /// </summary>
    [Fact]
    public void Resume_does_not_reconcile_immediately()
    {
        using Harness h = new();

        h.Power.RaiseResumed();
        h.Scheduler.Advance(Seconds(4));

        Assert.Equal(0, h.Link.ReadCount);
    }

    [Fact]
    public void Resume_reconciles_after_five_seconds()
    {
        using Harness h = new();

        h.Power.RaiseResumed();
        h.Scheduler.Advance(Seconds(5));

        Assert.Equal(1, h.Link.ReadCount);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    // --- auto-reconnect off --------------------------------------------------------------------

    [Fact]
    public void Auto_reconnect_off_does_not_connect_when_the_device_appears()
    {
        using Harness h = new(autoReconnect: false);

        h.Link.RaiseAppeared();

        Assert.Empty(h.Sink.ConnectCalls);
        Assert.Empty(h.Calls.ConnectCalls);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
        Assert.Equal("auto-reconnect is off", h.Manager.Detail);
    }

    /// <summary>
    /// A half sitting in Backoff has its own timer armed, and that timer does not ask permission - so
    /// the setting can only be honoured by standing the half down when it stops being permitted.
    /// </summary>
    [Fact]
    public void Auto_reconnect_off_does_not_retry_from_backoff()
    {
        using Harness h = new(enableCalls: false);
        h.Sink.ConnectResult = false;
        h.Link.RaiseAppeared();
        Assert.Equal(ConnectionState.RetryBackoff, h.Manager.State);

        int saves = h.Settings.SaveCount;

        h.Manager.SetAutoReconnect(false);
        h.Scheduler.Advance(Seconds(120));

        Assert.Single(h.Sink.ConnectCalls);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
        Assert.Equal("auto-reconnect is off", h.Manager.Detail);

        // A switch that does not survive a restart is a switch the user flips again every morning.
        Assert.False(h.Settings.AutoReconnect);
        Assert.Equal(saves + 1, h.Settings.SaveCount);
    }

    /// <summary>
    /// The carve-out: the setting removes permission to <em>initiate</em>, not permission to finish
    /// what the user started. Without it, picking a phone with auto-reconnect off opens a connection
    /// that never routes audio - finding #2, rebuilt out of a setting.
    /// </summary>
    [Fact]
    public void Auto_reconnect_off_still_completes_a_click_initiated_connect()
    {
        using Harness h = new(phoneDeviceId: null, autoReconnect: false);

        h.Manager.SelectPhone(PhoneId);
        h.SetEndpointPresent(true);

        Assert.Equal(new[] { PhoneId }, h.Sink.ConnectCalls);
        Assert.True(h.Router.IsRunning);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
        Assert.Equal("music and calls up", h.Manager.Detail);
    }

    /// <summary>
    /// And the grant ends when what the user asked for is delivering: from there on the setting is
    /// back in charge, so a drop is dormancy rather than a reconnect.
    /// </summary>
    [Fact]
    public void Auto_reconnect_off_after_a_drop_reports_Suppressed_with_its_own_detail()
    {
        using Harness h = new(phoneDeviceId: null, autoReconnect: false);
        h.Manager.SelectPhone(PhoneId);
        h.SetEndpointPresent(true);

        h.Link.Status = BluetoothLinkStatus.Disconnected;
        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Grace);

        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
        Assert.Equal("auto-reconnect is off", h.Manager.Detail);

        // Distinct from the deliberate wording, because the two end on completely different events
        // and a user shown the same words cannot tell whether waiting will help.
        Assert.NotEqual("disconnected until the phone leaves and returns", h.Manager.Detail);

        // And it does not go and get the phone back.
        h.Link.Status = BluetoothLinkStatus.Connected;
        h.Scheduler.Advance(Seconds(95));
        Assert.Single(h.Sink.ConnectCalls);
    }

    [Fact]
    public void Turning_auto_reconnect_back_on_clears_the_latch()
    {
        using Harness h = new(enableCalls: false);
        h.Sink.ConnectResult = false;
        h.Link.RaiseAppeared();
        h.Manager.SetAutoReconnect(false);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);

        h.Sink.ConnectResult = true;
        int saves = h.Settings.SaveCount;

        h.Manager.SetAutoReconnect(true);

        Assert.Equal(2, h.Sink.ConnectCalls.Count);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);

        Assert.True(h.Settings.AutoReconnect);
        Assert.Equal(saves + 1, h.Settings.SaveCount);
    }

    /// <summary>
    /// And the clearing is reported without waiting for the pass, for the reason
    /// <c>Reselecting_the_same_phone_repaints_even_while_a_pass_is_in_flight</c> gives: a pass that
    /// started under the stall threshold ago returns before it publishes. A switch that leaves the
    /// tray saying "Suppressed - auto-reconnect is off" for another half-minute is one the user
    /// turns off again before it has had a chance to work.
    /// </summary>
    [Fact]
    public void Turning_auto_reconnect_back_on_repaints_even_while_a_pass_is_in_flight()
    {
        using Harness h = new(enableCalls: false);
        h.Sink.ConnectResult = false;

        h.Link.RaiseAppeared();
        h.Manager.SetAutoReconnect(false);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
        Assert.Equal("auto-reconnect is off", h.Manager.Detail);

        h.Link.DeferRead = true;
        h.Scheduler.Advance(Seconds(30));
        Assert.Equal(1, h.Link.ReadCount);

        h.Manager.SetAutoReconnect(true);

        Assert.Equal(1, h.Link.ReadCount);
        Assert.Equal(ConnectionState.Idle, h.Manager.State);
        Assert.Equal("nothing is running yet", h.Manager.Detail);
    }

    // --- calls switch --------------------------------------------------------------------------

    [Fact]
    public void Disabling_calls_unregisters()
    {
        using Harness h = new();
        h.ReachConnected();

        int saves = h.Settings.SaveCount;

        h.Manager.SetCallsEnabled(false);

        Assert.Equal(1, h.Calls.DisconnectCount);

        Assert.False(h.Settings.EnableCalls);
        Assert.Equal(saves + 1, h.Settings.SaveCount);
    }

    [Fact]
    public void Disabling_calls_leaves_music_running()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Manager.SetCallsEnabled(false);

        Assert.Equal(0, h.Sink.DisconnectCount);
        Assert.Equal(0, h.Router.StopCount);
        Assert.True(h.Router.IsRunning);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    [Fact]
    public void Enabling_calls_registers_without_touching_music()
    {
        using Harness h = new(enableCalls: false);
        h.ReachRouting();

        h.Manager.SetCallsEnabled(true);

        Assert.Equal(new[] { TransportId }, h.Calls.ConnectCalls);
        Assert.Single(h.Sink.ConnectCalls);
        Assert.Equal(0, h.Router.StopCount);
    }

    /// <summary>
    /// The switch is as explicit a "do this now" as picking a phone is, so it carries the same grant.
    /// Without it, turning calls on with auto-reconnect off is a menu item that visibly does nothing -
    /// the half refuses for want of permission and there is no way for the user to tell.
    /// </summary>
    [Fact]
    public void Enabling_calls_registers_even_with_auto_reconnect_off()
    {
        using Harness h = new(autoReconnect: false, enableCalls: false);
        h.Link.RaiseAppeared();
        Assert.Empty(h.Calls.ConnectCalls);

        h.Manager.SetCallsEnabled(true);

        Assert.Equal(new[] { TransportId }, h.Calls.ConnectCalls);
    }

    /// <summary>
    /// And the grant goes back with the switch. Left standing, a user who turns calls on and straight
    /// off again has auto-reconnect defeated by a toggle they reverted - and it is the music half
    /// that connects on the strength of it, which is not even the switch they touched.
    /// </summary>
    [Fact]
    public void Turning_calls_off_again_takes_back_the_permission_it_granted()
    {
        using Harness h = new(autoReconnect: false, enableCalls: false);
        h.Link.RaiseAppeared();

        h.Manager.SetCallsEnabled(true);
        h.Manager.SetCallsEnabled(false);

        h.Scheduler.Advance(Seconds(95));

        Assert.Empty(h.Sink.ConnectCalls);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
    }

    /// <summary>
    /// And it takes back only what it granted. A user who picks a phone and then decides they only
    /// want music has made two decisions, and the second does not revoke the first - but a single
    /// bool cannot tell one grant from the other, so revoking on the calls switch would stand the
    /// music half down mid-retry and latch <c>AutoReconnectOff</c> against a connect the user
    /// explicitly asked for.
    /// </summary>
    [Fact]
    public void Turning_calls_off_does_not_revoke_what_the_phone_selection_granted()
    {
        using Harness h = new(phoneDeviceId: null, autoReconnect: false);
        h.Sink.ConnectResult = false;

        h.Manager.SelectPhone(PhoneId);
        Assert.Single(h.Sink.ConnectCalls);

        // Flipped off, on, and off again, because the switch must add its own ask to the phone's
        // rather than replace it - and only a round trip through "on" can tell those two apart.
        h.Manager.SetCallsEnabled(false);
        h.Manager.SetCallsEnabled(true);
        h.Manager.SetCallsEnabled(false);

        Assert.NotEqual(ConnectionState.Suppressed, h.Manager.State);

        // The music half's own retry, still permitted, still counting down.
        h.Sink.ConnectResult = true;
        h.Scheduler.Advance(Seconds(2));

        Assert.Equal(2, h.Sink.ConnectCalls.Count);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    // --- the captured context: one test per await a foreign thread can complete ------------------
    //
    // <b>What makes four state machines correct with no lock between them is that every await in
    // ConnectionManager, MusicHalf and CallsHalf resumes on the thread the turn started on.</b> Two
    // independent things have to hold for that.
    //
    // The first is that the UI thread carries a SynchronizationContext at all. In the app that is the
    // WinForms one, installed by ControlUiDispatcher's marshalling control, and
    // UiDispatcherTests.Control_InstallsTheWinFormsSynchronizationContextOnTheThreadThatBuildsIt pins
    // it. Nothing here covers that leg.
    //
    // The second is that nothing on these paths calls ConfigureAwait(false) - one token that looks
    // like a tidy-up and silently moves the continuation onto whichever thread answered the radio.
    // Until these tests existed the whole suite was blind to it: every double answered instantly, and
    // an await on an already-completed task runs its continuation inline whether the context was
    // captured or not.
    //
    // <b>There are fourteen awaits across the three classes. The eight tests below cover eleven of
    // them, one site each, and the remaining three are named at the bottom with the reason.</b> The
    // map is written out and kept honest because a prohibition that names a test has to be checkable -
    // an earlier version of this section claimed twelve on the strength of one aggregate mutant, and
    // an earlier one than that claimed a single test covered "the pass end to end".
    //
    // Five await a seam - a task some other thread completes - and get a test each:
    //
    //   ConnectionManager.ReconcileAsync            _linkMonitor.ReadLinkStatusAsync()   test 1
    //   ConnectionManager.OnGraceWindowElapsedAsync _linkMonitor.ReadLinkStatusAsync()   test 2
    //   MusicHalf.ConnectAsync                      _sink.ConnectAsync(deviceId)         test 3
    //   CallsHalf.RegisterAsync                     _calls.FindTransportsAsync()         test 4
    //   CallsHalf.RegisterAsync                     _calls.ConnectAsync(transport.Id)    test 5
    //
    // Six more await a Task one of our own async methods produced:
    //
    //   MusicHalf.OnLinkPresentAsync   await ConnectAsync()                              test 3
    //   ConnectHalvesAsync             first await (_music.OnLinkPresentAsync)           test 3
    //   StillOurs                      await step                                        test 6
    //   ReconcileAsync                 await StillOurs(_music.OnLinkPresentAsync ...)     test 6
    //   ReconcileAsync                 await StillOurs(_music.ReconcileAsync ...)         test 7
    //   ReconcileAsync                 await StillOurs(_calls.ReconcileAsync ...)         test 8
    //
    // <b>This was nearly got wrong in the obvious direction.</b> The intuition is that an inner await
    // returns to this thread first, so an outer one is awaiting a task that completes here and resumes
    // inline regardless. That is false under a custom SynchronizationContext:
    // AwaitTaskContinuation.IsValidLocationForInlining refuses to inline while one is installed, so a
    // ConfigureAwait(false) continuation goes to the threadpool instead - which is the app's situation
    // exactly, since WindowsFormsSynchronizationContext is installed. Measured by mutating all nine
    // internal awaits at once and watching test 3 go red.
    //
    // <b>What each test has to arrange, and it is not only the park.</b> Suspending at a site is half
    // of it; the other half is leaving the pass real work to do afterwards. A mutant moves only the
    // continuation, so if everything after the park is the level-triggered tail, the mutant runs on the
    // threadpool and changes nothing any assertion can reach. Tests 7 and 8 both had to be rebuilt for
    // this - the first versions parked correctly, passed, and killed nothing.
    //
    // <b>The three no test can cover, named rather than glossed:</b> ReconcileAsync's fourth
    // `await StillOurs(_calls.OnLinkPresentAsync ...)`, ConnectHalvesAsync's second await, and
    // RegisterCallsAsync's. Each is the last await in its turn, so its whole continuation is that
    // turn's tail - EnforceConnectPermission plus a Publish that recomputes from scratch, both
    // level-triggered and idempotent by design, and by then the halves have already announced whatever
    // they changed. So no deterministic assertion can see which thread ran it. That is a limit of the
    // instrument, not a safety argument: the work is still unsynchronized, and EnforceConnectPermission
    // can stand a half down and set the latch, which is a wrong answer and not merely a race.

    /// <summary>
    /// Drives the manager until a turn is parked on one seam await, answers that seam from a worker
    /// thread, and asserts that nothing the turn did afterwards happened on that worker.
    ///
    /// <b>The assertion is the thread every announcement was raised on, and getting there took a
    /// surviving mutant.</b> The obvious measure - "was the continuation posted back to this
    /// context?" - is not sufficient, because a <c>ConfigureAwait(false)</c> deep inside a half lets
    /// the half's own continuation run on the completing thread and the <em>next</em> await up the
    /// chain marshals back anyway. The post arrives; the state was still mutated on the worker on the
    /// way there. So this measures where the work ran, not where it ended up.
    ///
    /// The join is what makes it deterministic: by the time <c>SetResult</c> has returned, the
    /// continuation has either been queued here or been run on the worker, and the two are
    /// distinguishable without waiting for anything.
    /// </summary>
    private static void AssertResumesOnTheStartingContext(
        Action<Harness> parkOnTheSeam,
        Action<Harness> answerFromAnotherThread,
        Action<Harness> assertTheTurnFinished,
        Func<Harness>? build = null)
    {
        SynchronizationContext? original = SynchronizationContext.Current;
        var context = new RecordingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);

        try
        {
            int startingThread = Environment.CurrentManagedThreadId;

            // Built inside the context, so the manager's own turns capture it from the start.
            using Harness h = build is null ? new Harness() : build();

            parkOnTheSeam(h);

            // Nothing queued yet, so the drain below can only be running the continuation this test
            // is about.
            Assert.Equal(0, context.PendingCount);
            int announced = h.AnnouncedOn.Count;

            var worker = new Thread(() => answerFromAnotherThread(h));
            worker.Start();
            worker.Join();

            // Draining is the message loop's job. Doing it here carries the rest of the turn through
            // on this thread.
            context.Drain();

            assertTheTurnFinished(h);

            // Non-vacuity first: the turn has to have got far enough to announce something, or the
            // check below is quantifying over an empty set.
            Assert.True(
                h.AnnouncedOn.Count > announced,
                "the parked turn announced nothing after being answered, so this test proves nothing");

            // And every one of them on this thread. A ConfigureAwait(false) at the seam puts the
            // worker's id in here, because the half carries on there and raises Changed from it.
            Assert.All(h.AnnouncedOn, id => Assert.Equal(startingThread, id));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
    }

    /// <summary>Seam await 1: the reconcile's link status read, the deepest await in the class.</summary>
    [Fact]
    public void A_reconcile_resumes_on_the_context_that_started_it()
    {
        AssertResumesOnTheStartingContext(
            h =>
            {
                h.Link.DeferRead = true;
                h.Scheduler.Advance(Seconds(30));

                // Parked: the pass has asked the radio and gone no further.
                Assert.Equal(ConnectionState.Discovering, h.Manager.State);
            },
            h => h.Link.CompleteRead(BluetoothLinkStatus.Connected),
            h => Assert.Equal(ConnectionState.Connected, h.Manager.State));
    }

    /// <summary>
    /// Seam await 2: the grace window's own link read.
    ///
    /// Separate from the reconcile's, and not covered by it: it is a different <c>await</c> in a
    /// different method, reached from a scheduler callback rather than from a pass. It is also the one
    /// read in the class whose answer decides deliberate-versus-out-of-range, so a continuation that
    /// wandered onto a worker thread would drive the suppression latch from off the UI thread.
    /// </summary>
    [Fact]
    public void A_grace_window_resumes_on_the_context_that_started_it()
    {
        AssertResumesOnTheStartingContext(
            h =>
            {
                h.ReachRouting();

                h.Link.DeferRead = true;
                h.Sink.PublishState(AudioSinkConnectionState.Closed);
                h.Scheduler.Advance(Grace);

                // Parked: the window has fired and is waiting on the radio, so nothing is decided.
                Assert.Equal(ConnectionState.Connected, h.Manager.State);
            },
            h => h.Link.CompleteRead(BluetoothLinkStatus.Connected),

            // The link was up, so the phone dropped the audio profile deliberately.
            h => Assert.Equal(ConnectionState.Suppressed, h.Manager.State));
    }

    /// <summary>Seam await 3: <c>MusicHalf</c>'s connect, which awaits a radio round trip.</summary>
    [Fact]
    public void The_music_halfs_connect_resumes_on_the_context_that_started_it()
    {
        AssertResumesOnTheStartingContext(
            h =>
            {
                h.Sink.DeferConnect = true;
                h.Link.RaiseAppeared();

                Assert.Equal(ConnectionState.Connecting, h.Manager.State);
            },
            h => h.Sink.CompleteConnect(connected: true),
            h => Assert.Equal(ConnectionState.Connected, h.Manager.State));
    }

    /// <summary>Seam await 4: <c>CallsHalf</c>'s transport enumeration.</summary>
    [Fact]
    public void The_calls_halfs_enumeration_resumes_on_the_context_that_started_it()
    {
        AssertResumesOnTheStartingContext(
            h =>
            {
                h.Calls.DeferFind = true;
                h.Link.RaiseAppeared();

                // Music is already up; the calls half is parked mid-enumeration.
                Assert.Equal(ConnectionState.Connecting, h.Manager.State);
            },
            h => h.Calls.CompleteFind(),
            h => Assert.Equal(ConnectionState.Connected, h.Manager.State));
    }

    /// <summary>Seam await 5: <c>CallsHalf</c>'s registration, the call that claims the role.</summary>
    [Fact]
    public void The_calls_halfs_registration_resumes_on_the_context_that_started_it()
    {
        AssertResumesOnTheStartingContext(
            h =>
            {
                h.Calls.DeferConnect = true;
                h.Link.RaiseAppeared();

                Assert.Equal(ConnectionState.Connecting, h.Manager.State);
            },
            h => h.Calls.CompleteConnect(CallTransportResult.Claimed(true)),
            h => Assert.Equal(ConnectionState.Connected, h.Manager.State));
    }

    /// <summary>
    /// <c>StillOurs</c>'s own <c>await step</c>, and the third of the four call sites that awaits it -
    /// the music half's link-present report.
    ///
    /// The five tests above cannot reach either. They are only reached from a reconcile pass, and in
    /// every scenario above the halves answer that pass with an already-completed task, so the awaiter
    /// never registers a continuation and <c>ConfigureAwait</c> has nothing to configure. Measured, not
    /// assumed - historically, and the number dates the measurement rather than describing the suite:
    /// when this test was written, mutating those five sites left all 83 tests of the day green.
    ///
    /// So this drives the connect from the <em>pass</em> rather than from a watcher edge, with the sink
    /// held open.
    /// </summary>
    [Fact]
    public void A_reconcile_that_connects_a_half_resumes_on_the_context_that_started_it()
    {
        AssertResumesOnTheStartingContext(
            h =>
            {
                // No watcher edge. The pass reads the link itself, finds the phone present, and is
                // what calls into the half - so the connect is awaited through StillOurs.
                h.Sink.DeferConnect = true;
                h.Scheduler.Advance(Seconds(30));

                Assert.Equal(ConnectionState.Connecting, h.Manager.State);
            },
            h => h.Sink.CompleteConnect(connected: true),
            h => Assert.Equal(ConnectionState.Connected, h.Manager.State));
    }

    /// <summary>
    /// Check 3's first call site: <c>await StillOurs(_music.ReconcileAsync(...))</c>, which suspends
    /// only when the music half is in <see cref="MusicState.Backoff"/> with its retry overdue - the
    /// backstop for a retry a suspended machine never delivered.
    ///
    /// <b>Getting there needs the retry timer out of the way, and the honest way to do that is to
    /// reproduce the case the branch exists for.</b> An <c>Advance</c> long enough to fire the retry
    /// once leaves the re-armed one sitting out the rest of that same drain - see
    /// <c>FakeScheduler.Advance</c> - so the clock ends 29 s along with the half overdue and its
    /// re-armed retry still undelivered. That is precisely a machine that was asleep through its own
    /// backoff: a timer that is armed and has not fired. The pass is
    /// then forced without advancing again, because any further advance would fire the retry first and
    /// the half would be Connecting rather than Backoff.
    /// </summary>
    [Fact]
    public void A_reconcile_retrying_the_music_half_resumes_on_the_context_that_started_it()
    {
        AssertResumesOnTheStartingContext(
            h =>
            {
                // Both halves fail once and back off. The calls half is the one that matters after
                // the park: without work left for the pass to do, the mutated continuation would run
                // on the threadpool and change nothing anyone could assert on - measured, the first
                // version of this test had the calls half already Up and the mutant survived it.
                h.Sink.ConnectResult = false;
                h.Calls.Transports = Array.Empty<TransportCandidate>();
                h.Link.RaiseAppeared();

                h.Scheduler.Advance(Seconds(29));

                h.Sink.ConnectResult = true;
                h.Sink.DeferConnect = true;
                h.Calls.Transports = new[] { PhoneTransport };

                // A pass on demand. SetAutoReconnect runs one straight away rather than waiting for
                // the tick, which is what keeps the armed retries from firing first.
                h.Manager.SetAutoReconnect(true);

                Assert.Equal(ConnectionState.Connecting, h.Manager.State);
            },
            h => h.Sink.CompleteConnect(connected: true),
            h => Assert.Equal(ConnectionState.Connected, h.Manager.State));
    }

    /// <summary>
    /// Check 3's second call site: <c>await StillOurs(_calls.ReconcileAsync(...))</c>, the same
    /// overdue-backoff backstop on the other half.
    ///
    /// The music half is deliberately left <see cref="MusicState.Off"/> across the park - the watcher
    /// says the phone left, which tears music down and, by design, leaves the calls half's
    /// registration and backoff alone. That is what gives the pass something to do <em>after</em> this
    /// await: the link read below finds the phone present again and check 5 connects music. Without
    /// it there is nothing after the park for a mutant to move onto the threadpool.
    /// </summary>
    [Fact]
    public void A_reconcile_retrying_the_calls_half_resumes_on_the_context_that_started_it()
    {
        AssertResumesOnTheStartingContext(
            h =>
            {
                // Nothing to correlate against, so the registration finds no match and backs off.
                h.Calls.Transports = Array.Empty<TransportCandidate>();
                h.Link.RaiseAppeared();
                h.Scheduler.Advance(Seconds(29));

                // Music down, calls untouched - the asymmetry CallsHalf is built around.
                h.Link.RaiseRemoved();

                h.Calls.Transports = new[] { PhoneTransport };
                h.Calls.DeferFind = true;

                h.Manager.SetAutoReconnect(true);

                Assert.Equal(ConnectionState.Connecting, h.Manager.State);
            },
            h => h.Calls.CompleteFind(),
            h => Assert.Equal(ConnectionState.Connected, h.Manager.State));
    }

    // --- what the tray is told -----------------------------------------------------------------

    /// <summary>
    /// The halves move far more often than the name the user reads does. Every internal transition
    /// raises Changed and reaches the projection; only a different answer reaches the tray.
    /// </summary>
    [Fact]
    public void StateChanged_fires_once_per_reported_state_change()
    {
        using Harness h = new();

        h.Link.RaiseAppeared();

        Assert.Equal(
            new[]
            {
                ConnectionState.Discovering,
                ConnectionState.Connecting,
                ConnectionState.Degraded,
                ConnectionState.Connecting,
                ConnectionState.Connected,
            },
            h.States);

        // Music Linked -> Up is a real transition of a real machine and reports the same Connected.
        h.SetEndpointPresent(true);
        Assert.Equal(5, h.States.Count);
    }

    /// <summary>
    /// The name is not the whole sentence, and the half of it that moves most often is the half
    /// <c>StateChanged</c> cannot report.
    ///
    /// Music going Linked -> Up keeps the state at <c>Connected</c> and changes the detail from
    /// "waiting for phone audio" to "music and calls up" - two different things for the user to do,
    /// under one name. With only <c>StateChanged</c> the tray had no way to hear it, so the tooltip
    /// sat on the older phrase until the state itself moved.
    /// </summary>
    [Fact]
    public void Detail_changing_under_an_unchanged_state_is_reported()
    {
        using Harness h = new();

        h.ReachConnected();
        Assert.Equal("waiting for phone audio", h.Manager.Detail);

        int states = h.States.Count;
        int details = h.Details.Count;

        h.SetEndpointPresent(true);

        Assert.Equal(states, h.States.Count);
        Assert.Equal(details + 1, h.Details.Count);
        Assert.Equal("music and calls up", h.Details[^1]);
    }

    /// <summary>
    /// At most one of the pair per publish. Both firing would repaint the tooltip twice for one move
    /// and write the sentence to the log twice with it - and the tray's whole reason for repainting
    /// on the detail is that it does not already repaint on the state.
    /// </summary>
    [Fact]
    public void A_state_change_reports_itself_once_and_not_also_as_a_detail_change()
    {
        using Harness h = new();

        int details = h.Details.Count;

        // Discovering -> Connecting -> ... -> Connected: every one of them changes the detail too,
        // because the detail is derived from the state.
        h.ReachConnected();

        Assert.True(h.States.Count > 1);
        Assert.Equal(details, h.Details.Count);
    }

    /// <summary>
    /// A publish that corrects nothing says nothing. The reconcile publishes on every completed pass
    /// - once per 30 s for the life of the process - and the tray logs every sentence it is handed,
    /// so a detail report that fired unconditionally would be 2,880 identical entries a day.
    /// </summary>
    [Fact]
    public void An_unchanged_detail_is_not_reported()
    {
        using Harness h = new();

        h.ReachRouting();

        int states = h.States.Count;
        int details = h.Details.Count;

        // Two full reconcile passes over a connection that is behaving.
        h.Scheduler.Advance(Seconds(60));

        Assert.Equal(states, h.States.Count);
        Assert.Equal(details, h.Details.Count);
    }

    /// <summary>
    /// The tray tooltip has one line and two writers, and this is the pair wired together the way
    /// <c>Program.Main</c> and <c>TrayContext</c> wire them - a component's <c>Status</c> straight to
    /// the presenter, and the state sentence repainted from the manager.
    ///
    /// The failure being pinned: a component announcement displaces the state sentence, and before
    /// the detail was reported at all, only a change of <em>state</em> brought it back. "A2DP sink
    /// state: Closed" could therefore sit in the tooltip while the app was up and routing again,
    /// which is the exact thing leading with the state was meant to fix.
    /// </summary>
    [Fact]
    public void A_component_status_does_not_hold_the_tooltip_past_the_next_change()
    {
        var written = new List<string>();
        var presenter = new StatusPresenter(new ImmediateUiDispatcher(), written.Add);

        using Harness h = new();

        // The two lines the shell uses, and no others.
        h.Manager.StateChanged += (_, _) => presenter.Show(h.Manager.State, h.Manager.Detail);
        h.Manager.DetailChanged += (_, _) => presenter.Show(h.Manager.State, h.Manager.Detail);

        h.ReachConnected();
        Assert.Equal("Klangbruecke: Connected — waiting for phone audio", written[^1]);

        // A component speaking for itself, exactly as AudioSinkService.PublishState does.
        presenter.Show("A2DP sink state: Closed");
        Assert.Equal("Klangbruecke: A2DP sink state: Closed", written[^1]);

        // And the next thing that moves is a detail, not a state: the endpoint arrives and music
        // goes Linked -> Up under an unchanged Connected.
        h.SetEndpointPresent(true);

        Assert.Equal("Klangbruecke: Connected — music and calls up", written[^1]);
    }

    /// <summary>
    /// Three ways the audio can stop and three different things for the user to do about them. The
    /// reported state is <c>Suppressed</c> for two of them and <c>Discovering</c> for the third, which
    /// is not enough on its own: "the phone dropped the audio connection" is something to fix on the
    /// handset, and "out of range" is something to fix by walking back.
    /// </summary>
    [Fact]
    public void Status_names_what_ended_the_connection()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Manager.RequestDisconnect();
        Assert.Equal("Disconnected.", h.Status[^1].Text);

        h.Link.RaiseRemoved();
        h.Link.RaiseAppeared();
        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Grace);
        Assert.Equal("The phone dropped the audio connection.", h.Status[^1].Text);

        h.Link.RaiseAppeared();
        h.Link.Status = BluetoothLinkStatus.Disconnected;
        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Scheduler.Advance(Grace);
        Assert.Equal("The phone is out of range.", h.Status[^1].Text);

        Assert.All(h.Status, message => Assert.Equal(LogLevel.Info, message.Level));
    }

    [Fact]
    public void Every_inbound_event_is_posted_through_the_dispatcher()
    {
        using Harness h = new();
        MarshallingUiDispatcher ui = h.Marshaller!;

        int posts = ui.Posts;

        h.Link.RaiseAppeared();
        Assert.Equal(++posts, ui.Posts);

        // The one that arrives from the threadpool, because its answer is a 152-282 ms enumeration
        // that neither the notification thread nor the message loop may spend. Still one post.
        //
        // Waited on through the queue rather than the count, which is the same signal
        // Harness.SetEndpointPresent uses: a test that waits on the count and then drains can find
        // the queue still empty, drain nothing, and assert against work that never ran.
        // Both conditions, because the two are set one after the other and either can be observed
        // first: the queue is filled before the count is raised, so a wait on the queue alone can
        // beat the count, and a wait on the count alone could - before that ordering was fixed - beat
        // the queue. Neither is ever retracted, so waiting for both is deterministic.
        h.Endpoints.RaiseEndpointsChanged();
        posts++;
        Assert.True(
            SpinWait.SpinUntil(() => ui.HasQueuedWork && ui.Posts == posts, HandoffBudget),
            $"the endpoint probe never posted its answer: queued={ui.HasQueuedWork}, posts={ui.Posts}");

        Assert.Equal(1, ui.Drain());

        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        Assert.Equal(++posts, ui.Posts);

        h.Router.Die();
        Assert.Equal(++posts, ui.Posts);

        h.Power.RaiseResumed();
        Assert.Equal(++posts, ui.Posts);

        h.Link.RaiseRemoved();
        Assert.Equal(++posts, ui.Posts);
    }

    /// <summary>
    /// Both halves raise Changed synchronously from inside their own transitions, and MusicHalf has
    /// one site - SetState(Linked) followed by StartRouteIfDue - where a handler that called back in
    /// would run before the transition's own tail and could route audio over a sink it had just
    /// disconnected. This handler is the reason that hazard is unreachable: it reads and reports, and
    /// touches nothing. Every count below moves if it stops being true.
    /// </summary>
    [Fact]
    public void The_Changed_handler_does_no_work_back_into_the_halves()
    {
        using Harness h = new();

        h.Link.RaiseAppeared();
        h.SetEndpointPresent(true);

        Assert.Equal(new[] { PhoneId }, h.Sink.ConnectCalls);
        Assert.Equal(0, h.Sink.DisconnectCount);
        Assert.Equal(new string?[] { OutputId }, h.Router.StartCalls);
        Assert.Equal(0, h.Router.StopCount);
        Assert.Equal(1, h.Calls.FindCount);
        Assert.Equal(new[] { TransportId }, h.Calls.ConnectCalls);
        Assert.Equal(0, h.Calls.DisconnectCount);
        Assert.Equal(1, h.Endpoints.PresenceReads);
    }

    // --- the cost of the endpoint probe --------------------------------------------------------

    /// <summary>
    /// The probe is a live full endpoint enumeration, measured at 152-282 ms on this machine, and
    /// MMDevAPI produces several callbacks per cause - five, measured, in every recorded run. Reading
    /// once per callback is seconds of frozen message loop per phone connect: no tray menu, no
    /// balloon, no shutdown.
    ///
    /// The gate is only reopened once the answer has been applied, so the arrangement that shows the
    /// collapse is the one that actually happens: a busy UI thread with the callbacks piling up
    /// behind it. That is exactly what the marshalling dispatcher reproduces - the answer waits until
    /// the test thread pumps it.
    /// </summary>
    [Fact]
    public void A_burst_of_endpoint_notifications_costs_one_underlying_read()
    {
        using Harness h = new();
        MarshallingUiDispatcher ui = h.Marshaller!;

        for (int i = 0; i < 5; i++)
        {
            h.Endpoints.RaiseEndpointsChanged();
        }

        // The gate shuts synchronously on the notification thread, before the probe has even been
        // handed to the threadpool, so the four that follow are turned away whatever the timing. Only
        // the winner's answer has to be waited for.
        Assert.True(
            SpinWait.SpinUntil(() => ui.HasQueuedWork, HandoffBudget),
            "the endpoint probe never answered");

        Assert.Equal(1, h.Endpoints.PresenceReads);
        Assert.Equal(1, ui.Drain());

        // And the next cause is still heard: this collapses a burst, it does not go deaf.
        h.Endpoints.RaiseEndpointsChanged();

        Assert.True(
            SpinWait.SpinUntil(() => h.Endpoints.PresenceReads >= 2, HandoffBudget),
            "the gate was never reopened");

        Assert.Equal(2, h.Endpoints.PresenceReads);
    }

    /// <summary>
    /// The music half reads the endpoint level three times on the way up and again on every
    /// notification. It gets a cached bool; only the manager ever touches the monitor.
    /// </summary>
    [Fact]
    public void The_music_half_never_reads_the_underlying_endpoint_monitor()
    {
        using Harness h = new();
        h.ReachRouting();

        Assert.Equal(1, h.Endpoints.PresenceReads);

        h.SetEndpointPresent(false);

        Assert.Equal(2, h.Endpoints.PresenceReads);
        Assert.False(h.Router.IsRunning);
        Assert.Equal("waiting for phone audio", h.Manager.Detail);
    }

    /// <summary>
    /// The notification handler must mark dirty, not go and look. It runs on MMDevAPI's own worker
    /// threads - where an <c>IMMNotificationClient</c> callback is contractually forbidden to block -
    /// and once on the UI thread, because <c>EndpointMonitor.Start</c> reports an already-present
    /// endpoint by raising from inside itself. 152-282 ms is not something either thread may spend.
    /// </summary>
    [Fact]
    public void The_notification_path_reads_the_endpoint_level_off_the_calling_thread()
    {
        using Harness h = new();

        h.SetEndpointPresent(true);

        Assert.Equal(1, h.Endpoints.PresenceReads);
        Assert.NotEqual(Environment.CurrentManagedThreadId, h.Endpoints.LastReadThreadId);
    }

    /// <summary>
    /// The reconcile runs on the UI thread by contract, so its level read has to leave it. 282 ms of
    /// stalled message loop every 30 s is a visible hitch for the whole of the app's runtime.
    /// </summary>
    [Fact]
    public void The_reconcile_reads_the_endpoint_level_off_the_calling_thread()
    {
        using Harness h = new();
        h.ReachRouting();
        int before = h.Endpoints.PresenceReads;

        h.Scheduler.Advance(Seconds(30));

        Assert.True(
            SpinWait.SpinUntil(() => h.Endpoints.PresenceReads > before, HandoffBudget),
            "the reconcile never refreshed the endpoint level");

        Assert.NotEqual(Environment.CurrentManagedThreadId, h.Endpoints.LastReadThreadId);
    }

    /// <summary>
    /// Nothing below Linked can act on the answer, so nothing pays for it. This is what keeps a
    /// dormant app - the phone out of range, or auto-reconnect off - from spending 282 ms every 30 s
    /// asking a question no state machine would read.
    /// </summary>
    [Fact]
    public void The_endpoint_level_is_not_read_while_no_half_could_act_on_it()
    {
        using Harness h = new();

        h.Scheduler.Advance(Seconds(95));

        Assert.Equal(0, h.Endpoints.PresenceReads);
    }

    // --- teardown ------------------------------------------------------------------------------

    [Fact]
    public void Dispose_stops_watching_and_disposes_every_owned_seam()
    {
        Harness h = new();
        h.ReachRouting();

        h.Manager.Dispose();

        Assert.Equal(1, h.Link.StopWatchingCount);
        Assert.True(h.Link.Disposed);
        Assert.True(h.Endpoints.Disposed);
        Assert.True(h.Power.Disposed);
        Assert.True(h.Sink.Disposed);
        Assert.True(h.Calls.Disposed);
        Assert.True(h.Router.Disposed);

        // Nothing armed: neither half is IDisposable, and both can be holding a scheduler handle.
        Assert.Equal(0, h.Scheduler.PendingCount);
    }

    /// <summary>
    /// Neither half is <see cref="IDisposable"/>, and each can be holding a scheduler handle when the
    /// tray exits. A handle left armed fires a connect or a registration into a manager that has
    /// disposed every seam under it.
    /// </summary>
    [Fact]
    public void Dispose_leaves_no_retry_armed()
    {
        Harness h = new();
        h.Sink.ConnectResult = false;
        h.Calls.ConnectResult = CallTransportResult.NotClaimed("the role was not claimed");

        h.Link.RaiseAppeared();
        Assert.Equal(ConnectionState.RetryBackoff, h.Manager.State);

        // The reconcile tick plus one retry per half - so the assertion below has something to prove.
        Assert.Equal(3, h.Scheduler.PendingCount);

        h.Manager.Dispose();

        Assert.Equal(0, h.Scheduler.PendingCount);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        Harness h = new();
        h.ReachRouting();

        h.Manager.Dispose();

        Assert.Null(Record.Exception(h.Manager.Dispose));
        Assert.Equal(1, h.Link.StopWatchingCount);
    }

    /// <summary>
    /// A watcher edge can be in flight when the tray exits - the real monitor raises on WinRT's own
    /// thread and this class is what marshals - and reaching a half through a disposed manager is how
    /// a teardown reopens the connection it has just closed.
    /// </summary>
    [Fact]
    public void Events_arriving_after_Dispose_do_nothing()
    {
        Harness h = new();
        h.Manager.Dispose();

        h.Link.RaiseAppeared();

        // Raw, not the pumping helper: the manager is unsubscribed, so there is no probe to wait for
        // and waiting for one is what this test is asserting will not happen.
        h.Endpoints.SetPresent(true);

        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Power.RaiseResumed();
        h.Scheduler.Advance(Seconds(95));

        Assert.Empty(h.Sink.ConnectCalls);
        Assert.Empty(h.Calls.ConnectCalls);
        Assert.Equal(0, h.Endpoints.PresenceReads);
    }

    /// <summary>
    /// The case the unsubscribes in <c>Dispose</c> cannot cover, and the reason the disposal check
    /// lives on the far side of the hop: the real dispatcher marshals with <c>BeginInvoke</c>, so an
    /// edge raised on WinRT's thread a moment before the tray exits is already queued and arrives
    /// afterwards. Acting on it reopens the connection the teardown has just closed.
    /// </summary>
    [Fact]
    public void An_event_posted_before_Dispose_does_nothing_when_it_arrives_after()
    {
        Harness h = new();
        MarshallingUiDispatcher ui = h.Marshaller!;

        // Raised off the test thread, which is the only way the queue can ever hold one - and the
        // only way it happens for real. ControlUiDispatcher runs a post from the UI thread inline, so
        // the edge that can outlive a teardown is precisely the one WinRT raised on its own thread.
        //
        // A thread rather than Task.Run, so the test can join it without a blocking task operation:
        // the whole point is that this test stays on its own thread afterwards.
        Thread raiser = new(h.Link.RaiseAppeared);
        raiser.Start();
        raiser.Join();

        Assert.True(
            SpinWait.SpinUntil(() => ui.HasQueuedWork, HandoffBudget),
            "the watcher edge was never posted");

        h.Manager.Dispose();

        Assert.Equal(1, ui.Drain());
        Assert.Empty(h.Sink.ConnectCalls);
        Assert.Empty(h.Calls.ConnectCalls);
    }

    /// <summary>
    /// The other half of the same shutdown race: a turn that is already past its post and waiting on
    /// a radio. The tray can be gone by the time the connect answers, and finishing the turn then
    /// raises <c>StateChanged</c> into a view that no longer exists.
    /// </summary>
    [Fact]
    public void A_turn_that_was_awaiting_when_Dispose_ran_announces_nothing()
    {
        Harness h = new();
        h.Sink.DeferConnect = true;

        h.Link.RaiseAppeared();
        Assert.Equal(1, h.Sink.PendingConnects);

        h.Manager.Dispose();
        int reported = h.States.Count;

        h.Sink.CompleteConnect(true);

        Assert.Equal(reported, h.States.Count);

        // And it stops before the second half, not after it. The double goes on answering once
        // disposed - as the real transport does, which has no disposed guard on its enumerate or its
        // connect - so a turn that carried on here would claim the hands-free role on a service that
        // has been let go of, and leave this PC advertised in the phone's own picker after the
        // process is gone. A failed attempt would be no better: it arms a retry on a manager whose
        // seams are all disposed.
        Assert.Empty(h.Calls.ConnectCalls);
        Assert.Equal(0, h.Scheduler.PendingCount);
    }

    /// <summary>
    /// The same defect as <c>A_disconnect_during_a_connect_stops_the_other_half_registering</c>, in
    /// the reconcile - which reaches the halves through the same two calls and used to read
    /// permission once, above both.
    ///
    /// The pass's own supersession guard cannot cover this: nothing on the Disconnect path touches
    /// <c>_reconcilingSince</c>, so the pass is still legitimately the current one. It is only the
    /// <em>permission</em> that changed.
    /// </summary>
    [Fact]
    public void A_disconnect_during_a_reconcile_connect_stops_the_other_half_registering()
    {
        using Harness h = new(enableCalls: true);

        // A pass that finds the phone present with both halves off, and hands off to a connect that
        // does not answer.
        h.Sink.DeferConnect = true;
        h.Scheduler.Advance(Seconds(30));
        Assert.Equal(1, h.Sink.PendingConnects);
        Assert.Empty(h.Calls.ConnectCalls);

        h.Manager.RequestDisconnect();

        h.Sink.CompleteConnect(true);

        Assert.Empty(h.Calls.ConnectCalls);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
    }

    /// <summary>
    /// The residual of cancelling the window on a phone selection: if the window being cancelled came
    /// from check 2 rather than from a sink event, the pass the click starts immediately arms an
    /// identical one - and it suppresses three seconds after the user clicked Connect.
    ///
    /// A half that still believes in a connection the sink no longer has is stale, not ambiguous, and
    /// the click is the user saying which phone they mean. So the pass stands the half down and
    /// reconnects it rather than opening a window to ask why it went.
    /// </summary>
    [Fact]
    public void Picking_a_phone_reconnects_a_stale_connection_instead_of_suppressing()
    {
        using Harness h = new();
        h.ReachRouting();

        // The connection object goes away with no event at all - the across-a-suspend case.
        h.Sink.Disconnect();

        h.Manager.SelectPhone(PhoneId);

        Assert.Equal(2, h.Sink.ConnectCalls.Count);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);

        // And nothing was left armed to suppress three seconds after the click.
        h.Scheduler.Advance(Grace);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    /// <summary>
    /// And the carve-out is exactly one click wide: a tick that finds the same drift on its own still
    /// opens the window, because nobody has said anything about intent and the difference between a
    /// deliberate drop and a range exit is still worth three seconds to establish.
    /// </summary>
    [Fact]
    public void A_reconcile_nobody_asked_for_still_opens_the_window()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Sink.Disconnect();

        h.Scheduler.Advance(Seconds(30));
        Assert.Single(h.Sink.ConnectCalls);

        h.Scheduler.Advance(Grace);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
    }

    /// <summary>
    /// The same shutdown race on the other turn that awaits. The calls switch has one await and no
    /// guard between it and the tail, so the tail's own disposal check is the only thing standing
    /// between a registration that answers late and a tray that has already gone.
    /// </summary>
    [Fact]
    public void A_calls_registration_that_answered_after_Dispose_announces_nothing()
    {
        Harness h = new(enableCalls: false);
        h.Link.RaiseAppeared();

        h.Calls.DeferConnect = true;
        h.Manager.SetCallsEnabled(true);
        Assert.Equal(new[] { TransportId }, h.Calls.ConnectCalls);

        h.Manager.Dispose();
        int reported = h.States.Count;

        h.Calls.CompleteConnect(CallTransportResult.Claimed(true));

        Assert.Equal(reported, h.States.Count);
        Assert.Equal(0, h.Scheduler.PendingCount);
    }

    /// <summary>
    /// The tray's Disconnect is one keystroke away during a connect, and a connect is a real
    /// <c>OpenAsync</c> round trip to a radio. Permission read once before both halves is permission
    /// from before the user said no - and the calls half would then claim the hands-free role seconds
    /// after they disconnected, putting this PC back in the phone's picker.
    ///
    /// Nothing downstream repairs it: <c>EnforceConnectPermission</c> stands down precisely when the
    /// latch is <em>not</em> set, so the one state that would need repairing is the one it skips.
    /// </summary>
    [Fact]
    public void A_disconnect_during_a_connect_stops_the_other_half_registering()
    {
        using Harness h = new();
        h.Sink.DeferConnect = true;

        h.Link.RaiseAppeared();
        Assert.Equal(1, h.Sink.PendingConnects);
        Assert.Empty(h.Calls.ConnectCalls);

        h.Manager.RequestDisconnect();

        h.Sink.CompleteConnect(true);

        Assert.Empty(h.Calls.ConnectCalls);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
    }

    /// <summary>
    /// The tray rebuilds its menu on every right-click, and both of these enumerate real hardware.
    /// A right-click landing during shutdown would otherwise reach a device enumerator and a sink
    /// this class has already disposed.
    /// </summary>
    [Fact]
    public async Task The_menu_queries_answer_empty_once_disposed()
    {
        Harness h = new();
        h.Router.Outputs = new[] { new AudioOutputDevice(OutputId, "Speakers") };
        h.Sink.Devices = new[] { new PhoneDevice(PhoneId, "MYSTRAPIX9") };

        h.Manager.Dispose();

        Assert.Empty(h.Manager.ListOutputDevices());
        Assert.Empty(await h.Manager.FindPhonesAsync());
    }

    // --- the harness ---------------------------------------------------------------------------

    /// <summary>
    /// The manager and its nine seams. Everything is a double; nothing here touches a radio, an audio
    /// endpoint, the registry or the settings file.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public Harness(
            string? phoneDeviceId = PhoneId,
            bool autoReconnect = true,
            bool enableCalls = true,
            IUiDispatcher? ui = null)
        {
            Settings = new RecordingSettings
            {
                PhoneDeviceId = phoneDeviceId,
                OutputDeviceId = OutputId,
                AutoReconnect = autoReconnect,
                EnableCalls = enableCalls,
            };

            Calls.Transports = new[] { PhoneTransport };

            // The phone is in the room. The watcher has still seen nothing - that is the edge a test
            // raises - so this is only what a level read would answer.
            Link.Status = BluetoothLinkStatus.Connected;

            // Marshalling rather than immediate, and it is not a detail. The endpoint level is read
            // on the threadpool - it must never run on the message loop - so its answer comes back
            // through Post from another thread. ImmediateUiDispatcher would apply that answer on the
            // threadpool thread, waking a half and republishing state while the test asserts about
            // it: a suite certifying a single-threaded contract by breaking it. Here the answer is
            // queued and run on the test thread, exactly as ControlUiDispatcher runs it on the UI
            // thread. Everything else a test raises still runs inline.
            Marshaller = ui as MarshallingUiDispatcher ?? (ui is null ? new MarshallingUiDispatcher() : null);
            Ui = ui ?? Marshaller!;

            Manager = new ConnectionManager(Settings, Sink, Calls, Router, Endpoints, Link, Scheduler, Power, Ui);
            Manager.StateChanged += (_, state) =>
            {
                States.Add(state);
                AnnouncedOn.Add(Environment.CurrentManagedThreadId);
            };

            Manager.DetailChanged += (_, _) =>
            {
                Details.Add(Manager.Detail);
                AnnouncedOn.Add(Environment.CurrentManagedThreadId);
            };

            Manager.Status += (_, message) =>
            {
                Status.Add(message);
                AnnouncedOn.Add(Environment.CurrentManagedThreadId);
            };

            Manager.Start();
        }

        public RecordingSettings Settings { get; }

        public FakeAudioSinkService Sink { get; } = new();

        public FakeCallTransportService Calls { get; } = new();

        public FakeAudioRouter Router { get; } = new();

        public FakeEndpointMonitor Endpoints { get; } = new();

        public FakeLinkMonitor Link { get; } = new();

        public FakeScheduler Scheduler { get; } =
            new(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

        public FakePowerNotifier Power { get; } = new();

        public IUiDispatcher Ui { get; }

        /// <summary>The dispatcher as a marshaller, or null when a test supplied a different one.</summary>
        public MarshallingUiDispatcher? Marshaller { get; }

        public ConnectionManager Manager { get; }

        /// <summary>Every reported state change, oldest first.</summary>
        public List<ConnectionState> States { get; } = new();

        /// <summary>
        /// Every detail reported <em>without</em> the state moving, oldest first. Deliberately not the
        /// detail behind every entry in <see cref="States"/> as well: the two events are exclusive, and
        /// a list that merged them could not tell a test which of the pair had fired.
        /// </summary>
        public List<string> Details { get; } = new();

        public List<StatusMessage> Status { get; } = new();

        /// <summary>
        /// The thread every announcement above was raised on, oldest first.
        ///
        /// The manager's contract is that <em>all</em> of them arrive on the one thread its turns run
        /// on, and an announcement is the cheapest proof that state was touched: it is raised from
        /// <c>Publish</c>, which recomputes from all four machines. So a foreign id in here is a turn
        /// that carried on somewhere it should not have.
        ///
        /// This is what the captured-context tests assert on, and it replaced counting posts. Counting
        /// posts cannot see the case that matters: a <c>ConfigureAwait(false)</c> deep in a half lets
        /// the half's own continuation run on the completing thread, and the <em>next</em> await up the
        /// chain then marshals back anyway - so the post arrives, and the state was still mutated off
        /// the UI thread on the way there. Measured, not reasoned: that mutant survived the post-count
        /// assertion.
        /// </summary>
        public List<int> AnnouncedOn { get; } = new();

        /// <summary>
        /// The phone walks into the room and both halves come up. The capture endpoint is not there
        /// yet, which is the ordinary arrival - it can be minutes behind the connection.
        /// </summary>
        public void ReachConnected()
        {
            Link.RaiseAppeared();
            Assert.Equal(ConnectionState.Connected, Manager.State);
        }

        /// <summary>And then the endpoint arrives and audio is actually routing.</summary>
        public void ReachRouting()
        {
            ReachConnected();
            SetEndpointPresent(true);
            Assert.True(Router.IsRunning);
        }

        /// <summary>
        /// The capture endpoint arriving or going away, driven the way the OS drives it: the level
        /// changes, MMDevAPI says something changed, and the manager answers from the threadpool
        /// because the read is 152-282 ms and neither the notification thread nor the message loop
        /// may spend that.
        ///
        /// <b>The two waits bend this project's "no test may sleep" rule, deliberately, and here is
        /// the argument.</b> <c>SpinWait.SpinUntil</c> does sleep internally once it has spun for a
        /// while, so this is not a technicality-free exception and should not be presented as one.
        ///
        /// It is unavoidable in this shape once the probe genuinely leaves the calling thread - which
        /// it must, because a 152-282 ms enumeration may run on neither the notification thread nor
        /// the message loop - and there is no handle on the resulting work that a test can await.
        /// What the rule exists to prevent is a test that passes or fails on timing, and neither
        /// happens here: both waits are inside <see cref="Assert.True(bool, string)"/> with a
        /// message, so a wait that runs out is a named failure and never a quiet pass; nothing here
        /// drives time, which is still <see cref="FakeScheduler.Advance"/>'s job; and
        /// <c>DisableTestParallelization</c> removes the pool-starvation path that would make the
        /// handoff slow.
        ///
        /// The alternative that would satisfy the rule exactly: have <see cref="FakeEndpointMonitor"/>
        /// signal a <c>TaskCompletionSource</c> from its read, and await that. It was not taken
        /// because it puts a test-only synchronisation primitive inside a double whose whole value is
        /// being dumber than the thing it stands in for. Its cost is named on
        /// <see cref="HandoffBudget"/>, which is also where the size of the wait is argued.
        ///
        /// Everything the manager does with the answer still runs on this thread, in
        /// <see cref="MarshallingUiDispatcher.Drain"/>.
        ///
        /// <b>One trap, latent today, that will bite whoever removes the condition keeping it
        /// latent.</b> The manager's probe gate shuts synchronously and reopens only when the answer
        /// is applied - which under this dispatcher means only when a test drains. A reconcile tick
        /// taken while the music half is <c>Linked</c> or <c>Up</c> kicks a probe of its own and
        /// leaves that answer undrained, so the gate stays shut. Call this method after such a tick
        /// and the raise lands on a shut gate: no read happens, and the first wait below burns the
        /// whole budget before failing. Worse, there is a narrow interleaving in which the leftover
        /// probe's read lands between <c>readsBefore</c> being captured and the level being set,
        /// which satisfies the wait with the value from before the change and silently drops the
        /// notification - a one-in-many failure no budget size can fix.
        ///
        /// No current test does it: none of the call sites is preceded in its own test by a
        /// reconcile that could have kicked a probe. If you need one that is, drain first.
        /// </summary>
        public void SetEndpointPresent(bool present)
        {
            MarshallingUiDispatcher marshaller =
                Marshaller ?? throw new InvalidOperationException("This harness has a different dispatcher.");

            int readsBefore = Endpoints.PresenceReads;

            Endpoints.SetPresent(present);

            Assert.True(
                SpinWait.SpinUntil(() => Endpoints.PresenceReads > readsBefore, HandoffBudget),
                "the endpoint level was never read");

            Assert.True(
                SpinWait.SpinUntil(() => marshaller.HasQueuedWork, HandoffBudget),
                "the endpoint probe never posted its answer");

            marshaller.Drain();
        }

        public void Dispose() => Manager.Dispose();
    }
}
