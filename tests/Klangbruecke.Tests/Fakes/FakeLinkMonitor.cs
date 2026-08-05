using Klangbruecke.Bluetooth;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// "Is the phone there?" answered by a test rather than by a radio.
///
/// Both halves of <see cref="ILinkMonitor"/> are here because the manager needs both and they
/// disagree on purpose: <see cref="RaiseAppeared"/> / <see cref="RaiseRemoved"/> are the watcher's
/// edges, and <see cref="Status"/> is the level the 30 s reconcile polls. A double that tied the two
/// together could not stage the case the whole design exists for - an edge that never arrives - and
/// it could not stage the grace window's question either, which is precisely "the audio connection
/// closed; is the ACL link still up?"
///
/// <see cref="ReadLinkStatusAsync"/> answers from an already-completed task, which is what keeps the
/// manager's reconcile synchronous under <see cref="ImmediateUiDispatcher"/>: a test can advance the
/// scheduler and assert immediately afterwards. It never throws, as the interface promises - every
/// failure is <see cref="BluetoothLinkStatus.Unknown"/>.
///
/// Public, in Fakes, and not <c>file</c>-scoped, following every other double in here.
/// </summary>
public sealed class FakeLinkMonitor : ILinkMonitor
{
    /// <summary>Every device id <see cref="Watch"/> was asked for, oldest first.</summary>
    public List<string> WatchCalls { get; } = new();

    public int StopWatchingCount { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>How many times the level was polled. The reconcile's own heartbeat.</summary>
    public int ReadCount { get; private set; }

    /// <summary>
    /// What the next level read reports. Defaults to <see cref="BluetoothLinkStatus.Disconnected"/>:
    /// a fresh watch has seen nothing, and starting from Connected would let a test pass because the
    /// double was optimistic rather than because the manager looked.
    /// </summary>
    public BluetoothLinkStatus Status { get; set; } = BluetoothLinkStatus.Disconnected;

    public bool DevicePresent { get; private set; }

    public event EventHandler? DeviceAppeared;

    public event EventHandler? DeviceRemoved;

    /// <summary>
    /// Presence always starts false, as the real one guarantees: selection is an intent, not an
    /// observation.
    /// </summary>
    public void Watch(string phoneDeviceId)
    {
        WatchCalls.Add(phoneDeviceId);
        DevicePresent = false;
    }

    public void StopWatching()
    {
        StopWatchingCount++;
        DevicePresent = false;
    }

    public Task<BluetoothLinkStatus> ReadLinkStatusAsync()
    {
        ReadCount++;
        return Task.FromResult(Status);
    }

    /// <summary>The watcher saw the phone.</summary>
    public void RaiseAppeared()
    {
        DevicePresent = true;
        DeviceAppeared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The watcher lost the phone.</summary>
    public void RaiseRemoved()
    {
        DevicePresent = false;
        DeviceRemoved?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Disposed = true;
}
