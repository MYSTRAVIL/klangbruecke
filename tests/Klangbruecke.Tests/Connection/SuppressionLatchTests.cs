using Klangbruecke.Connection;
using Xunit;

// System.Windows.Forms.LinkState is a public enum, and UseWindowsForms + ImplicitUsings puts it in
// every file of this project via a global using - so an unqualified LinkState here is CS0104,
// ambiguous. The alias picks ours, exactly as LinkMachineTests does.
using LinkState = Klangbruecke.Connection.LinkState;

namespace Klangbruecke.Tests.Connection;

public sealed class SuppressionLatchTests
{
    // Every test asserts the pair, not just the reason: IsSet is what the halves' "not suppressed"
    // guards read and Reason is what the tray's detail text reads, so the two drifting apart would
    // be a dormant app that reports itself connected.
    private static void AssertSet(SuppressionLatch latch, SuppressionReason expected)
    {
        Assert.Equal(expected, latch.Reason);
        Assert.True(latch.IsSet);
    }

    private static void AssertClear(SuppressionLatch latch)
    {
        Assert.Equal(SuppressionReason.None, latch.Reason);
        Assert.False(latch.IsSet);
    }

    // Enumerated rather than listed, so a fourth LinkState could not arrive without the
    // never-sets-the-latch rule below being asked about it.
    public static IEnumerable<object[]> EveryLinkState() =>
        Enum.GetValues<LinkState>().Select(state => new object[] { state });

    [Fact]
    public void Starts_clear()
    {
        AssertClear(new SuppressionLatch());
    }

    [Fact]
    public void Deliberate_suppression_sets_the_reason()
    {
        SuppressionLatch latch = new();

        latch.SuppressDeliberate();

        AssertSet(latch, SuppressionReason.Deliberate);
    }

    // The expiry that makes a deliberate disconnect a moment rather than a setting: disconnecting
    // before bed must leave the app connected again in the morning.
    [Fact]
    public void Deliberate_clears_after_the_link_drops_and_returns()
    {
        SuppressionLatch latch = new();
        latch.SuppressDeliberate();

        latch.OnLinkState(LinkState.Absent);
        latch.OnLinkState(LinkState.Present);

        AssertClear(latch);
    }

    // The whole point of sawAbsent. The phone is still Present at the instant the user clicks
    // Disconnect, and the reconcile keeps reporting Present every 30 s afterwards. A latch that
    // cleared on Present alone would reconnect on the very next tick and undo the click.
    [Fact]
    public void Deliberate_does_not_clear_on_Present_alone()
    {
        SuppressionLatch latch = new();
        latch.SuppressDeliberate();

        latch.OnLinkState(LinkState.Present);

        AssertSet(latch, SuppressionReason.Deliberate);
    }

    // The same rule over five minutes of reconcile ticks rather than one, because the level-triggered
    // poll re-reports Present forever, not once.
    [Fact]
    public void Deliberate_survives_repeated_Present_reports()
    {
        SuppressionLatch latch = new();
        latch.SuppressDeliberate();

        for (int i = 0; i < 10; i++)
        {
            latch.OnLinkState(LinkState.Present);
        }

        AssertSet(latch, SuppressionReason.Deliberate);
    }

    // The accepted cost of polling, pinned so it stays a decision rather than a surprise. The link is
    // read every 30 s; a phone that leaves and returns entirely between two reads is never observed
    // Absent, so both reads say Present and the latch stays set until the next observed absence.
    [Fact]
    public void A_drop_and_return_inside_one_reconcile_tick_leaves_it_set()
    {
        SuppressionLatch latch = new();
        latch.SuppressDeliberate();

        latch.OnLinkState(LinkState.Present); // tick n
        // The phone drops and returns here, entirely unobserved.
        latch.OnLinkState(LinkState.Present); // tick n+1

        AssertSet(latch, SuppressionReason.Deliberate);
    }

    // The difference between the two reasons. AutoReconnectOff is a setting, so it has to survive the
    // phone leaving and coming back - the exact sequence that expires a Deliberate latch.
    [Fact]
    public void AutoReconnectOff_does_not_clear_on_link_drop_and_return()
    {
        SuppressionLatch latch = new();

        latch.SuppressAutoReconnectOff();
        AssertSet(latch, SuppressionReason.AutoReconnectOff);

        latch.OnLinkState(LinkState.Absent);
        latch.OnLinkState(LinkState.Present);

        AssertSet(latch, SuppressionReason.AutoReconnectOff);
    }

