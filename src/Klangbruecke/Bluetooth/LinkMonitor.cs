using System.Globalization;
using Klangbruecke.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace Klangbruecke.Bluetooth;

/// <summary>
/// Answers "is the selected phone there?" from the radio, two ways.
///
/// A <see cref="DeviceWatcher"/> over the A2DP selector, filtered to one device id, fires the edges.
/// <see cref="ReadLinkStatusAsync()"/> reads <c>BluetoothDevice.ConnectionStatus</c> on demand and is
/// the level-triggered backstop: WinRT device events are unreliable across sleep/resume, and a
/// dropped edge under a purely edge-triggered design leaves the app idle believing it is connected,
/// with nothing to correct it. That is the predecessor app's defining bug.
///
/// **Nothing here marshals.** The watcher raises Added/Removed on a thread of WinRT's choosing -
/// measured as an RPC thread, not the managed threadpool and never the UI thread - and this class
/// re-raises them on that same thread. <c>ConnectionManager</c> posts every inbound event through
/// <c>IUiDispatcher</c> before touching state, which is what makes it single-threaded by contract.
/// Marshalling here as well would put a second hop in front of every edge and buy nothing - see the
/// threading table in the Stage 1 design.
///
/// The status read holds no <see cref="BluetoothDevice"/> open. Polling was chosen over subscribing
/// to <c>ConnectionStatusChanged</c> precisely so there is no long-lived WinRT object and no
/// subscription to unwind across sleep/resume, so the device is disposed on every path out of the
/// read.
/// </summary>
public sealed class LinkMonitor : ILinkMonitor
{
    private DeviceWatcher? _watcher;

    // volatile, not locked. Both are written by the thread that calls Watch/StopWatching - the UI
    // thread - and read on the watcher's own callback thread, which is neither of the two. The design
    // forbids locks anywhere in this stage; volatile is what makes a write on one visible to the
    // other without one, and a torn read is impossible for either type.
    //
    // What volatile does not buy is atomicity across the two, so StopWatching cannot fully exclude a
    // callback that is already past its id check - see the ordering there. A stale *edge* out of that
    // window is harmless: LinkMachine ignores an appeared/removed with no phone selected. A stale
    // *level* is not, because nothing de-duplicates DevicePresent, which is why the ordering exists.
    private volatile string? _watchedDeviceId;
    private volatile bool _devicePresent;

    private bool _disposed;

    public bool DevicePresent => _devicePresent;

    public event EventHandler? DeviceAppeared;
    public event EventHandler? DeviceRemoved;

    /// <summary>
    /// Watch one device id. Any previous watch is stopped first, and presence always starts false:
    /// selection is an intent, not an observation, and nothing has looked for the phone yet.
    /// </summary>
    public void Watch(string phoneDeviceId)
    {
        // Refused rather than tolerated, and the only method here that refuses anything. Dispose's
        // idempotence guard returns early on a second call, so a watcher started after the first
        // Dispose would never be stopped: it would run for the life of the process with live handlers
        // firing into a disposed object. Nothing downstream can detect that, and the caller doing it
        // has a real defect worth surfacing. StopWatching stays harmless by contrast - teardown paths
        // call it without knowing what has already gone.
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Replace, never stack. Two live watchers over the same selector would double every edge, and
        // the older one would keep reporting the phone the user just stopped caring about.
        StopWatching();

        SetWatchTarget(phoneDeviceId);

        // GetDeviceSelector, not TryCreateFromId. The statics interface is live unpackaged and only
        // that one method on it faults (FINDINGS §8), so the selector plus DeviceInformation
        // enumeration is the half of this API that is known to work without package identity.
        string selector = AudioPlaybackConnection.GetDeviceSelector();

        // The plain overload. Deliberately no extraProps: the obvious thing to ask for here is
        // System.Devices.Aep.IsPaired, and it reads false unless it is requested explicitly - a
        // false negative that has already cost this project once (HANDOFF.md). Presence is what this
        // class reports; pairing state is not needed for it and is not collected.
        DeviceWatcher watcher = DeviceInformation.CreateWatcher(selector);

        watcher.Added += OnAdded;
        watcher.Removed += OnRemoved;

        // Updated is deliberately not subscribed. It reports a property change on a device that is
        // already present and already reported, so it carries no edge - and the widely repeated claim
        // that a DeviceWatcher needs handlers for all three of Added/Updated/Removed before Start is
        // not true of this selector on this machine: measured both ways, the phone's Added arrives
        // either way. An empty handler kept "just in case" would be untestable code defending an
        // unverified claim.
        _watcher = watcher;
        watcher.Start();
    }

