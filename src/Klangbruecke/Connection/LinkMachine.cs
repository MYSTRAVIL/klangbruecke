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
    /// </summary>
    public bool OnLinkStatusRead(BluetoothLinkStatus status)
    {
        if (State == LinkState.NoPhone)
        {
            return false;
        }

        return MoveTo(status == BluetoothLinkStatus.Connected ? LinkState.Present : LinkState.Absent);
    }

    /// <summary>
    /// The single place the state moves, and the single source of the returned flag: true only when
    /// something actually changed. Callers log on that flag, so a method that always said "changed"
    /// would write a reconcile line every 30 seconds for as long as the app runs.
    /// </summary>
    private bool MoveTo(LinkState next)
    {
        if (State == next)
        {
            return false;
        }

        State = next;
        return true;
    }
}
