using Klangbruecke.Companion;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// The phone link with the radio taken out. Records what was sent, lets a test raise an inbound frame
/// or a disconnect on cue, and answers <c>TryConnectAsync</c> with whatever <see cref="NextConnectResult"/>
/// is set to - so <c>CompanionLink</c>'s connect / send / receive / reconnect behaviour can be driven
/// with no <c>StreamSocket</c> underneath it.
///
/// Public, in Fakes, and not <c>file</c>-scoped so later tests (and <c>ConnectionManager</c>'s) can
/// consume it too.
/// </summary>
public sealed class FakeCompanionTransport : ICompanionTransport
{
    /// <summary>Every whole frame handed to <see cref="SendAsync"/>, oldest first.</summary>
    public List<byte[]> Sent { get; } = new();

    /// <summary>What the next <see cref="TryConnectAsync"/> reports.</summary>
    public bool NextConnectResult { get; set; }

    /// <summary>How many times a connect was attempted - the reconnect tests read this.</summary>
    public int ConnectAttempts { get; private set; }

    public bool IsConnected { get; private set; }

    public bool Disposed { get; private set; }

    public event EventHandler<byte[]>? FrameReceived;

    public event EventHandler? Disconnected;

    public Task<bool> TryConnectAsync(CancellationToken ct)
    {
        ConnectAttempts++;
        IsConnected = NextConnectResult;
        return Task.FromResult(NextConnectResult);
    }

    public Task SendAsync(byte[] frame, CancellationToken ct)
    {
        Sent.Add(frame);
        return Task.CompletedTask;
    }

    /// <summary>Delivers one decoded frame (<c>type + payload</c>, no length) to the link.</summary>
    public void Raise(byte[] frame) => FrameReceived?.Invoke(this, frame);

    /// <summary>The link dropping. Clears <see cref="IsConnected"/> as the real one would.</summary>
    public void RaiseDisconnected()
    {
        IsConnected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Disposed = true;
}
