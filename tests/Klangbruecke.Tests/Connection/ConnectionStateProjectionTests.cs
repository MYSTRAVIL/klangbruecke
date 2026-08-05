using Klangbruecke.Connection;
using Xunit;

// System.Windows.Forms.LinkState is a public enum, and UseWindowsForms + ImplicitUsings puts it in
// every file of this project via a global using - so an unqualified LinkState here is CS0104,
// ambiguous. The alias picks ours, exactly as LinkMachineTests and SuppressionLatchTests do.
using LinkState = Klangbruecke.Connection.LinkState;

namespace Klangbruecke.Tests.Connection;

public sealed class ConnectionStateProjectionTests
{
    private static readonly bool[] Bools = { false, true };

    // A working app is the default, so every test below names only the fields it is about:
    // Snapshot(music: MusicState.Backoff) reads as "the music half is retrying and nothing else is
    // wrong", which is exactly the situation the projection exists to describe.
    private static ConnectionSnapshot Snapshot(
        bool phoneSelected = true,
        SuppressionReason suppression = SuppressionReason.None,
        LinkState link = LinkState.Present,
        bool musicEnabled = true,
        MusicState music = MusicState.Up,
        bool callsEnabled = true,
        CallsState calls = CallsState.Up,
        TimeSpan? nextRetryIn = null) =>
        new(phoneSelected, suppression, link, musicEnabled, music, callsEnabled, calls, nextRetryIn);

    // Enumerated rather than listed. The manager hands this function whatever its three machines
    // happen to hold at the instant the tray asks, so every combination is reachable - and a
    // hand-written list would be the one place a sixth MusicState could arrive without anything
    // asking whether Project still has an answer. 2 x 3 x 3 x 2 x 5 x 2 x 4 = 1440 rows.
    //
    // The parameters are primitives rather than a ConnectionSnapshot because xunit only
    // pre-enumerates theory data it can serialise; handing it the record struct would collapse all
    // 1440 rows into a single test case and lose the failing combination from the failure message.
    public static IEnumerable<object[]> EverySnapshot() =>
        from phoneSelected in Bools
        from suppression in Enum.GetValues<SuppressionReason>()
        from link in Enum.GetValues<LinkState>()
        from musicEnabled in Bools
        from music in Enum.GetValues<MusicState>()
        from callsEnabled in Bools
        from calls in Enum.GetValues<CallsState>()
        select new object[] { phoneSelected, suppression, link, musicEnabled, music, callsEnabled, calls };

    // The same cross-product as values, for the assertions that do not need a row each in the runner.
    private static IEnumerable<ConnectionSnapshot> EverySnapshotValue(TimeSpan? nextRetryIn) =>
        from row in EverySnapshot()
        select Snapshot(
            phoneSelected: (bool)row[0],
            suppression: (SuppressionReason)row[1],
            link: (LinkState)row[2],
            musicEnabled: (bool)row[3],
            music: (MusicState)row[4],
            callsEnabled: (bool)row[5],
            calls: (CallsState)row[6],
            nextRetryIn: nextRetryIn);

    // The same cross-product with the phone left out, for the rule that is supposed to dominate all
    // of it.
    public static IEnumerable<object[]> EveryOtherField() =>
        from suppression in Enum.GetValues<SuppressionReason>()
        from link in Enum.GetValues<LinkState>()
        from musicEnabled in Bools
        from music in Enum.GetValues<MusicState>()
        from callsEnabled in Bools
        from calls in Enum.GetValues<CallsState>()
        select new object[] { suppression, link, musicEnabled, music, callsEnabled, calls };

    // --- the eight rules, in order ---

    // Rule 1, and the whole cross-product asserts it beats every later rule at once: a suppressed
    // latch, an absent link and a half mid-connect are all fields of a phone the user has not
    // picked, and none of them may put anything but Idle in the tray.
    [Theory]
    [MemberData(nameof(EveryOtherField))]
    public void No_phone_selected_is_Idle(
        SuppressionReason suppression,
        LinkState link,
        bool musicEnabled,
        MusicState music,
        bool callsEnabled,
        CallsState calls)
    {
        ConnectionSnapshot snapshot = Snapshot(
            phoneSelected: false,
            suppression: suppression,
            link: link,
            musicEnabled: musicEnabled,
            music: music,
            callsEnabled: callsEnabled,
            calls: calls);

        Assert.Equal(ConnectionState.Idle, ConnectionStateProjection.Project(snapshot));
    }

