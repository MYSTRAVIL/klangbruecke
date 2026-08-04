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

    public void Show(string message)
    {
        // Logged where the status arrives rather than inside the post: the log is a record of what
        // happened, not of what the tooltip ended up saying. A post issued during shutdown is dropped
        // outright - NAudio's RecordingStopped fires while the router is being disposed - so this
        // line is the only surviving trace of those.
        Log.Info(message);

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
