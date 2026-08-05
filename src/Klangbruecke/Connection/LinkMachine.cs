using Klangbruecke.Bluetooth;

namespace Klangbruecke.Connection;

/// <summary>Is the selected phone there?</summary>
public enum LinkState
{
    /// <summary>No phone is selected, so the question does not apply.</summary>
    NoPhone,

    /// <summary>A phone is selected and is not currently reachable - or has not been seen yet.</summary>
    Absent,

    /// <summary>A phone is selected and the radio says it is connected.</summary>
    Present,
}

/// <summary>
/// The smallest of the three machines: presence only, no connecting and no audio.
///
/// It exists as its own type because two independent signals answer the same question. A
/// <c>DeviceWatcher</c> fires edges (<see cref="OnDeviceAppeared"/> / <see cref="OnDeviceRemoved"/>),
/// and the 30 s reconcile polls <c>BluetoothDevice.ConnectionStatus</c> and reports the level
/// (<see cref="OnLinkStatusRead"/>). The poll is not redundant: WinRT device events are unreliable
/// across sleep/resume, and an edge that never arrives would otherwise leave the app wrong forever -
/// the predecessor's defining bug. So both paths have to land in the same two states, which is what
/// makes this worth a type rather than a bool.
/// </summary>
public sealed class LinkMachine
{
    /// <summary>
    /// How many consecutive non-Connected <em>polls</em> it takes to give up on a Present link.
    ///
    /// Two, not one. <c>ILinkMonitor.ReadLinkStatusAsync</c> collapses every failed read to
    /// <see cref="BluetoothLinkStatus.Unknown"/> - an address that would not parse, a null from
    /// WinRT, a throw - and <see cref="OnLinkStatusRead"/> is right to treat that as disconnected,
    /// because the other guess is silent permanent dormancy. But it makes one transient hiccup look
    /// exactly like the phone leaving the room, and consumers act on that: the music half's Absent
    /// row stops the router and disconnects the sink, tearing down a working A2DP route mid-song,
    /// and the suppression latch re-arms on Present -> Absent -> Present, undoing a deliberate tray
    /// Disconnect about a minute after the user asked for it.
    ///
    /// Requiring a second sample costs nothing on a real range exit: the halves tear down on their
    /// own evidence - the connection closes, the endpoint vanishes - so this delays only the state
    /// label, never the teardown. It is deliberately not applied to watcher edges: a removal is a
    /// definite observation, and delaying it would have the app claim a phone that is provably gone.
    /// </summary>
    private const int NonConnectedPollsBeforeAbsent = 2;

    /// <summary>
    /// Length of the current run of non-Connected polls. Only consulted while
    /// <see cref="LinkState.Present"/> - the one transition the debounce guards - and cleared by
    /// every arrival in Present, which is what makes the run consecutive rather than cumulative.
    ///
    /// It does keep counting while Absent, which is deliberate and unobservable: reaching the
    /// threshold there calls <c>MoveTo(Absent)</c> from Absent, which moves nothing and reports no
    /// change, and the count is erased on the next entry to Present regardless. An explicit
    /// "only count while Present" guard was written and then deleted in review - it was dead code,
    /// and dead code cannot be given an assertion.
    /// </summary>
    private int _nonConnectedPolls;

    public LinkState State { get; private set; } = LinkState.NoPhone;

    /// <summary>
    /// A phone is now the selected one. Always lands in <see cref="LinkState.Absent"/>: selection is
    /// an intent, not an observation, and nothing has looked for the phone yet. That also covers
    /// picking a different phone while the old one was <see cref="LinkState.Present"/> - staying
    /// Present would claim the new phone is in the room on no evidence.
    /// </summary>
    public bool OnPhoneSelected() => MoveTo(LinkState.Absent);

    public bool OnPhoneDeselected() => MoveTo(LinkState.NoPhone);

    /// <summary>
    /// The watcher saw the phone. Ignored with no phone selected: a <c>DeviceWatcher</c> can still
    /// deliver a queued event after it has been stopped, and a stale edge must not resurrect a phone
    /// the user just cleared.
    ///
    /// Note that this clears the poll debounce even when already Present, where it reports no change.
    /// That is correct today because <c>LinkMonitor</c>'s selector is presence-scoped, so an Added
    /// edge means the radio currently sees the phone. It is worth knowing that a watcher which
    /// re-delivered Added more often than once per <see cref="NonConnectedPollsBeforeAbsent"/> polls
    /// would hold Present against a real range exit, because the poll could never finish a run.
    /// </summary>
    public bool OnDeviceAppeared() => State != LinkState.NoPhone && MoveTo(LinkState.Present);

    /// <summary>The watcher lost the phone. Ignored with no phone selected, for the same reason.</summary>
    public bool OnDeviceRemoved() => State != LinkState.NoPhone && MoveTo(LinkState.Absent);

    /// <summary>
    /// The reconcile poll's level-triggered report, and the backstop for a dropped watcher event.
    ///
    /// Only <see cref="BluetoothLinkStatus.Connected"/> counts as connected: everything else -
    /// <see cref="BluetoothLinkStatus.Unknown"/>, which is what a failed read collapses to, and any
    /// value the enum does not define - means Absent. Guessing the other way would make a read that
    /// could not answer look exactly like a healthy link, and the app would sit in Present and never
    /// rediscover the phone.
    ///
    /// Leaving Present is debounced: it takes <see cref="NonConnectedPollsBeforeAbsent"/> consecutive
    /// non-Connected reads, because one of them is indistinguishable from a failed read. Everything
    /// else about the rule is unchanged, including which values count as "not connected".
    /// </summary>
    public bool OnLinkStatusRead(BluetoothLinkStatus status)
    {
        if (State == LinkState.NoPhone)
        {
            return false;
        }

        if (status == BluetoothLinkStatus.Connected)
        {
            return MoveTo(LinkState.Present);
        }

        _nonConnectedPolls++;
        return _nonConnectedPolls >= NonConnectedPollsBeforeAbsent && MoveTo(LinkState.Absent);
    }

    /// <summary>
    /// The single place the state moves, and the single source of the returned flag: true only when
    /// something actually changed. Callers log on that flag, so a method that always said "changed"
    /// would write a reconcile line every 30 seconds for as long as the app runs.
    ///
    /// It is also the single place the poll debounce is cleared, for the same reason - every route
    /// into Present passes through here, so no caller can add one and forget.
    /// </summary>
    private bool MoveTo(LinkState next)
    {
        if (next == LinkState.Present)
        {
            // Every arrival in Present - watcher edge or Connected poll - starts the debounce over,
            // which is what makes the run consecutive rather than cumulative. Deliberately above the
            // no-change return: a Connected poll while already Present moves nothing but is still
            // evidence the link is up, and it is the only thing that can clear a half-finished run.
            _nonConnectedPolls = 0;
        }

        if (State == next)
        {
            return false;
        }

        State = next;
        return true;
    }
}
