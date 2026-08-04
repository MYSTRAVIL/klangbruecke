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

    public event EventHandler<string>? Status;

    private void Report(string message) => Status?.Invoke(this, message);

    /// <summary>Paired devices that can act as an audio source for this PC.</summary>
    public static async Task<IReadOnlyList<DeviceInformation>> FindDevicesAsync()
    {
        string selector = AudioPlaybackConnection.GetDeviceSelector();
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
        return devices.ToList();
    }

    public async Task<bool> ConnectAsync(string deviceId)
    {
        Disconnect();

        AudioPlaybackConnection? connection = AudioPlaybackConnection.TryCreateFromId(deviceId);
        if (connection is null)
        {
            Report("Could not create an audio playback connection for that device.");
            return false;
        }

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
            Report($"Connection threw: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    private void OnStateChanged(AudioPlaybackConnection sender, object args)
    {
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