    [Fact]
    public void AutoReconnectOff_clears_when_auto_reconnect_is_switched_on()
    {
        SuppressionLatch latch = new();
        latch.SuppressAutoReconnectOff();

        latch.OnAutoReconnectEnabled();

        AssertClear(latch);
    }

    [Fact]
    public void AutoReconnectOff_clears_when_a_phone_is_picked()
    {
        SuppressionLatch latch = new();
        latch.SuppressAutoReconnectOff();

        latch.OnPhoneSelectionChanged();

        AssertClear(latch);
    }

    [Fact]
    public void Deliberate_clears_when_a_phone_is_picked()
    {
        SuppressionLatch latch = new();
        latch.SuppressDeliberate();

        latch.OnPhoneSelectionChanged();

        AssertClear(latch);
    }

    // Clearing has to forget the absence as well as the reason. A latch that remembered it would
    // re-arm into "one Present away from clearing", so the next deliberate disconnect would survive
    // exactly one reconcile tick.
    [Fact]
    public void Clear_resets_the_sawAbsent_memory()
    {
        SuppressionLatch latch = new();
        latch.SuppressDeliberate();
        latch.OnLinkState(LinkState.Absent);

        latch.Clear();
        latch.SuppressDeliberate();
        latch.OnLinkState(LinkState.Present);

        AssertSet(latch, SuppressionReason.Deliberate);
    }

    // The same reset on the other path in: suppressing again is a fresh decision, so it cannot
    // inherit an absence observed before it was made.
    [Fact]
    public void Suppressing_again_resets_the_sawAbsent_memory()
    {
        SuppressionLatch latch = new();
        latch.SuppressDeliberate();
        latch.OnLinkState(LinkState.Absent);

        latch.SuppressDeliberate();
        latch.OnLinkState(LinkState.Present);

        AssertSet(latch, SuppressionReason.Deliberate);
    }

    // --- beyond the brief's table, pinned because each is a deliberate reading of an undefined case ---

    // NoPhone is the third LinkState and the reconcile feeds it in like any other, so OnLinkState has
    // to say something about it. It means "no phone is selected, the question does not apply" - not
    // "the phone left the room" - so it is not an observed absence and must not arm the re-clear.
    // Deselection itself clears the latch, but through OnPhoneSelectionChanged, which is an intent
    // rather than an observation.
    [Fact]
    public void NoPhone_is_not_an_observed_absence()
    {
        SuppressionLatch latch = new();
        latch.SuppressDeliberate();

        latch.OnLinkState(LinkState.NoPhone);
        AssertSet(latch, SuppressionReason.Deliberate);

        latch.OnLinkState(LinkState.Present);

        AssertSet(latch, SuppressionReason.Deliberate);
    }

    // Same instinct as LinkMachine's unrecognised-status rule, pointed the other way: a value the
    // enum does not define must not be able to expire the user's decision. Silently reconnecting
    // after a deliberate disconnect is the failure the user notices.
    [Fact]
    public void An_unrecognised_link_state_is_not_an_observed_absence()
    {
        SuppressionLatch latch = new();
        latch.SuppressDeliberate();

        latch.OnLinkState((LinkState)99);
        latch.OnLinkState(LinkState.Present);

        AssertSet(latch, SuppressionReason.Deliberate);
    }

    // OnAutoReconnectEnabled is scoped to the reason it undoes. The user turning a setting back on
    // says nothing about the Disconnect they clicked a moment ago, and clearing here would reconnect
    // them on the next tick.
    [Fact]
    public void Deliberate_survives_auto_reconnect_being_switched_on()
    {
        SuppressionLatch latch = new();
        latch.SuppressDeliberate();

        latch.OnAutoReconnectEnabled();

        AssertSet(latch, SuppressionReason.Deliberate);
    }

    // The latch is only ever set by an explicit decision. A link report is an observation, so no
    // value of it - present, absent, or no phone at all - may put the app into dormancy on its own.
    [Theory]
    [MemberData(nameof(EveryLinkState))]
    public void Link_reports_never_set_a_clear_latch(LinkState state)
    {
        SuppressionLatch latch = new();

        latch.OnLinkState(state);
        latch.OnLinkState(state);

        AssertClear(latch);
    }
}
