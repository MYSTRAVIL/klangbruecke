using System.Reflection;
using Klangbruecke.Diagnostics;

namespace Klangbruecke;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        // A second instance would fight the first for the Bluetooth connection.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Klangbruecke.SingleInstance", out bool isNew);
        if (!isNew)
        {
            return;
        }

        Log.Current = new FileLog(FileLog.DefaultDirectory);
        Log.Info($"Klangbruecke {Assembly.GetExecutingAssembly().GetName().Version} starting.");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception.", e.ExceptionObject as Exception);

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayContext());
        }
        finally
        {
            Log.Info("Klangbruecke exiting.");
        }

        GC.KeepAlive(_singleInstance);
    }
}
