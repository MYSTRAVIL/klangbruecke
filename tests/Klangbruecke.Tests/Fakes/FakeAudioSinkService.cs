using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// The A2DP sink connection with the WinRT taken out, so the reconnect machinery can be driven
/// without a phone - and, more to the point, without the call that kills an unpackaged process
/// outright (docs/FINDINGS.md §8).
///
/// The one capability worth explaining is <see cref="DeferConnect"/>. Everything else here could be
/// a synchronous stub, but <c>ConnectAsync</c> is genuinely slow - it awaits
/// <c>AudioPlaybackConnection.OpenAsync</c> against a radio - and the window between asking and
/// being answered is the only one in which the half can be torn down mid-connect. A double that
/// always answered before it returned would make that window unrepresentable, which is precisely how
/// a stale success gets to resurrect a half the user just disconnected.
///
/// Public, in Fakes, and not <c>file</c>-scoped: <c>ConnectionManager</c>'s tests consume it too.
/// </summary>
public sealed class FakeAudioSinkService : IAudioSinkService
{
    private readonly Queue<Pending> _pending = new();

    /// <summary>Every device id <see cref="ConnectAsync"/> was asked for, oldest first.</summary>
    public List<string> ConnectCalls { get; } = new();

    public int DisconnectCount { get; private set; }

    public bool Disposed { get; private set; }

    public string? ConnectedDeviceId { get; private set; }

    /// <summary>
    /// Only ever the connection object being open, never a claim about the capture endpoint. Nothing
    /// in <c>MusicHalf</c> reads it: the endpoint is <c>IAudioEndpointMonitor</c>'s job and the two
    /// are separated by an unbounded interval.
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>What <see cref="FindDevicesAsync"/> reports.</summary>
    public IReadOnlyList<PhoneDevice> Devices { get; set; } = Array.Empty<PhoneDevice>();

    /// <summary>What a connect that answers immediately reports.</summary>
    public bool ConnectResult { get; set; } = true;

    /// <summary>
    /// When set, <see cref="ConnectAsync"/> throws it instead of answering, synchronously and before
    /// any await - which is where <c>TryCreateFromId</c> would fail. Beats <see cref="DeferConnect"/>.
    /// </summary>
    public Exception? ConnectThrows { get; set; }

    /// <summary>Holds every connect open until <see cref="CompleteConnect"/> answers it.</summary>
    public bool DeferConnect { get; set; }

    /// <summary>Connects asked for and not yet answered.</summary>
    public int PendingConnects => _pending.Count;

    /// <summary>
    /// Text for the tray and the log. Discarding accessors: this one carries no decision, and a
    /// subscriber list nothing invokes is a fake promising a channel it does not have.
    /// </summary>
    public event EventHandler<StatusMessage>? Status { add { } remove { } }

    /// <summary>
    /// Real, and raised by <see cref="PublishState"/>. <c>MusicHalf</c> subscribes to nothing itself,
    /// but this is the event <c>ConnectionManager</c> turns into <c>OnConnectionClosed</c> - the
    /// input behind the grace window - so a double that could not raise it would leave the one
    /// transition the manager owns undrivable.
    /// </summary>
    public event EventHandler<AudioSinkConnectionState>? StateChanged;

    public Task<IReadOnlyList<PhoneDevice>> FindDevicesAsync() => Task.FromResult(Devices);

    public Task<bool> ConnectAsync(string deviceId)
    {
        ConnectCalls.Add(deviceId);

        if (ConnectThrows is { } thrown)
        {
            throw thrown;
        }

        if (DeferConnect)
        {
            Pending pending = new(deviceId, new TaskCompletionSource<bool>());
            _pending.Enqueue(pending);
            return pending.Source.Task;
        }

        return Task.FromResult(Settle(deviceId, ConnectResult));
    }

    /// <summary>Answers the oldest connect still waiting. Throws if none is.</summary>
    public void CompleteConnect(bool connected)
    {
        Pending pending = _pending.Dequeue();
        pending.Source.SetResult(Settle(pending.DeviceId, connected));
    }

    /// <summary>
    /// The connection object reporting a state, on its own. Does not touch
    /// <see cref="IsConnected"/>: the real service publishes what WinRT told it, and a double that
    /// tidied the two into agreement would hide the interval in which they disagree.
    /// </summary>
    public void PublishState(AudioSinkConnectionState state)
        => StateChanged?.Invoke(this, state);

    public void Disconnect()
    {
        DisconnectCount++;
        IsConnected = false;
        ConnectedDeviceId = null;
    }

    public void Dispose() => Disposed = true;

    private bool Settle(string deviceId, bool connected)
    {
        IsConnected = connected;
        ConnectedDeviceId = connected ? deviceId : null;
        return connected;
    }

    private readonly record struct Pending(string DeviceId, TaskCompletionSource<bool> Source);
}
