using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace Klangbruecke.Bluetooth;

/// <summary>
/// Owns the A2DP sink half: makes this PC an audio sink the phone can stream to.
///
/// Lifecycle is two-step and easy to get wrong:
///   Start()     - begin advertising / listening. Does NOT connect.
///   OpenAsync() - actually open the connection to a specific device.
///
/// While a connection is open Windows exposes a capture endpoint named
/// "Line (&lt;phone&gt; A2DP SNK)". If that endpoint is absent, nothing is holding a
/// connection and the phone cannot see this PC as an output. See docs/FINDINGS.md §4.
/// </summary>
public sealed class AudioSinkService : IDisposable
{
    private AudioPlaybackConnection? _connection;
    private bool _disposed;

    public string? ConnectedDeviceId { get; private set; }
    public bool IsConnected => _connection is not null && ConnectedDeviceId is not null;

    public event EventHandler<StatusMessage>? Status;

    /// <summary>
    /// Info unless said otherwise. The level travels with the message because this class is the only
    /// thing that knows it - see <see cref="StatusMessage"/>.
    /// </summary>
    private void Report(string message, LogLevel level = LogLevel.Info) =>
        Status?.Invoke(this, new StatusMessage(message, level));

    /// <summary>Paired devices that can act as an audio source for this PC.</summary>
    public static async Task<IReadOnlyList<DeviceInformation>> FindDevicesAsync()
    {
        string selector = AudioPlaybackConnection.GetDeviceSelector();
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);

        // Logged in full, not counted. The id is the input to BluetoothDeviceId's address extraction
        // and to the transport correlation built on it, so when either misbehaves the log has to carry
        // the exact text that produced it - a name and a count cannot be re-run against a regex.
        Log.Info($"A2DP selector matched {devices.Count} device(s).");
        foreach (DeviceInformation device in devices)
        {
            Log.Info($"  A2DP candidate '{device.Name}' id={device.Id}");
        }

        return devices.ToList();
    }

    public async Task<bool> ConnectAsync(string deviceId)
    {
        // Backstop, not the primary gate - TrayContext checks the same policy so it can log the
        // reason once and skip the attempt cleanly. This one guards the call itself, because the
        // failure mode is process death rather than a false return: a Stage 1 reconnect watcher that
        // called straight into here without asking would silently reintroduce a bricked startup.
        // Read once and passed to both, so the reason logged is provably the reason decided on. The
        // literal that used to sit in the Explain call was right only because the policy currently
        // takes one input; this call site exists for a caller that does not exist yet.
        bool isPackaged = PackageIdentity.IsPackaged;
        if (!AudioSinkPolicy.CanOpenConnection(isPackaged))
        {
            Log.Warn(AudioSinkPolicy.Explain(isPackaged));
            return false;
        }

        Log.Info($"Opening A2DP sink connection to id={deviceId}");

        Disconnect();

        // Bracketed by log lines rather than wrapped in a try, because unpackaged this call does not
        // throw - it takes the process down with an AccessViolationException raised inside the CsWinRT
        // ABI shim (ABI.Windows.Media.Audio.IAudioPlaybackConnectionStaticsMethods.TryCreateFromId).
        // A corrupted-state exception runs no managed handler, so nothing here, in Program.Main's
        // hooks, or in the log can record it after the fact. The only instrument left is absence: the
        // "Opening A2DP sink connection" line above with no line below it means the process died
        // inside this call. Reproduced with a valid live device id and with garbage, on STA and MTA,
        // on two Windows SDK projection versions. See docs/FINDINGS.md §8.
        AudioPlaybackConnection? connection = AudioPlaybackConnection.TryCreateFromId(deviceId);
        if (connection is null)
        {
            Report("Could not create an audio playback connection for that device.");
            return false;
        }

        Log.Info("Playback connection created; starting the listener.");

        _connection = connection;
        _connection.StateChanged += OnStateChanged;

        try
        {
            // Listen first, then open. Opening without Start() will not work.
            _connection.Start();

            AudioPlaybackConnectionOpenResult result = await _connection.OpenAsync();
            if (result.Status != AudioPlaybackConnectionOpenResultStatus.Success)
            {
                Report($"Connection failed: {result.Status}. If this says the device is unreachable, " +
                       "check the pairing before suspecting the code - see docs/FINDINGS.md §3.");
                Disconnect();
                return false;
            }

            ConnectedDeviceId = deviceId;
            Report("A2DP sink connected.");
            return true;
        }
        catch (Exception ex)
        {
            // Log.Error before Report, and both. Report reaches the file through StatusPresenter
            // carrying ex.Message alone, and OpenAsync is the awaited WinRT call whose failure
            // renders as "One or more errors occurred." with the cause entirely in the inner
            // exception and the stack - precisely the case FileLog's full ToString() rendering
            // exists for, and had never once received.
            Log.Error("Opening the A2DP sink connection threw.", ex);

            // Error, matching the line above. Raised at Info it reached the file as a second entry
            // describing the same throw one level down, so a reader scanning [INF] met "Connection
            // threw" among the ordinary progress and a reader scanning [ERR] saw only half of it.
            Report($"Connection threw: {ex.Message}", LogLevel.Error);
            Disconnect();
            return false;
        }
    }

    private void OnStateChanged(AudioPlaybackConnection sender, object args)
    {
        // Reports state only. Acting on a drop is Stage 1's job; recording it is this stage's - and
        // Report is already a recording: StatusPresenter writes every status to the log before it
        // touches the tray, so a Log call beside this one would only double the entry.
        Report($"A2DP sink state: {sender.State}");
    }

    public void Disconnect()
    {
        if (_connection is not null)
        {
            _connection.StateChanged -= OnStateChanged;
            _connection.Dispose();
            _connection = null;
        }

        ConnectedDeviceId = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disconnect();
    }
}