    /// <summary>
    /// Everything <see cref="Watch"/> does that is not WinRT: adopt the id, and say what that means.
    ///
    /// Public, and the seam the callbacks below are tested through - <see cref="Watch"/> itself
    /// starts a real <see cref="DeviceWatcher"/>, which is OS device enumeration inside a suite that
    /// runs in two seconds. Call <see cref="Watch"/>, not this: on its own it adopts an id that
    /// nothing is watching for, so no edge will ever arrive.
    ///
    /// <b>It refuses nothing, deliberately, and that is only safe because nothing in production calls
    /// it.</b> It is not on <c>ILinkMonitor</c> and <see cref="Watch"/> is its one caller here, so the
    /// two states it can reach and <see cref="Watch"/> cannot - retargeting a live watcher, which
    /// leaves that watcher running against the old selector while the id says otherwise, and running
    /// after <see cref="Dispose"/>, which <see cref="Watch"/> throws for - are reachable from a test
    /// and from nowhere else. A guard here would be one no consumer can trip, so the sentence is the
    /// guard: if this ever grows a production caller, it needs <see cref="Watch"/>'s
    /// <see cref="ObjectDisposedException"/> check and a stop of the live watcher, not a comment.
    /// </summary>
    public void SetWatchTarget(string phoneDeviceId)
    {
        _watchedDeviceId = phoneDeviceId;

        // Always false, even though StopWatching has just cleared it. Watch's documented guarantee is
        // that presence starts from nothing - picking a different phone while the old one was present
        // must not claim the new one is in the room - and stating it here is what lets it be asserted
        // without a watcher.
        _devicePresent = false;

        Log.Info($"Watching the Bluetooth link for id={phoneDeviceId}");

        if (!TryParseAddress(phoneDeviceId, out _))
        {
            // Warned once, at the moment it becomes true, because every consequence downstream is
            // silent: ReadLinkStatusAsync will answer Unknown for this id forever, the link will read
            // Absent forever, and the app will be indistinguishable from one whose phone is out of
            // range. The id is named because the id is the thing that has to be corrected.
            Log.Warn($"No Bluetooth address can be read from the watched id '{phoneDeviceId}'. "
                     + "Every link status read for it will answer Unknown, which reads as an absent "
                     + "phone - check what was persisted in Settings.");
        }
    }

    public void StopWatching()
    {
        // First, so that a callback which has not yet reached its id check fails it.
        _watchedDeviceId = null;

        if (_watcher is not null)
        {
            DeviceWatcher watcher = _watcher;
            _watcher = null;

            // Unsubscribe before stopping. Stop() is asynchronous - the watcher passes through
            // Stopping on its way to Stopped - so a handler left attached can still be invoked after
            // this method returns.
            watcher.Added -= OnAdded;
            watcher.Removed -= OnRemoved;

            // Guarded and absorbed. Stop() is only legal from Started or EnumerationCompleted and
            // answers anything else with E_ILLEGAL_METHOD_CALL; a watcher that aborted on its own
            // reaches here in exactly that state. Teardown.Quietly rather than a silent catch because
            // this runs on the path to TrayContext.Dispose, where a throw escapes Main and Windows
            // answers with a WER dialog - a window, in an app whose premise is not to have one.
            Teardown.Quietly(
                () =>
                {
                    if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                    {
                        watcher.Stop();
                    }
                },
                "stop the Bluetooth link watcher");
        }

        // Last, after the unsubscribe, and on every path through this method. Clearing it first left
        // a callback that was already past its id check free to set it back to true after this
        // returned, latching DevicePresent for a device nobody is watching - and unlike a stale edge,
        // which LinkMachine drops, nothing anywhere corrects a stale level.
        //
        // This narrows that window to a callback already inside the assignment; it cannot close it
        // without a lock, and the design forbids one. What remains is corrected by the next
        // SetWatchTarget and by the reconcile's own level read.
        //
        // Cleared without raising DeviceRemoved: this is the caller asking to stop looking, not the
        // radio reporting a departure, and ConnectionManager would act on the edge as though it were.
        _devicePresent = false;
    }

