using Klangbruecke.Diagnostics;

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
    /// What the verdict is worth in the log. The same rule as the calls half's
    /// <c>CallsPolicy.LevelFor</c>: a half that cannot run at all is Warn, anything the user chose is
    /// Info. The music half has no "switched off by the user" state, so it only ever produces the
    /// two ends.
    ///
    /// The two gates used to disagree about the identical root cause - missing package identity -
    /// which left [WRN] telling half the story.
    /// </summary>
    public static LogLevel LevelFor(bool isPackaged) => isPackaged ? LogLevel.Info : LogLevel.Warn;

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

    /// <summary>
    /// The tray's "Phone" submenu: what it says, and whether it can be opened.
    ///
    /// <b>This is the music half's only user-facing statement that it needs MSIX.</b> The tray used to
    /// raise "Music needs the packaged build (MSIX)." into the tooltip from its own connect path;
    /// that path moved into <c>MusicHalf</c>, which reads no package identity and cannot say it. Left
    /// as it was, an unpackaged run showed an ordinary phone list, a permanent "RetryBackoff - music
    /// retrying in 60s", and the reason only in the log.
    ///
    /// <b>Still clickable unpackaged, unlike <c>CallsPolicy.MenuItem</c>, and the asymmetry is
    /// deliberate.</b> Picking a phone is what starts the <c>DeviceWatcher</c> and the transport
    /// enumeration, and both of those work with no package identity - they are the only link-side and
    /// calls-side facts an unpackaged run can establish, and the ones a smoke test reads. Greying this
    /// out to match the calls item would buy a tidier menu and cost the one thing a development build
    /// is for. So the label carries the whole warning. Pinned by
    /// <c>AudioSinkPolicyTests.MenuItem_StaysClickable_EvenUnpackaged</c>, which is where anyone who
    /// disagrees has to come.
    ///
    /// A pair rather than a bare string for the reason <c>CallsPolicy.MenuItem</c> returns a triple:
    /// the text and the clickability are one decision, and the combination that must never appear -
    /// a label naming a blocker beside a control that pretends the blocker is not there - is only
    /// unrepresentable while they are returned together.
    /// </summary>
    public static (string Text, bool Enabled) MenuItem(bool isPackaged) => isPackaged
        ? ("Phone", true)
        : ("Phone (music needs MSIX)", true);
}
