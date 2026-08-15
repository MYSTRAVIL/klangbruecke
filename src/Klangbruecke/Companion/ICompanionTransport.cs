namespace Klangbruecke.Companion;

/// <summary>
/// The phone link as <see cref="CompanionLink"/> sees it: connect to the companion service, push a
/// frame at it, and be told when a whole frame arrives or the link drops. Everything WinRT - the
/// uncached SDP discovery, the <c>StreamSocket</c>, the read loop that reassembles frames - lives
/// below this seam in <c>RfcommCompanionTransport</c>, so the orchestrator can be driven by a fake
/// with no radio and no phone.
///
/// The asymmetry in the byte payloads is deliberate. <see cref="SendAsync"/> takes a whole frame,
/// length prefix and all, because the caller has just encoded one; <see cref="FrameReceived"/>
/// hands back a single decoded frame's <c>type + payload</c> with the length already stripped,
/// because the transport is the only thing that had to reassemble it from a byte stream.
/// </summary>
internal interface ICompanionTransport : IDisposable
{
    /// <summary>Discover (uncached) and connect. False on any failure - the caller backs off, never throws.</summary>
    Task<bool> TryConnectAsync(CancellationToken ct);

    /// <summary>Writes one whole frame (length prefix included) to the phone.</summary>
    Task SendAsync(byte[] frame, CancellationToken ct);

    /// <summary>One decoded frame's raw bytes: <c>type + payload</c>, no length prefix.</summary>
    event EventHandler<byte[]>? FrameReceived;

    /// <summary>The link dropped - the phone left, the socket closed, the read loop ended.</summary>
    event EventHandler? Disconnected;

    bool IsConnected { get; }
}
