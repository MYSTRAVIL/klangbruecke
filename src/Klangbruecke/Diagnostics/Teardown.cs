namespace Klangbruecke.Diagnostics;

/// <summary>
/// Runs one step of a teardown sequence and absorbs its failure.
///
/// Every teardown path in this app converges on TrayContext.Dispose, which runs during
/// Application.Run's own teardown - outside the message loop's exception guard, so
/// Application.ThreadException never sees it. A throw there escapes Main, and Windows answers an
/// unhandled exception with a WER dialog: a visible window, in an app whose entire premise is not to
/// have one, at the moment the user can least explain it.
///
/// Absorbing per step rather than wrapping the sequence is the point. A single try around the whole
/// thing would let one failure skip every step after it - the tray icon left drawn until the shell
/// reaps it, or a disposed object left in a field so that every later retry fails on it and the
/// feature never recovers without a restart.
/// </summary>
internal static class Teardown
{
    public static void Quietly(Action step, string what)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            // Log.Warn is itself failure-proof; see FileLog. A logger that threw here would recreate
            // the exact crash this exists to prevent.
            Log.Warn($"Ignoring failure to {what}: {ex.Message}");
        }
    }
}
