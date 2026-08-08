using Klangbruecke.Audio;
using Klangbruecke.Bluetooth;
using Klangbruecke.Config;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke.Connection;

/// <summary>
/// The one object that owns the connection lifecycle: intent, wiring, and the timings that make an
/// unattended recovery possible. It delegates the 3 s grace window to <see cref="GraceWindow"/> and
/// the 30 s reconcile to <see cref="Reconciler"/>, and owns the 5 s settle after a resume.
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
/// <b>Never add <c>ConfigureAwait(false)</c> to anything in here, in the two seams, or in the two
/// halves.</b> It reads like a tidy-up and it is the one token that takes the whole design apart:
/// four machines that hold no lock start sharing state across threads. Which thread it leaks onto
/// depends on the await, and both cases are real - the five that await a seam resume <em>on the
/// answering thread</em>, because a radio's own thread carries no <c>SynchronizationContext</c> and
/// the runtime inlines there; the nine that await one of our own methods resume <em>on the
/// threadpool</em>, because the runtime refuses to inline while a custom context is installed, which
/// is always the case on the UI thread.
///
/// Eleven of the fourteen awaits in these five classes have a named test that goes red for that site
/// alone; the eight tests are in <c>ConnectionManagerTests</c> under "the captured context", which maps
/// every site to its test and names the three it cannot cover and why. Do not read the prohibition as
/// covered by one test, and do not read an aggregate mutant as covering the sites inside it: earlier
/// versions of this comment did both.
///
/// <b>What it does not do.</b> It never reads <c>ICallTransportService.IsRegistered</c>: that is a
/// live CsWinRT ABI call, and a throw out of a timer callback reaches
/// <c>Application.ThreadException</c> where no ordering helps. <see cref="CallsHalf"/> reads the
/// guarded tri-state instead. It never reads the <c>PackageIdentity</c> static either - a manager
/// that did could not be tested at all - but it is <em>told</em> the answer once through the
/// constructor and gates both halves on it in <see cref="ApplySettingsToHalves"/>, so an unpackaged
/// run reports both halves disabled instead of retrying what it cannot do.
/// </summary>
public sealed class ConnectionManager : IDisposable, IConnectionCoordinator
{
    /// <summary>
    /// How long after a resume to wait before looking. The Bluetooth stack is not back at the moment
    /// the notification fires, so an immediate attempt only burns the first backoff step for nothing.
    /// </summary>
    private static readonly TimeSpan ResumeSettle = TimeSpan.FromSeconds(5);

    private readonly Settings _settings;
    private readonly IAudioSinkService _sink;
    private readonly ICallTransportService _callTransport;
    private readonly IAudioRouter _router;
    private readonly IAudioEndpointMonitor _endpoints;
    private readonly ILinkMonitor _linkMonitor;
    private readonly IScheduler _scheduler;
    private readonly IPowerNotifier _power;
    private readonly IUiDispatcher _ui;

    /// <summary>
    /// Whether this process has MSIX package identity. <b>Injected, never read from the
    /// <c>PackageIdentity</c> static</b> - that is the whole reason a bool is passed rather than the
    /// static consulted, since a manager that read the static could not be tested at all. It reaches
    /// exactly one place, <see cref="ApplySettingsToHalves"/>, where it gates both halves off in an
    /// unpackaged run. See <see cref="AudioSinkPolicy.CanOpenConnection"/> and
    /// <see cref="CallsPolicy.ShouldRegister"/> for the per-half rules.
    /// </summary>
    private readonly bool _isPackaged;

    private readonly LinkMachine _linkMachine = new();
    private readonly SuppressionLatch _latch = new();
    private readonly EndpointPresenceCache _presence = new();
    private readonly MusicHalf _music;
    private readonly CallsHalf _calls;
    private readonly GraceWindow _graceWindow;
    private readonly Reconciler _reconciler;

    private IDisposable? _resumeTimer;
    private IDisposable? _resolverTick;

