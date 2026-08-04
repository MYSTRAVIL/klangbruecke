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

        Log.Info($"Klangbruecke {Assembly.GetExecutingAssembly().GetName().Version} starting.");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception.", e.ExceptionObject as Exception);

        // WinForms routes exceptions thrown out of message-loop callbacks here rather than to the
        // AppDomain hook above, and its own answer is a modal dialog - a window, in an app whose
        // whole point is not to have one. Every TrayContext menu handler dispatches through here.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
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
}
