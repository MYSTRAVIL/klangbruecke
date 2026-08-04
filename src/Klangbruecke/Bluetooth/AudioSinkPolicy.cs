namespace Klangbruecke.Bluetooth;

/// <summary>
/// Whether the music half can be attempted at all.
///
/// The calls half is gated on package identity because a restricted capability needs it. The music
/// half is gated on the same thing for a harsher reason: unpackaged,
/// <c>AudioPlaybackConnection.TryCreateFromId</c> raises an AccessViolationException inside the
/// CsWinRT ABI shim and terminates the process. That is a corrupted-state exception, so no managed
/// handler runs and nothing can be logged after the fact - the app simply vanishes, leaving a log
/// that stops mid-connect. See docs/FINDINGS.md §8 for the full reproduction.
///
/// Persisting the chosen phone is still right, so without this gate the trap compounds: one menu
/// click writes PhoneDeviceId, and every launch after it auto-connects, crashes before the tray is
/// usable, and does it again - recoverable only by deleting settings.json by hand, which nobody is
/// going to guess from a log that says "starting." and nothing else.
/// </summary>
public static class AudioSinkPolicy
{
    public static bool CanOpenConnection(bool isPackaged) => isPackaged;

    /// <summary>
    /// Why the music half is not being attempted, in the words the log will carry.
    ///
    /// Names the specific call rather than the feature, because the reader who meets this line is
    /// looking for something they did wrong and there is nothing: the same call crashes a bare test
    /// host with no app code in the frame.
    /// </summary>
    public static string Explain(bool isPackaged) => isPackaged
        ? "Music enabled."
        : "Music half skipped: no MSIX package identity. AudioPlaybackConnection.TryCreateFromId "
          + "terminates an unpackaged process with an access violation that cannot be caught or "
          + "logged - see docs/FINDINGS.md §8. Run the packaged build to route music.";
}
