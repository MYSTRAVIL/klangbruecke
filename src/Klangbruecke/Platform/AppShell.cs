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

    public void Restart()
    {
        try
        {
            // The AUMID, not RequestRestartAsync. That API terminates-and-relaunches a packaged app on
            // success, but only from the foreground, and this is a windowless tray app: measured
            // NotInForeground even when called from the tray menu (docs/FINDINGS.md §23). Reactivating by
            // AUMID has no such requirement and still comes up with package identity - proven on this
            // machine - so it is the mechanism for both the tray item and the automatic watchdog.
            string? aumid = PackageIdentity.IsPackaged ? PackageIdentity.CurrentAppUserModelId() : null;
            if (aumid is not null)
            {
                RelaunchPackaged(aumid);
            }
            else
            {
                // Unpackaged development build (or the AUMID lookup failed). Identity is already absent
                // here, so the music half is disabled regardless (docs/FINDINGS.md §8) and there is nothing
                // to preserve - a plain relaunch of the same binary is the honest equivalent, and never the
                // path the shipped app takes.
                string? exe = Environment.ProcessPath;
                if (exe is not null)
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
                }
            }

            // Ends the message loop, which unwinds RunTray, disposes every seam and lets the process exit -
            // releasing the single-instance mutex. The relauncher above waits for exactly that before it
            // reactivates, so the new instance never races the old one for the mutex.
            Application.Exit();
        }
        catch (Exception ex)
        {
            Log.Error("Restarting the app failed.", ex);
        }
    }

    /// <summary>
    /// Reactivates this packaged app by AUMID, but only after this process has gone.
    ///
    /// A detached helper waits on this process id and then activates <c>shell:AppsFolder\&lt;AUMID&gt;</c>,
    /// which starts the app with package identity intact. Waiting for the exit is what keeps the
    /// single-instance mutex (<see cref="Klangbruecke.Program"/>) from turning the relaunch into a no-op:
    /// launch the new instance while the old still holds the mutex and it sees <c>isNew == false</c> and
    /// quits. <c>Wait-Process</c> handles both orderings - if this process is already gone its id is
    /// unknown and the wait returns at once, and either way the activation happens with the mutex free.
    /// The 20 s cap is a backstop against a hung exit, not the expected path; teardown here is sub-second.
    ///
    /// Windows PowerShell is the helper because it is always present and its <c>Start-Process</c> resolves
    /// <c>shell:AppsFolder</c>, and it is detached (its own process) so it outlives this one.
    /// </summary>
    private static void RelaunchPackaged(string aumid)
    {
        int pid = Environment.ProcessId;

        string command =
            $"Wait-Process -Id {pid} -Timeout 20 -ErrorAction SilentlyContinue; "
            + $"Start-Process 'shell:AppsFolder\\{aumid}'";

        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{command}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

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
