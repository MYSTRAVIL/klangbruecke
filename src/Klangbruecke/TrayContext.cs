using System.ComponentModel;
using Klangbruecke.Audio;
using Klangbruecke.Bluetooth;
using Klangbruecke.Config;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke;

/// <summary>
/// Tray-only shell. There is deliberately no main window: an app you have to keep open is
/// the exact shortcoming of the app this replaces.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Settings _settings;
    // Held as the interface, not the class: everything this view needs from the music half is on the
    // seam, and typing it here is what keeps that true - a member that drifts back onto the concrete
    // class stops compiling rather than quietly re-coupling the tray to WinRT.
    private readonly IAudioSinkService _sink = new AudioSinkService();

    // Held as the interface for the same reason as _sink above, and with one extra consequence: this
    // seam is what finally takes the WinRT device-enumeration type out of this file. Transports
    // arrive as TransportCandidate, so nothing here declares it - and the name is left unwritten even
    // in comments, so that grepping this file for it stays a real check rather than one that always
    // hits prose.
    private readonly ICallTransportService _calls = new CallTransportService();

    // Field initializer, so the marshalling control is built on the thread that constructs this -
    // the UI thread, since Program.Main is what does it.
    private readonly ControlUiDispatcher _ui = new();

    // The one thing in the app that opens WASAPI endpoints. Shared between the router, which routes
    // through them, and the Output menu, which lists them.
    private readonly IAudioDeviceFactory _devices = new WasapiDeviceFactory();

    // Not a field initializer, unlike its neighbours: it needs _ui, and a field initializer cannot
    // read another instance field.
    private readonly AudioRouter _router;

    private readonly StatusPresenter _status;

    /// <summary>
    /// Set only across the <c>Show()</c> call in <see cref="OnMenuOpening"/>, to let the Opening
    /// that <c>Show()</c> itself raises through. UI thread only, so no synchronization.
    /// </summary>
    private bool _menuRebuilt;

    public TrayContext()
    {
        _settings = Settings.Load();

        // The router hands its stopped-event teardown to this dispatcher so it never runs on the
        // NAudio thread that raised the event; see AudioRouter.RequestTeardown for why that deadlocks.
        _router = new AudioRouter(_ui, _devices);

        // The severity comes with the message. Forwarding only the text would have this view decide
        // how serious an event it did not witness was, which is how every component failure used to
        // reach the log at Info. See StatusMessage.
        _sink.Status += (_, m) => SetStatus(m);
        _calls.Status += (_, m) => SetStatus(m);
        _router.Status += (_, m) => SetStatus(m);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Klangbruecke",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };

        // After the icon, whose Text it writes. Nothing raises status before this point: the
        // services subscribed above are not started until ConnectAsync at the end of this ctor.
        _status = new StatusPresenter(_ui, text => _icon.Text = text);

        _icon.ContextMenuStrip.Opening += OnMenuOpening;

        if (_settings.PhoneDeviceId is not null)
        {
            // Fire-and-forget by necessity - a constructor cannot await - but not unobserved.
            // ConnectGuardedAsync catches for itself because the alternative is
            // TaskScheduler.UnobservedTaskException, which fires when the Task is finalized: a
            // reconnect that failed at startup could surface a collection later, or never, and
            // reconnect-after-reboot is the predecessor app's defining bug. It is the one path that
            // must not be able to fail quietly.
            _ = ConnectGuardedAsync(_settings.PhoneDeviceId, StartupTrigger);
        }
    }

    /// <summary>
    /// A component's status, forwarded with the severity it came with. This class did not witness
    /// the event and must not re-decide how serious it was - which is what forwarding the text alone
    /// amounted to, since everything then landed at Info. See <see cref="StatusMessage"/>.
    /// </summary>
    private void SetStatus(StatusMessage status) => _status.Show(status);

    /// <summary>The tray's own messages, which are progress unless this class says otherwise.</summary>
    private void SetStatus(string message, LogLevel level = LogLevel.Info) => _status.Show(message, level);

    /// <summary>
    /// The menu lists devices, which must be enumerated, which is async - but Opening is a
    /// synchronous veto. So the first open is cancelled, the menu rebuilt, and Show() called.
    ///
    /// Show() raises Opening again. Without <see cref="_menuRebuilt"/> that second pass cancels
    /// its own display and rebuilds again, forever: about three enumerations a second, no menu
    /// ever on screen, and the only outward sign is a tray icon that ignores right-clicks.
    /// </summary>
    private async void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (_menuRebuilt)
        {
            _menuRebuilt = false;
            return;
        }

        e.Cancel = true;

        try
        {
            await RebuildMenuAsync();
        }
        catch (Exception ex)
        {
            // Show what was built regardless; a menu missing its device list still offers Exit,
            // and a tray app whose only exit is Task Manager is worse than a partial menu.
            Log.Error("Rebuilding the tray menu failed.", ex);
        }

        _menuRebuilt = true;

        try
        {
            _icon.ContextMenuStrip!.Show(Cursor.Position);
        }
        catch (Exception ex)
        {
            // Leaving the flag set would let the next right-click through without a rebuild.
            _menuRebuilt = false;
            Log.Error("Showing the tray menu failed.", ex);
        }
    }

    private async Task RebuildMenuAsync()
    {
        ContextMenuStrip menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();

        menu.Items.Add(new ToolStripMenuItem(_status.Last) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        // --- phones ---
        var phoneMenu = new ToolStripMenuItem("Phone");
        try
        {
            IReadOnlyList<PhoneDevice> devices = await _sink.FindDevicesAsync();
            if (devices.Count == 0)
            {
                phoneMenu.DropDownItems.Add(new ToolStripMenuItem("No paired devices found") { Enabled = false });
            }

            foreach (PhoneDevice device in devices)
            {
                var item = new ToolStripMenuItem(device.Name)
                {
                    Checked = device.Id == _sink.ConnectedDeviceId,
                };
                item.Click += async (_, _) => await ConnectGuardedAsync(device.Id, MenuTrigger);
                phoneMenu.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            // Logged as well as shown. A disabled menu item lives until the menu closes and takes the
            // stack trace with it; the failure it describes - device enumeration throwing - is the
            // first thing anyone reading the log will need, and it left no other trace.
            Log.Error("Device enumeration for the phone menu failed.", ex);
            phoneMenu.DropDownItems.Add(new ToolStripMenuItem($"Enumeration failed: {ex.Message}") { Enabled = false });
        }

        menu.Items.Add(phoneMenu);

        // --- output device ---
        var outputMenu = new ToolStripMenuItem("Output");
        var systemDefault = new ToolStripMenuItem("System default")
        {
            Checked = _settings.OutputDeviceId is null,
        };
        systemDefault.Click += (_, _) => SelectOutput(null);
        outputMenu.DropDownItems.Add(systemDefault);
        outputMenu.DropDownItems.Add(new ToolStripSeparator());

        foreach (AudioOutputDevice device in _devices.ListOutputs())
        {
            var item = new ToolStripMenuItem(device.Name)
            {
                Checked = device.Id == _settings.OutputDeviceId,
            };
            string id = device.Id;
            item.Click += (_, _) => SelectOutput(id);
            outputMenu.DropDownItems.Add(item);
        }

        menu.Items.Add(outputMenu);
        menu.Items.Add(new ToolStripSeparator());

        var calls = new ToolStripMenuItem("Route calls to PC") { Checked = _settings.EnableCalls };
        calls.Click += (_, _) =>
        {
            _settings.EnableCalls = !_settings.EnableCalls;
            _settings.Save();
        };
        menu.Items.Add(calls);

        var autoReconnect = new ToolStripMenuItem("Reconnect automatically") { Checked = _settings.AutoReconnect };
        autoReconnect.Click += (_, _) =>
        {
            _settings.AutoReconnect = !_settings.AutoReconnect;
            _settings.Save();
        };
        menu.Items.Add(autoReconnect);

        menu.Items.Add(new ToolStripSeparator());

        // Exit is built and added FIRST, then Disconnect is inserted above it, so the menu reads
        // Disconnect-then-Exit while Exit is already in the collection before anything below can
        // throw. Ordering, not a catch: _calls.IsRegistered is a live CsWinRT ABI call - the slot
        // used to hold an auto-property that could not fail - and a throw from it unwinds to
        // OnMenuOpening, which logs and then Show()s whatever was built. Added last, Exit would be
        // the item missing, which is the one failure this method's own comment calls worse than a
        // partial menu. This costs two lines and needs no invented state.
        //
        // It does not cover the AccessViolationException case in docs/FINDINGS.md §8 - a
        // corrupted-state exception is not caught by OnMenuOpening either - but it does cover the
        // ordinary device-gone HRESULT, which is the reachable one.
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();
        int exitIndex = menu.Items.Add(exit);

        // IsRegistered, not a connected flag. The old IsConnected was set only when
        // PhoneLineTransportDevice.ConnectAsync returned true, which it never has on this machine -
        // so the calls half could never enable this item even while it held the hands-free role, and
        // the one action that releases the role was unreachable from the tray.
        var disconnect = new ToolStripMenuItem("Disconnect") { Enabled = _sink.IsConnected || _calls.IsRegistered };
        disconnect.Click += (_, _) => Disconnect();
        menu.Items.Insert(exitIndex, disconnect);
    }

    private void SelectOutput(string? deviceId)
    {
        _settings.OutputDeviceId = deviceId;
        _settings.Save();

        // Re-point an in-flight stream without dropping the Bluetooth connection.
        if (_router.IsRunning)
        {
            StartRouting();
        }
    }

    // What started a connect, carried into the log. Reconnect-after-reboot is the predecessor app's
    // defining bug, so "did this run because the app just started, or because someone clicked?" is
    // the question the log will be read to answer. It is inferable from position relative to the
    // "starting." line, but only while the two are adjacent - a reconnect that fires seconds late,
    // which is exactly the failure being hunted, is the case where the inference stops working.
    private const string StartupTrigger = "startup reconnect";
    private const string MenuTrigger = "menu selection";

    /// <summary>
    /// The only way in to <see cref="ConnectAsync"/>. Both call sites - the startup reconnect and a
    /// menu click - are fire-and-forget, so an escaping exception would land somewhere that logs it
    /// late or not at all; catching here makes every connect attempt account for itself.
    /// </summary>
    private async Task ConnectGuardedAsync(string deviceId, string trigger)
    {
        try
        {
            await ConnectAsync(deviceId, trigger);
        }
        catch (Exception ex)
        {
            Log.Error($"Connect failed ({trigger}) for id={deviceId}", ex);
            SetStatus($"Connect failed: {ex.Message}", LogLevel.Error);
        }
    }

    private async Task ConnectAsync(string deviceId, string trigger)
    {
        Log.Info($"Connect requested ({trigger}) for id={deviceId}");

        // Saved before either half is attempted, and deliberately still saved when the music half is
        // skipped below: the choice is the user's answer to "which phone", not a record of what
        // happened to connect, and the packaged build needs it on the next start.
        _settings.PhoneDeviceId = deviceId;
        _settings.Save();

        await ConnectMusicAsync(deviceId);
        await ConnectCallsAsync(deviceId);
    }

    /// <summary>
    /// The music half. Gated on package identity for the same reason as the calls half but a blunter
    /// consequence: unpackaged, the connect call kills the process outright rather than failing.
    /// See <see cref="AudioSinkPolicy"/>.
    /// </summary>
    private async Task ConnectMusicAsync(string deviceId)
    {
        // One read, passed to both, so the reason logged is the reason decided on.
        bool isPackaged = PackageIdentity.IsPackaged;
        bool canOpen = AudioSinkPolicy.CanOpenConnection(isPackaged);

        // Logged on both branches, mirroring the calls half below. While this line lived inside the
        // failure branch, Explain(true) - "Music enabled." - was unreachable, and a healthy packaged
        // run announced "Calls enabled." and said nothing whatsoever about music. Task 9 reads this
        // file to find out which halves were even attempted, and silence where music should be is
        // indistinguishable from music having been skipped.
        //
        // The level comes from the policy rather than from an if here, so it cannot drift from the
        // calls gate's the way it already had once.
        Log.Write(AudioSinkPolicy.LevelFor(isPackaged), AudioSinkPolicy.Explain(isPackaged));

        if (!canOpen)
        {
            // Worth the tooltip, unlike the calls explanation: there is no music status for it to
            // overwrite, because unpackaged there is never any music. Without it the tray reads
            // "Idle" forever and the app looks broken rather than incomplete.
            SetStatus("Music needs the packaged build (MSIX).");
            return;
        }

        bool sinkOk = await _sink.ConnectAsync(deviceId);
        Log.Info($"A2DP connect {(sinkOk ? "succeeded" : "failed")}.");

        if (sinkOk)
        {
            StartRouting();
        }
    }

    /// <summary>
    /// The calls half. Independent of the music half - one failing must not take out the other - and
    /// deliberately not gated as a whole on the availability verdict: see
    /// <see cref="CallsPolicy.ShouldEnumerate"/> for why an unpackaged run still enumerates.
    /// </summary>
    private async Task ConnectCallsAsync(string phoneDeviceId)
    {
        CallsAvailability availability = CallsPolicy.Decide(_settings.EnableCalls, PackageIdentity.IsPackaged);

        // Straight to the log rather than through SetStatus: the explanation runs to a couple of
        // hundred characters and the tooltip caps at 96, so routing it through the tray would truncate
        // the log copy too - and overwrite "A2DP sink connected" with a permanent condition the user
        // cannot act on from the tray. The music status is the one worth showing there.
        //
        // Level from the policy, the same shape as the music gate above: the user's own choice is
        // ordinary, a capability that cannot apply is not.
        Log.Write(CallsPolicy.LevelFor(availability), CallsPolicy.Explain(availability));

        if (!CallsPolicy.ShouldEnumerate(availability))
        {
            return;
        }

        try
        {
            // Declared, at last. The calls seam now hands back the same record the matcher takes, so
            // the projection that used to happen here - and the WinRT type name it forced this file
            // to know - are both gone.
            IReadOnlyList<TransportCandidate> transports = await _calls.FindTransportsAsync();

            TransportMatchResult result = TransportMatcher.Match(transports, phoneDeviceId);

            // The level follows the outcome, which is what TransportMatchOutcome's own summary
            // promises. NoCandidates used to warn while the enum documented it as "Not an error" -
            // a phone that offers no HFP transport is a fact about the phone, and TrayContext puts
            // it in the tray below where the user will see it regardless.
            Log.Write(TransportMatcher.LevelFor(result.Outcome), result.Reason);

            if (!CallsPolicy.ShouldRegister(availability))
            {
                Log.Info("Enumeration only: not registering or connecting the call transport.");
                return;
            }

            if (result.Match is null)
            {
                // Only NoCandidates and Ambiguous get here, and they are different facts: nothing
                // offered a transport, versus several did and none was this phone's. The log tells
                // them apart through result.Reason; the tooltip must not assert a mismatch that
                // never happened, because "matches" sends the reader looking for the other phone.
                SetStatus(result.Outcome == TransportMatchOutcome.NoCandidates
                    ? "No call transport found for this phone."
                    : "No call transport matches the selected phone.");
                return;
            }

            // Symmetric with the music half's "A2DP connect ..." line. Discarding this left the
            // calls half with no single line saying whether it came up - the reader had to infer it
            // from which of the service's own messages happened to be last.
            CallTransportResult connect = await _calls.ConnectAsync(result.Match.Value.Id);

            // Registered, and only Registered. This line used to follow
            // PhoneLineTransportDevice.ConnectAsync's bool, which is False on every run on this
            // machine - so the one line a reader greps for said "failed" on the runs where calls
            // demonstrably worked.
            //
            // "registration", not "connect", and the one word is load-bearing. A throw below a
            // successful RegisterApp reports Registered=true - correct, the role is held and the
            // caller must not re-claim it - but that put "Call transport connect succeeded." one
            // line above "[ERR] The call transport connect path threw.", the same noun reaching
            // opposite verdicts in one grep. Naming what the verdict is actually read from settles
            // it: registration succeeded, the connect attempt threw, and both are true.
            Log.Info($"Call transport registration {(connect.Registered ? "succeeded" : "failed")}.");

            // Separate line, deliberately: the transport's own answer stays in the log as a fact to
            // correlate against, and never as part of the verdict above. "not reached" rather than a
            // bool when the call never ran, because false there would be a measurement nothing made.
            Log.Info(
                "Call transport reported TransportConnected="
                + $"{connect.TransportConnected?.ToString() ?? "not reached"}. {connect.Reason}");
        }
        catch (Exception ex)
        {
            Log.Error("Call transport enumeration failed.", ex);
            SetStatus($"Call transport unavailable: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Both endpoint lookups now live in the router, behind <see cref="IAudioDeviceFactory"/>, and
    /// so do the two statuses they used to raise from here - "No A2DP sink endpoint ..." and
    /// "No usable output device." They arrive at the tray through <c>_router.Status</c> instead,
    /// which is the same subscription every other router message already uses.
    /// </summary>
    private void StartRouting() => _router.Start(_settings.OutputDeviceId);

    private void Disconnect()
    {
        // Distinguishes a deliberate teardown from a dropped connection. Both end with the router
        // stopped and the same endpoints gone; only this one was asked for.
        Log.Info("Disconnect requested from the tray.");

        _router.Stop();
        _sink.Disconnect();
        _calls.Disconnect();
        SetStatus("Disconnected.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Every step is guarded separately rather than the sequence as a whole. This runs during
            // Application.Run's teardown, outside the message loop's exception guard, so a throw here
            // reaches neither Application.ThreadException nor Main's try - it kills the process with a
            // WER dialog, which is a visible window in an app that must never show one. Guarding the
            // sequence would stop the crash but let the first failure skip everything after it; the
            // tray icon in particular is the last thing hidden, and leaving it drawn strands a dead
            // icon on the taskbar until the shell next reaps it.
            Teardown.Quietly(_router.Dispose, "dispose the audio router");
            Teardown.Quietly(_sink.Dispose, "dispose the audio sink");
            Teardown.Quietly(_calls.Dispose, "dispose the call transport");

            // After the three above, whose teardown still raises status, and before the icon:
            // disposing it drops any queued update that would otherwise reach a dead icon.
            Teardown.Quietly(_ui.Dispose, "dispose the ui dispatcher");

            // Split from the Dispose below it. Hiding is what the user sees; if the property setter
            // throws - it touches the shell - the handle must still be released.
            Teardown.Quietly(() => _icon.Visible = false, "hide the tray icon");
            Teardown.Quietly(_icon.Dispose, "dispose the tray icon");
        }

        base.Dispose(disposing);
    }
}
