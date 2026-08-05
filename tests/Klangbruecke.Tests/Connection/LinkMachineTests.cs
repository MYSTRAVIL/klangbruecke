using Klangbruecke.Bluetooth;
using Klangbruecke.Connection;
using Xunit;

// System.Windows.Forms.LinkState is a public enum, and UseWindowsForms + ImplicitUsings puts it in
// every file of this project via a global using - so an unqualified LinkState here is CS0104,
// ambiguous. The alias picks ours. Production code inside namespace Klangbruecke.Connection is not
// affected: a type declared in the enclosing namespace beats anything a using directive imports.
using LinkState = Klangbruecke.Connection.LinkState;

namespace Klangbruecke.Tests.Connection;

public sealed class LinkMachineTests
{
    /// <summary>
    /// One name per input the machine accepts, so the two return-value theories can be tables of
    /// (from, signal) rows rather than nine near-identical facts. Nothing in production sees this -
    /// it exists only so a theory row can name an event.
    /// </summary>
    public enum Signal
    {
        Select,
        Deselect,
        Appeared,
        Removed,
        ReadConnected,
        ReadDisconnected,
        ReadUnknown,
    }

    private static bool Apply(LinkMachine machine, Signal signal) => signal switch
    {
        Signal.Select => machine.OnPhoneSelected(),
        Signal.Deselect => machine.OnPhoneDeselected(),
        Signal.Appeared => machine.OnDeviceAppeared(),
        Signal.Removed => machine.OnDeviceRemoved(),
        Signal.ReadConnected => machine.OnLinkStatusRead(BluetoothLinkStatus.Connected),
        Signal.ReadDisconnected => machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected),
        Signal.ReadUnknown => machine.OnLinkStatusRead(BluetoothLinkStatus.Unknown),
        _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, null),
    };

    /// <summary>
    /// Drives a fresh machine to the requested state through the public API - there is no back door,
    /// and there should not be one. The assert at the end is what stops a broken helper from turning
    /// every theory below into a test of <c>NoPhone</c>.
    /// </summary>
    private static LinkMachine InState(LinkState state)
    {
        LinkMachine machine = new();
        switch (state)
        {
            case LinkState.NoPhone:
                break;
            case LinkState.Absent:
                machine.OnPhoneSelected();
                break;
            case LinkState.Present:
                machine.OnPhoneSelected();
                machine.OnDeviceAppeared();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        Assert.Equal(state, machine.State);
        return machine;
    }

    // Enumerated rather than listed, so a fourth LinkState could not be added without the
    // deselect-from-anywhere rule being asked about it.
    public static IEnumerable<object[]> EveryLinkState() =>
        Enum.GetValues<LinkState>().Select(state => new object[] { state });

    public static IEnumerable<object[]> EveryLinkStatus() =>
        Enum.GetValues<BluetoothLinkStatus>().Select(status => new object[] { status });

    [Fact]
    public void Starts_in_NoPhone()
    {
        Assert.Equal(LinkState.NoPhone, new LinkMachine().State);
    }

    // Absent, not Present: nothing has been observed yet. Landing in Present on selection alone would
    // report a phone that is switched off in another room as being there, and - worse - would let the
    // halves start connecting to it before a single signal said it was reachable.
    [Fact]
    public void Selecting_a_phone_moves_to_Absent()
    {
        LinkMachine machine = new();

        bool changed = machine.OnPhoneSelected();

        Assert.True(changed);
        Assert.Equal(LinkState.Absent, machine.State);
        Assert.NotEqual(LinkState.Present, machine.State);
    }

    [Theory]
    [MemberData(nameof(EveryLinkState))]
    public void Deselecting_from_any_state_returns_to_NoPhone(LinkState from)
    {
        LinkMachine machine = InState(from);

        bool changed = machine.OnPhoneDeselected();

        Assert.Equal(LinkState.NoPhone, machine.State);
        Assert.Equal(from != LinkState.NoPhone, changed);
    }

    [Fact]
    public void Device_appearing_moves_Absent_to_Present()
    {
        LinkMachine machine = InState(LinkState.Absent);

        bool changed = machine.OnDeviceAppeared();

        Assert.True(changed);
        Assert.Equal(LinkState.Present, machine.State);
    }

    [Fact]
    public void Device_removed_moves_Present_to_Absent()
    {
        LinkMachine machine = InState(LinkState.Present);

        bool changed = machine.OnDeviceRemoved();

        Assert.True(changed);
        Assert.Equal(LinkState.Absent, machine.State);
    }

    // The reconcile path. The watcher edge and the 30 s poll have to reach the same two states, or a
    // dropped WinRT event across sleep/resume leaves the app wrong forever - the predecessor's
    // defining bug. These two tests are the poll's half of that pairing.
    [Fact]
    public void Link_status_Connected_moves_Absent_to_Present()
    {
        LinkMachine machine = InState(LinkState.Absent);

        bool changed = machine.OnLinkStatusRead(BluetoothLinkStatus.Connected);

        Assert.True(changed);
        Assert.Equal(LinkState.Present, machine.State);
    }

    // Two reads, deliberately: since Task 19 the poll is debounced, so it takes two consecutive
    // non-Connected reads to give up on a Present link. The rule this test exists for is unchanged -
    // a poll that says "not connected" does reach Absent - it just no longer does so on one sample.
    [Fact]
    public void Link_status_Disconnected_moves_Present_to_Absent()
    {
        LinkMachine machine = InState(LinkState.Present);

        machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected);
        bool changed = machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected);

        Assert.True(changed);
        Assert.Equal(LinkState.Absent, machine.State);
    }

    // Spec rule, not an implementation convenience. Unknown is what every failed read collapses to:
    // an address that would not parse, a null from WinRT, a throw. Treating it as "still connected"
    // would make a failed read indistinguishable from a healthy link, so the app would sit in Present
    // and never rediscover the phone - silent permanent dormancy.
    //
    // Asserted from Present, where the two readings differ: Unknown-as-connected leaves Present and
    // returns false, and both assertions below catch it. The Absent half then pins that it is inert
    // in exactly the way Disconnected is.
    //
    // Two reads from Present, deliberately, for the same Task 19 reason as the test above: the rule
    // is still "Unknown means not connected", it just takes the same two samples Disconnected does.
    [Fact]
    public void Link_status_Unknown_is_treated_as_Disconnected()
    {
        LinkMachine present = InState(LinkState.Present);

        present.OnLinkStatusRead(BluetoothLinkStatus.Unknown);
        bool changed = present.OnLinkStatusRead(BluetoothLinkStatus.Unknown);

        Assert.True(changed);
        Assert.Equal(LinkState.Absent, present.State);

        LinkMachine absent = InState(LinkState.Absent);

        Assert.False(absent.OnLinkStatusRead(BluetoothLinkStatus.Unknown));
        Assert.Equal(LinkState.Absent, absent.State);
    }

    // --- the poll debounce ---
    //
    // The Unknown rule above is right and stays: a read that could not answer must never look like a
    // healthy link. But it makes one transient WinRT hiccup look exactly like the phone leaving the
    // room, and two consumers act on that. The music half's Absent row calls router.Stop() and
    // sink.Disconnect(), so a single failed read tears down a working A2DP route mid-song; and
    // SuppressionLatch re-arms on Present -> Absent -> Present, so a failed poll followed by a good
    // one silently undoes a deliberate tray Disconnect about 60 s after the user asked for it.
    //
    // So a *poll* needs two consecutive non-Connected reads before Present becomes Absent. Watcher
    // edges are definite observations and are not debounced. This costs nothing on a real range
    // exit, because the halves tear down on their own evidence - the connection closes and the
    // endpoint vanishes - so the debounce delays only the state label, never the teardown.

    [Fact]
    public void A_single_non_connected_poll_does_not_leave_Present()
    {
        LinkMachine machine = InState(LinkState.Present);

        bool changed = machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected);

        Assert.False(changed);
        Assert.Equal(LinkState.Present, machine.State);
    }

    [Fact]
    public void Two_consecutive_non_connected_polls_move_Present_to_Absent()
    {
        LinkMachine machine = InState(LinkState.Present);

        Assert.False(machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected));
        Assert.Equal(LinkState.Present, machine.State);

        bool changed = machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected);

        Assert.True(changed);
        Assert.Equal(LinkState.Absent, machine.State);
    }

    // Consecutive, not cumulative. A phone at the edge of range that reads badly once an hour must
    // never accumulate its way to Absent while the link is in fact up the whole time.
    [Fact]
    public void A_Connected_poll_resets_the_debounce()
    {
        LinkMachine machine = InState(LinkState.Present);

        Assert.False(machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected));
        Assert.False(machine.OnLinkStatusRead(BluetoothLinkStatus.Connected));

        bool changed = machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected);

        Assert.False(changed);
        Assert.Equal(LinkState.Present, machine.State);

        // Reset, not switched off: the run starts again from that read, so the next one still lands.
        Assert.True(machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected));
        Assert.Equal(LinkState.Absent, machine.State);
    }

    // Unknown is a failed read and Disconnected is a successful read of a dead link; the debounce
    // exists for the first and has to tolerate the second, so both count, and a mixed pair counts
    // too. Anything else would let an alternating Unknown/Disconnected sequence hold Present forever.
    [Theory]
    [InlineData(BluetoothLinkStatus.Disconnected, BluetoothLinkStatus.Disconnected)]
    [InlineData(BluetoothLinkStatus.Disconnected, BluetoothLinkStatus.Unknown)]
    [InlineData(BluetoothLinkStatus.Unknown, BluetoothLinkStatus.Disconnected)]
    [InlineData(BluetoothLinkStatus.Unknown, BluetoothLinkStatus.Unknown)]
    public void Unknown_and_Disconnected_both_count_toward_the_debounce(
        BluetoothLinkStatus first,
        BluetoothLinkStatus second)
    {
        LinkMachine machine = InState(LinkState.Present);

        Assert.False(machine.OnLinkStatusRead(first));
        Assert.Equal(LinkState.Present, machine.State);

        Assert.True(machine.OnLinkStatusRead(second));
        Assert.Equal(LinkState.Absent, machine.State);
    }

    // The debounce is for the poll only. A watcher removal is a definite observation - the radio said
    // the device went away - so delaying it would make the app claim a phone that is provably gone.
    [Fact]
    public void Device_removed_still_moves_Present_to_Absent_immediately()
    {
        LinkMachine machine = InState(LinkState.Present);

        bool changed = machine.OnDeviceRemoved();

        Assert.True(changed);
        Assert.Equal(LinkState.Absent, machine.State);

        // And an edge arriving mid-run is not swallowed by the counter either.
        LinkMachine midRun = InState(LinkState.Present);
        Assert.False(midRun.OnLinkStatusRead(BluetoothLinkStatus.Disconnected));

        Assert.True(midRun.OnDeviceRemoved());
        Assert.Equal(LinkState.Absent, midRun.State);
    }

    // A counter that only ever cleared on a Connected read would let a bad poll from a *previous*
    // visit shorten the next one to a single sample. Every arrival in Present starts the count over,
    // whichever signal did the arriving - here the watcher edge, which never touches the poll path.
    [Fact]
    public void The_debounce_is_reset_on_entering_Present()
    {
        LinkMachine machine = InState(LinkState.Present);

        Assert.False(machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected));
        Assert.True(machine.OnDeviceRemoved());
        Assert.True(machine.OnDeviceAppeared());
        Assert.Equal(LinkState.Present, machine.State);

        bool changed = machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected);

        Assert.False(changed);
        Assert.Equal(LinkState.Present, machine.State);
    }

    // Absent is where a phone that is switched off sits for hours, so the poll says "not connected"
    // there indefinitely. That must stay inert - and must not bank anything for later either.
    [Fact]
    public void Polls_while_Absent_do_not_accumulate_toward_anything()
    {
        LinkMachine machine = InState(LinkState.Absent);

        for (int i = 0; i < 5; i++)
        {
            Assert.False(machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected));
            Assert.Equal(LinkState.Absent, machine.State);
        }

        // "toward anything": the phone coming back and then glitching once still leaves it Present,
        // so none of those five shortened the next debounce.
        Assert.True(machine.OnLinkStatusRead(BluetoothLinkStatus.Connected));
        Assert.False(machine.OnLinkStatusRead(BluetoothLinkStatus.Disconnected));
        Assert.Equal(LinkState.Present, machine.State);
    }

    // Every status, enumerated: a read that arrives after the user cleared their phone must not drag
    // the machine to Absent either, which would report "discovering" with nothing to discover.
    [Theory]
    [MemberData(nameof(EveryLinkStatus))]
    public void Link_status_read_while_NoPhone_changes_nothing(BluetoothLinkStatus status)
    {
        LinkMachine machine = new();

        bool changed = machine.OnLinkStatusRead(status);

        Assert.False(changed);
        Assert.Equal(LinkState.NoPhone, machine.State);
    }

    // A DeviceWatcher keeps firing for a moment after StopWatching, so this arrives in real life.
    [Fact]
    public void Device_appearing_while_NoPhone_changes_nothing()
    {
        LinkMachine machine = new();

        bool changed = machine.OnDeviceAppeared();

        Assert.False(changed);
        Assert.Equal(LinkState.NoPhone, machine.State);
    }

    // The return value is a "did this change anything" flag the caller logs on. A method that always
    // returned true would put a reconcile line in the log every 30 seconds for as long as the app
    // runs. Each row also asserts the state stayed put, so the theory cannot pass by accident against
    // an implementation that moved and reported no change.
    //
    // These two rows and the other theory's rows together are the whole 3 x 7 table, once each. The
    // last two rows here moved over from that theory in Task 19: one non-Connected *poll* no longer
    // leaves Present, so from a fresh Present a single such signal now changes nothing. The pair of
    // them still reaches Absent - see Two_consecutive_non_connected_polls_move_Present_to_Absent -
    // and the watcher's Removed edge, one row below in the other theory, is not debounced at all.
    [Theory]
    [InlineData(LinkState.NoPhone, Signal.Deselect)]
    [InlineData(LinkState.NoPhone, Signal.Appeared)]
    [InlineData(LinkState.NoPhone, Signal.Removed)]
    [InlineData(LinkState.NoPhone, Signal.ReadConnected)]
    [InlineData(LinkState.NoPhone, Signal.ReadDisconnected)]
    [InlineData(LinkState.NoPhone, Signal.ReadUnknown)]
    [InlineData(LinkState.Absent, Signal.Select)]
    [InlineData(LinkState.Absent, Signal.Removed)]
    [InlineData(LinkState.Absent, Signal.ReadDisconnected)]
    [InlineData(LinkState.Absent, Signal.ReadUnknown)]
    [InlineData(LinkState.Present, Signal.Appeared)]
    [InlineData(LinkState.Present, Signal.ReadConnected)]
    [InlineData(LinkState.Present, Signal.ReadDisconnected)]
    [InlineData(LinkState.Present, Signal.ReadUnknown)]
    public void Transitions_that_change_nothing_return_false(LinkState from, Signal signal)
    {
        LinkMachine machine = InState(from);

        bool changed = Apply(machine, signal);

        Assert.False(changed);
        Assert.Equal(from, machine.State);
    }

    // The other half of the flag's contract, and the whole transition table besides: every row that
    // does move names where it lands, so an implementation that returned true without moving - or
    // moved somewhere else - fails here rather than passing a bare Assert.True.
    [Theory]
    [InlineData(LinkState.NoPhone, Signal.Select, LinkState.Absent)]
    [InlineData(LinkState.Absent, Signal.Deselect, LinkState.NoPhone)]
    [InlineData(LinkState.Absent, Signal.Appeared, LinkState.Present)]
    [InlineData(LinkState.Absent, Signal.ReadConnected, LinkState.Present)]
    [InlineData(LinkState.Present, Signal.Deselect, LinkState.NoPhone)]
    [InlineData(LinkState.Present, Signal.Select, LinkState.Absent)]
    [InlineData(LinkState.Present, Signal.Removed, LinkState.Absent)]
    public void Transitions_that_change_something_return_true(LinkState from, Signal signal, LinkState expected)
    {
        LinkMachine machine = InState(from);

        bool changed = Apply(machine, signal);

        Assert.True(changed);
        Assert.Equal(expected, machine.State);
    }

    // --- beyond the brief's table, pinned here because both are deliberate ---

    // The design's transition table only says what "phone selected" does from NoPhone. Picking a
    // different phone while the old one is Present is a real tray action (the suppression latch has a
    // "reselected" row for it), and leaving the machine in Present would have it claim the *new*
    // phone is in the room on no evidence at all. Selection always means "presence not yet observed".
    [Fact]
    public void Selecting_a_phone_while_Present_returns_to_Absent()
    {
        LinkMachine machine = InState(LinkState.Present);

        bool changed = machine.OnPhoneSelected();

        Assert.True(changed);
        Assert.Equal(LinkState.Absent, machine.State);
    }

    // The same instinct as the Unknown rule, one step further out: only Connected means connected.
    // A value the enum does not define - a future member, or a cast gone wrong - must not be able to
    // hold the machine in Present, because that is the failure mode with no recovery.
    //
    // Two reads since Task 19, for the same reason as the two named tests above: an undefined value
    // counts toward the debounce exactly as Unknown does, and still cannot hold Present forever.
    [Fact]
    public void An_unrecognised_link_status_is_not_treated_as_Connected()
    {
        LinkMachine machine = InState(LinkState.Present);

        machine.OnLinkStatusRead((BluetoothLinkStatus)99);
        bool changed = machine.OnLinkStatusRead((BluetoothLinkStatus)99);

        Assert.True(changed);
        Assert.Equal(LinkState.Absent, machine.State);
    }
}
