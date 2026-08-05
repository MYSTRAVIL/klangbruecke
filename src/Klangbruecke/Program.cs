using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Klangbruecke.Audio;
using Klangbruecke.Bluetooth;
using Klangbruecke.Config;
using Klangbruecke.Connection;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

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
        // thread exceptions stop being logged. Stage 1 edited this file and did not add one; nothing
        // here would have stopped it, and no test would have gone red. Add to this lambda instead.
        Application.ThreadException += (_, e) => Log.Error("Unhandled exception on the UI thread.", e.Exception);

        // ConnectionManager runs every turn that awaits as a discarded Task - a reconcile pass, a
        // connect turn, a registration retry, a grace window's answer - so nothing rethrows their
        // exceptions and they reach neither hook above. The reconnect path would fail in total
        // silence, which is the one failure this log exists to make visible. A net, not the fix - it
        // fires only when the Task is finalized, so the entry can lag the failure by a collection.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception.", e.Exception);

            // Answered for, so the runtime's escalation policy cannot also act on it.
            e.SetObserved();
        };

        try
        {
            // Before any control exists: it sets the DPI mode, the default font and visual styles,
            // and every one of those is read when a handle is created. The first handle in this
            // process belongs to the dispatcher's marshalling control, on RunTray's first line.
            ApplicationConfiguration.Initialize();
            RunTray();
        }
        finally
        {
            Log.Info("Klangbruecke exiting.");
        }
    }

    /// <summary>
    /// Builds the seams, hands them to the one object that owns the connection lifecycle, and hands
    /// that to the tray.
    ///
    /// <b>The order of these lines is behaviour, not style.</b> Four of them in particular:
    ///
    /// <list type="number">
    /// <item>The dispatcher is first. Constructing it creates a <c>Control</c>, and a Control's
    /// constructor installs the WinForms <c>SynchronizationContext</c> on this thread - which is what
    /// makes <c>PowerNotifier</c>'s subscription later capture this thread rather than the
    /// <c>SystemEvents</c> window thread. "Before <c>Application.Run</c>" is not the same question as
    /// "before the context exists", and this is the line that separates them. Pinned by
    /// <c>UiDispatcherTests.Control_InstallsTheWinFormsSynchronizationContextOnTheThreadThatBuildsIt</c>.
    /// <para>
    /// It is also one of the two things that make every <c>await</c> in <c>ConnectionManager</c>
    /// resume on the UI thread, which is what lets four state machines share it with no lock. The
    /// other is that nothing on those paths calls <c>ConfigureAwait(false)</c> - a separate claim,
    /// with its own six tests under "the captured context" in <c>ConnectionManagerTests</c>, covering
    /// twelve of the fourteen await sites. Neither leg is evidence for the other, and neither is
    /// complete on its own; the test section names the two sites it cannot reach.
    /// </para></item>
    /// <item>The status subscriptions come after the presenter is constructed, never before. They
    /// used to be taken first and were safe only because nothing raised a status until the end of the
    /// same constructor - a fact about call ordering, not about the code, and one edit away from
    /// being false.</item>
    /// <item>The dispatcher and the scheduler are disposed <em>after</em> the tray, which disposes
    /// the manager, which disposes every seam. The manager's teardown still raises status - it posts
    /// through this dispatcher and writes this icon - and a late endpoint probe still posts through
    /// it from the threadpool. Disposing it first would drop the first and let the second run inline
    /// on a threadpool thread.</item>
    /// <item>The icon is made visible <em>after</em> the tray is constructed, because the tray's
    /// constructor is where <c>ConnectionManager.Start</c> makes its three live-device calls and a
    /// throw out of any of them never reaches the Dispose that would hide it.</item>
    /// </list>
    /// </summary>
    private static void RunTray()
    {
        using var ui = new ControlUiDispatcher();

        // Nothing else gives its timers back. ConnectionManager disposes the handles it was issued,
        // not the scheduler that issued them, and a WinForms timer left armed is a tick into a
        // torn-down state machine.
        using var scheduler = new UiScheduler();

        Settings settings = Settings.Load();

        // Read once, and written here rather than where the two gates fire.
        //
        // Both explanations used to be logged by TrayContext, on the connect path that has now moved
        // out of it, and that path ran once per attempt. Stage 1 retries: an unpackaged run reaches
        // the music gate again on every backoff step, so a gate that logged where it refused would
        // write the same permanent condition once a minute for the life of the process. The gate
        // itself stays exactly where it is - AudioSinkService.ConnectAsync - because what it prevents
        // is not a failed connect but an uncatchable process death (docs/FINDINGS.md section 8).
        bool isPackaged = PackageIdentity.IsPackaged;
        Log.Write(AudioSinkPolicy.LevelFor(isPackaged), AudioSinkPolicy.Explain(isPackaged));

        CallsAvailability callsAvailability = CallsPolicy.Decide(settings.EnableCalls, isPackaged);
        Log.Write(CallsPolicy.LevelFor(callsAvailability), CallsPolicy.Explain(callsAvailability));

        var devices = new WasapiDeviceFactory();
        var router = new AudioRouter(ui, devices);
        var sink = new AudioSinkService();
        var callTransport = new CallTransportService();
        var endpoints = new EndpointMonitor();
        var link = new LinkMonitor();
        var power = new PowerNotifier();

        // Owns all six seams from here on: it is the only thing that disposes them, and TrayContext
        // is the only thing that disposes it. Nothing has started yet - no watcher, no notification
        // registration, no timer - because Start() is called from the tray's constructor, on the UI
        // thread, with the context above already installed.
        var connection = new ConnectionManager(
            settings, sink, callTransport, router, endpoints, link, scheduler, power, ui);

        // Built hidden. It is shown below, after the tray exists to hide it again.
        //
        // TrayContext's constructor calls ConnectionManager.Start, which subscribes SystemEvents,
        // registers an MMDevAPI notification client and starts a DeviceWatcher - three live-device
        // calls, none of them inside a try. A throw out of any of them escapes before
        // Application.Run, so ApplicationContext.Dispose is never reached and nothing ever hides the
        // icon: a visible icon in the notification area belonging to a process that has already
        // gone, which the shell leaves drawn until it next reaps it. Shown after construction, that
        // failure leaves nothing on screen at all.
        var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Klangbruecke",
            Visible = false,
        };

        var status = new StatusPresenter(ui, text => icon.Text = text);

        // The severity comes with the message. Forwarding only the text would have the shell decide
        // how serious an event it did not witness was, which is how every component failure used to
        // reach the log at Info. See StatusMessage.
        //
        // Each seam speaks for itself and the manager speaks only for its own decisions - it
        // deliberately does not re-broadcast the others, which would put it between a component and
        // its own words about an event it did not see.
        sink.Status += (_, m) => status.Show(m);
        callTransport.Status += (_, m) => status.Show(m);
        router.Status += (_, m) => status.Show(m);
        connection.Status += (_, m) => status.Show(m);

        var tray = new TrayContext(icon, status, connection, settings);

        // Only now. Everything that could throw on the way up has run, and from here the icon has an
        // owner whose Dispose hides it. See the note where it was built.
        icon.Visible = true;

        Application.Run(tray);
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