    // Rule 2. Both reasons report the same state - the detail is what tells them apart - and each is
    // asserted against the three rules below it, because a dormant app that reports Discovering or
    // Connecting is an app the user waits for forever.
    [Theory]
    [InlineData(SuppressionReason.Deliberate)]
    [InlineData(SuppressionReason.AutoReconnectOff)]
    public void Suppressed_beats_link_and_half_state(SuppressionReason reason)
    {
        Assert.Equal(
            ConnectionState.Suppressed,
            ConnectionStateProjection.Project(Snapshot(suppression: reason, link: LinkState.Absent)));

        Assert.Equal(
            ConnectionState.Suppressed,
            ConnectionStateProjection.Project(
                Snapshot(suppression: reason, music: MusicState.Connecting, calls: CallsState.Registering)));

        Assert.Equal(
            ConnectionState.Suppressed,
            ConnectionStateProjection.Project(
                Snapshot(suppression: reason, music: MusicState.Backoff, calls: CallsState.Backoff)));
    }

    // Rule 3. The calls half is deliberately not torn down when the phone leaves the room -
    // registration is what makes the phone offer the PC when it returns - so a calls half still
    // reporting Up is the normal reading of a range exit, not a contradiction. The link check has to
    // dominate it, or the tray claims a connection to a phone that is not in the building.
    [Fact]
    public void Link_absent_is_Discovering()
    {
        Assert.Equal(
            ConnectionState.Discovering,
            ConnectionStateProjection.Project(
                Snapshot(link: LinkState.Absent, music: MusicState.Off, calls: CallsState.Up)));
    }

    // Rule 5, the healthy case.
    [Fact]
    public void Both_halves_up_is_Connected()
    {
        Assert.Equal(ConnectionState.Connected, ConnectionStateProjection.Project(Snapshot()));
    }

    // The rule most likely to be got wrong. Linked means the Bluetooth connection is open and the
    // WASAPI capture endpoint is not present, which is what every moment of not-currently-streaming
    // looks like - and what a phone call looks like, because the call invalidates the endpoint
    // without closing the connection. Reporting the commonest idle condition as Degraded would cry
    // wolf until the user stopped reading the tray.
    [Fact]
    public void Music_Linked_counts_as_up()
    {
        Assert.Equal(
            ConnectionState.Connected,
            ConnectionStateProjection.Project(Snapshot(music: MusicState.Linked, calls: CallsState.Up)));
    }

    // Rule 6, both ways round.
    [Fact]
    public void Music_up_and_calls_backoff_is_Degraded()
    {
        Assert.Equal(
            ConnectionState.Degraded,
            ConnectionStateProjection.Project(Snapshot(music: MusicState.Up, calls: CallsState.Backoff)));
    }

    [Fact]
    public void Calls_up_and_music_backoff_is_Degraded()
    {
        Assert.Equal(
            ConnectionState.Degraded,
            ConnectionStateProjection.Project(Snapshot(music: MusicState.Backoff, calls: CallsState.Up)));
    }

    // Rule 7.
    [Fact]
    public void Both_halves_backoff_is_RetryBackoff()
    {
        Assert.Equal(
            ConnectionState.RetryBackoff,
            ConnectionStateProjection.Project(
                Snapshot(music: MusicState.Backoff, calls: CallsState.Backoff)));
    }

    // Rule 4, and it has to beat every state below it: a half on its way up is news, and burying it
    // under Degraded or RetryBackoff would report the app as broken while it is in the middle of
    // fixing itself.
    [Theory]
    [InlineData(MusicState.Connecting, CallsState.Up)]
    [InlineData(MusicState.Up, CallsState.Registering)]
    [InlineData(MusicState.Linked, CallsState.Registering)]
    [InlineData(MusicState.Connecting, CallsState.Registering)]
    [InlineData(MusicState.Connecting, CallsState.Backoff)]
    [InlineData(MusicState.Backoff, CallsState.Registering)]
    [InlineData(MusicState.Connecting, CallsState.Off)]
    [InlineData(MusicState.Off, CallsState.Registering)]
    public void Either_half_connecting_is_Connecting(MusicState music, CallsState calls)
    {
        Assert.Equal(
            ConnectionState.Connecting,
            ConnectionStateProjection.Project(Snapshot(music: music, calls: calls)));
    }

