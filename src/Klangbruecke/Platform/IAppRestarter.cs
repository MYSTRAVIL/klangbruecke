namespace Klangbruecke.Platform;

/// <summary>
/// Just "relaunch this app". The narrow seam the connection layer restarts through, so
/// <see cref="Klangbruecke.Connection.SinkWedgeWatchdog"/> depends on this one verb rather than the
/// whole tray shell, and a test can restart with a one-method fake.
///
/// <see cref="IAppShell"/> extends it - the tray already has an <see cref="IAppShell"/> and gets the
/// verb for free - and <see cref="AppShell"/> is the only implementation. See <see cref="AppShell.Restart"/>
/// for why the packaged relaunch goes through the AUMID rather than <c>RequestRestartAsync</c>.
/// </summary>
public interface IAppRestarter
{
    /// <summary>Start a fresh instance and end this one. Preserves package identity; see the implementation.</summary>
    void Restart();
}
