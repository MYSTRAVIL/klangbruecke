namespace Klangbruecke.Bluetooth;

/// <summary>
/// What a read of the phone's <c>BluetoothDevice.ConnectionStatus</c> said.
///
/// <see cref="Unknown"/> is every way the read can fail to produce an answer - an address that will
/// not parse, a null device back from WinRT, a throw from the ABI - collapsed into one value.
/// Callers must treat it as <see cref="Disconnected"/>: see <c>LinkMachine.OnLinkStatusRead</c>.
/// </summary>
public enum BluetoothLinkStatus
{
    Unknown,
    Disconnected,
    Connected,
}

/// <summary>
/// Is the selected phone there? Asked two ways, deliberately.
///
/// <see cref="DeviceAppeared"/> / <see cref="DeviceRemoved"/> are edges from a <c>DeviceWatcher</c>;
/// <see cref="ReadLinkStatusAsync"/> is a level, read on demand by the 30 s reconcile. Both exist
/// because WinRT device events are unreliable across sleep/resume: under a purely edge-triggered
/// design one dropped event leaves the app idle believing it is connected, with nothing to correct
/// it - the predecessor app's defining bug, and the reason this project exists.
///
/// The two are not interchangeable either. <see cref="DevicePresent"/> answers for the A2DP device
/// interface; the status read answers for the ACL link, which survives the audio profile going away.
/// Telling a deliberate phone-side disconnect from a walk out of range needs the second one - see the
/// grace window in the Stage 1 design.
///
/// Implementations raise the events on whatever thread WinRT hands them and do <b>not</b> marshal:
/// <c>ConnectionManager</c> posts through <c>IUiDispatcher</c>. Do not add a second marshalling layer.
/// </summary>
public interface ILinkMonitor : IDisposable
{
    /// <summary>The watcher has seen the watched device and has not seen it leave.</summary>
    bool DevicePresent { get; }

    event EventHandler? DeviceAppeared;
    event EventHandler? DeviceRemoved;

    /// <summary>
    /// Watch one device id. Replaces any previous watch, and always starts from
    /// <see cref="DevicePresent"/> false: selection is an intent, not an observation.
    /// </summary>
    void Watch(string phoneDeviceId);

    void StopWatching();

    /// <summary>
    /// The level-triggered backstop. Never throws: every failure - an id with no address, a null
    /// device from WinRT, a throw out of the ABI - collapses to <see cref="BluetoothLinkStatus.Unknown"/>,
    /// which <c>LinkMachine</c> treats as disconnected.
    /// </summary>
    Task<BluetoothLinkStatus> ReadLinkStatusAsync();
}