    // Disabled is not failed. A half the user switched off is not attempted, so it cannot be missing.
    [Fact]
    public void Music_alone_with_calls_disabled_is_Connected()
    {
        Assert.Equal(
            ConnectionState.Connected,
            ConnectionStateProjection.Project(
                Snapshot(music: MusicState.Up, callsEnabled: false, calls: CallsState.Off)));
    }

    // The development case: no MSIX package identity means the restricted capability cannot apply, so
    // the calls half is structurally unavailable. Pinning the app in Degraded for the whole of every
    // "dotnet run" would make the tray useless exactly where it is read most.
    [Fact]
    public void Calls_alone_with_music_disabled_is_Connected()
    {
        Assert.Equal(
            ConnectionState.Connected,
            ConnectionStateProjection.Project(
                Snapshot(musicEnabled: false, music: MusicState.Off, calls: CallsState.Up)));
    }

    // Rule 8, and the trap inside rule 5: "every enabled half is up" is vacuously true when no half
    // is enabled, so a projection written the obvious way reports a Connected app that is not
    // running anything at all.
    [Fact]
    public void No_half_enabled_is_Idle()
    {
        Assert.Equal(
            ConnectionState.Idle,
            ConnectionStateProjection.Project(
                Snapshot(musicEnabled: false, music: MusicState.Off, callsEnabled: false, calls: CallsState.Off)));
    }

    // The half states are not cleared when a half is switched off, so a Backoff left over from before
    // the setting changed is a live possibility. Reading it would report a failure in a half that is
    // not being attempted.
    [Fact]
    public void Disabled_half_in_Backoff_is_ignored()
    {
        Assert.Equal(
            ConnectionState.Connected,
            ConnectionStateProjection.Project(
                Snapshot(music: MusicState.Up, callsEnabled: false, calls: CallsState.Backoff)));

        Assert.Equal(
            ConnectionState.Connected,
            ConnectionStateProjection.Project(
                Snapshot(musicEnabled: false, music: MusicState.Backoff, calls: CallsState.Up)));
    }

    // The same staleness as the brief's Backoff case, in the other three states and pointed the other
    // way: a half switched off mid-run keeps whatever it last held, so an Up or a Connecting left
    // behind must not be read as service the user is no longer being given. Each pair below moves to
    // a different reported state if the enabled guard is dropped from one of the three reads.
    [Fact]
    public void Disabled_half_in_any_other_state_is_ignored_too()
    {
        // A stale Up would count as a half delivering, turning a plain retry into partial service.
        Assert.Equal(
            ConnectionState.RetryBackoff,
            ConnectionStateProjection.Project(
                Snapshot(musicEnabled: false, music: MusicState.Up, calls: CallsState.Backoff)));

        Assert.Equal(
            ConnectionState.RetryBackoff,
            ConnectionStateProjection.Project(
                Snapshot(music: MusicState.Backoff, callsEnabled: false, calls: CallsState.Up)));

        // A stale Connecting would claim the app is on its way up when nothing is being attempted.
        Assert.Equal(
            ConnectionState.Connected,
            ConnectionStateProjection.Project(
                Snapshot(musicEnabled: false, music: MusicState.Connecting, calls: CallsState.Up)));

        Assert.Equal(
            ConnectionState.Connected,
            ConnectionStateProjection.Project(
                Snapshot(music: MusicState.Up, callsEnabled: false, calls: CallsState.Registering)));

        // A stale Backoff on the disabled half, with the enabled half doing nothing, would report a
        // countdown that no timer is running.
        Assert.Equal(
            ConnectionState.Idle,
            ConnectionStateProjection.Project(
                Snapshot(musicEnabled: false, music: MusicState.Backoff, calls: CallsState.Off)));

        Assert.Equal(
            ConnectionState.Idle,
            ConnectionStateProjection.Project(
                Snapshot(music: MusicState.Off, callsEnabled: false, calls: CallsState.Backoff)));
    }

    // --- totality ---

    // The tray asks on a timer and cannot take a throw, and neither can the log line beside it. Every
    // combination gets an answer, and the answer is a state the tray knows how to render.
    [Theory]
    [MemberData(nameof(EverySnapshot))]
    public void Project_is_total(
        bool phoneSelected,
        SuppressionReason suppression,
        LinkState link,
        bool musicEnabled,
        MusicState music,
        bool callsEnabled,
        CallsState calls)
    {
        ConnectionState state = ConnectionStateProjection.Project(Snapshot(
            phoneSelected: phoneSelected,
            suppression: suppression,
            link: link,
            musicEnabled: musicEnabled,
            music: music,
            callsEnabled: callsEnabled,
            calls: calls));

        Assert.True(Enum.IsDefined(state), $"Project returned the undefined ConnectionState {(int)state}.");
    }

