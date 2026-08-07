using Klangbruecke.Connection;
using Klangbruecke.Diagnostics;

namespace Klangbruecke;

/// <summary>
/// Turns a status message into a tray tooltip, on the UI thread.
///
/// Split out of TrayContext to be reachable from a test. Constructing a TrayContext registers a real
/// icon with the shell and reads the user's settings, so while the marshalling and the length cap
/// lived there - the two things that actually go wrong on this path - neither had any automated
/// guard.
/// </summary>
public sealed class StatusPresenter
{
    // NotifyIcon.Text throws ArgumentOutOfRangeException past 127 characters - measured, not assumed -
    // and status text interpolates exception messages, so its own length is unbounded. Set well below
    // that limit because a tooltip that long is unreadable anyway.
    private const int MaxTooltip = 96;

    /// <summary>
    /// Between the state and its detail. A spaced em dash rather than a colon or a hyphen: the detail
    /// phrases contain both - "music retrying in 8s", "disconnected until the phone leaves and
    /// returns" - and a separator that also appears inside what it separates is one the reader has to
    /// parse twice.
    /// </summary>
    private const string Separator = " — ";

    private readonly IUiDispatcher _ui;
    private readonly Action<string> _write;

    private volatile string _last = "Idle";

    public StatusPresenter(IUiDispatcher ui, Action<string> write)
    {
        _ui = ui;
        _write = write;
    }

    /// <summary>
    /// The message the tooltip is currently showing, for the menu to repeat.
    ///
    /// Assigned inside the posted callback rather than when the status arrives, so it and the tooltip
    /// change in the same UI-thread turn and the menu can never contradict the tray.
    ///
    /// The field is volatile because nothing structurally confines callers to the UI thread. A
    /// reference assignment is already atomic, so the risk was never a torn read; it was that without
    /// an acquire/release edge a reader on another thread has no guarantee of ever seeing the new
    /// value, and a read inside a polling loop can be hoisted out of it entirely. Volatile does not
    /// make this atomic with the tooltip write - a cross-thread reader can still catch it in the few
    /// instructions before the sink runs - but the two converge within the same callback.
    /// </summary>
    public string Last => _last;

    /// <summary>Raised by a component, which brought its own severity.</summary>
    public void Show(StatusMessage status) => Show(status.Text, status.Level);

    /// <summary>
    /// The connection's own state, which leads, and the phrase that explains it.
    ///
    /// <b>State first because a component's last announcement is not an answer to "is it working?".</b>
    /// "A2DP sink connected." stays in the tooltip for as long as nothing else speaks - minutes after
    /// the phone has left the room, and for the whole of a call, which takes the capture endpoint away
    /// without closing the connection. The projected state is the one thing that is always current,
    /// and the detail is what it means; the two arrive together so the tooltip cannot show one
    /// component's news beside another's state.
    ///
    /// Always Info, and deliberately not a parameter. Every one of the seven states is an ordinary
    /// condition of a working app - <c>Suppressed</c> and <c>RetryBackoff</c> included, which are the
    /// two that look like failures and are not: one is the user's own Disconnect and the other is the
    /// app doing exactly what it should. What actually went wrong is announced by the component that
    /// witnessed it, at the level it witnessed it at, through the overload above. Grading a projection
    /// here would be this class deciding the severity of events it did not see, which is the mistake
    /// <see cref="StatusMessage"/> exists to prevent.
    /// </summary>
    public void Show(ConnectionState state, string detail) => Show($"{state}{Separator}{detail}");

    /// <summary>
    /// Ordinary progress unless told otherwise. The level is a parameter rather than something
    /// inferred here: this class sees a string, and a presenter that guessed would be guessing about
    /// events it did not witness. See <see cref="StatusMessage"/>.
    /// </summary>
    public void Show(string message, LogLevel level = LogLevel.Info)
    {
        // Logged where the status arrives rather than inside the post: the log is a record of what
        // happened, not of what the tooltip ended up saying. A post issued during shutdown is dropped
        // outright - NAudio's RecordingStopped fires while the router is being disposed - so this
        // line is the only surviving trace of those.
        //
        // The full message, not the composed tooltip: Compose truncates at 96 characters and the
        // reason a status is long is almost always that it carries the detail worth keeping.
        Log.Write(level, message);

        // Reached from the WinRT threadpool and from NAudio callbacks; touching the tray icon off the
        // UI thread throws intermittently rather than failing cleanly.
        _ui.Post(() =>
        {
            // Assigned here, not above, so the menu that reads it and the tooltip change together on
            // the UI thread. A menu opening is queued behind an already-posted update, so the two
            // cannot disagree.
            _last = message;

            _write(Compose(message));
        });
    }

    // The composed string is what gets measured. The earlier version measured the message but
    // assigned the message plus a prefix and an ellipsis, so the guard and the value it guarded were
    // different strings - which is how it enforced a cap 11 characters larger than the one it named.
    private static string Compose(string message)
    {
        string tooltip = $"Klangbruecke: {message}";

        return tooltip.Length > MaxTooltip ? $"{tooltip[..(MaxTooltip - 3)]}..." : tooltip;
    }
}
