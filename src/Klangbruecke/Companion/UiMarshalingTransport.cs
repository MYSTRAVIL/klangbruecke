namespace Klangbruecke.Companion;

/// <summary>
/// Wraps a <see cref="ICompanionTransport"/> and re-raises its two inbound events -
/// <see cref="ICompanionTransport.FrameReceived"/> and <see cref="ICompanionTransport.Disconnected"/> -
/// on the UI thread through <see cref="IUiDispatcher"/>.
///
/// <b>This is the one piece of threading glue the companion needs, and why it exists is precise.</b>
/// <see cref="RfcommCompanionTransport"/> raises both events from its background read loop (a threadpool
/// thread), but <see cref="CompanionLink"/>'s handlers for them mutate its running snapshot and its
/// reconnect/backoff state - the same state <c>ConnectAsync</c> touches on the UI thread. Left
/// unmarshalled, a frame arriving mid-connect would race that state with no lock, which is exactly the
/// class of bug the rest of <c>Connection/</c> avoids by marshalling every callback onto the one thread.
/// Verified necessary empirically (docs/FINDINGS.md §21).
///
/// The outbound direction is deliberately <em>not</em> wrapped. <see cref="SendAsync"/> is already
/// write-gated in the transport, and <c>CompanionLink.OnCommandRequested</c> touches no shared state -
/// it only encodes and sends - so a media-key press arriving on a threadpool thread is safe as-is.
/// </summary>
internal sealed class UiMarshalingTransport : ICompanionTransport
{
    private readonly ICompanionTransport _inner;
    private readonly IUiDispatcher _ui;

    public UiMarshalingTransport(ICompanionTransport inner, IUiDispatcher ui)
    {
        _inner = inner;
        _ui = ui;

        _inner.FrameReceived += OnInnerFrameReceived;
        _inner.Disconnected += OnInnerDisconnected;
    }

    public event EventHandler<byte[]>? FrameReceived;
    public event EventHandler? Disconnected;

    public bool IsConnected => _inner.IsConnected;

    public Task<bool> TryConnectAsync(CancellationToken ct) => _inner.TryConnectAsync(ct);

    public Task SendAsync(byte[] frame, CancellationToken ct) => _inner.SendAsync(frame, ct);

    private void OnInnerFrameReceived(object? sender, byte[] frame)
        => _ui.Post(() => FrameReceived?.Invoke(this, frame));

    private void OnInnerDisconnected(object? sender, EventArgs e)
        => _ui.Post(() => Disconnected?.Invoke(this, EventArgs.Empty));

    public void Dispose()
    {
        _inner.FrameReceived -= OnInnerFrameReceived;
        _inner.Disconnected -= OnInnerDisconnected;
        _inner.Dispose();
    }
}
