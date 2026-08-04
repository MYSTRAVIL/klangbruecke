using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Klangbruecke.Diagnostics;

namespace Klangbruecke;

internal static class Program
{
    // Static so the mutex stays rooted for the life of the process. As a local it would fall out of
    // reach while Application.Run was still going, and the finalizer releasing the handle would
    // quietly let a second instance in.
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        // Before the single-instance check, so a launch that does nothing still records why. The
        // Local\ namespace is not virtualized for MSIX, so a packaged launch and a dev build do
        // collide here in practice, and from the outside that is indistinguishable from a crash.
        Log.Current = new FileLog(FileLog.DefaultDirectory);

        // A second instance would fight the first for the Bluetooth connection.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Klangbruecke.SingleInstance", out bool isNew);
        if (!isNew)
        {
            Log.Warn("Another instance already holds the single-instance mutex. Exiting.");
            return;
        }

        Log.Info($"Klangbruecke {Version()} (built {BuildStamp()}) starting.");

        // The only value in-process that separates the installed package from a development build:
        // C:\Program Files\WindowsApps\Klangbruecke_... versus src\Klangbruecke\bin\.... It has to be
        // a line rather than an inference, because both builds now append to the *same* file - the
        // manifest disables write virtualization precisely so the packaged build stops getting a
        // private copy nobody can find - so a single day's log interleaves runs of both. Reading a
        // stale unpackaged run as if it were the packaged one has already produced a wrong
        // conclusion on this project once.
        Log.Info($"Base directory: {AppContext.BaseDirectory}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception.", e.ExceptionObject as Exception);

        // WinForms routes exceptions thrown out of message-loop callbacks here rather than to the
        // AppDomain hook above, and its own answer is a modal dialog - a window, in an app whose
        // whole point is not to have one. Every TrayContext menu handler dispatches through here.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // '+=' reads like a subscription but this event's add accessor *assigns*: WinForms keeps one
        // handler per thread, so a second subscription anywhere silently replaces this one and UI
        // thread exceptions stop being logged. Stage 1 will edit this file; there is nothing here to
        // stop it adding a second handler and no test that would go red. Add to this lambda instead.
        Application.ThreadException += (_, e) => Log.Error("Unhandled exception on the UI thread.", e.Exception);

        // The startup auto-connect is a discarded Task, so nothing rethrows its exceptions and they
        // reach neither hook above: the reconnect path fails in total silence, which is the one
        // failure this log exists to make visible. A net, not the fix - it fires only when the Task
        // is finalized, so the entry can lag the failure by a collection.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception.", e.Exception);

            // Answered for, so the runtime's escalation policy cannot also act on it.
            e.SetObserved();
        };

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayContext());
        }
        finally
        {
            Log.Info("Klangbruecke exiting.");
        }
    }

    /// <summary>
    /// Kept in step with the Identity Version in packaging/AppxManifest.xml by hand - the csproj
    /// carries the same number and nothing enforces the match. Left unset it defaults to 1.0.0.0,
    /// which is a version the package has never had.
    /// </summary>
    private static string Version() => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// When this binary was written, from its own file timestamp. The version alone cannot date a
    /// run: Task 9 is an install-fix-reinstall loop and nobody bumps a version twelve times, so
    /// without this every iteration's log claims to be the same build.
    ///
    /// Not the PE header's linker timestamp - the SDK builds deterministically by default, which
    /// replaces that field with a content hash that renders as a date decades away and is not a time
    /// at all.
    ///
    /// Never throws. This runs before Main's try, so an escaping exception reaches no handler and
    /// Windows answers it with a WER dialog: a visible window, in an app whose premise is not to
    /// have one, before the tray icon even exists.
    /// </summary>
    private static string BuildStamp()
    {
        try
        {
            // Empty for a single-file publish, where there is no managed file to stat.
            string path = Assembly.GetExecutingAssembly().Location;

            return path.Length == 0
                ? "unknown"
                : File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            // Trace, not Debug: Debug.WriteLine compiles out of the Release build that ships. Same
            // reasoning as FileLog's never-throw boundary.
            Trace.WriteLine(ex);
            return "unknown";
        }
    }
}