    /// <summary>
    /// Supersession token for <see cref="ResolveActivePhoneAsync"/>. Bumped by explicit intent-setting
    /// actions (<see cref="SetActivePhone"/>, <see cref="ClearRememberedPhones"/>) so an in-flight
    /// resolver that resumes after a newer explicit selection is superseded and does not override it.
    /// </summary>
    private int _resolveGeneration;

    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Set the first time the link is seen <see cref="LinkState.Present"/>, and never cleared. It
    /// gates the reconcile's fast reconnect probe: before the app has connected even once there is
    /// nothing to <em>re</em>-connect, and initial discovery is the watcher's enumeration edge plus
    /// the 30 s backstop - so the probe stays off and a phone that is simply not here yet at startup
    /// is not polled every few seconds.
    ///
    /// <b>App-wide, not per-phone.</b> Once any phone has connected this stays set, so re-picking a
    /// different, never-seen phone does arm the probe for it. That is intended rather than a leak: a
    /// tray re-pick carries a click grant and is an explicit "connect to this now", so polling for it
    /// is what the user just asked for. See <see cref="Reconciler.Pace"/> and <see cref="Refresh"/>.
    /// </summary>
    private bool _everConnected;

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
        IUiDispatcher ui,
        bool isPackaged)
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
        _isPackaged = isPackaged;

        // The cache, not the monitor. See EndpointPresenceCache for the 282 ms this is about.
        _music = new MusicHalf(sink, router, _presence, scheduler);
        _calls = new CallsHalf(calls, scheduler);
        _graceWindow = new GraceWindow(_scheduler, _linkMonitor, _linkMachine, _latch, _music, this);
        _reconciler = new Reconciler(_scheduler, _linkMonitor, _sink, _linkMachine, _latch, _music, _calls, _graceWindow, this);

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
    /// the process - the same arithmetic the reconcile refuses.
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

        // If there's an active phone already (from settings), set up watching before the resolver runs.
        // The resolver's "incumbent kept" path doesn't start watching, so this ensures an initial
        // PhoneDeviceId is watched even if the resolver keeps it.
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
        // exactly: the endpoint's lifetime is not this app's connection's, and it is routinely there
        // before this app connects at all - measured Active before the connection was opened and
        // still Active after the process was killed (docs/FINDINGS.md section 4).
        _endpoints.Start();

        _reconciler.Start();

        // The resolver picks the active phone from the remembered set. Run it at startup so an upgraded
        // user whose single PhoneDeviceId was migrated into RememberedPhoneIds behaves as before.
        _ = ResolveActivePhoneAsync();

        // And schedule the 30 s periodic tick. Disposed in Dispose.
        _resolverTick = _scheduler.SchedulePeriodic(TimeSpan.FromSeconds(30), () => _ = ResolveActivePhoneAsync());

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
    /// Add or remove a phone from the remembered set. When adding a phone that's already remembered,
    /// forces a reconnect to it (like a tray re-pick). When adding a new phone, the resolver picks
    /// based on presence.
    /// </summary>
    public void SetPhoneRemembered(string id, bool remembered)
    {
        if (_disposed)
        {
            return;
        }

        if (remembered)
        {
            bool alreadyRemembered = _settings.RememberedPhoneIds.Contains(id);
            if (!alreadyRemembered)
            {
                _settings.RememberedPhoneIds.Add(id);
                _settings.Save();
                // Adding a new phone: let the resolver pick based on presence.
                _ = ResolveActivePhoneAsync();
            }
            else
            {
                // Re-remembering an already-remembered phone: force reconnect to it (tray re-pick).
                SetActivePhone(id);
            }
        }
        else
        {
            if (_settings.RememberedPhoneIds.Remove(id))
            {
                _settings.Save();
                // Removed a phone: let the resolver pick from what's left.
                _ = ResolveActivePhoneAsync();
            }
        }
    }

    /// <summary>
    /// Clear the remembered phone set and stop watching. The app goes dormant until a phone is
    /// remembered again.
    /// </summary>
    public void ClearRememberedPhones()
    {
        if (_disposed)
        {
            return;
        }

        // Bump generation to supersede any in-flight resolver.
        _resolveGeneration++;

        _settings.RememberedPhoneIds.Clear();
        _settings.Save();

        _settings.PhoneDeviceId = null;

        _latch.OnPhoneSelectionChanged();
        _clickGrant = ClickGrant.None;
        _graceWindow.Cancel();

        _calls.OnPhoneDeselected();
        ApplySettingsToHalves();

        _linkMachine.OnPhoneDeselected();
        _linkMonitor.StopWatching();

        Publish();
    }

    /// <summary>
    /// Enable or disable event sounds (connection/disconnection notifications). Saved and
    /// published.
    /// </summary>
    public void SetEventSounds(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _settings.EventSounds = enabled;
        _settings.Save();

        Publish();
    }

    /// <summary>
    /// Make the given phone active: set <c>PhoneDeviceId</c>, clear the suppression latch, grant
    /// permission, cancel the grace window, handle calls-role on change, notify the link machine, start
    /// watching, and run the reconcile. This is the generalized old <c>SelectPhone</c> body, now called
    /// by the resolver when it picks a phone from the remembered set.
    /// </summary>
    private void SetActivePhone(string id)
    {
        if (_disposed)
        {
            return;
        }

        // Bump generation to supersede any in-flight resolver.
        _resolveGeneration++;

        bool phoneChanged = !string.Equals(_settings.PhoneDeviceId, id, StringComparison.Ordinal);

        // Saved before anything is attempted, and deliberately saved even if everything below fails:
        // this is the user's answer to "which phone", not a record of what happened to connect, and
        // the packaged build has to be able to come back to it after a reboot (FINDINGS.md section 8).
        _settings.PhoneDeviceId = id;
        _settings.Save();

        _latch.OnPhoneSelectionChanged();
        _clickGrant = ClickGrant.Phone;
        _graceWindow.Cancel();

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
        _linkMonitor.Watch(id);

        // Through the reconcile rather than straight into a connect: the phone's presence is a
        // question only the radio can answer, the pass already asks it, and routing this through the
        // same five checks as everything else is what keeps one connect path in the class.
        _ = _reconciler.RunAsync("phone resolved", userAsked: true);

        // The repaint this click owes the tray, because the pass above cannot be relied on for it: a
        // pass that started under ReconcileStall ago returns at the defer check without publishing,
        // so reselecting the same phone while suppressed cleared the latch and repainted nothing.
        //
        // <b>And it must not consume the click on the way past.</b> Refresh releases the grant when
        // every enabled half looks satisfied, which on a same-phone re-pick is true of both halves'
        // own beliefs before the pass has checked either of them. Released here, the pass runs with
        // no permission, and a half that then fails - the hands-free role lost phone-side is the
        // measured case - is stood down by EnforceConnectPermission instead of retried. That
        // releases the role the user just asked for and nothing re-arms it, because
        // OnLinkPresentAsync needs the permission that is now gone.
        //
        // So the grant is put back. Moving this call below the pass is not enough on its own and
        // reads as though it were: in the app the link read is a real round trip that suspends, so
        // this line runs while the pass is parked and the halves are still exactly as the click
        // found them. Pinned from both sides - one test with the read answering inline and one with
        // it outstanding, which is the only shape the app itself ever takes.
        ClickGrant granted = _clickGrant;
        Publish();
        _clickGrant = granted;
    }

    /// <summary>
    /// Resolve the active phone from the remembered set: read link status for each remembered phone,
    /// call <see cref="PhonePicker.Pick"/>, then act on the result. A pick that differs from the
    /// current active phone means switch to it via <see cref="SetActivePhone"/>. Otherwise keep the
    /// incumbent.
    ///
    /// Runs at <see cref="Start"/>, on the active-phone-lost path, and on a 30 s periodic tick.
    /// Snapshots the remembered set to avoid modification-during-enumeration. Re-checks
    /// <c>_disposed</c> and the generation token after each await so an in-flight resolve that
    /// resumes after disposal or after a newer explicit selection is superseded and does not act.
    /// </summary>
    private async Task ResolveActivePhoneAsync()
    {
        if (_disposed || _settings.RememberedPhoneIds.Count == 0)
        {
            return;
        }

        // Supersession token: capture generation so an in-flight resolve that resumes after a newer
        // explicit selection (SetActivePhone, ClearRememberedPhones) is superseded and does not act.
        int generation = ++_resolveGeneration;

        // Snapshot the remembered set to avoid modification-during-enumeration if the live set is
        // mutated (SetPhoneRemembered, ClearRememberedPhones) while this resolve is awaiting.
        List<string> remembered = _settings.RememberedPhoneIds.ToList();

        // Build a presence map by awaiting ReadLinkStatusForAsync for each remembered phone.
        Dictionary<string, bool> presenceMap = new();

        foreach (string id in remembered)
        {
            BluetoothLinkStatus status = await _linkMonitor.ReadLinkStatusForAsync(id);

            // Re-check disposed and generation after the await: a newer resolve or explicit selection
            // supersedes this one.
            if (_disposed || generation != _resolveGeneration)
            {
                return;
            }

            presenceMap[id] = status == BluetoothLinkStatus.Connected;
        }

        // Call the pure picker with the snapshot.
        string? pick = PhonePicker.Pick(_settings.PhoneDeviceId, remembered, id => presenceMap.GetValueOrDefault(id, false));

        // Re-check disposed and generation before acting on the pick.
        if (_disposed || generation != _resolveGeneration)
        {
            return;
        }

        // Act on the result. Pick is never null for a non-empty remembered set (PhonePicker rule 4),
        // but treat it as keep-incumbent if it were.
        if (pick is not null && !string.Equals(pick, _settings.PhoneDeviceId, StringComparison.Ordinal))
        {
            // Picked phone differs from current: switch to it.
            SetActivePhone(pick);
        }
        // else: incumbent kept, nothing to do.
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

            // The latch clearing is a reported change in its own right, and the pass below cannot be
            // relied on to say so: one that started under ReconcileStall ago returns without
            // publishing. See SetActivePhone, which had the same hole for the same reason.
            //
            // No grant to protect here, unlike SetActivePhone's: the setting this method has just turned
            // on is itself what ConnectPermitted reads, so a release by this Publish cannot take the
            // permission away from the pass below.
            Publish();

            // Straight into a pass rather than waiting for the next tick. The user has just said
            // "come back"; a switch that appears to do nothing for half a minute is one they turn off
            // again before it has had a chance to work.
            _ = _reconciler.RunAsync("auto-reconnect on");
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

    /// <summary>
    /// Connect now, to a phone in the remembered set. The manual, one-shot override: it clears the
    /// suppression latch (whether a deliberate Disconnect or an auto-reconnect-off suppression) and grants
    /// a connect even with auto-reconnect off - exactly as <see cref="SetPhoneRemembered"/> does - but
    /// changes neither the remembered set, the calls role, nor the auto-reconnect setting.
    ///
    /// The grant is one-shot: <see cref="ReleaseClickGrantIfDelivering"/> drops it once a half is
    /// delivering, so after the next drop with auto-reconnect off the app goes dormant again, matching the
    /// toggle. Nothing to connect to with no remembered phones, so that is a no-op.
    /// </summary>
    public void RequestConnect()
    {
        if (_disposed || _settings.RememberedPhoneIds.Count == 0)
        {
            return;
        }

        Log.Info("Connect requested from the tray.");

        _latch.OnPhoneSelectionChanged();
        _clickGrant = ClickGrant.Phone;
        _graceWindow.Cancel();

        // Force reconnect to the active phone if set, otherwise pick from the remembered set.
        // Unlike the resolver (which only switches when needed), RequestConnect is a manual "connect NOW"
        // command that must reconnect even to the incumbent.
        string? target = _settings.PhoneDeviceId ?? _settings.RememberedPhoneIds.FirstOrDefault();
        if (target is not null)
        {
            SetActivePhone(target);
        }

        // No need to preserve the grant here - SetActivePhone above already did that dance.
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

        _reconciler.Dispose();
        _graceWindow.Dispose();
        _resumeTimer?.Dispose();
        _resumeTimer = null;
        _resolverTick?.Dispose();
        _resolverTick = null;

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

            // The active phone is lost. Run the resolver to switch to another remembered phone if one
            // is present.
            _ = ResolveActivePhoneAsync();
        }

        Publish();
    });

    private void OnSinkStateChanged(object? sender, AudioSinkConnectionState state) => Post(() =>
    {
        if (state == AudioSinkConnectionState.Closed)
        {
            _graceWindow.OnConnectionClosed();
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
            _ = _reconciler.RunAsync("resume");
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
    /// The settings, pushed at both halves - each gated on package identity so an unpackaged run
    /// reports both halves disabled and retries nothing, rather than sitting in a permanent error
    /// state hammering the calls half's <c>RegisterApp</c> and a music connect its own service refuses,
    /// once a minute for the life of the process. Music is otherwise enabled by a phone being picked;
    /// there is no separate switch for it.
    ///
    /// The gate reads <see cref="_isPackaged"/>, which is injected, not the <c>PackageIdentity</c>
    /// static. An earlier version of this comment justified telling the halves nothing on the grounds
    /// that a manager reading the static could not be tested - true of the static, false of an injected
    /// bool. Each half's rule stays in its own policy - <see cref="AudioSinkPolicy.CanOpenConnection"/>
    /// and <see cref="CallsPolicy.ShouldRegister"/> - so the two gates cannot drift.
    ///
    /// <b><see cref="AudioSinkService"/>'s own gate stays regardless, and is not this one's redundant
    /// twin.</b> Unpackaged, <c>AudioPlaybackConnection.TryCreateFromId</c> takes the process down with
    /// an uncatchable access violation (docs/FINDINGS.md §8), so the last line of defence has to sit at
    /// the call. This gate stops the half ever reaching it; that gate stops the crash if anything ever
    /// does.
    /// </summary>
    private void ApplySettingsToHalves()
    {
        string? phoneDeviceId = _settings.PhoneDeviceId;

        bool musicEnabled = phoneDeviceId is not null && AudioSinkPolicy.CanOpenConnection(_isPackaged);
        _music.Configure(musicEnabled, phoneDeviceId, _settings.OutputDeviceId);

        CallsAvailability calls = CallsPolicy.Decide(_settings.EnableCalls, _isPackaged);
        _calls.Configure(CallsPolicy.ShouldRegister(calls), phoneDeviceId);
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

        // The last await in this turn, so its continuation is only FinishTurn - which is why no test
        // can catch a ConfigureAwait(false) here. See the map in ConnectionManagerTests under "the
        // captured context"; the prohibition still applies, it just has no tripwire.
        await _calls.OnLinkPresentAsync(ConnectPermitted);

        FinishTurn();
    }

    private async Task RegisterCallsAsync()
    {
        if (_linkMachine.State == LinkState.Present)
        {
            // The third of the three awaits with no tripwire, for the same reason as
            // ConnectHalvesAsync's second - the map in ConnectionManagerTests under "the captured
            // context" names all three together at the bottom.
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

        // Latched on the first connection and never cleared - see _everConnected. Before it, the
        // probe stays off and initial discovery is the watcher edge plus the 30 s backstop.
        if (snapshot.Link == LinkState.Present)
        {
            _everConnected = true;
        }

        // The fast reconnect probe reads the same snapshot the projection does: poll quickly only
        // once the app has connected at least once and is now waiting out of range (Absent) for a
        // reconnect it is permitted to make. Idempotent - this runs on every Refresh but arms or
        // disarms the probe only on the transition into or out of that state. See Reconciler.Pace,
        // and the fast-reconnect-probe tests.
        _reconciler.Pace(_everConnected && snapshot.Link == LinkState.Absent && snapshot.ConnectPermitted);

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

    // --- the coordinator the two timing seams reach back through ---------------------------------

    bool IConnectionCoordinator.IsDisposed => _disposed;
    bool IConnectionCoordinator.ConnectPermitted => ConnectPermitted;
    void IConnectionCoordinator.RefreshEndpointLevel() => RefreshEndpointLevel();
    void IConnectionCoordinator.EnforceConnectPermission() => EnforceConnectPermission();
    void IConnectionCoordinator.Publish() => Publish();
    void IConnectionCoordinator.SuppressDeliberately(string status) => SuppressDeliberately(status);
    void IConnectionCoordinator.Report(string message) => Report(message);
}
