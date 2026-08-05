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

        h.Manager.DeselectPhone();

        Assert.Equal(1, h.Calls.DisconnectCount);
        Assert.Equal(1, h.Sink.DisconnectCount);
        Assert.True(h.Router.StopCount >= 1, "the route was left running over a phone nobody selected");
        Assert.False(h.Router.IsRunning);
        Assert.Equal(1, h.Link.StopWatchingCount);
        Assert.Null(h.Settings.PhoneDeviceId);
        Assert.Equal(ConnectionState.Idle, h.Manager.State);
    }

    [Fact]
    public void Selecting_an_output_restarts_the_route_without_touching_bluetooth()
    {
        using Harness h = new();
        h.ReachRouting();

        h.Manager.SelectOutput(OtherOutputId);

        Assert.Equal(new string?[] { OutputId, OtherOutputId }, h.Router.StartCalls);
        Assert.Single(h.Sink.ConnectCalls);
        Assert.Equal(0, h.Sink.DisconnectCount);
        Assert.Equal(OtherOutputId, h.Settings.OutputDeviceId);
    }

    /// <summary>
    /// A re-point that cannot open the new endpoint leaves the half believing it is routing audio
    /// over a route that is not running. Without this the tray reads "music and calls up" over
    /// silence until the reconcile notices, up to 30 s later.
    /// </summary>
    [Fact]
    public void Selecting_an_output_that_cannot_start_returns_the_half_to_waiting()
    {
        using Harness h = new();
        h.ReachRouting();
        h.Router.StartResult = false;

        h.Manager.SelectOutput(OtherOutputId);

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

    // --- the reconcile -------------------------------------------------------------------------

    [Fact]
    public void Reconcile_runs_every_thirty_seconds()
    {
        using Harness h = new();

        h.Scheduler.Advance(Seconds(95));

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

        // A route that stopped without saying so. Only a level read finds this one, and it is the
        // one drift in the five checks that no other component logs for itself.
        h.Router.DieSilently();
        h.Scheduler.Advance(Seconds(30));

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

        h.Manager.SetAutoReconnect(false);
        h.Scheduler.Advance(Seconds(120));

        Assert.Single(h.Sink.ConnectCalls);
        Assert.Equal(ConnectionState.Suppressed, h.Manager.State);
        Assert.Equal("auto-reconnect is off", h.Manager.Detail);
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
        h.Endpoints.SetPresent(true);

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
        h.Endpoints.SetPresent(true);

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
        h.Manager.SetAutoReconnect(true);

        Assert.Equal(2, h.Sink.ConnectCalls.Count);
        Assert.Equal(ConnectionState.Connected, h.Manager.State);
    }

    // --- calls switch --------------------------------------------------------------------------

    [Fact]
    public void Disabling_calls_unregisters()
    {
        using Harness h = new();
        h.ReachConnected();

        h.Manager.SetCallsEnabled(false);

        Assert.Equal(1, h.Calls.DisconnectCount);
        Assert.False(h.Settings.EnableCalls);
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
        h.Endpoints.SetPresent(true);
        Assert.Equal(5, h.States.Count);
    }

    [Fact]
    public void Every_inbound_event_is_posted_through_the_dispatcher()
    {
        CountingUiDispatcher ui = new();
        using Harness h = new(ui: ui);

        int posts = ui.Posts;

        h.Link.RaiseAppeared();
        Assert.Equal(++posts, ui.Posts);

        h.Endpoints.RaiseEndpointsChanged();
        Assert.Equal(++posts, ui.Posts);

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
        h.Endpoints.SetPresent(true);

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
    /// Deferring dispatcher on purpose. Under <see cref="ImmediateUiDispatcher"/> each notification is
    /// fully applied before the next arrives, so a burst is not a burst; this is the arrangement that
    /// actually happens, where the UI thread is busy and the callbacks pile up behind it.
    /// </summary>
    [Fact]
    public void A_burst_of_endpoint_notifications_costs_one_underlying_read()
    {
        DeferringUiDispatcher ui = new();
        using Harness h = new(ui: ui);

        for (int i = 0; i < 5; i++)
        {
            h.Endpoints.RaiseEndpointsChanged();
        }

        Assert.Equal(1, h.Endpoints.PresenceReads);
        Assert.Single(ui.Captured);

        // And the next cause is still heard: this collapses a burst, it does not go deaf.
        ui.Drain();
        h.Endpoints.RaiseEndpointsChanged();
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

        h.Endpoints.SetPresent(false);

        Assert.Equal(2, h.Endpoints.PresenceReads);
        Assert.False(h.Router.IsRunning);
        Assert.Equal("waiting for phone audio", h.Manager.Detail);
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
            SpinWait.SpinUntil(() => h.Endpoints.PresenceReads > before, TimeSpan.FromSeconds(5)),
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
        h.Endpoints.SetPresent(true);
        h.Sink.PublishState(AudioSinkConnectionState.Closed);
        h.Power.RaiseResumed();
        h.Scheduler.Advance(Seconds(95));

        Assert.Empty(h.Sink.ConnectCalls);
        Assert.Empty(h.Calls.ConnectCalls);
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

            Ui = ui ?? new ImmediateUiDispatcher();

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
            Endpoints.SetPresent(true);
            Assert.True(Router.IsRunning);
        }

        public void Dispose() => Manager.Dispose();
    }
}
