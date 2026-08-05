using System.ComponentModel;
using Klangbruecke.Audio;
using Klangbruecke.Bluetooth;
using Klangbruecke.Config;
using Klangbruecke.Connection;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke;

/// <summary>
/// Tray-only shell. There is deliberately no main window: an app you have to keep open is
/// the exact shortcoming of the app this replaces.
///
/// <b>A view, and only a view.</b> It draws a menu, turns a click into one call, and writes what it
/// is told into a tooltip. It opens no Bluetooth connection, starts no route, and holds no state
/// machine - <see cref="ConnectionManager"/> owns all of that, and every handler that changes
/// anything calls exactly one of its methods. (Exit is the exception, and the only one: it ends the
/// message loop, which is <see cref="ApplicationContext"/>'s own job and not the manager's.) The rule
/// is worth stating as a rule because the previous version of this file was the opposite: it held the
/// sink, the call transport, the router and the device factory, and the connect sequence was 140
/// lines of tray code that nothing could test and that had no answer at all for a phone that came
/// back into range.
///
/// <b>It does read <see cref="PackageIdentity.IsPackaged"/>, for two menu labels and nothing else.</b>
/// Said plainly because the first draft of this comment claimed it read none at all, which the
/// menu-building path 100 lines below already contradicted - the exact defect class this task was
/// sweeping up elsewhere. The two reads are <see cref="AudioSinkPolicy.MenuItem"/> and
/// <see cref="CallsPolicy.MenuItem"/>, both pure functions of the flag, both asserted, and neither
/// used to decide whether anything is attempted. Deciding <em>that</em> from a process-wide static
/// stays out of the manager on purpose - see its class comment - which is why the labels are the only
/// place the flag surfaces at all.
///
/// <b>Four things reach it, and none of them is a seam.</b> The icon it writes to, the presenter that
/// writes to it, the manager it asks, and the settings - read only, for the ticks beside the menu
/// items. Every write to those settings goes through the manager, which saves them; a view that wrote
/// them directly would be a second author of the same file and the manager's own copy would be stale
/// the moment it did.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly StatusPresenter _status;
    private readonly ConnectionManager _connection;

    /// <summary>
    /// The user's choices, for the ticks only. <b>Never written here.</b> See the class summary.
    /// </summary>
    private readonly Settings _settings;

    /// <summary>
    /// Set only across the <c>Show()</c> call in <see cref="OnMenuOpening"/>, to let the Opening
    /// that <c>Show()</c> itself raises through. UI thread only, so no synchronization.
    /// </summary>
    private bool _menuRebuilt;

    public TrayContext(NotifyIcon icon, StatusPresenter status, ConnectionManager connection, Settings settings)
    {
        _icon = icon;
        _status = status;
        _connection = connection;
        _settings = settings;

        // Built here rather than handed in, so the field is non-null by construction and the three
        // places below that use it need no assertion. Attaching it is all NotifyIcon needs.
        _menu = new ContextMenuStrip();
        _icon.ContextMenuStrip = _menu;
        _menu.Opening += OnMenuOpening;

        // Both, and the second is not a nicety. The tooltip is one line with two writers - these, and
        // every component's own Status - so a component announcement displaces the state sentence and
        // something has to bring it back. StateChanged alone could not: a connection that stays
        // Connected while its detail moves from "waiting for phone audio" to "music and calls up"
        // raises nothing, and "A2DP sink state: Closed" would sit in the tooltip over a working route.
        // That is the exact failure leading with the state was meant to fix.
        //
        // Before Start, because Start ends in a Publish and an announcement with nobody listening is
        // the one the tooltip would spend the whole session missing.
        _connection.StateChanged += OnConnectionStateChanged;
        _connection.DetailChanged += OnConnectionDetailChanged;

        // <b>Here, and the thread this runs on is the point.</b> Start subscribes PowerNotifier, and
        // SystemEvents captures whichever SynchronizationContext is current at that moment and
        // dispatches through it - so where this line sits decides which thread every resume arrives
        // on. A constructor that runs "before Application.Run" sounds like it runs before the UI
        // thread has a context, and for this app that is false: Program.Main builds a
        // ControlUiDispatcher first, and a Control's constructor installs the WinForms context. So
        // this lands on the UI thread. Pinned by
        // UiDispatcherTests.Control_InstallsTheWinFormsSynchronizationContextOnTheThreadThatBuildsIt.
        _connection.Start();

        // Start publishes only when the state actually moved, and at startup it usually has not - the
        // manager's own constructor already computed it. Without this the tooltip would read
        // "Klangbruecke" until something changed, which for a phone that is simply out of range is
        // the whole session.
        ShowConnectionState();
    }

    /// <summary>
    /// The state moved. Both halves of the sentence are re-read from the manager rather than taken
    /// from the event, which carries only the state - a detail read from anywhere else could describe
    /// a different instant, and "Connected" beside "retrying in 8s" is worse than either alone.
    /// </summary>
    private void OnConnectionStateChanged(object? sender, ConnectionState state) => ShowConnectionState();

    /// <summary>The name held still and the phrase moved. Same sentence, same repaint.</summary>
    private void OnConnectionDetailChanged(object? sender, EventArgs e) => ShowConnectionState();

    private void ShowConnectionState() => _status.Show(_connection.State, _connection.Detail);

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
            _menu.Show(Cursor.Position);
        }
        catch (Exception ex)
        {
            // Leaving the flag set would let the next right-click through without a rebuild.
            _menuRebuilt = false;
            Log.Error("Showing the tray menu failed.", ex);
        }
    }

    /// <summary>
    /// Builds the menu in reading order, top to bottom.
    ///
    /// <b>Exit is last and is still guaranteed</b>, which used to take an ordering trick: the old
    /// Disconnect item asked <c>ICallTransportService.IsRegistered</c>, a live ABI call, so Exit was
    /// added first and Disconnect inserted above it. That call is gone - the manager owns the
    /// question now and nothing in this method touches an ABI directly - and the two remaining calls
    /// that reach a live device, the phone and output enumerations, each answer for themselves below.
    /// So no path through here can throw before Exit is added, and the ordering trick would now be
    /// defending against nothing while reading as if it defended against something.
    /// </summary>
    private async Task RebuildMenuAsync()
    {
        _menu.Items.Clear();

        _menu.Items.Add(new ToolStripMenuItem(_status.Last) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(await BuildPhoneMenuAsync());
        _menu.Items.Add(BuildOutputMenu());
        _menu.Items.Add(new ToolStripSeparator());

        // Text, clickability and tick together, from one rule - see CallsPolicy.MenuItem. Unpackaged
        // this item used to read as switched on while nothing could ever claim the hands-free role.
        (string callsText, bool callsEnabled, bool callsTicked) =
            CallsPolicy.MenuItem(PackageIdentity.IsPackaged, _settings.EnableCalls);

        var calls = new ToolStripMenuItem(callsText) { Checked = callsTicked, Enabled = callsEnabled };
        calls.Click += (_, _) => _connection.SetCallsEnabled(!_settings.EnableCalls);
        _menu.Items.Add(calls);

        var autoReconnect = new ToolStripMenuItem("Reconnect automatically") { Checked = _settings.AutoReconnect };
        autoReconnect.Click += (_, _) => _connection.SetAutoReconnect(!_settings.AutoReconnect);
        _menu.Items.Add(autoReconnect);

        _menu.Items.Add(new ToolStripSeparator());

        // On the selection, not on anything live. Disconnect is a decision about the phone the user
        // picked - it latches until that phone leaves and returns - so it is offerable exactly when
        // there is a phone to make it about. The old test asked the call transport whether it held
        // the hands-free role, which is a live ABI call on the menu-building path and answered for
        // only one of the two halves.
        var disconnect = new ToolStripMenuItem("Disconnect") { Enabled = _settings.PhoneDeviceId is not null };
        disconnect.Click += (_, _) => _connection.RequestDisconnect();
        _menu.Items.Add(disconnect);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();
        _menu.Items.Add(exit);
    }

    private async Task<ToolStripMenuItem> BuildPhoneMenuAsync()
    {
        // The music half's only statement anywhere that it needs the packaged build. See
        // AudioSinkPolicy.MenuItem for why this one stays clickable where the calls item does not.
        (string text, bool enabled) = AudioSinkPolicy.MenuItem(PackageIdentity.IsPackaged);

        var phoneMenu = new ToolStripMenuItem(text) { Enabled = enabled };

        // Mirrors the Output submenu's "System default", and it is the only way to tell the app to
        // stop caring about phones at all - Disconnect is a latch that expires when the phone leaves
        // and returns, which is a different thing.
        var none = new ToolStripMenuItem("None") { Checked = _settings.PhoneDeviceId is null };
        none.Click += (_, _) => _connection.DeselectPhone();
        phoneMenu.DropDownItems.Add(none);
        phoneMenu.DropDownItems.Add(new ToolStripSeparator());

        try
        {
            IReadOnlyList<PhoneDevice> devices = await _connection.FindPhonesAsync();

            if (devices.Count == 0)
            {
                phoneMenu.DropDownItems.Add(new ToolStripMenuItem("No paired devices found") { Enabled = false });
            }

            foreach (PhoneDevice device in devices)
            {
                // The selected phone, not the connected one. They differ for the whole of every
                // range exit and every reconnect, and the old reading left the menu with no tick at
                // all while the tray was reporting "waiting for the phone to appear" - so the one
                // screen that could have said which phone it was waiting for did not.
                var item = new ToolStripMenuItem(device.Name)
                {
                    Checked = device.Id == _settings.PhoneDeviceId,
                };

                string id = device.Id;
                item.Click += (_, _) => _connection.SelectPhone(id);
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

        return phoneMenu;
    }

    private ToolStripMenuItem BuildOutputMenu()
    {
        var outputMenu = new ToolStripMenuItem("Output");

        var systemDefault = new ToolStripMenuItem("System default")
        {
            Checked = _settings.OutputDeviceId is null,
        };

        systemDefault.Click += (_, _) => _connection.SelectOutput(null);
        outputMenu.DropDownItems.Add(systemDefault);
        outputMenu.DropDownItems.Add(new ToolStripSeparator());

        try
        {
            // A live WASAPI enumeration, and it throws for the ordinary reason: an endpoint can be
            // unplugged between the enumerate and the property read. Caught here, beside the phone
            // list's catch, rather than left to OnMenuOpening - which would show whatever had been
            // built so far and lose every item below this one, Disconnect and Exit included.
            foreach (AudioOutputDevice device in _connection.ListOutputDevices())
            {
                var item = new ToolStripMenuItem(device.Name)
                {
                    Checked = device.Id == _settings.OutputDeviceId,
                };

                string id = device.Id;
                item.Click += (_, _) => _connection.SelectOutput(id);
                outputMenu.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Endpoint enumeration for the output menu failed.", ex);
            outputMenu.DropDownItems.Add(new ToolStripMenuItem($"Enumeration failed: {ex.Message}") { Enabled = false });
        }

        return outputMenu;
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
            //
            // One call where there were three: the manager owns every seam and disposes all of them.
            Teardown.Quietly(_connection.Dispose, "dispose the connection manager");

            // After the manager, whose teardown still raises status through the presenter, and so
            // still writes this icon's Text. Program.Main disposes the dispatcher after this returns,
            // which is the other half of the same ordering - see the note there.
            //
            // Split from the Dispose below it. Hiding is what the user sees; if the property setter
            // throws - it touches the shell - the handle must still be released.
            Teardown.Quietly(() => _icon.Visible = false, "hide the tray icon");
            Teardown.Quietly(_icon.Dispose, "dispose the tray icon");
        }

        base.Dispose(disposing);
    }
}
