using System;
using System.Diagnostics;
using System.Windows.Forms;
using Klangbruecke.Diagnostics;

namespace Klangbruecke.Platform;

/// <summary>
/// Thin, guarded wrapper over the shell. Untested by design, like WasapiDeviceFactory: it is only OS
/// calls, and each is guarded so a shell failure cannot crash the tray. Every method here runs on the
/// UI (STA) thread, dispatched from a menu click - which is what Clipboard requires.
/// </summary>
public sealed class AppShell : IAppShell
{
    public void OpenFolder(string path) => Launch(path, $"open the folder {path}");

    public void OpenUrl(string url) => Launch(url, $"open {url}");

    public void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Log.Error("Copying to the clipboard failed.", ex);
        }
    }

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;

    private static void Launch(string target, string describe)
    {
        try
        {
            // UseShellExecute so a folder path opens Explorer and an http(s) url opens the browser.
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Shell action failed: {describe}.", ex);
        }
    }
}
