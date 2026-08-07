using Klangbruecke.Connection;

namespace Klangbruecke;

/// <summary>
/// Which of the three tray glyphs is shown. Fewer buckets than there are reported states on purpose:
/// the icon is a glance, not a sentence - the tooltip already carries the seven-state detail, and an
/// icon with seven appearances would be seven things to learn rather than one colour to recognise.
/// </summary>
public enum TrayIconStatus
{
    /// <summary>Everything the user asked for is delivering. The blue mark.</summary>
    Active,

    /// <summary>
    /// On its way up, partly up, or counting down to another try. The amber mark. One bucket for all
    /// four because the distinction the user acts on - "is it fully working?" - is already answered by
    /// it not being <see cref="Active"/>, and the tooltip says which of the four it is.
    /// </summary>
    Busy,

    /// <summary>Nothing is being attempted, by configuration or by the user's own hand. The grey mark.</summary>
    Idle,
}

/// <summary>
/// Maps a reported <see cref="ConnectionState"/> to the tray glyph that stands for it.
///
/// Pure and total, and split out of <see cref="TrayContext"/> for the same reason the other policies
/// are: the view that swaps the icon registers a real shell icon and cannot be reached from a test,
/// so the decision about <em>which</em> icon lives here where every state can be asserted, and the
/// view does nothing but load the file the decision names.
///
/// The mapping is the whole design, so it is worth stating what each bucket earns its place by:
/// <list type="bullet">
/// <item><see cref="ConnectionState.Connected"/> is the only <see cref="TrayIconStatus.Active"/> - it
/// is the one state in which every enabled half is delivering.</item>
/// <item><see cref="ConnectionState.Degraded"/> is <see cref="TrayIconStatus.Busy"/>, not Active,
/// even though one half is up: "half of what you asked for" is not "working", and colouring it Active
/// would tell a user whose calls have dropped that everything is fine.</item>
/// <item><see cref="ConnectionState.Suppressed"/> is <see cref="TrayIconStatus.Idle"/>, not Busy: a
/// deliberate Disconnect and an auto-reconnect that is switched off are both the app being dormant on
/// purpose, and Busy would promise motion that is not coming.</item>
/// </list>
/// </summary>
public static class TrayIconPolicy
{
    /// <summary>The glyph for a state. The default arm is <see cref="TrayIconStatus.Idle"/> so a state
    /// added later shows the dormant mark rather than a false Active - the same conservative default
    /// <see cref="ConnectionStateProjection"/> takes for an unrecognised suppression reason.</summary>
    public static TrayIconStatus For(ConnectionState state) => state switch
    {
        ConnectionState.Connected => TrayIconStatus.Active,

        ConnectionState.Connecting
            or ConnectionState.Discovering
            or ConnectionState.Degraded
            or ConnectionState.RetryBackoff => TrayIconStatus.Busy,

        ConnectionState.Idle or ConnectionState.Suppressed => TrayIconStatus.Idle,

        _ => TrayIconStatus.Idle,
    };
}
