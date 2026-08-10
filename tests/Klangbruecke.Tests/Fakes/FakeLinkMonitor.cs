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
    private readonly Queue<TaskCompletionSource<BluetoothLinkStatus>> _pending = new();

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

    /// <summary>
    /// Per-device staged presence so resolver tests can say "A present, B absent." Unmapped ids fall
    /// back to <see cref="Status"/>.
    /// </summary>
    public Dictionary<string, BluetoothLinkStatus> StatusById { get; } = new();

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

    /// <summary>
    /// Holds every read open until <see cref="CompleteRead"/> answers it.
    ///
    /// The real read awaits WinRT against a radio, and the window between asking and being answered
    /// is the only one in which a second reconcile - a phone picked, a resume, the 30 s tick - can
    /// land on top of the first. A double that always answered before it returned would make that
    /// window unrepresentable.
    /// </summary>
    public bool DeferRead { get; set; }

    public Task<BluetoothLinkStatus> ReadLinkStatusAsync()
    {
        ReadCount++;

        if (!DeferRead)
        {
            return Task.FromResult(Status);
        }

        TaskCompletionSource<BluetoothLinkStatus> source = new();
        _pending.Enqueue(source);
        return source.Task;
    }

    /// <summary>Answers the oldest read still waiting. Throws if none is.</summary>
    public void CompleteRead(BluetoothLinkStatus status) => _pending.Dequeue().SetResult(status);

    /// <summary>
    /// Read the status of a named device, independent of the watched one. Returns from
    /// <see cref="StatusById"/> if mapped, otherwise falls back to <see cref="Status"/>.
    /// </summary>
    public Task<BluetoothLinkStatus> ReadLinkStatusForAsync(string deviceId) =>
        Task.FromResult(StatusById.GetValueOrDefault(deviceId, Status));

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
