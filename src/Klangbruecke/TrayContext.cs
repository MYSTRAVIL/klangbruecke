using Klangbruecke.Audio;
using Klangbruecke.Bluetooth;
using Klangbruecke.Config;
using Klangbruecke.Diagnostics;
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
    private readonly AudioRouter _router = new();

    // Field initializer, so the marshalling control is built on the thread that constructs this -
    // the UI thread, since Program.Main is what does it.
    private readonly ControlUiDispatcher _ui = new();

    private string _lastStatus = "Idle";

    public TrayContext()
    {
        _settings = Settings.Load();

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

        _icon.ContextMenuStrip.Opening += async (s, e) =>
        {
            e.Cancel = true;
            await RebuildMenuAsync();
            _icon.ContextMenuStrip.Show(Cursor.Position);
        };

        if (_settings.PhoneDeviceId is not null)
        {
            _ = ConnectAsync(_settings.PhoneDeviceId);
        }
    }

    private void SetStatus(string message)
    {
        // Logged where the status arrives rather than inside the post below: the log is a record of
        // what happened, not of what the tooltip ended up saying, and an update dropped during
        // shutdown must still leave a line behind - shutdown is when one is worth most.
        Log.Info(message);

        // Reached from the WinRT threadpool and from NAudio callbacks; touching the icon
        // off the UI thread throws intermittently rather than failing cleanly.
        _ui.Post(() =>
        {
            // Assigned here, not above, so the menu that reads it and the tooltip change together
            // on the UI thread. A menu opening is queued behind an already-posted update, so the
            // two cannot disagree.
            _lastStatus = message;

            // Tray tooltips are capped at 63 characters.
            _icon.Text = message.Length > 60 ? $"Klangbruecke: {message[..57]}..." : $"Klangbruecke: {message}";
        });
    }

    private async Task RebuildMenuAsync()
    {
        ContextMenuStrip menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();

        menu.Items.Add(new ToolStripMenuItem(_lastStatus) { Enabled = false });
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
                item.Click += async (_, _) => await ConnectAsync(device.Id);
                phoneMenu.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
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

    private async Task ConnectAsync(string deviceId)
    {
        _settings.PhoneDeviceId = deviceId;
        _settings.Save();

        bool sinkOk = await _sink.ConnectAsync(deviceId);
        if (sinkOk)
        {
            StartRouting();
        }

        if (_settings.EnableCalls)
        {
            // Independent of the music half - one failing must not take out the other.
            try
            {
                IReadOnlyList<DeviceInformation> transports = await CallTransportService.FindDevicesAsync();
                DeviceInformation? match = transports.FirstOrDefault();
                if (match is not null)
                {
                    await _calls.ConnectAsync(match.Id);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Call transport unavailable: {ex.Message}");
            }
        }
    }

    private void StartRouting()
    {
        MMDevice? source = AudioRouter.FindSinkCaptureEndpoint();
        if (source is null)
        {
            SetStatus("No A2DP sink endpoint - nothing is holding a connection open.");
            return;
        }

        MMDevice? sink = AudioRouter.GetOutputDeviceOrDefault(_settings.OutputDeviceId);
        if (sink is null)
        {
            SetStatus("No usable output device.");
            return;
        }

        _router.Start(source, sink);
    }

    private void Disconnect()
    {
        _router.Stop();
        _sink.Disconnect();
        _calls.Disconnect();
        SetStatus("Disconnected.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _router.Dispose();
            _sink.Dispose();
            _calls.Dispose();

            // After the three above, whose teardown still raises status, and before the icon:
            // disposing it drops any queued update that would otherwise reach a dead icon.
            _ui.Dispose();

            _icon.Visible = false;
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
