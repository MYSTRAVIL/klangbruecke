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
/// While a connection is open Windows eventually exposes a capture endpoint named
/// "Line (&lt;phone&gt; A2DP SNK)", and its presence is proof that something is holding a connection.
///
/// The converse does not hold, and assuming it did is a measured defect rather than a theoretical
/// one: the endpoint is absent for an unbounded interval *after* the connection reports Opened, and
/// in 5 of 8 recorded launches the app looked for it too early, found nothing, and silently never
/// routed audio for the whole session. Endpoint presence is therefore sufficient evidence of a live
/// connection, never necessary - absence proves nothing at all about the connection.
///
/// That is why <see cref="IsConnected"/> below answers only for the WinRT connection object and the
/// endpoint half belongs to <c>IAudioEndpointMonitor</c>. Use the endpoint to verify a route; never
/// to infer a disconnection. See docs/FINDINGS.md §4.
/// </summary>
public sealed class AudioSinkService : IAudioSinkService
{
    private AudioPlaybackConnection? _connection;
    private bool _disposed;

    public string? ConnectedDeviceId { get; private set; }

    /// <summary>
    /// The connection object, and nothing else. See <see cref="IAudioSinkService.IsConnected"/> for
    /// why the capture endpoint is deliberately not part of this answer.
    /// </summary>
    public bool IsConnected => _connection is not null && ConnectedDeviceId is not null;

    public event EventHandler<StatusMessage>? Status;

    public event EventHandler<AudioSinkConnectionState>? StateChanged;

    /// <summary>
    /// Info unless said otherwise. The level travels with the message because this class is the only
    /// thing that knows it - see <see cref="StatusMessage"/>.
    /// </summary>
    private void Report(string message, LogLevel level = LogLevel.Info) =>
        Status?.Invoke(this, new StatusMessage(message, level));

    /// <summary>Paired devices that can act as an audio source for this PC.</summary>
    public async Task<IReadOnlyList<PhoneDevice>> FindDevicesAsync()
    {
        string selector = AudioPlaybackConnection.GetDeviceSelector();
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);

        // Logged in full, not counted. The id is the input to BluetoothDeviceId's address extraction
        // and to the transport correlation built on it, so when either misbehaves the log has to carry
        // the exact text that produced it - a name and a count cannot be re-run against a regex.
        Log.Info($"A2DP selector matched {devices.Count} device(s).");

        // Projected inside the same loop that logs, so the record and the line describing it are one
        // read of one DeviceInformation. Two passes would let a caller compare a log line against a
        // record that came from a different snapshot.
        var phones = new List<PhoneDevice>(devices.Count);
        foreach (DeviceInformation device in devices)
        {
            Log.Info($"  A2DP candidate '{device.Name}' id={device.Id}");
            phones.Add(new PhoneDevice(device.Id, device.Name));
        }

        return phones;
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
            // Warn regardless of what AudioSinkPolicy.LevelFor says, unlike the same verdict in
            // TrayContext.ConnectMusicAsync, because this is not the same event. TrayContext returns
            // before ever calling here, so reaching this line means a caller went straight to the
            // service without asking the policy - a defect in the caller, not the expected unpackaged
            // path. The two can never appear in one run.
            Log.Warn("Reached AudioSinkService.ConnectAsync with the music gate shut: the caller did "
                     + "not consult AudioSinkPolicy. " + AudioSinkPolicy.Explain(isPackaged));
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
            // Both, and in this order. Report carries ex.Message; only this call carries the
            // exception, and OpenAsync is the awaited WinRT call whose failure renders as "One or
            // more errors occurred." with the cause entirely in the inner exception and the stack -
            // precisely the case FileLog's ToString() rendering exists for, and had never once
            // received. Unconditional rather than folded into Report, which reaches the log only
            // while something is subscribed to Status.
            Log.Error("Opening the A2DP sink connection threw.", ex);

            // Error, matching the line above. At Info this was a second entry describing the same
            // throw one level down, so a reader scanning [INF] met "Connection threw" among the
            // ordinary progress while a reader grepping [ERR] saw only half of it.
            Report($"Connection threw: {ex.Message}", LogLevel.Error);
            Disconnect();
            return false;
        }
    }

    /// <summary>
    /// The WinRT enum, translated. Pure and public so the mapping can be asserted: reading these
    /// constants touches no ABI - the projection renders them as ordinary C# constants - which is
    /// what makes this the one part of the connection path a test host can execute.
    /// </summary>
    public static AudioSinkConnectionState Translate(AudioPlaybackConnectionState state) =>
        state == AudioPlaybackConnectionState.Opened
            ? AudioSinkConnectionState.Opened
            : AudioSinkConnectionState.Closed;

    private void OnStateChanged(AudioPlaybackConnection sender, object args) => PublishState(sender.State);

    /// <summary>
    /// One read of the connection state, published to both audiences.
    ///
    /// Split out from <see cref="OnStateChanged"/> and public because
    /// <see cref="AudioPlaybackConnection"/> cannot be constructed without a phone, and unpackaged it
    /// cannot be constructed at all without killing the process. This signature is the only way the
    /// "both, not either" rule below can be exercised by a test, and an unasserted rule is one
    /// tidy-up away from becoming a one-line regression that nothing catches. Not on
    /// <see cref="IAudioSinkService"/>: nothing above the seam may announce a state the connection
    /// object never reported.
    /// </summary>
    public void PublishState(AudioPlaybackConnectionState state)
    {
        // Both, from one read of sender.State. Two reads could disagree, which would put a status
        // line in the log describing a state the event never carried - and the log is the only
        // instrument this half has.
        //
        // Report first, and kept: Stage 1 acts on StateChanged, but StatusPresenter writes every
        // status to the log before it touches the tray, so removing this call would delete the
        // record of the drop as well as the announcement of it. A Log call beside it would instead
        // double the entry.
        Report($"A2DP sink state: {state}");

        // Added, not substituted. This is the machine-readable half - a reconnect state machine
        // cannot parse a tooltip.
        StateChanged?.Invoke(this, Translate(state));
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