    /// <summary>
    /// The status of the device currently being watched, or <see cref="BluetoothLinkStatus.Unknown"/>
    /// if none is.
    /// </summary>
    public Task<BluetoothLinkStatus> ReadLinkStatusAsync() => ReadLinkStatusAsync(_watchedDeviceId);

    /// <summary>
    /// One read of one device id, start to finish. Never throws.
    ///
    /// Static and public because that is what makes its failure branches reachable from a test: an
    /// instance would have to be driven through <see cref="Watch"/>, which starts a real
    /// <see cref="DeviceWatcher"/>. Same reason <c>AudioSinkService.PublishState</c> is public.
    /// </summary>
    public static async Task<BluetoothLinkStatus> ReadLinkStatusAsync(string? deviceId)
    {
        // Declared out here only so the catch can name the address it failed on.
        ulong address = 0;

        BluetoothDevice? device = null;
        try
        {
            // Inside the try, not before it. TryExtractAddress cannot throw today - it guards null and
            // whitespace and then runs three bounded GeneratedRegex patterns - but "never throws" is
            // this method's contract and TryExtractAddress belongs to another class, which is free to
            // change. Structural is worth more than currently-true.
            if (!TryParseAddress(deviceId, out address))
            {
                // Not logged. With no phone selected this is the ordinary answer, and the reconcile
                // asks every 30 s - an entry here would be 2,880 lines a day saying nothing happened.
                // A watched id that cannot yield an address is warned about once, in SetWatchTarget.
                return BluetoothLinkStatus.Unknown;
            }

            device = await BluetoothDevice.FromBluetoothAddressAsync(address);

            // Unknown rather than Disconnected on null: "no answer" and "answered, not connected" are
            // different facts. Unlogged, for the reason above - a phone left selected while the
            // radio cannot answer for it is a steady state, not an event.
            //
            // Measured, and the obvious reading of null is wrong: an address this machine has never
            // paired with came back as a live object reporting Disconnected, not as null. So a stale
            // or mistyped device id reads as "phone out of range" forever rather than surfacing as a
            // failed read, and this branch is defensive - no input has yet been found that reaches
            // it. Do not delete it on that basis: an ABI that answers null is far likelier than one
            // that answers wrongly, and the cost of being ready for it is one comparison.
            return device is null ? BluetoothLinkStatus.Unknown : Translate(device.ConnectionStatus);
        }
        catch (Exception ex)
        {
            // Warn, unconditionally, accepting that a persistently failing read writes one line every
            // 30 s. Nothing is known to reach here: the call was run unpackaged against this
            // machine's radio during Task 11 and answered cleanly for a connected phone, a
            // disconnected one and an address never paired - which closes risk #2 in the Stage 1
            // design. So a throw means something genuinely unaccounted for, most likely across
            // sleep/resume or with the adapter pulled, and a repeating warn is the right volume for a
            // condition that would otherwise present only as the app quietly deciding the phone is
            // absent.
            Log.Warn($"Reading the Bluetooth link status for {address:X12} failed: {ex.Message}");
            return BluetoothLinkStatus.Unknown;
        }
        finally
        {
            // Every path, including the throw. Holding one open is the design this project
            // deliberately did not choose.
            device?.Dispose();
        }
    }

