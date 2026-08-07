namespace Klangbruecke.Connection;

/// <summary>
/// The manager-private operations the two timing seams - <see cref="GraceWindow"/> and
/// <see cref="Reconciler"/> - reach back for. Everything else they need is a shared machine
/// (<see cref="LinkMachine"/>, <see cref="SuppressionLatch"/>, <see cref="MusicHalf"/>,
/// <see cref="CallsHalf"/>) or a shared service, held directly.
///
/// Narrow on purpose: it is the whole answer to "what is a seam allowed to ask the hub for", and a
/// member added here is a coupling added on purpose rather than by reaching through a concrete
/// manager. <see cref="ConnectionManager"/> is the only implementor.
/// </summary>
internal interface IConnectionCoordinator
{
    /// <summary>The hub is being torn down; a seam must stop acting the moment it sees this.</summary>
    bool IsDisposed { get; }

    /// <summary>May a connect be initiated right now? Reads the latch, the setting and the click grant.</summary>
    bool ConnectPermitted { get; }

    /// <summary>Kick an off-thread refresh of the cached capture-endpoint level.</summary>
    void RefreshEndpointLevel();

    /// <summary>Stand down a half counting down to an attempt it is no longer allowed to make.</summary>
    void EnforceConnectPermission();

    /// <summary>Recompute the reported state and announce it if it moved.</summary>
    void Publish();

    /// <summary>Latch a deliberate suppression and tear both halves down. Used by the grace window's Connected branch.</summary>
    void SuppressDeliberately(string status);

    /// <summary>Raise the manager's own status announcement (always Info).</summary>
    void Report(string message);
}
