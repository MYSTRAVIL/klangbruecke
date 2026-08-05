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
    /// Finding #2, and the case that produced it: the capture endpoint tracks the phone's own A2DP
    /// link and was measured Active before this app opened a connection at all. One route, not two -
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
            Manager.StateChanged += (_, state) => States.Add(state);
            Manager.Status += (_, message) => Status.Add(message);

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

        public List<StatusMessage> Status { get; } = new();

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