    // Both retry intervals per row: null is what the manager hands over whenever nothing is scheduled,
    // and it is the value a detail string built round NextRetryIn.Value throws on.
    [Theory]
    [MemberData(nameof(EverySnapshot))]
    public void Detail_is_never_null_or_empty(
        bool phoneSelected,
        SuppressionReason suppression,
        LinkState link,
        bool musicEnabled,
        MusicState music,
        bool callsEnabled,
        CallsState calls)
    {
        foreach (TimeSpan? nextRetryIn in new TimeSpan?[] { null, TimeSpan.FromSeconds(8) })
        {
            ConnectionSnapshot snapshot = Snapshot(
                phoneSelected: phoneSelected,
                suppression: suppression,
                link: link,
                musicEnabled: musicEnabled,
                music: music,
                callsEnabled: callsEnabled,
                calls: calls,
                nextRetryIn: nextRetryIn);

            Assert.False(string.IsNullOrWhiteSpace(ConnectionStateProjection.DetailFor(snapshot)));
        }
    }

    // --- the detail strings ---

    // Pinned, because the tray is the only place a user ever learns the difference between "the
    // connection is open and your phone is not playing anything" and "the connection is broken". The
    // state is the same Connected either way; this phrase is the whole of the nuance.
    [Fact]
    public void Detail_for_music_Linked_names_waiting_for_phone_audio()
    {
        Assert.Equal(
            "waiting for phone audio",
            ConnectionStateProjection.DetailFor(Snapshot(music: MusicState.Linked, calls: CallsState.Up)));

        Assert.Equal(
            "waiting for phone audio",
            ConnectionStateProjection.DetailFor(
                Snapshot(music: MusicState.Linked, callsEnabled: false, calls: CallsState.Off)));
    }

    [Fact]
    public void Detail_for_backoff_names_the_retry_interval()
    {
        Assert.Equal(
            "retrying in 8s",
            ConnectionStateProjection.DetailFor(Snapshot(
                music: MusicState.Backoff,
                calls: CallsState.Backoff,
                nextRetryIn: TimeSpan.FromSeconds(8))));

        // The degraded half names itself as well as the interval: "retrying" on its own leaves the
        // user unable to tell which half of the app they have lost.
        Assert.Equal(
            "calls retrying in 30s",
            ConnectionStateProjection.DetailFor(Snapshot(
                music: MusicState.Up,
                calls: CallsState.Backoff,
                nextRetryIn: TimeSpan.FromSeconds(30))));

        Assert.Equal(
            "music retrying in 2s",
            ConnectionStateProjection.DetailFor(Snapshot(
                music: MusicState.Backoff,
                calls: CallsState.Up,
                nextRetryIn: TimeSpan.FromSeconds(2))));
    }

    // The two reasons re-arm on completely different events - one expires when the phone leaves the
    // room, the other lasts until a setting changes - so a user shown the same words for both cannot
    // tell whether waiting will help.
    [Fact]
    public void Detail_for_AutoReconnectOff_differs_from_Deliberate()
    {
        ConnectionSnapshot deliberate = Snapshot(suppression: SuppressionReason.Deliberate);
        ConnectionSnapshot autoReconnectOff = Snapshot(suppression: SuppressionReason.AutoReconnectOff);

        Assert.Equal(ConnectionState.Suppressed, ConnectionStateProjection.Project(deliberate));
        Assert.Equal(ConnectionState.Suppressed, ConnectionStateProjection.Project(autoReconnectOff));

        Assert.NotEqual(
            ConnectionStateProjection.DetailFor(deliberate),
            ConnectionStateProjection.DetailFor(autoReconnectOff));

        Assert.Contains(
            "auto-reconnect",
            ConnectionStateProjection.DetailFor(autoReconnectOff),
            StringComparison.OrdinalIgnoreCase);
    }

