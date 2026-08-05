using Klangbruecke.Audio;
using Klangbruecke.Bluetooth;
using Klangbruecke.Config;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke.Connection;

/// <summary>
/// The one object that owns the connection lifecycle: intent, wiring, and the three timings that
/// make an unattended recovery possible - the 3 s grace window, the 30 s reconcile, and the 5 s
/// settle after a resume.
///
/// <b>It assembles rather than decides.</b> Everything that could be a state machine already is one:
/// <see cref="LinkMachine"/> answers "is the phone there", <see cref="SuppressionLatch"/> remembers
/// "we were told not to", <see cref="MusicHalf"/> and <see cref="CallsHalf"/> each run their own
/// half, and <see cref="ConnectionStateProjection"/> turns all four into the name the tray shows.
/// Nothing here assigns a reported state. That decomposition is the whole design: one seven-state
/// table that every component wrote into is how a tray icon comes to disagree with the OS, because
/// each writer knows only its own half and the last one to speak wins.
///
/// <b>Single-threaded by contract, and no locks.</b> Every inbound event - a watcher edge, a
/// connection state, an endpoint notification, a stopped route, a resume - is posted through
/// <see cref="IUiDispatcher"/> before any state is touched, and <see cref="IScheduler"/> delivers its
/// callbacks on the same thread. That is what makes a class with four machines in it correct without
/// a single lock, and it is what makes the <c>WasapiOut</c> play-thread deadlock structurally
/// impossible. The two <see cref="Interlocked"/> uses below are not an exception - see
/// <see cref="_endpointProbe"/> - because the thing they guard is deliberately <em>not</em> on this
/// thread.
///
/// <b>Never add <c>ConfigureAwait(false)</c> to anything in here or in the two halves.</b> It reads
/// like a tidy-up and it is the one token that takes the whole design apart: the continuation leaves
/// the UI thread - for the threadpool, not for the answering thread, because the runtime refuses to
/// inline a suppressed continuation while a custom <c>SynchronizationContext</c> is installed - and
/// four machines that hold no lock start sharing state across threads.
///
/// Twelve of the fourteen awaits in these three classes have a named test that goes red for it; the
/// six are in <c>ConnectionManagerTests</c> under "the captured context", which maps every site to its
/// test and names the two it cannot cover and why. Do not read the prohibition as covered by one test:
/// an earlier version of this comment did, and it was true of one await out of fourteen.
///
/// <b>What it does not do.</b> It never reads <c>ICallTransportService.IsRegistered</c>: that is a
/// live CsWinRT ABI call, and a throw out of a timer callback reaches
/// <c>Application.ThreadException</c> where no ordering helps. <see cref="CallsHalf"/> reads the
/// guarded tri-state instead. It never asks <c>PackageIdentity</c> anything either - the sink service
/// owns that gate, and a manager that read a process-wide static could not be tested at all.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    /// <summary>
    /// How long to wait before believing a closed audio connection.
    ///
    /// The connection closing is the same event for two opposite causes: the phone dropped the audio
    /// profile deliberately (the ACL link stays up, and reconnecting would fight the user), or the
    /// phone left the room. Three seconds is long enough for the radio to settle and short enough
    /// that a real range exit is not left looking connected - and, more to the point, it is what
    /// keeps a one-second dropout from flapping the tray, because nothing is decided until it
    /// elapses.
    /// </summary>
    private static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The drift correction. Level-triggered, because the events that should have told us are exactly
    /// the ones that go missing across sleep and resume - and an edge that never arrives is what
    /// leaves an app wrong forever, which is the predecessor app's defining bug and the reason this
    /// project exists.
    /// </summary>
    private static readonly TimeSpan ReconcilePeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after a resume to wait before looking. The Bluetooth stack is not back at the moment
    /// the notification fires, so an immediate attempt only burns the first backoff step for nothing.
    /// </summary>
    private static readonly TimeSpan ResumeSettle = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a pass may be running before the next one stops deferring to it.
    /// </summary>
    /// <remarks>
    /// Deliberately shorter than <see cref="ReconcilePeriod"/> rather than equal to it. A pass starts
    /// a hair <em>after</em> the tick that launched it, so with the two the same the tick one period
    /// later would find the wedged pass a few microseconds short of the threshold and defer as well -
    /// recovery would cost two periods instead of one, on the real timer, and never in a test where
    /// virtual time lands exactly on the boundary. Five seconds of margin, on the same reasoning as
    /// the resume settle: long enough that a slow-but-live read is not abandoned, short enough that
    /// the tick that finds it does not have to be punctual.
    /// </remarks>
    private static readonly TimeSpan ReconcileStall = TimeSpan.FromSeconds(25);

    private readonly Settings _settings;
    private readonly IAudioSinkService _sink;
    private readonly ICallTransportService _callTransport;
    private readonly IAudioRouter _router;
    private readonly IAudioEndpointMonitor _endpoints;
    private readonly ILinkMonitor _linkMonitor;
    private readonly IScheduler _scheduler;
    private readonly IPowerNotifier _power;
    private readonly IUiDispatcher _ui;

    private readonly LinkMachine _linkMachine = new();
    private readonly SuppressionLatch _latch = new();
    private readonly EndpointPresenceCache _presence = new();
    private readonly MusicHalf _music;
    private readonly CallsHalf _calls;

    private IDisposable? _reconcileTimer;
    private IDisposable? _graceTimer;
    private IDisposable? _resumeTimer;

    /// <summary>
    /// Bumped by every grace window that is armed, and read - never written - by the window that
    /// finally answers. The same shape the halves spell <c>_generation</c>, and here for the same
    /// reason: what crosses the await is a stale answer, not a data race.
    /// </summary>
    private int _graceGeneration;

    private bool _started;
    private bool _disposed;

    /// <summary>
    /// When the pass currently running started, or null when none is.
    ///
    /// A pass has five awaits in it and the link read is a real round trip to a radio, so a forced
    /// pass - a phone picked, a resume, the setting coming back on - can land on top of the periodic
    /// one. Two interleaved passes would each decide against a link status the other is still acting
    /// on, and both would open a grace window against the same closed connection.
    ///
    /// A time rather than a bool, and the difference is the whole reason this app exists. A read that
    /// never completes would leave a bool set for the life of the process and silently stop the only
    /// backstop the app has - an app that is wrong forever with nothing to correct it, which is the
    /// predecessor's defining bug rebuilt out of a mutex. A pass still running after
    /// <see cref="ReconcileStall"/> has stopped being one to defer to.
    ///
    /// Deferring is only half the invariant; the abandoned pass must also stop acting when it finally
    /// answers. See <see cref="Superseded"/>.
    /// </summary>
    private DateTimeOffset? _reconcilingSince;

    /// <summary>
    /// The user picked a phone and what they picked is not yet delivering.
    ///
    /// This is the whole of the auto-reconnect-off carve-out. The setting removes permission to
    /// <em>initiate</em>; it does not remove permission to finish what the user just started, and
    /// without this an explicit tray selection would open a Bluetooth connection and then decline to
    /// route audio over it when the capture endpoint finally arrived - finding #2, rebuilt out of a
    /// setting. Released the moment every enabled half is up, in <see cref="Publish"/>, because from
    /// there on the setting is what the user meant.
    ///
    /// <b>Flags rather than a bool, because a grant has to be withdrawable by whoever gave it.</b>
    /// The calls switch going off takes back what the calls switch granted - and a bool made that
    /// take back a phone selection's grant too, so a user who picked a phone and then decided they
    /// only wanted music had the music half's own click-initiated attempt stood down and latched
    /// against them.
    ///
    /// <b>It is released by success, not by time, and that is the one shape of "auto-reconnect off
    /// still reconnects".</b> A click whose halves never come up keeps its permission and goes on
    /// retrying on the backoff - one attempt a minute at the ceiling - until the user disconnects,
    /// deselects, or it works. That is the brief's definition taken literally, and it is the
    /// defensible half of the trade: the failure mode is an app that keeps trying to do what it was
    /// asked, and the alternative failure mode is the predecessor's.
    /// </summary>
    private ClickGrant _clickGrant;

    /// <summary>Which explicit user action is still owed something. See <see cref="_clickGrant"/>.</summary>
    [Flags]
    private enum ClickGrant
    {
        None = 0,

        /// <summary>A phone was picked and what it was picked for is not yet delivering.</summary>
        Phone = 1,

        /// <summary>The calls switch was turned on and the role is not yet held.</summary>
        Calls = 2,
    }

    /// <summary>
    /// Shut while a level read of the endpoint monitor is on its way back to this thread.
    ///
    /// The read is a live full endpoint enumeration, measured at 152-282 ms, and MMDevAPI produces
    /// several callbacks per cause - five, measured, in every recorded run - so a handler that read
    /// once per callback would spend seconds of frozen message loop on a single phone connect. The
    /// gate collapses a burst to one read.
    ///
    /// <see cref="Interlocked"/> rather than a lock, and it is the one thing here that is genuinely
    /// cross-thread: notification callbacks arrive on MMDevAPI's own worker threads, the reconcile's
    /// refresh runs on the threadpool, and the reopen happens on the UI thread. A lock would put the
    /// message loop and an OS callback thread in each other's way, which is exactly what this class
    /// exists to avoid.
    ///
    /// Reopened by <see cref="ApplyEndpointPresence"/>'s finally, so no path can leave it shut - with
    /// one dependency worth naming: the level read itself must not throw.
    /// <see cref="IAudioEndpointMonitor"/>'s only implementation guarantees that explicitly, and a
    /// catch here would be one nothing in the suite could reach.
    /// </summary>
    private int _endpointProbe;

    public ConnectionManager(
        Settings settings,
        IAudioSinkService sink,
        ICallTransportService calls,
        IAudioRouter router,
        IAudioEndpointMonitor endpoints,
        ILinkMonitor link,
        IScheduler scheduler,
        IPowerNotifier power,
        IUiDispatcher ui)
    {
        _settings = settings;
        _sink = sink;
        _callTransport = calls;
        _router = router;
        _endpoints = endpoints;
        _linkMonitor = link;
        _scheduler = scheduler;
        _power = power;
        _ui = ui;

        // The cache, not the monitor. See EndpointPresenceCache for the 282 ms this is about.
        _music = new MusicHalf(sink, router, _presence, scheduler);
        _calls = new CallsHalf(calls, scheduler);

        Refresh();
    }

    /// <summary>The name the tray shows. Derived from the four machines, never assigned.</summary>
    public ConnectionState State { get; private set; }

    /// <summary>The short phrase after the name. Never null, never empty.</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>Raised on the UI thread, once per change of <see cref="State"/>.</summary>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>
    /// Raised on the UI thread when <see cref="Detail"/> moved and <see cref="State"/> did not.
    ///
    /// <b>The name is not the whole sentence, and the half that moves most often is the half the other
    /// event cannot report.</b> Music going Linked to Up leaves the state at <c>Connected</c> and
    /// changes the detail from "waiting for phone audio" to "music and calls up" - two different
    /// things for the user to do, under one name. So does a Degraded half going from "music is not
    /// running" to "music retrying in 8s". A tray listening only to <see cref="StateChanged"/> shows
    /// the older phrase until the state itself moves, which for a stable connection can be the rest of
    /// the session - and, worse, leaves a component's own announcement sitting in the tooltip with
    /// nothing to displace it.
    ///
    /// <b>Exclusive with <see cref="StateChanged"/>, never both.</b> A state change already carries a
    /// recomputed detail, so raising both would repaint the tray twice for one move and write the
    /// sentence to the log twice with it.
    ///
    /// <b>And only on a change.</b> <see cref="Publish"/> runs at the end of every completed reconcile
    /// pass, so an unconditional report would be one identical log entry every 30 s for the life of
    /// the process - the same arithmetic <see cref="ReportDrift"/> refuses.
    ///
    /// No argument, unlike <see cref="StateChanged"/>: a subscriber that wants the phrase reads
    /// <see cref="Detail"/> beside <see cref="State"/>, which is the only way to get both halves of
    /// the sentence from one instant.
    /// </summary>
    public event EventHandler? DetailChanged;

    /// <summary>
    /// The manager's own announcements, for the tray tooltip and the log.
    ///
    /// Deliberately only this class's decisions - a disconnect, and each of the two answers the grace
    /// window can give. The seams raise their own status and the shell subscribes to them directly;
    /// re-broadcasting them here would put this class between a component and its own words, and it
    /// did not witness any of them.
    /// </summary>
    public event EventHandler<StatusMessage>? Status;

    /// <summary>Subscribes to every source and begins watching. Call once, from the UI thread.</summary>
    public void Start()
    {
        // A second call would double every subscription: two watchers, two reconcile timers, and two
        // of every inbound event. Start is wired from a constructor this class does not own.
        if (_started || _disposed)
        {
            return;
        }

        _started = true;

        _sink.StateChanged += OnSinkStateChanged;
        _router.Stopped += OnRouteStopped;
        _endpoints.EndpointsChanged += OnEndpointsChanged;
        _linkMonitor.DeviceAppeared += OnDeviceAppeared;
        _linkMonitor.DeviceRemoved += OnDeviceRemoved;
        _power.Resumed += OnResumed;

        _music.Changed += OnHalfChanged;
        _calls.Changed += OnHalfChanged;

        ApplySettingsToHalves();

        if (_settings.PhoneDeviceId is { } phoneDeviceId)
        {
            // Absent, not Present: selection is an intent and nothing has looked for the phone yet.
            // The watcher edge or the first reconcile is what finds it.
            _linkMachine.OnPhoneSelected();
            _linkMonitor.Watch(phoneDeviceId);
        }

        _power.Start();

        // After subscribing, because the already-present case is reported by raising EndpointsChanged
        // from inside Start and a handler attached afterwards would miss it - which is finding #2
        // exactly: the endpoint tracks the phone's own A2DP link and is routinely there before this
        // app connects at all.
        _endpoints.Start();

        _reconcileTimer = _scheduler.SchedulePeriodic(ReconcilePeriod, () => _ = ReconcileAsync("tick"));

        Publish();
    }

    /// <summary>
    /// Empty rather than an enumeration once disposed, for the reason the setters return early: the
    /// tray rebuilds its menu on every right-click, and a right-click during shutdown would otherwise
    /// reach a device enumerator this class has already let go of.
    /// </summary>
    public Task<IReadOnlyList<PhoneDevice>> FindPhonesAsync() => _disposed
        ? Task.FromResult<IReadOnlyList<PhoneDevice>>(Array.Empty<PhoneDevice>())
        : _sink.FindDevicesAsync();

    /// <summary>
    /// Through the router, so nothing above this class ever names a WASAPI type - see
    /// <see cref="IAudioRouter.ListOutputs"/>. Empty once disposed, as
    /// <see cref="FindPhonesAsync"/> is, and for the same right-click.
    /// </summary>
    public IReadOnlyList<AudioOutputDevice> ListOutputDevices() => _disposed
        ? Array.Empty<AudioOutputDevice>()
        : _router.ListOutputs();

    /// <summary>
    /// The user picked a phone. The most explicit "connect to this" the app has: it clears the
    /// suppression latch whatever the reason, and it grants permission to connect even with
    /// auto-reconnect off.
    /// </summary>
    public void SelectPhone(string deviceId)
    {
        if (_disposed)
        {
            return;
        }

        bool phoneChanged = !string.Equals(_settings.PhoneDeviceId, deviceId, StringComparison.Ordinal);

        // Saved before anything is attempted, and deliberately saved even if everything below fails:
        // this is the user's answer to "which phone", not a record of what happened to connect, and
        // the packaged build has to be able to come back to it after a reboot (FINDINGS.md section 8).
        _settings.PhoneDeviceId = deviceId;
        _settings.Save();

        _latch.OnPhoneSelectionChanged();
        _clickGrant = ClickGrant.Phone;
        CancelGraceWindow();

        if (phoneChanged)
        {
            // The one release of the hands-free role that is neither a Disconnect nor a switch. Left
            // alone it would sit on the handset the user has stopped using and go on offering this PC
            // there. Guarded on the id, because every unregister/re-register round trip makes this PC
            // vanish from and reappear in the phone's own call-audio picker.
            _calls.OnPhoneDeselected();
        }

        ApplySettingsToHalves();

        _linkMachine.OnPhoneSelected();
        _linkMonitor.Watch(deviceId);

        // Through the reconcile rather than straight into a connect: the phone's presence is a
        // question only the radio can answer, the pass already asks it, and routing this through the
        // same five checks as everything else is what keeps one connect path in the class.
        _ = ReconcileAsync("phone selected", userAsked: true);
    }

    public void DeselectPhone()
    {
        if (_disposed)
        {
            return;
        }

        _settings.PhoneDeviceId = null;
        _settings.Save();

        _latch.OnPhoneSelectionChanged();
        _clickGrant = ClickGrant.None;
        CancelGraceWindow();

        _calls.OnPhoneDeselected();
        ApplySettingsToHalves();

        _linkMachine.OnPhoneDeselected();
        _linkMonitor.StopWatching();

        Publish();
    }

    public void SelectOutput(string? deviceId)
    {
        if (_disposed)
        {
            return;
        }

        _settings.OutputDeviceId = deviceId;
        _settings.Save();

        ApplySettingsToHalves();
        RepointRoute(deviceId);

        Publish();
    }

    public void SetCallsEnabled(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _settings.EnableCalls = enabled;
        _settings.Save();

        ApplySettingsToHalves();

        if (!enabled)
        {
            // The switch is one of the two inputs that mean the user decided, so this is a release
            // rather than a settings re-push - see CallsHalf.Configure for why the difference matters
            // more here than it does for music.
            _calls.OnDisabled();

            // And the switch takes back what the switch granted - only that. Without any revocation,
            // turning calls on and straight off again with auto-reconnect off leaves permission
            // standing and the next reconcile connects the *music* half on the strength of a switch
            // the user reverted. Revoking everything is the opposite error and just as real: a user
            // who picks a phone and then decides they only want music would have the music half's own
            // click-initiated attempt stood down and latched against them.
            _clickGrant &= ~ClickGrant.Calls;

            Publish();
            return;
        }

        // The same grant a phone selection gets, on the same reasoning: the switch going on is an
        // explicit "do this now", and without it turning calls on with auto-reconnect off is a menu
        // item that visibly does nothing. It is a grant rather than a one-off attempt because
        // registration is a two-step round trip that can legitimately need a retry.
        //
        // It does also let the music half finish coming up, since permission is not per half. That is
        // the intended reading of "finishing what the user started" - the user asked for the phone to
        // be usable, not for half of it. What the flag records is which action is still owed
        // something, so that the switch going off again can withdraw its own ask without withdrawing
        // a phone selection's.
        _clickGrant |= ClickGrant.Calls;

        _ = RegisterCallsAsync();
    }

    public void SetAutoReconnect(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _settings.AutoReconnect = enabled;
        _settings.Save();

        if (enabled)
        {
            _latch.OnAutoReconnectEnabled();

            // Straight into a pass rather than waiting for the next tick. The user has just said
            // "come back"; a switch that appears to do nothing for half a minute is one they turn off
            // again before it has had a chance to work.
            _ = ReconcileAsync("auto-reconnect on");
            return;
        }

        EnforceConnectPermission();
        Publish();
    }

    /// <summary>The tray's Disconnect. Deliberate, and it lasts until the phone leaves and returns.</summary>
    public void RequestDisconnect()
    {
        if (_disposed)
        {
            return;
        }

        // Distinguishes a deliberate teardown from a dropped connection. Both end with the router
        // stopped and the same endpoints gone; only this one was asked for.
        Log.Info("Disconnect requested from the tray.");

        SuppressDeliberately("Disconnected.");
        Publish();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_started)
        {
            _sink.StateChanged -= OnSinkStateChanged;
            _router.Stopped -= OnRouteStopped;
            _endpoints.EndpointsChanged -= OnEndpointsChanged;
            _linkMonitor.DeviceAppeared -= OnDeviceAppeared;
            _linkMonitor.DeviceRemoved -= OnDeviceRemoved;
            _power.Resumed -= OnResumed;

            _music.Changed -= OnHalfChanged;
            _calls.Changed -= OnHalfChanged;
        }

        _reconcileTimer?.Dispose();
        _reconcileTimer = null;
        _graceTimer?.Dispose();
        _graceTimer = null;
        _resumeTimer?.Dispose();
        _resumeTimer = null;

        // Neither half is IDisposable and both can be holding a scheduler handle at this point - a
        // connect retry, a route retry, a registration retry. These two calls are what release them,
        // and without them a disposed manager leaves timers armed that will fire into it.
        //
        // Quietly, one step at a time, for the reason Teardown gives: this runs on the path to
        // TrayContext.Dispose, outside the message loop's exception guard, where a throw is a WER
        // dialog in an app whose whole premise is not to show a window.
        Teardown.Quietly(() => _music.Configure(false, null, null), "stand the music half down");
        Teardown.Quietly(_calls.OnDisabled, "stand the calls half down");

        Teardown.Quietly(_linkMonitor.StopWatching, "stop watching for the phone");
        Teardown.Quietly(_linkMonitor.Dispose, "dispose the link monitor");
        Teardown.Quietly(_endpoints.Dispose, "dispose the endpoint monitor");
        Teardown.Quietly(_power.Dispose, "dispose the power notifier");
        Teardown.Quietly(_router.Dispose, "dispose the audio router");
        Teardown.Quietly(_sink.Dispose, "dispose the audio sink");
        Teardown.Quietly(_callTransport.Dispose, "dispose the call transport");
    }

    // --- inbound events. Every one of them posts before it touches anything. ---------------------

    private void OnDeviceAppeared(object? sender, EventArgs e) => Post(() =>
    {
        _linkMachine.OnDeviceAppeared();
        _latch.OnLinkState(_linkMachine.State);

        if (_linkMachine.State != LinkState.Present)
        {
            // A watcher can deliver a queued edge after it has been stopped, and a stale one must not
            // resurrect a phone the user just cleared. LinkMachine already refuses it; this is only
            // the manager declining to act on a move that did not happen.
            Publish();
            return;
        }

        _ = ConnectHalvesAsync();
    });

    private void OnDeviceRemoved(object? sender, EventArgs e) => Post(() =>
    {
        _linkMachine.OnDeviceRemoved();
        _latch.OnLinkState(_linkMachine.State);

        if (_linkMachine.State == LinkState.Absent)
        {
            // Music only. Registration is not link-scoped and is what makes the phone offer this PC
            // when it comes back into range, so releasing it here would cost the user the entry in
            // their handset's own picker - see CallsHalf's missing OnLinkAbsent.
            _music.OnLinkAbsent();
        }

        Publish();
    });

    private void OnSinkStateChanged(object? sender, AudioSinkConnectionState state) => Post(() =>
    {
        if (state == AudioSinkConnectionState.Closed)
        {
            OnConnectionClosed();
            return;
        }

        Publish();
    });

    private void OnRouteStopped(object? sender, EventArgs e) => Post(() =>
    {
        // Back to Linked, and nothing touches Bluetooth. A call invalidates the capture endpoint
        // while the A2DP connection stays open, so reconnecting here would tear down a working link
        // to fix something that was never broken - the predecessor app's defining bug.
        _music.OnRouteStopped();
        Publish();
    });

    private void OnResumed(object? sender, EventArgs e) => Post(() =>
    {
        _resumeTimer?.Dispose();
        _resumeTimer = _scheduler.Schedule(ResumeSettle, () =>
        {
            _resumeTimer = null;
            _ = ReconcileAsync("resume");
        });
    });

    /// <summary>
    /// An audio endpoint somewhere on the machine changed - most likely nothing to do with us.
    ///
    /// <b>Marks dirty; it does not go and look.</b> This runs on whichever thread the OS raised it on -
    /// MMDevAPI's own MTA workers, and once on the UI thread, because <c>EndpointMonitor.Start</c>
    /// reports an already-present endpoint by raising from inside itself. Neither may spend 152-282 ms
    /// here: an <c>IMMNotificationClient</c> callback is contractually required not to block, and the
    /// UI-thread raise would put a second full enumeration on the startup path on top of the one
    /// <c>EndpointMonitor.Start</c> has just done. So the only work on this thread is shutting the
    /// gate; the read happens on the threadpool and only its answer comes back.
    ///
    /// The gate closes here rather than on the worker, and that ordering is the collapse: it is shut
    /// synchronously, before this returns, so the rest of a burst is turned away whether or not the
    /// probe has started yet.
    /// </summary>
    private void OnEndpointsChanged(object? sender, EventArgs e)
    {
        if (Interlocked.CompareExchange(ref _endpointProbe, 1, 0) != 0)
        {
            // A duplicate, or a second cause arriving while the first answer is still in flight. The
            // level is read after the change either way, so the answer already on its way describes
            // the settled state; anything this drops is picked up by the reconcile.
            return;
        }

        _ = Task.Run(ProbeEndpointLevel);
    }

    // --- the grace window -----------------------------------------------------------------------

    /// <summary>
    /// The audio connection reported Closed - or the reconcile found it gone without one.
    ///
    /// Nothing is decided here. The half drops its route, because there is nothing to route, and
    /// keeps everything else: which of the two causes this was is a question only a link status read
    /// can answer, and asking it immediately gets the wrong answer for a radio that has not settled.
    /// </summary>
    private void OnConnectionClosed()
    {
        _music.OnConnectionClosed();

        if (_graceTimer is null)
        {
            // One window at a time. A connection that reports Closed twice, or a reconcile that finds
            // it gone on two ticks running, would otherwise arm two windows that each read the link
            // and decide again.
            //
            // "At a time" only covers the wait, though. The handle is dropped the moment the window
            // fires, so a second Closed arriving while the first window's link read is still
            // outstanding arms a second window on top of it - which is why the generation is taken
            // here, at the moment the question is asked.
            int generation = ++_graceGeneration;

            _graceTimer = _scheduler.Schedule(GraceWindow, () =>
            {
                _graceTimer = null;
                _ = OnGraceWindowElapsedAsync(generation);
            });
        }

        Publish();
    }

    private async Task OnGraceWindowElapsedAsync(int generation)
    {
        if (_disposed)
        {
            return;
        }

        BluetoothLinkStatus status = await _linkMonitor.ReadLinkStatusAsync();

        if (Superseded(generation))
        {
            // A newer Closed has asked the same question since, and this answer predates it. Acting
            // on it is worse here than anywhere else in the class: this is the one read that decides
            // deliberate-versus-out-of-range, so a stale one either latches a suppression nobody
            // asked for - the app then sitting next to a phone it refuses to reconnect to, which is
            // the predecessor's defining bug reached from a new direction - or records an absence
            // that expires a suppression the user did ask for.
            return;
        }

        if (status == BluetoothLinkStatus.Connected)
        {
            // The ACL link is alive and only the audio profile went, which is what the phone dropping
            // this PC looks like. Reconnecting would fight the user, once every backoff step, for as
            // long as they left the phone in the room.
            Log.Info("The audio connection closed with the Bluetooth link still up: treating it as deliberate.");
            SuppressDeliberately("The phone dropped the audio connection.");
        }
        else
        {
            // Disconnected, or a read that could not answer - and Unknown belongs here rather than
            // with the branch above for the same reason LinkMachine collapses it to Absent: guessing
            // the link is up leaves the app dormant next to a phone that walked out of the building.
            Log.Info("The audio connection closed and the phone is not reachable: treating it as a range exit.");

            // Immediately, not through the poll debounce. That debounce exists because one failed
            // read is indistinguishable from the phone leaving; here two independent observations
            // agree, which is the definite kind - the same kind as a watcher removal.
            _linkMachine.OnDeviceRemoved();
            _latch.OnLinkState(_linkMachine.State);

            _music.OnLinkAbsent();
            Report("The phone is out of range.");
        }

        EnforceConnectPermission();
        Publish();
    }

    // --- the reconcile: the spec's five checks, in order -----------------------------------------

    /// <param name="userAsked">
    /// True when this pass descends from the user naming a phone. It changes exactly one thing -
    /// check 2 - and see there for why.
    /// </param>
    private async Task ReconcileAsync(string trigger, bool userAsked = false)
    {
        if (_disposed)
        {
            return;
        }

        if (_reconcilingSince is { } running && _scheduler.Now - running < ReconcileStall)
        {
            return;
        }

        DateTimeOffset startedAt = _scheduler.Now;
        _reconcilingSince = startedAt;

        try
        {
            // Three things in this method outlive an await on purpose, and they are the only three:
            // this snapshot, whose whole job is to be from before; startedAt, which is the token the
            // supersession check compares against; and the trigger string, which is a constant.
            // Everything else - permission most of all - is read at the point it is used. See the
            // note on ConnectHalvesAsync's first await for what a hoisted permission flag costs.
            Drift before = TakeDrift();

            // 1. The link, level-triggered. This is the backstop for a watcher edge that never
            // arrived, which is what sleep and resume do to WinRT device events.
            BluetoothLinkStatus status = await _linkMonitor.ReadLinkStatusAsync();

            if (Superseded(startedAt))
            {
                // The answer is older than the pass that replaced this one, and a link status from
                // 45 s ago is not a correction - it is drift, arriving in the machine whose job is to
                // remove it.
                return;
            }

            _linkMachine.OnLinkStatusRead(status);
            _latch.OnLinkState(_linkMachine.State);

            RefreshEndpointLevel();

            // 2. A consistency check between two seams - and deliberately not described as more than
            // that any more.
            //
            // It was written for "the connection object can go away without ever reporting Closed,
            // across a suspend most of all". <b>It cannot see that case</b>, and the honest version of
            // the comment is worth more than the reassuring one. <c>AudioSinkService.IsConnected</c>
            // answers from two fields that only this app writes - the connection reference and the
            // connected id - and a WinRT object killed underneath them leaves both set. The one
            // in-process caller that clears them is <c>MusicHalf.TearDown</c>, which lands on
            // <see cref="MusicState.Off"/> in the same call, so with the shipping sink this condition
            // is not reachable at all. Task 17 removed the last caller that could reach it: the tray's
            // own Disconnect, which stopped the sink without telling the half.
            //
            // It stays, as the seam guard it actually is. <see cref="IAudioSinkService"/> is an
            // interface, and an implementation whose IsConnected tracked the connection rather than
            // this app's bookkeeping would make this live again - which is the direction to fix it in
            // if the suspend case is ever measured. That fix needs a guarded tri-state in the shape of
            // <c>ICallTransportService.ReadRegistration</c>, never a bool: reading the live WinRT
            // State is an ABI call that can throw or fail to answer, and "could not tell" read as
            // "gone" tears down a working connection. What does back the suspend case up today is the
            // link status read above and the endpoint level below.
            //
            // The premise is pinned by
            // MusicHalfTests.Linked_and_Up_are_only_ever_held_over_a_connected_sink.
            if (!_sink.IsConnected && _music.State is MusicState.Linked or MusicState.Up)
            {
                if (userAsked)
                {
                    // The user has just named this phone, so there is nothing here to adjudicate: a
                    // half that still believes in a connection the sink no longer has is stale, not
                    // ambiguous. Opening a window would answer "the link is up, so the audio profile
                    // was dropped deliberately" and suppress the app three seconds after the click -
                    // and SelectPhone cancelling the previous window is what made that reachable.
                    //
                    // OnSuppressed is the teardown, not a claim about why: the half offers no
                    // "start again" input, and every other route out of Linked in this class ends at
                    // the same call. Standing it down here is what lets the link-present report below
                    // reconnect it inside this same pass, which is what the click asked for.
                    _music.OnSuppressed();
                }
                else
                {
                    OnConnectionClosed();

                    // Nothing else this pass. What just opened is a question with a 3 s answer, and
                    // correcting the halves against a connection that is already gone would start a
                    // route over it in the meantime.
                    ReportDrift(before, trigger);
                    return;
                }
            }

            // 3, 4 and 5 - the capture endpoint, the route, and the registration - are each discharged
            // inside the half that owns them. Reading any of them here would be a second opinion that
            // could disagree with the machine acting on it.
            //
            // Permission is read per half and never hoisted into a local above these awaits. The
            // first of them can be a real ConnectAsync round trip to a radio, and the tray's
            // Disconnect during it sets the latch - which StillOurs cannot see, because nothing on
            // the Disconnect path touches _reconcilingSince, and which EnforceConnectPermission below
            // cannot repair, because it stands down only when the latch is *not* set. A hoisted flag
            // therefore claims the hands-free role seconds after the user disconnected, while the
            // tray reports Suppressed.
            if (!await StillOurs(_music.ReconcileAsync(ConnectPermitted), startedAt))
            {
                return;
            }

            if (!await StillOurs(_calls.ReconcileAsync(ConnectPermitted), startedAt))
            {
                return;
            }

            if (_linkMachine.State == LinkState.Present)
            {
                // Level-triggered, like everything else in the pass: both halves ignore this unless
                // they are Off, so saying it every 30 s costs nothing and saying it never is how an
                // app that missed one edge stays down for the rest of the session.
                if (!await StillOurs(_music.OnLinkPresentAsync(ConnectPermitted), startedAt))
                {
                    return;
                }

                if (!await StillOurs(_calls.OnLinkPresentAsync(ConnectPermitted), startedAt))
                {
                    return;
                }
            }

            EnforceConnectPermission();
            ReportDrift(before, trigger);

            // Deliberately no timeout on a half stuck in Connecting or Registering, and CallsHalf's
            // note about "a reconcile-side timeout question" is answered here: no. The only lever
            // this class has is a teardown, and a teardown disposes the WinRT connection object
            // underneath an OpenAsync that has not returned - which is the class of call that takes
            // the process out rather than failing (FINDINGS.md section 8). Both seams' shipping
            // implementations catch their own throws and always complete, so "never completes" means
            // the radio stack is wedged, and the honest report for that is a tray that goes on saying
            // "connecting music" - visible, diagnosable, and not a crash.
        }
        finally
        {
            if (_reconcilingSince == startedAt)
            {
                // Only if it is still ours. A pass that was given up on and has now finally answered
                // must not clear the marker of the one that replaced it - the same reason the halves
                // capture a generation before their own awaits.
                _reconcilingSince = null;
            }
        }
    }

    /// <summary>
    /// Has this pass been given up on and replaced?
    ///
    /// Asked after every await, and it is the same guard the halves spell <c>_generation</c>: what
    /// crosses an await is not a data race - the whole class is one thread - but a stale
    /// <em>answer</em>. Holding the marker is only half of it. A pass whose link read finally returns
    /// 45 s late would otherwise write that status into the link machine, feed it to the latch and
    /// run both halves against a state its replacement is halfway through establishing, which is
    /// exactly the interleaving <see cref="_reconcilingSince"/> exists to prevent.
    /// </summary>
    private bool Superseded(DateTimeOffset startedAt) => _disposed || _reconcilingSince != startedAt;

    /// <summary>
    /// The same question for the grace window, which awaits the same radio and has the same hole
    /// without it. Two overloads rather than two differently-named checks, so the two paths read
    /// alike at the call site.
    ///
    /// A generation rather than a timestamp, because the two guards are answering different
    /// questions. The reconcile also needs to know when to <em>stop waiting</em> for a pass that has
    /// wedged - hence a time it can compare against. A window needs no such rule: it is superseded by
    /// events, and there are exactly two - another window being armed, which happens whenever the
    /// connection reports Closed again, and the phone selection changing, which voids the question
    /// rather than re-asking it. See <see cref="CancelGraceWindow"/> for the second.
    /// </summary>
    private bool Superseded(int graceGeneration) => _disposed || _graceGeneration != graceGeneration;

    /// <summary>
    /// Awaits one step of a pass and answers whether the pass is still the current one.
    ///
    /// A helper rather than the check written out four times, for the reason
    /// <c>MusicHalf.StartRouteIfDue</c> is the one place a route is started: written out, a fifth
    /// awaited step could be added and the guard forgotten, and the two arms would agree on every
    /// input the suite can produce - so neither could be broken without the other covering for it.
    /// Every await inside a pass except the link read, which has a value to hand back, goes through
    /// here.
    /// </summary>
    private async Task<bool> StillOurs(Task step, DateTimeOffset startedAt)
    {
        await step;

        return !Superseded(startedAt);
    }

    /// <summary>
    /// Everything a pass can correct, in one value, so that "did this tick change anything?" is one
    /// comparison rather than five conditionals that can each forget to report.
    /// </summary>
    private readonly record struct Drift(
        LinkState Link,
        MusicState Music,
        CallsState Calls,
        SuppressionReason Suppression);

    private Drift TakeDrift() => new(_linkMachine.State, _music.State, _calls.State, _latch.Reason);

    /// <summary>
    /// One line, and only when something moved.
    ///
    /// At 30 s an unconditional line is 2,880 entries a day, every one of them synchronous file I/O
    /// under a lock on the UI thread - and a log where nothing stands out is one nobody reads when
    /// the reconnect they are hunting finally fails.
    /// </summary>
    private void ReportDrift(Drift before, string trigger)
    {
        Drift after = TakeDrift();

        if (after != before)
        {
            Log.Info(
                $"Reconcile ({trigger}) corrected drift: link {before.Link}->{after.Link}, "
                + $"music {before.Music}->{after.Music}, calls {before.Calls}->{after.Calls}, "
                + $"suppression {before.Suppression}->{after.Suppression}.");
        }

        Publish();
    }

    // --- connect permission ---------------------------------------------------------------------

    /// <summary>
    /// May a connect be <em>initiated</em> right now?
    ///
    /// Two independent vetoes and one carve-out. The latch is a decision already taken - a tray
    /// Disconnect, or the phone dropping the audio profile - and the setting is the user's standing
    /// answer to "come and get it". <see cref="_clickGrant"/> is the carve-out: an attempt that
    /// descends from a phone the user just picked runs to completion whatever the setting says.
    /// </summary>
    private bool ConnectPermitted => !_latch.IsSet && (_settings.AutoReconnect || _clickGrant != ClickGrant.None);

    private bool AnyHalfDelivering =>
        (_music.Enabled && _music.State is MusicState.Linked or MusicState.Up)
        || (_calls.Enabled && _calls.State == CallsState.Up);

    /// <summary>
    /// Stands down a half that is counting down to an attempt it is no longer allowed to make.
    ///
    /// This is what actually implements "auto-reconnect off", and it has to exist because a half in
    /// Backoff has already armed its own timer and that timer does not ask permission - it cannot,
    /// because the same countdown is correct for a click-initiated attempt. So permission is enforced
    /// at the moment it is withheld, by ending the countdown.
    ///
    /// The latch is only set when nothing is left delivering. Suppressed is what the tray reports for
    /// it, and reporting it over a working half would hide service the user is still getting - the
    /// projection says Degraded there instead, and names the half that is missing.
    /// </summary>
    private void EnforceConnectPermission()
    {
        if (ConnectPermitted || _latch.IsSet)
        {
            // Already latched means already dormant for a reason that owns its own teardown, and one
            // reason overwriting another is how a deliberate Disconnect comes to describe itself as a
            // setting - which expires on a completely different event.
            return;
        }

        bool stoodDown = false;

        if (_music.State == MusicState.Backoff)
        {
            _music.OnSuppressed();
            stoodDown = true;
        }

        if (_calls.State == CallsState.Backoff)
        {
            _calls.OnDisabled();
            stoodDown = true;
        }

        if (stoodDown && !AnyHalfDelivering)
        {
            _latch.SuppressAutoReconnectOff();
        }
    }

    /// <summary>
    /// Voids an outstanding grace window, because the question it is going to answer is about a phone
    /// the user has just changed their mind about.
    ///
    /// Both halves matter. Bumping the generation alone would leave the armed timer standing, and
    /// <see cref="OnConnectionClosed"/> declines to arm a window while one is armed - so the next
    /// Closed would get no window at all. Disposing alone would leave a window that has already fired
    /// and is waiting on its read free to come back and decide.
    ///
    /// The decision it would otherwise reach is not harmless: a window that opened before the
    /// selection and answers Connected afterwards calls <see cref="SuppressDeliberately"/>, which
    /// latches, drops the grant and tears down both halves - defeating the click the user just made.
    /// </summary>
    private void CancelGraceWindow()
    {
        _graceTimer?.Dispose();
        _graceTimer = null;
        _graceGeneration++;
    }

    private void SuppressDeliberately(string status)
    {
        _latch.SuppressDeliberate();
        _clickGrant = ClickGrant.None;

        _music.OnSuppressed();
        _calls.OnDisabled();

        Report(status);
    }

    // --- the halves -------------------------------------------------------------------------------

    /// <summary>
    /// The settings, pushed at both halves. Music is enabled by a phone being picked - there is no
    /// separate switch for it - and neither half is told anything about package identity: the sink
    /// service owns that gate (<c>AudioSinkPolicy</c>), and a manager that read the process-wide
    /// static would report a different state on a developer's machine than in the installed build,
    /// with no test able to tell.
    /// </summary>
    private void ApplySettingsToHalves()
    {
        string? phoneDeviceId = _settings.PhoneDeviceId;

        _music.Configure(phoneDeviceId is not null, phoneDeviceId, _settings.OutputDeviceId);
        _calls.Configure(_settings.EnableCalls, phoneDeviceId);
    }

    private async Task ConnectHalvesAsync()
    {
        // Read per half, never hoisted into a local above the awaits. The first of these is a real
        // OpenAsync round trip to a radio, and the tray's Disconnect is one keystroke away during it:
        // hoisted, the calls half would be handed a permission flag from before the user said no, and
        // it would pass its own gates and claim the hands-free role seconds after they disconnected.
        // Nothing downstream repairs that - EnforceConnectPermission returns early precisely because
        // the latch it would be repairing against is set.
        await _music.OnLinkPresentAsync(ConnectPermitted);

        if (_disposed)
        {
            // Between the two, not only after them. A re-read answers "may this still be started";
            // it cannot answer "does anything still exist to start it on", because permission stays
            // true through a teardown. Left to the tail check, this turn would register the role on a
            // disposed transport - leaving the PC advertised in the phone's picker after the process
            // is gone - or arm a retry on a manager whose seams have all been let go of.
            return;
        }

        await _calls.OnLinkPresentAsync(ConnectPermitted);

        FinishTurn();
    }

    private async Task RegisterCallsAsync()
    {
        if (_linkMachine.State == LinkState.Present)
        {
            await _calls.OnLinkPresentAsync(ConnectPermitted);
        }

        FinishTurn();
    }

    /// <summary>
    /// The tail every turn that awaits something ends with, and the disposal re-check that has to go
    /// with it.
    ///
    /// These two turns deliberately do <em>not</em> take the reconcile's supersession guard, and the
    /// difference is worth stating precisely, because an earlier version of this comment stated it
    /// wrongly - it claimed nothing was carried across their awaits while a hoisted permission flag
    /// was being carried across two of them.
    ///
    /// What is true is that everything these turns need after an await can be <em>re-derived</em>,
    /// and now is: permission is read per half at the call site, and what this tail does is
    /// level-triggered and idempotent - <see cref="EnforceConnectPermission"/> reads the halves'
    /// current states, <see cref="Publish"/> recomputes from scratch. A pass interleaving with them
    /// changes what they compute, never whether it is correct. The reconcile cannot do the same:
    /// it carries a link status that no longer exists to be re-read and a "before" snapshot that is
    /// the whole point of the pass, so it needs a guard rather than a re-read.
    ///
    /// Disposal is a different question from staleness, and re-reading cannot answer it: permission
    /// stays true through a teardown. The tray can be gone by the time an await returns, and raising
    /// <c>StateChanged</c> into it - or standing a half down after everything under it has been
    /// disposed - is work on an object that no longer exists. Hence a check here, and another between
    /// <see cref="ConnectHalvesAsync"/>'s two awaits, where the damage is bigger than an announcement.
    /// </summary>
    private void FinishTurn()
    {
        if (_disposed)
        {
            return;
        }

        EnforceConnectPermission();
        Publish();
    }

    /// <summary>
    /// Re-points a running route at a different output without touching Bluetooth.
    ///
    /// The one place outside <see cref="MusicHalf"/> that starts a route, and it is deliberately not
    /// a retry: the half has no input meaning "same state, different endpoint", and the two ways of
    /// asking for one through its existing surface both cost a route backoff step and a second of
    /// silence for a user who only changed a preference. <see cref="IAudioRouter.Start"/> stops the
    /// old route itself.
    /// </summary>
    private void RepointRoute(string? deviceId)
    {
        if (!_router.IsRunning)
        {
            return;
        }

        bool started = _router.Start(deviceId);

        if (started && _router.IsRunning)
        {
            return;
        }

        // The bool is advisory - Start returns true for a capture that died inside StartRecording,
        // measured - so both are asked. Without this the half sits in Up over a route that is not
        // running, and the tray reads "music and calls up" over silence until the reconcile notices.
        _music.OnRouteStopped();
    }

    /// <summary>
    /// A half moved. <b>This handler does nothing except recompute what the tray reports.</b>
    ///
    /// That restraint is load-bearing rather than tidy. Both halves raise <c>Changed</c>
    /// synchronously from inside their own transitions, and <see cref="MusicHalf"/> has one site -
    /// <c>SetState(Linked)</c> followed by <c>StartRouteIfDue</c> - where a handler that called back
    /// into the half would run before the transition's own tail: a teardown from here would leave the
    /// half Off and the tail would then start a route and claim Up over a sink it had just
    /// disconnected. Nothing below touches a half, and
    /// <c>The_Changed_handler_does_no_work_back_into_the_halves</c> is what keeps it that way.
    /// </summary>
    private void OnHalfChanged(object? sender, EventArgs e) => Publish();

    // --- the endpoint level -----------------------------------------------------------------------

    /// <summary>
    /// A last-known bool wearing <see cref="IAudioEndpointMonitor"/>'s clothes, handed to
    /// <see cref="MusicHalf"/> in place of the real monitor.
    ///
    /// The half reads the level three times on the way up and again on every notification, and the
    /// real property is a live full endpoint enumeration measured at 152-282 ms on the UI thread. A
    /// dozen callbacks around a phone connect is seconds of frozen message loop: no tray menu, no
    /// balloon, no shutdown. So the manager does the reading, off this thread, and the half reads a
    /// field.
    ///
    /// The cost, stated plainly: the value is stale by at most one refresh. That is bounded - a
    /// notification refreshes it, and so does every reconcile pass in which a half could act on it -
    /// and the alternative is a UI that stops responding, which is not.
    ///
    /// The three unused members are not an oversight. <see cref="MusicHalf"/> subscribes to nothing
    /// and owns nothing; the manager holds the real monitor's lifecycle, and this object is a value
    /// with an interface on it.
    /// </summary>
    private sealed class EndpointPresenceCache : IAudioEndpointMonitor
    {
        public bool SinkCaptureEndpointPresent { get; set; }

        public event EventHandler? EndpointsChanged
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Refreshes the cached level from a reconcile pass, off the UI thread.
    ///
    /// The pass itself runs on the message loop by <see cref="IScheduler"/>'s contract, so this is
    /// the one refresh that has to hire a thread rather than borrow the one it was called on. The
    /// answer lands after this pass and is what the next one reads - see the note on staleness in
    /// <see cref="EndpointPresenceCache"/>.
    /// </summary>
    private void RefreshEndpointLevel()
    {
        if (_music.State is not (MusicState.Linked or MusicState.Up))
        {
            // Nothing below Linked can act on the answer, so nothing pays 282 ms for it. A dormant
            // app - phone out of range, or auto-reconnect off - would otherwise spend it every 30 s
            // for as long as it runs.
            return;
        }

        if (Interlocked.CompareExchange(ref _endpointProbe, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(ProbeEndpointLevel);
    }

    /// <summary>
    /// The 152-282 ms enumeration, and the one thing in this class that deliberately does not run on
    /// the UI thread. Both callers hand it to the threadpool; only the bool comes back.
    /// </summary>
    private void ProbeEndpointLevel()
    {
        bool present = _endpoints.SinkCaptureEndpointPresent;

        _ui.Post(() => ApplyEndpointPresence(present));
    }

    private void ApplyEndpointPresence(bool present)
    {
        try
        {
            if (_disposed || _presence.SinkCaptureEndpointPresent == present)
            {
                // Most notifications are about another endpoint entirely and MMDevAPI duplicates the
                // ones that are not, so an unchanged level is the ordinary case rather than the
                // exception - and waking the half for it would be the duplicate arriving anyway.
                return;
            }

            _presence.SinkCaptureEndpointPresent = present;

            _music.OnEndpointsChanged();
            Publish();
        }
        finally
        {
            Volatile.Write(ref _endpointProbe, 0);
        }
    }

    // --- what the tray is told ---------------------------------------------------------------------

    /// <summary>
    /// Recomputes the reported state and announces it if it moved. Called at the end of every turn,
    /// and from every half transition.
    /// </summary>
    private void Publish()
    {
        ConnectionState previousState = State;

        // Captured beside the state, and the pair is why both are captured rather than only the one
        // that used to be. A detail that moved under an unchanged name is a real change with a real
        // consumer - see DetailChanged - and it was previously unobservable from outside this class.
        string previousDetail = Detail;

        Refresh();

        if (State != previousState)
        {
            StateChanged?.Invoke(this, State);
        }
        else if (!string.Equals(Detail, previousDetail, StringComparison.Ordinal))
        {
            // else, not a second if. The state change above already carries a recomputed detail, and
            // both firing for one move would repaint the tray twice and log the sentence twice.
            DetailChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Refresh()
    {
        ReleaseClickGrantIfDelivering();

        ConnectionSnapshot snapshot = new(
            PhoneSelected: _settings.PhoneDeviceId is not null,
            Suppression: _latch.Reason,
            Link: _linkMachine.State,
            MusicEnabled: _music.Enabled,
            Music: _music.State,
            CallsEnabled: _calls.Enabled,
            Calls: _calls.State,
            NextRetryIn: SoonestRetry(),
            ConnectPermitted: ConnectPermitted);

        State = ConnectionStateProjection.Project(snapshot);
        Detail = ConnectionStateProjection.DetailFor(snapshot);
    }

    /// <summary>
    /// The click grant ends when what the user asked for is delivering. From there the setting is
    /// back in charge, so the next drop is dormancy rather than a reconnect.
    /// </summary>
    private void ReleaseClickGrantIfDelivering()
    {
        if (_clickGrant == ClickGrant.None)
        {
            return;
        }

        bool anyEnabled = _music.Enabled || _calls.Enabled;
        bool musicSatisfied = !_music.Enabled || _music.State is MusicState.Linked or MusicState.Up;
        bool callsSatisfied = !_calls.Enabled || _calls.State == CallsState.Up;

        // anyEnabled is load-bearing: "every enabled half is up" is vacuously true when none is, and
        // without it the grant would be released before the halves had been configured at all.
        if (anyEnabled && musicSatisfied && callsSatisfied)
        {
            _clickGrant = ClickGrant.None;
        }
    }

    /// <summary>The sooner of the two countdowns, for the one detail string that carries a number.</summary>
    private TimeSpan? SoonestRetry()
    {
        TimeSpan? music = _music.NextRetryIn;
        TimeSpan? calls = _calls.NextRetryIn;

        if (music is not { } m)
        {
            return calls;
        }

        return calls is { } c && c < m ? c : m;
    }

    /// <summary>
    /// Always Info. This class announces decisions, not failures - the seams raise their own status
    /// for what went wrong, at the severity they witnessed it at, which is what
    /// <see cref="StatusMessage"/> carries a level for at all. A level parameter here would only ever
    /// let this class re-grade an event it did not see.
    /// </summary>
    private void Report(string message)
        => Status?.Invoke(this, new StatusMessage(message, LogLevel.Info));

    /// <summary>
    /// The hop every inbound event makes before it is allowed to touch anything, and the disposal
    /// check that goes with it: an edge can be in flight when the tray exits, and reaching a half
    /// through a disposed manager is how a teardown reopens the connection it has just closed.
    /// </summary>
    private void Post(Action action) => _ui.Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        action();
    });
}