    /// <summary>
    /// The 12-character uppercase hex address <see cref="BluetoothDeviceId"/> extracts, as the
    /// <c>ulong</c> <c>FromBluetoothAddressAsync</c> wants. False when the id carries no address.
    /// </summary>
    public static bool TryParseAddress(string? deviceId, out ulong address)
    {
        address = 0;

        string? hex = BluetoothDeviceId.TryExtractAddress(deviceId);
        if (hex is null)
        {
            return false;
        }

        // HexNumber and InvariantCulture, and TryParse rather than Parse. The input is 12 hex
        // characters, so a decimal parse does not read the wrong address - it throws - and a throw
        // from here would be swallowed as Unknown on every tick, leaving the app permanently unable
        // to see a phone that is in the room.
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    /// <summary>
    /// Is this the device being watched? Case-insensitive, and false when nothing is watched.
    /// </summary>
    public static bool IsWatchedDevice(string? watchedDeviceId, string? candidateDeviceId)
    {
        if (string.IsNullOrEmpty(watchedDeviceId) || string.IsNullOrEmpty(candidateDeviceId))
        {
            // Nothing watched matches nothing. A DeviceWatcher can deliver a queued event after it
            // has been stopped, and a stale edge must not resurrect a phone the user just cleared -
            // LinkMachine guards the same case from the other side.
            return false;
        }

        // OrdinalIgnoreCase: Windows device interface ids are case-insensitive, and the id the
        // watcher hands back is not guaranteed to be byte-identical to the one that came out of
        // FindAllAsync and was persisted in Settings. An ordinal comparison would drop every edge for
        // a phone that is present, which is indistinguishable from a phone that is not.
        return string.Equals(watchedDeviceId, candidateDeviceId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The WinRT enum, translated. Pure and public so the mapping can be asserted: reading these
    /// constants touches no ABI, which is what makes this the one part of the read a test host can
    /// execute. Never <see cref="BluetoothLinkStatus.Unknown"/> - a status in hand is an answer.
    /// </summary>
    public static BluetoothLinkStatus Translate(BluetoothConnectionStatus status) =>
        status == BluetoothConnectionStatus.Connected
            ? BluetoothLinkStatus.Connected
            : BluetoothLinkStatus.Disconnected;

    // --- watcher callbacks: WinRT's thread, deliberately not marshalled -----------------------
    //
    // The WinRT handlers are adapters and nothing else. DeviceInformation and DeviceInformationUpdate
    // cannot be constructed outside WinRT, so a filter or a presence transition written inside them
    // is a filter no test can reach - and the whole edge-triggered half of this class would then rest
    // on one hardware probe that does not run again. Both handlers read a single string, so the
    // string is the seam.

    private void OnAdded(DeviceWatcher sender, DeviceInformation info) => OnCandidateAdded(info.Id);

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update) => OnCandidateRemoved(update.Id);

    /// <summary>The watcher offered a device. Public for the reason above; call it only as a test.</summary>
    public void OnCandidateAdded(string candidateDeviceId)
    {
        if (!IsWatchedDevice(_watchedDeviceId, candidateDeviceId))
        {
            return;
        }

        _devicePresent = true;

        // Logged here rather than left to the subscriber: this is the radio's own account of the
        // phone arriving, and phone-initiated reconnect is one of the two paths CLAUDE.md names as
        // historically fragile. A log that only records what the state machine concluded cannot show
        // whether the edge arrived at all.
        Log.Info($"Bluetooth link watcher: device appeared, id={candidateDeviceId}");

        // Raised on the calling thread. ConnectionManager posts through IUiDispatcher; do not add a
        // second marshalling layer here.
        DeviceAppeared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The watcher withdrew a device. Public for the reason above; call it only as a test.</summary>
    public void OnCandidateRemoved(string candidateDeviceId)
    {
        if (!IsWatchedDevice(_watchedDeviceId, candidateDeviceId))
        {
            return;
        }

        _devicePresent = false;

        Log.Info($"Bluetooth link watcher: device removed, id={candidateDeviceId}");

        // Raised on the calling thread, for the reason above.
        DeviceRemoved?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopWatching();
    }
}