    // "Idle" alone next to a phone that is sitting right there reads as a bug. The reason is the
    // whole message: both halves are switched off, so nothing is being attempted.
    [Fact]
    public void Detail_for_no_half_enabled_names_the_reason()
    {
        string detail = ConnectionStateProjection.DetailFor(
            Snapshot(musicEnabled: false, music: MusicState.Off, callsEnabled: false, calls: CallsState.Off));

        Assert.Contains("music", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("calls", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("idle", detail, StringComparison.OrdinalIgnoreCase);
    }

    // --- beyond the brief: the order where it is observable, and the cases the eight rules leave open ---

    // Only the first four rules can ever contend: rules 5 to 8 are mutually exclusive by
    // construction, since no half is up and in backoff at once and none of them fire when no half is
    // enabled. So these three tests plus No_phone_selected_is_Idle pin every swap that is
    // observable, and rule 4 is the last one that needs pinning against what follows it.
    [Fact]
    public void Link_absent_beats_a_connecting_half()
    {
        // The music half can be mid-connect against a link the reconcile has just reported gone: the
        // connect attempt was issued before the phone left. Reporting Connecting would promise a
        // connection to a phone that is not there.
        Assert.Equal(
            ConnectionState.Discovering,
            ConnectionStateProjection.Project(
                Snapshot(link: LinkState.Absent, music: MusicState.Connecting, calls: CallsState.Registering)));
    }

    // Linked is up everywhere, not only on the path to Connected. A music half waiting for the phone
    // to press play beside a calls half that has fallen over is a degraded app, not a retrying one.
    [Fact]
    public void Music_Linked_beside_calls_backoff_is_Degraded()
    {
        Assert.Equal(
            ConnectionState.Degraded,
            ConnectionStateProjection.Project(Snapshot(music: MusicState.Linked, calls: CallsState.Backoff)));
    }

    // The eight rules do not cover an enabled half sitting in Off - the state every half starts in,
    // and the one they return to while a gate is closed - so the projection needs an answer for it.
    // Something up beside something not running is partial service.
    [Fact]
    public void A_half_up_beside_an_enabled_half_that_is_off_is_Degraded()
    {
        Assert.Equal(
            ConnectionState.Degraded,
            ConnectionStateProjection.Project(Snapshot(music: MusicState.Up, calls: CallsState.Off)));

        Assert.Equal(
            ConnectionState.Degraded,
            ConnectionStateProjection.Project(Snapshot(music: MusicState.Off, calls: CallsState.Up)));
    }

    // Nothing up, but something is still counting down to its next attempt. RetryBackoff is what the
    // user needs to see - it is the state whose detail carries the interval.
    [Fact]
    public void A_half_retrying_beside_an_enabled_half_that_is_off_is_RetryBackoff()
    {
        Assert.Equal(
            ConnectionState.RetryBackoff,
            ConnectionStateProjection.Project(Snapshot(music: MusicState.Backoff, calls: CallsState.Off)));

        Assert.Equal(
            ConnectionState.RetryBackoff,
            ConnectionStateProjection.Project(Snapshot(music: MusicState.Off, calls: CallsState.Backoff)));
    }

    // Enabled halves that are all Off: the app is up, the phone is there, and nothing is being
    // attempted. Idle is the honest answer, and the detail says so rather than leaving the user to
    // guess whether the tray has stopped updating.
    [Fact]
    public void Enabled_halves_that_are_all_off_is_Idle()
    {
        ConnectionSnapshot snapshot = Snapshot(music: MusicState.Off, calls: CallsState.Off);

        Assert.Equal(ConnectionState.Idle, ConnectionStateProjection.Project(snapshot));
        Assert.False(string.IsNullOrWhiteSpace(ConnectionStateProjection.DetailFor(snapshot)));
    }

    // Same instinct as LinkMachine's "only Connected counts as connected", pointed at the halves: a
    // value the enum does not define is not evidence that a half is delivering audio.
    [Fact]
    public void An_unrecognised_half_state_is_not_counted_as_up()
    {
        Assert.Equal(
            ConnectionState.Degraded,
            ConnectionStateProjection.Project(Snapshot(music: (MusicState)99, calls: CallsState.Up)));

        Assert.Equal(
            ConnectionState.Degraded,
            ConnectionStateProjection.Project(Snapshot(music: MusicState.Up, calls: (CallsState)99)));
    }

    // Pointed the other way for the link, and deliberately: an unrecognised link value is not an
    // observed absence, and the halves' own evidence is the better witness. LinkMachine already
    // collapses a failed status read to Absent before it reaches here, so this is the value that
    // cannot be produced rather than the value that means "read failed".
    [Fact]
    public void An_unrecognised_link_state_defers_to_the_halves()
    {
        Assert.Equal(
            ConnectionState.Connected,
            ConnectionStateProjection.Project(Snapshot(link: (LinkState)99)));
    }

    // NoPhone alongside PhoneSelected is the one-turn disagreement between the tray's selection and
    // the link machine's view of it. It is not Absent, so it does not claim the phone has left; the
    // halves answer instead.
    [Fact]
    public void A_link_that_has_not_been_looked_at_yet_defers_to_the_halves()
    {
        Assert.Equal(
            ConnectionState.Connected,
            ConnectionStateProjection.Project(Snapshot(link: LinkState.NoPhone)));
    }

    // A reason the enum does not define still means "not None", and the safe reading of that is
    // dormant rather than connected. The detail cannot borrow either named reason's words, because
    // both would be a claim about why.
    [Fact]
    public void An_unrecognised_suppression_reason_still_suppresses()
    {
        ConnectionSnapshot snapshot = Snapshot(suppression: (SuppressionReason)99);

        Assert.Equal(ConnectionState.Suppressed, ConnectionStateProjection.Project(snapshot));

        string detail = ConnectionStateProjection.DetailFor(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.NotEqual(
            ConnectionStateProjection.DetailFor(Snapshot(suppression: SuppressionReason.Deliberate)),
            detail);
        Assert.NotEqual(
            ConnectionStateProjection.DetailFor(Snapshot(suppression: SuppressionReason.AutoReconnectOff)),
            detail);
    }

    // The manager may have no interval to offer - a half can be in Backoff for the moment between the
    // failure and the retry being scheduled - and NextRetryIn is nullable precisely because of it.
    // The detail must still say something, and must not invent a number.
    [Fact]
    public void Detail_for_a_missing_retry_interval_names_no_number()
    {
        string detail = ConnectionStateProjection.DetailFor(
            Snapshot(music: MusicState.Backoff, calls: CallsState.Backoff, nextRetryIn: null));

        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.False(detail.Any(char.IsDigit), $"Invented an interval it was not given: {detail}");
    }

    // Rounded up rather than truncated, so a retry 7.4 s away reads 8s and counts down. Truncation
    // would show 7s for a whole second before the attempt and 0s for the last one.
    [Fact]
    public void Detail_rounds_the_retry_interval_up()
    {
        Assert.Equal(
            "retrying in 8s",
            ConnectionStateProjection.DetailFor(Snapshot(
                music: MusicState.Backoff,
                calls: CallsState.Backoff,
                nextRetryIn: TimeSpan.FromSeconds(7.4))));
    }

    // A retry that is already due is reported as due. "retrying in 0s" is a countdown that has
    // stopped, which reads like a stuck app.
    [Fact]
    public void Detail_for_an_elapsed_retry_interval_says_now()
    {
        Assert.Equal(
            "retrying now",
            ConnectionStateProjection.DetailFor(Snapshot(
                music: MusicState.Backoff,
                calls: CallsState.Backoff,
                nextRetryIn: TimeSpan.Zero)));

        Assert.Equal(
            "retrying now",
            ConnectionStateProjection.DetailFor(Snapshot(
                music: MusicState.Backoff,
                calls: CallsState.Backoff,
                nextRetryIn: TimeSpan.FromSeconds(-5))));
    }

    // StatusPresenter composes "Klangbruecke: <message>" and truncates the result at 96 characters
    // with an ellipsis. A detail that has to be cut off loses its last words, which is where every
    // one of these phrases puts the thing the user needs - the interval, the half, the reason.
    // Cheaper to keep them short than to discover a truncated tray on the machine.
    //
    // A Fact looping the cross-product rather than a fourth 1440-row theory: the set of distinct
    // phrases is a dozen strings, so the extra rows would buy nothing but runner noise.
    [Fact]
    public void Detail_stays_short_enough_for_the_tray()
    {
        // The longest schedule entry, so the interval is the widest it ever gets.
        foreach (ConnectionSnapshot snapshot in EverySnapshotValue(TimeSpan.FromSeconds(60)))
        {
            string composed =
                $"Klangbruecke: {ConnectionStateProjection.Project(snapshot)} - "
                + ConnectionStateProjection.DetailFor(snapshot);

            Assert.True(composed.Length <= 96, $"Tooltip would be truncated: {composed}");
        }
    }
}
