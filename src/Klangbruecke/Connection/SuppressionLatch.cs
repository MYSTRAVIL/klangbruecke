namespace Klangbruecke.Connection;

/// <summary>Why the app is sitting dormant next to a phone it could connect to.</summary>
public enum SuppressionReason
{
    /// <summary>Not suppressed. The halves may connect whenever the rest of their guards allow.</summary>
    None,

    /// <summary>The user disconnected, or the phone dropped the audio profile with the link still up.</summary>
    Deliberate,

    /// <summary>A half dropped while the auto-reconnect setting was off.</summary>
    AutoReconnectOff,
}

/// <summary>
/// In-memory only, never persisted: a reboot is the link dropping and returning.
/// Deliberate suppression expires when the phone leaves the room; AutoReconnectOff does not,
/// because it is a setting rather than a moment.
///
/// The reason is what makes this more than a bool. Both report <c>Suppressed</c> to the tray, but
/// they re-arm on completely different events, so a flag would have to pick one of the two wrong
/// behaviours: either a deliberate disconnect outlives the phone leaving the room and the app never
/// comes back, or turning auto-reconnect off lasts until the user next walks out of range.
/// </summary>
/// <remarks>
/// <b>Single-threaded, and it subscribes to nothing.</b> Every input is a method call that
/// <c>ConnectionManager</c> has already marshalled onto the UI thread through <c>IUiDispatcher</c>,
/// which is the same contract <see cref="MusicHalf"/> and <see cref="CallsHalf"/> state and the
/// reason all four hold no locks. It is worth stating here rather than left to inference:
/// <see cref="Set"/> writes two fields, and the pair is the invariant - a reader that saw the new
/// <see cref="Reason"/> beside the old sawAbsent would have a fresh deliberate disconnect one
/// Present report away from expiring, which is the exact bug <see cref="Set"/> clears both fields to
/// prevent.
/// </remarks>
public sealed class SuppressionLatch
{
    /// <summary>
    /// Whether the link has been observed <see cref="LinkState.Absent"/> since the latch was last set
    /// or cleared. Stored rather than inferred, because the app has no other memory of it: the
    /// reconcile poll reports a level every 30 s, not an edge, so "the phone came back" is only
    /// distinguishable from "the phone never left" by having recorded the leaving.
    /// </summary>
    private bool _sawAbsent;

    public SuppressionReason Reason { get; private set; } = SuppressionReason.None;

    public bool IsSet => Reason != SuppressionReason.None;

    /// <summary>Tray Disconnect, or the audio profile closing while the Bluetooth link stayed up.</summary>
    public void SuppressDeliberate() => Set(SuppressionReason.Deliberate);

    /// <summary>A half dropped and the auto-reconnect setting forbids the manager from re-initiating.</summary>
    public void SuppressAutoReconnectOff() => Set(SuppressionReason.AutoReconnectOff);

    /// <summary>Feeds the link machine's state in. Drives the sawAbsent memory and the re-arm.</summary>
    public void OnLinkState(LinkState state)
    {
        // Only a *selected* phone can be observed to leave. NoPhone means the question does not
        // apply, and an unrecognised value means the read cannot be trusted; neither is evidence the
        // phone left the room, and treating either as absence would let the next Present report
        // expire a decision the user made. Deselection does clear the latch, but through
        // OnPhoneSelectionChanged - an intent, not an observation.
        if (state == LinkState.Absent)
        {
            _sawAbsent = true;
            return;
        }

        // The phone is back, and it was seen to leave. Only Deliberate expires here: AutoReconnectOff
        // is the user's setting and outlasts the trip out of range that ends a deliberate disconnect.
        if (state == LinkState.Present && _sawAbsent && Reason == SuppressionReason.Deliberate)
        {
            Clear();
        }
    }

    /// <summary>
    /// The setting came back on, which undoes the reason it caused - and only that reason. Turning a
    /// setting on says nothing about a Disconnect the user clicked a moment ago, and clearing that
    /// too would reconnect them on the next reconcile tick.
    /// </summary>
    public void OnAutoReconnectEnabled()
    {
        if (Reason == SuppressionReason.AutoReconnectOff)
        {
            Clear();
        }
    }

    /// <summary>
    /// A phone was picked, repicked, or cleared in the tray. Always clears, whatever the reason:
    /// choosing a phone is the most explicit "connect to this" the app has, and it is also the only
    /// escape from an AutoReconnectOff latch other than the setting itself.
    /// </summary>
    public void OnPhoneSelectionChanged() => Clear();

    public void Clear() => Set(SuppressionReason.None);

    /// <summary>
    /// The single place both fields move. Every set and every clear forgets the absence, because
    /// sawAbsent only means anything relative to the decision currently latched: carrying an older
    /// observation forward would leave a fresh deliberate disconnect one Present report - one
    /// reconcile tick - from expiring.
    /// </summary>
    private void Set(SuppressionReason reason)
    {
        Reason = reason;
        _sawAbsent = false;
    }
}
