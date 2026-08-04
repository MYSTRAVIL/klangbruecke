using Klangbruecke.Audio;
using Klangbruecke.Bluetooth;
using Klangbruecke.Config;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;
using NAudio.CoreAudioApi;
using Windows.Devices.Enumeration;

namespace Klangbruecke;

/// <summary>
/// Tray-only shell. There is deliberately no main window: an app you have to keep open is
/// the exact shortcoming of the app this replaces.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Settings _settings;
    private readonly AudioSinkService _sink = new();
    private readonly CallTransportService _calls = new();

    // Field initializer, so the marshalling control is built on the thread that constructs this -
    // the UI thread, since Program.Main is what does it.
    private readonly ControlUiDispatcher _ui = new();

    // Not a field initializer, unlike its neighbours: it needs _ui, and a field initializer cannot
    // read another instance field.
    private readonly AudioRouter _router;

    private readonly StatusPresenter _status;

    public TrayContext()
    {
        _settings = Settings.Load();

        // The router hands its stopped-event teardown to this dispatcher so it never runs on the
        // NAudio thread that raised the event; see AudioRouter.RequestTeardown for why that deadlocks.
        _router = new AudioRouter(_ui);

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

        _icon.ContextMenuStrip.Opening += async (s, e) =>
        {
            e.Cancel = true;
            await RebuildMenuAsync();
            _icon.ContextMenuStrip.Show(Cursor.Position);
        };

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

    private void SetStatus(string message) => _status.Show(message);

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
            IReadOnlyList<DeviceInformation> devices = await AudioSinkService.FindDevicesAsync();
            if (devices.Count == 0)
            {
                phoneMenu.DropDownItems.Add(new ToolStripMenuItem("No paired devices found") { Enabled = false });
            }

            foreach (DeviceInformation device in devices)
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

        foreach (MMDevice device in AudioRouter.GetOutputDevices())
        {
            var item = new ToolStripMenuItem(device.FriendlyName)
            {
                Checked = device.ID == _settings.OutputDeviceId,
            };
            string id = device.ID;
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

        var disconnect = new ToolStripMenuItem("Disconnect") { Enabled = _sink.IsConnected || _calls.IsConnected };
        disconnect.Click += (_, _) => Disconnect();
        menu.Items.Add(disconnect);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();
        menu.Items.Add(exit);
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
            SetStatus($"Connect failed: {ex.Message}");
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
        if (!AudioSinkPolicy.CanOpenConnection(isPackaged))
        {
            Log.Warn(AudioSinkPolicy.Explain(isPackaged));

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
    /// deliberately not gated as a whole on the availability verdict: see <see cref="CallTransportPlan"/>
    /// for why an unpackaged run still enumerates.
    /// </summary>
    private async Task ConnectCallsAsync(string phoneDeviceId)
    {
        CallsAvailability availability = CallsPolicy.Decide(_settings.EnableCalls, PackageIdentity.IsPackaged);

        // Straight to the log rather than through SetStatus: the explanation runs to a couple of
        // hundred characters and the tooltip caps at 96, so routing it through the tray would truncate
        // the log copy too - and overwrite "A2DP sink connected" with a permanent condition the user
        // cannot act on from the tray. The music status is the one worth showing there.
        Log.Info(CallsPolicy.Explain(availability));

        if (!CallTransportPlan.ShouldEnumerate(availability))
        {
            return;
        }

        try
        {
            IReadOnlyList<DeviceInformation> transports = await CallTransportService.FindDevicesAsync();

            TransportMatchResult result = TransportMatcher.Match(
                transports.Select(t => new TransportCandidate(t.Id, t.Name)).ToList(),
                phoneDeviceId);

            if (result.Outcome == TransportMatchOutcome.AddressMatch)
            {
                Log.Info(result.Reason);
            }
            else
            {
                Log.Warn(result.Reason);
            }

            if (!CallTransportPlan.ShouldRegister(availability))
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

            await _calls.ConnectAsync(result.Match.Value.Id);
        }
        catch (Exception ex)
        {
            Log.Error("Call transport enumeration failed.", ex);
            SetStatus($"Call transport unavailable: {ex.Message}");
        }
    }

    private void StartRouting()
    {
        MMDevice? source = AudioRouter.FindSinkCaptureEndpoint();
        if (source is null)
        {
            // Per docs/FINDINGS.md §4 this is the expected state when nothing holds a connection open,
            // not a bug. It is also exactly what a failed connect looks like, which is why the A2DP
            // connect result is logged above rather than inferred from here.
            SetStatus("No A2DP sink endpoint - nothing is holding a connection open.");
            return;
        }

        MMDevice? sink = AudioRouter.GetOutputDeviceOrDefault(_settings.OutputDeviceId);
        if (sink is null)
        {
            SetStatus("No usable output device.");
            return;
        }

        // Both endpoint names, before the stream starts: a route that runs silently is almost always
        // the right source paired with the wrong sink, and afterwards nothing says which two were used.
        Log.Info($"Routing source='{source.FriendlyName}' sink='{sink.FriendlyName}'.");

        _router.Start(source, sink);
    }

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
