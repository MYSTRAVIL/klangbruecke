using Klangbruecke.Diagnostics;

namespace Klangbruecke.Audio;

/// <summary>
/// The routing half of the app, as its callers see it: start a route to a chosen output, stop it,
/// and be told when it died on its own.
///
/// Separate from <see cref="AudioRouter"/> so the connection machinery can be driven without an
/// audio endpoint. See <see cref="IAudioDeviceFactory"/> for the seam underneath it.
/// </summary>
public interface IAudioRouter : IDisposable
{
    bool IsRunning { get; }
    event EventHandler<StatusMessage>? Status;

    /// <summary>Raised after the route has been torn down, on the dispatcher thread.</summary>
    /// <remarks>
    /// Only for a route that stopped by itself - a phone out of range, an endpoint that vanished.
    /// A deliberate <see cref="Stop"/> does not raise it: the caller that asked for the teardown
    /// already knows, and echoing it back is how a reconnect loop restarts what a user just
    /// switched off.
    ///
    /// Raised after the teardown rather than during it, so a subscriber sees
    /// <see cref="IsRunning"/> false and cannot re-enter it - and on the dispatcher thread, never
    /// on the NAudio thread that noticed the failure. Both are load-bearing; see
    /// <see cref="AudioRouter"/>'s teardown for the deadlock behind the second one.
    /// </remarks>
    event EventHandler? Stopped;

    bool Start(string? preferredOutputDeviceId);
    void Stop();
}
