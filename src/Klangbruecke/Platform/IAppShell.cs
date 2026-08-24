namespace Klangbruecke.Platform;

/// <summary>
/// The shell verbs the tray needs. A seam so <see cref="TrayContext"/> stays a view - one call per
/// click - and so all the raw OS calls live in one guarded place (<see cref="AppShell"/>).
///
/// Extends <see cref="IAppRestarter"/> for the relaunch verb: the tray's Restart item wants it here
/// beside the other shell calls, while the watchdog depends on the narrower interface. See
/// <see cref="AppShell.Restart"/>.
/// </summary>
public interface IAppShell : IAppRestarter
{
    void OpenFolder(string path);
    void OpenUrl(string url);
    void CopyToClipboard(string text);
    void ShowInfo(string title, string message);
    bool Confirm(string title, string message);
}
