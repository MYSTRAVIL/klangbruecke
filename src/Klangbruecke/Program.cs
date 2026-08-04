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

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());

        GC.KeepAlive(_singleInstance);
    }
}
