namespace Klangbruecke.Tests;

/// <summary>
/// Runs a body on an STA thread and rethrows whatever it threw.
///
/// WinForms controls want an STA thread. xunit v2 gives tests an MTA thread and has no built-in way
/// to change that, so the STA thread is created explicitly here rather than via a custom test
/// framework.
/// </summary>
internal static class StaThread
{
    public static void Run(Action action)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }
    }
}
