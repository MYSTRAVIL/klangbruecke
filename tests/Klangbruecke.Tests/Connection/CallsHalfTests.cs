using System.Reflection;
using Klangbruecke.Bluetooth;
using Klangbruecke.Connection;
using Klangbruecke.Diagnostics;
using Klangbruecke.Tests.Diagnostics;
using Klangbruecke.Tests.Fakes;
using Xunit;

namespace Klangbruecke.Tests.Connection;

/// <summary>
/// The device ids are the real ones: a probe with the phone (MYSTRAPIX9, C01C6A90E174) connected
/// returned exactly these two, one per selector. The other two transports are synthetic - only one
/// phone is paired on this machine - and are built by substituting a different address into the real
/// transport id, which keeps every other token of the real shape intact.
/// </summary>
public sealed class CallsHalfTests : IDisposable
{
    // A2DP selector, 110a, ...\SNK. This is what the settings store as the selected phone.
    private const string PhoneId =
        @"\\?\BTHENUM#{0000110a-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&C01C6A90E174_C00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\SNK";

    // Phone-line selector, 111f, ...\service. Same phone, same address, different id shape.
    private const string TransportId =
        @"\\?\BTHENUM#{0000111f-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&C01C6A90E174_C00000000#{bd41df2d-addd-4fc9-a194-b9881d2a2efa}\service";

    private const string OtherTransportId =
        @"\\?\BTHENUM#{0000111f-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&D8C0A63F1B22_C00000000#{bd41df2d-addd-4fc9-a194-b9881d2a2efa}\service";

    private const string ThirdTransportId =
        @"\\?\BTHENUM#{0000111f-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&E41B7CD02A66_C00000000#{bd41df2d-addd-4fc9-a194-b9881d2a2efa}\service";

    // A second phone, as the settings would store it: the A2DP shape carrying the other address.
    private const string OtherPhoneId =
        @"\\?\BTHENUM#{0000110a-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&D8C0A63F1B22_C00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\SNK";

    /// <summary>The smallest step the timing tests use to straddle a due time.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    private static readonly TransportCandidate PhoneTransport = new(TransportId, "MYSTRAPIX9");
    private static readonly TransportCandidate OtherTransport = new(OtherTransportId, "Someone else's phone");
    private static readonly TransportCandidate ThirdTransport = new(ThirdTransportId, "A third phone");

    private readonly ILog _originalLog = Log.Current;
    private readonly RecordingLog _log = new();

    public CallsHalfTests() => Log.Current = _log;

    public void Dispose() => Log.Current = _originalLog;

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private static CallTransportResult Refused =>
        CallTransportResult.NotClaimed("RegisterApp did not throw but the role was not claimed.");

    // --- Off ---------------------------------------------------------------------------------

    [Fact]
    public async Task Disabled_stays_Off_when_the_link_arrives()
    {
        Harness half = new(enabled: false);

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(CallsState.Off, half.Half.State);
        Assert.Equal(0, half.Calls.FindCount);
        Assert.Empty(half.Calls.ConnectCalls);
    }

    [Fact]
    public async Task Connect_not_permitted_leaves_Off_on_link_present()
    {
        Harness half = new();

        await half.Half.OnLinkPresentAsync(connectPermitted: false);

        Assert.Equal(CallsState.Off, half.Half.State);
        Assert.Equal(0, half.Calls.FindCount);
        Assert.Empty(half.Calls.ConnectCalls);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, PhoneId)]
    [InlineData(true, null)]
    public void Enabled_needs_both_the_switch_and_a_phone(bool enabled, string? phoneDeviceId)
    {
        Harness half = new(enabled, phoneDeviceId);

        Assert.False(half.Half.Enabled);
    }

    [Fact]
    public void Enabled_is_true_with_the_switch_on_and_a_phone_picked()
    {
        Assert.True(new Harness().Half.Enabled);
    }

    // --- Registering -------------------------------------------------------------------------

    [Fact]
    public async Task Link_present_enumerates_and_registers()
    {
        Harness half = new();
        half.Calls.DeferConnect = true;

        Task registering = half.Half.OnLinkPresentAsync(connectPermitted: true);

        // Enumerated, matched, and asked - and saying so before the answer arrives, because
        // Registering is a state the projection reports as Connecting.
        Assert.Equal(1, half.Calls.FindCount);
        Assert.Equal(new[] { TransportId }, half.Calls.ConnectCalls);
        Assert.Equal(CallsState.Registering, half.Half.State);

        half.Calls.CompleteConnect(CallTransportResult.Claimed(true));
        await registering;

        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Equal(0, half.Scheduler.PendingCount);
    }

    /// <summary>
    /// <b>The single most important test in this task.</b> <c>PhoneLineTransportDevice.ConnectAsync</c>
    /// returns False on this machine on every run, including the ones where a real cellular call
    /// routed audio both directions (docs/FINDINGS.md §12). Grading on it rather than on
    /// <c>Registered</c> would have the half back off and re-register forever while calls worked -
    /// and each round trip flaps the phone's call-audio-device option.
    /// </summary>
    [Fact]
    public async Task Registered_true_with_TransportConnected_false_reaches_Up()
    {
        Harness half = new();
        half.Calls.ConnectResult = CallTransportResult.Claimed(transportConnected: false);

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Equal(0, half.Scheduler.PendingCount);
        Assert.Null(half.Half.NextRetryIn);
    }

    [Fact]
    public async Task Repeated_link_present_reports_do_not_start_a_second_registration()
    {
        Harness half = new();
        await half.ReachUpAsync();

        // The reconcile poll is level-triggered: it says "the phone is there" every 30 s for as long
        // as it is there.
        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Single(half.Calls.ConnectCalls);
        Assert.Equal(1, half.Calls.FindCount);
        Assert.Equal(CallsState.Up, half.Half.State);
    }

    /// <summary>
    /// The matcher decides, not this half. One transport that does not carry the phone's address is
    /// <c>SoleCandidate</c>, which connects it anyway - a judgement <c>TransportMatcher</c> owns and
    /// explains. A half that re-derived the rule by asking for <c>AddressMatch</c> would silently
    /// refuse to register on every phone that reports different addresses per profile.
    /// </summary>
    [Fact]
    public async Task A_sole_candidate_is_registered_even_without_an_address_match()
    {
        Harness half = new();
        half.Calls.Transports = new[] { OtherTransport };

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Equal(new[] { OtherTransportId }, half.Calls.ConnectCalls);
    }

    // --- Backoff -----------------------------------------------------------------------------

    [Fact]
    public async Task Registered_false_moves_to_Backoff()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(CallsState.Backoff, half.Half.State);
        Assert.Equal(Seconds(2), half.Half.NextRetryIn);
        Assert.Equal(1, half.Scheduler.PendingCount);
    }

    /// <summary>
    /// Not an escape. The shipping service catches its own throws, so this covers the seam rather
    /// than today's implementation - which is the point: a throw that escaped would leave the half in
    /// <c>Registering</c> with no timer and no event that could ever move it again.
    /// </summary>
    [Fact]
    public async Task A_throw_from_ConnectAsync_moves_to_Backoff()
    {
        Harness half = new();
        var boom = new InvalidOperationException("the hands-free role said no");
        half.Calls.ConnectThrows = boom;

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(CallsState.Backoff, half.Half.State);
        Assert.Equal(Seconds(2), half.Half.NextRetryIn);
        Assert.Equal(1, half.Scheduler.PendingCount);

        // The exception itself, not just its message: a faulted WinRT async op's Message is "One or
        // more errors occurred." and the cause lives in the inner exception and the stack.
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, boom));
    }

    /// <summary>The same seam, one call earlier: enumeration can fail too, and must not escape either.</summary>
    [Fact]
    public async Task A_throw_from_FindTransportsAsync_moves_to_Backoff()
    {
        Harness half = new();
        half.Calls.FindThrows = new InvalidOperationException("the selector said no");

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(CallsState.Backoff, half.Half.State);
        Assert.Empty(half.Calls.ConnectCalls);
    }

    [Fact]
    public async Task No_matching_transport_moves_to_Backoff_without_connecting()
    {
        Harness half = new();
        half.Calls.Transports = Array.Empty<TransportCandidate>();

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(CallsState.Backoff, half.Half.State);
        Assert.Empty(half.Calls.ConnectCalls);
        Assert.Equal(Seconds(2), half.Half.NextRetryIn);
    }

    /// <summary>
    /// <c>TransportMatcher</c> connects nothing when it cannot tell two phones apart, and this half
    /// must not paper over that by taking the first one. The wrong phone's hands-free role looks like
    /// a working app until someone else's handset rings through this PC.
    /// </summary>
    [Fact]
    public async Task Ambiguous_match_moves_to_Backoff_without_connecting()
    {
        Harness half = new();
        half.Calls.Transports = new[] { OtherTransport, ThirdTransport };

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(CallsState.Backoff, half.Half.State);
        Assert.Empty(half.Calls.ConnectCalls);
    }

    /// <summary>
    /// The one line that says why nothing was connected, at the level
    /// <see cref="TransportMatcher.LevelFor"/> chose. Without it, "Ambiguous" and "NoCandidates" -
    /// two different facts about the user's pairing - are both just a half that quietly backs off.
    /// </summary>
    [Theory]
    [InlineData(0, LogLevel.Info)]
    [InlineData(1, LogLevel.Warn)]
    [InlineData(2, LogLevel.Warn)]
    public async Task The_match_outcome_is_logged_at_the_level_the_matcher_chose(int candidates, LogLevel expected)
    {
        Harness half = new();
        half.Calls.Transports = candidates switch
        {
            0 => Array.Empty<TransportCandidate>(),
            1 => new[] { OtherTransport },
            _ => new[] { OtherTransport, ThirdTransport },
        };

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        TransportMatchResult expectedMatch = TransportMatcher.Match(half.Calls.Transports, PhoneId);
        Assert.Contains(_log.Entries, e => e.Level == expected && e.Message == expectedMatch.Reason);
    }

    /// <summary>
    /// Once per distinct reason. A pairing that is permanently ambiguous produces the identical
    /// warning on every attempt, and the attempts never stop - so an unguarded line here is one
    /// warning a minute for as long as the app runs, against the project's rule that only a change
    /// gets logged.
    /// </summary>
    [Fact]
    public async Task A_repeated_match_reason_is_logged_once()
    {
        Harness half = new();
        half.Calls.Transports = new[] { OtherTransport, ThirdTransport };

        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        half.Scheduler.Advance(Seconds(2));
        half.Scheduler.Advance(Seconds(4));

        Assert.Equal(3, half.Calls.FindCount);
        Assert.Single(_log.Entries);

        // A reason that changed is news again, which is what makes this a filter and not a mute.
        half.Calls.Transports = new[] { PhoneTransport };
        half.Scheduler.Advance(Seconds(8));

        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Equal(2, _log.Entries.Count);
    }

    [Fact]
    public async Task Backoff_retries_on_the_2_4_8_sequence()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;

        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        Assert.Single(half.Calls.ConnectCalls);

        foreach (TimeSpan delay in new[] { Seconds(2), Seconds(4), Seconds(8) })
        {
            int before = half.Calls.ConnectCalls.Count;

            half.Scheduler.Advance(delay - Tick);
            Assert.Equal(before, half.Calls.ConnectCalls.Count);

            half.Scheduler.Advance(Tick);
            Assert.Equal(before + 1, half.Calls.ConnectCalls.Count);
        }

        Assert.Equal(4, half.Calls.ConnectCalls.Count);
        Assert.Equal(CallsState.Backoff, half.Half.State);
    }

    [Fact]
    public async Task Backoff_resets_after_reaching_Up()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;

        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        Assert.Equal(Seconds(2), half.Half.NextRetryIn);

        half.Scheduler.Advance(Seconds(2));
        Assert.Equal(Seconds(4), half.Half.NextRetryIn);

        half.Calls.ConnectResult = CallTransportResult.Claimed(true);
        half.Scheduler.Advance(Seconds(4));
        Assert.Equal(CallsState.Up, half.Half.State);

        // Round the loop again. The next failure has to start the sequence from the beginning rather
        // than from where the last run of failures left it.
        half.Calls.Registration = RegistrationStatus.NotRegistered;
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(CallsState.Backoff, half.Half.State);
        Assert.Equal(Seconds(2), half.Half.NextRetryIn);
    }

    /// <summary>
    /// Registration is not link-scoped, so <c>Backoff</c> is not either. The half sits there counting
    /// down whether or not the phone is in the room, and the level-triggered "the phone is there"
    /// report must not shortcut the wait - that is what turns a failing register into a tight loop.
    /// </summary>
    [Fact]
    public async Task Link_present_while_backing_off_does_not_jump_the_queue()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Single(half.Calls.ConnectCalls);
        Assert.Equal(1, half.Scheduler.PendingCount);
    }

    /// <summary>
    /// Unregistering first would flap the phone's call-audio-device option - the option disappears
    /// and reappears on the handset - for a role that is already not held. <c>Backoff</c> is reached
    /// only when registration failed, so there is nothing to release.
    /// </summary>
    [Fact]
    public async Task Reregistering_after_backoff_does_not_call_Disconnect_first()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        Assert.Equal(CallsState.Backoff, half.Half.State);

        half.Calls.ConnectResult = CallTransportResult.Claimed(true);
        half.Scheduler.Advance(Seconds(2));

        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Equal(2, half.Calls.ConnectCalls.Count);
        Assert.Equal(0, half.Calls.DisconnectCount);
    }

    // --- Deliberate intent -------------------------------------------------------------------

    /// <summary>
    /// There is deliberately no <c>OnLinkAbsent</c>. Registration is what makes the phone offer this
    /// PC when it comes back, and unregistering on every range exit would flap that option in the
    /// handset's own settings. The absence of the method is what makes the rule structural rather
    /// than something a later edit has to remember - which is what this test is for.
    /// </summary>
    [Fact]
    public void Link_absent_is_not_an_input()
    {
        MethodInfo[] methods = typeof(CallsHalf).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        Assert.DoesNotContain(methods, m => m.Name.Contains("LinkAbsent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Phone_deselected_unregisters()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Half.OnPhoneDeselected();

        Assert.Equal(CallsState.Off, half.Half.State);
        Assert.Equal(1, half.Calls.DisconnectCount);
    }

    /// <summary>
    /// Closes the <c>HANDOFF.md</c> carry-forward where toggling <c>EnableCalls</c> off left the
    /// registration live: the phone went on offering this PC as its call audio device for a half the
    /// user had switched off.
    /// </summary>
    [Fact]
    public async Task Disabling_calls_unregisters()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Half.OnDisabled();

        Assert.Equal(CallsState.Off, half.Half.State);
        Assert.Equal(1, half.Calls.DisconnectCount);
        Assert.Equal(0, half.Scheduler.PendingCount);
    }

    /// <summary>
    /// A different phone is a different failure history. Making the newly-picked phone serve out the
    /// old one's penalty punishes the user for the action they took precisely because the first phone
    /// was not working - and the longer the first phone failed, the longer they wait for the second.
    /// </summary>
    [Fact]
    public async Task Deselecting_the_phone_resets_the_backoff()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;

        // Two failures, so the schedule is demonstrably past its first step.
        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        half.Scheduler.Advance(Seconds(2));
        Assert.Equal(Seconds(4), half.Half.NextRetryIn);

        half.Half.OnPhoneDeselected();
        half.Half.Configure(enabled: true, OtherPhoneId);
        half.Calls.Transports = new[] { OtherTransport };
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(Seconds(2), half.Half.NextRetryIn);
    }

    /// <summary>
    /// The same phone and the same radio are still there afterwards, so flipping the switch off and
    /// on again has repaired nothing. Forgetting the failure history here is how a permanently broken
    /// pairing gets retried every two seconds for as long as somebody keeps toggling.
    /// </summary>
    [Fact]
    public async Task Disabling_calls_keeps_the_backoff()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;

        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        half.Scheduler.Advance(Seconds(2));
        Assert.Equal(Seconds(4), half.Half.NextRetryIn);

        half.Half.OnDisabled();
        half.Half.Configure(enabled: false, PhoneId);

        // Back on again, same phone.
        half.Half.Configure(enabled: true, PhoneId);
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(Seconds(8), half.Half.NextRetryIn);
    }

    /// <summary>
    /// Unconditional, with no "were we up?" guard. This class's own belief about whether the role is
    /// held is the one thing that can be stale - a registration whose answer was discarded mid-flight
    /// leaves the service holding a role this half never recorded - and the service already checks
    /// whether there is anything to release before it releases it
    /// (<c>Disconnect_on_a_fresh_service_does_not_throw</c>). Skipping the call to avoid a no-op
    /// would be the one case where the no-op was not one.
    /// </summary>
    [Fact]
    public void Unregistering_from_Off_still_releases_the_role()
    {
        Harness half = new();

        half.Half.OnDisabled();

        Assert.Equal(CallsState.Off, half.Half.State);
        Assert.Equal(1, half.Calls.DisconnectCount);
        Assert.Equal(0, half.ChangedCount);
    }

    /// <summary>
    /// Releasing the role is not the same as ending the service, and this half owns neither the
    /// service nor its lifetime - the manager does. Disposing it from a tray Disconnect would make
    /// the calls half unrecoverable for the rest of the process: the transport can be released and
    /// claimed again any number of times, but a disposed service has no second registration in it.
    /// </summary>
    [Fact]
    public async Task Unregistering_does_not_dispose_the_service()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Half.OnDisabled();
        half.Half.OnPhoneDeselected();

        Assert.False(half.Calls.Disposed);
    }

    /// <summary>
    /// A registration in flight when the user pulls the plug. Without the generation guard the
    /// answer lands in a half that has been shut down and reports <c>Up</c> over a role that was
    /// released while the radio was deciding.
    /// </summary>
    [Fact]
    public async Task A_registration_that_completes_after_a_teardown_does_not_reach_Up()
    {
        Harness half = new();
        half.Calls.DeferConnect = true;
        Task registering = half.Half.OnLinkPresentAsync(connectPermitted: true);
        Assert.Equal(CallsState.Registering, half.Half.State);

        half.Half.OnDisabled();
        Assert.Equal(CallsState.Off, half.Half.State);

        half.Calls.CompleteConnect(CallTransportResult.Claimed(true));
        await registering;

        Assert.Equal(CallsState.Off, half.Half.State);
        Assert.Equal(0, half.Scheduler.PendingCount);

        // Named rather than tidied away: the fake ends holding the role, because Disconnect ran while
        // the registration was still awaiting and the answer that arrived afterwards claimed it. The
        // real service behaves the same way, and that is why CallTransportService.ConnectAsync
        // assigns _device before its first await - a Disconnect landing mid-flight must still find
        // something to release. What this half owes is that no *second* registration compounds it,
        // which is the next test's subject.
        Assert.Equal(RegistrationStatus.Registered, half.Calls.Registration);
        Assert.Single(half.Calls.ConnectCalls);
    }

    /// <summary>
    /// Two registrations must never overlap on the service, and the state machine alone cannot
    /// promise it. <c>OnLinkPresentAsync</c> starts only from <c>Off</c> and the registration leaves
    /// <c>Off</c> before its first await - but a teardown puts the half back in <c>Off</c> while one
    /// is still awaiting, and the very next level-triggered report walks in. On the real service that
    /// is two <c>ConnectAsync</c> bodies sharing one device field across awaits: the second begins by
    /// unregistering, which can release the role the first is about to claim, and the first's
    /// continuation then registers through the second's device. The generation counter guards the
    /// answer; it has never guarded the call.
    /// </summary>
    [Fact]
    public async Task A_second_registration_does_not_start_while_one_is_in_flight()
    {
        Harness half = new();
        half.Calls.DeferConnect = true;
        Task first = half.Half.OnLinkPresentAsync(connectPermitted: true);
        Assert.Single(half.Calls.ConnectCalls);

        // The sequence the manager is told to use when the user picks a different phone.
        half.Half.OnPhoneDeselected();
        Assert.Equal(CallsState.Off, half.Half.State);
        half.Half.Configure(enabled: true, OtherPhoneId);
        half.Calls.Transports = new[] { PhoneTransport, OtherTransport };

        // Deliberately not awaited on the spot. A second registration that did start would be
        // awaiting a deferred connect nothing is going to answer, so an await here would hang the
        // suite instead of failing it - and a mutant that hangs the runner is a mutant nobody can
        // read a result from. Completing synchronously is itself the assertion: it is what a call
        // that turned round at the door looks like.
        Task blocked = half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.True(blocked.IsCompleted);
        Assert.Single(half.Calls.ConnectCalls);
        Assert.Equal(CallsState.Off, half.Half.State);
        await blocked;

        // And the block lifts the moment the first one answers, however it answers.
        half.Calls.DeferConnect = false;
        half.Calls.CompleteConnect(CallTransportResult.Claimed(true));
        await first;

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(new[] { TransportId, OtherTransportId }, half.Calls.ConnectCalls);
        Assert.Equal(CallsState.Up, half.Half.State);
    }

    // --- Configure ---------------------------------------------------------------------------

    /// <summary>
    /// <c>Configure</c> records the settings and does nothing else - the deliberate opposite of
    /// <c>MusicHalf.Configure</c>, which tears down.
    ///
    /// Settings arrive here for reasons that have nothing to do with calls, and the price of a wrong
    /// guess is not symmetric: unregistering costs the user the PC's entry in their phone's
    /// call-audio picker and cannot be undone without a round trip that flaps it again. So releasing
    /// the role is reserved for the two methods named for deliberate intent, and the manager calls
    /// one of them when a settings change really is the user's decision.
    /// </summary>
    [Fact]
    public async Task Configure_off_does_not_unregister()
    {
        Harness half = new();
        await half.ReachUpAsync();

        half.Half.Configure(enabled: false, PhoneId);

        Assert.False(half.Half.Enabled);
        Assert.Equal(0, half.Calls.DisconnectCount);
        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Equal(RegistrationStatus.Registered, half.Calls.Registration);
    }

    /// <summary>
    /// What <c>Configure</c> does change is what the half attempts next. A retry armed before the
    /// switch went off must not register a half the user has since turned off - and standing down to
    /// <c>Off</c> there releases nothing, because <c>Backoff</c> is only ever reached with the role
    /// unheld.
    /// </summary>
    [Fact]
    public async Task Configure_off_stops_the_backoff_retry_from_registering()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        Assert.Equal(CallsState.Backoff, half.Half.State);

        half.Half.Configure(enabled: false, PhoneId);
        half.Scheduler.Advance(Seconds(2));

        Assert.Single(half.Calls.ConnectCalls);
        Assert.Equal(CallsState.Off, half.Half.State);
        Assert.Equal(0, half.Calls.DisconnectCount);
        Assert.Equal(0, half.Scheduler.PendingCount);
    }

    [Fact]
    public async Task Reconcile_does_not_register_a_half_that_was_switched_off()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        // Overdue, so the switch is the only thing left that could refuse.
        half.Scheduler.Advance(Seconds(30));
        Assert.Equal(TimeSpan.Zero, half.Half.NextRetryIn);
        int before = half.Calls.ConnectCalls.Count;

        half.Half.Configure(enabled: false, PhoneId);
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(before, half.Calls.ConnectCalls.Count);
    }

    /// <summary>
    /// A phone swapped under a half that is counting down. The id is read at the moment of the
    /// attempt, not captured when the countdown started, so the retry registers what the settings say
    /// now rather than what they said two seconds ago.
    /// </summary>
    [Fact]
    public async Task A_retry_registers_the_phone_the_settings_name_now()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        // Both transports are on offer, so the id the retry uses is the only thing that decides
        // which one it asks for.
        half.Calls.Transports = new[] { PhoneTransport, OtherTransport };
        half.Half.Configure(enabled: true, OtherPhoneId);
        half.Calls.ConnectResult = CallTransportResult.Claimed(true);
        half.Scheduler.Advance(Seconds(2));

        Assert.Equal(new[] { TransportId, OtherTransportId }, half.Calls.ConnectCalls);
        Assert.Equal(CallsState.Up, half.Half.State);
    }

    // --- Reconcile ---------------------------------------------------------------------------

    /// <summary>
    /// The drift the 30 s poll exists to catch: another app claimed the role, the phone re-paired, or
    /// Windows dropped it, and no event said so.
    /// </summary>
    [Fact]
    public async Task Reconcile_drops_Up_to_Backoff_when_IsRegistered_goes_false()
    {
        Harness half = new();
        await half.ReachUpAsync();
        _log.Entries.Clear();

        half.Calls.Registration = RegistrationStatus.NotRegistered;
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(CallsState.Backoff, half.Half.State);
        Assert.Equal(Seconds(2), half.Half.NextRetryIn);

        // The role is already gone. Releasing it again would be the flap for nothing.
        Assert.Equal(0, half.Calls.DisconnectCount);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warn);
    }

    /// <summary>
    /// The second kind of drift, and the one a "is a role held?" question can never see: the role is
    /// held, and it is held on the wrong phone. <c>Configure</c> deliberately does not release it -
    /// only intent does - so without this the half sits satisfied in <c>Up</c> forever while the PC
    /// goes on being offered by a handset the user has stopped using.
    ///
    /// Releasing it here is not the flap the missing <c>OnLinkAbsent</c> guards against. That rule is
    /// about releasing a role nobody asked to release; this is the user asking, in the only way the
    /// settings can say it.
    /// </summary>
    [Fact]
    public async Task Reconcile_re_registers_when_the_selected_phone_changed()
    {
        Harness half = new();
        await half.ReachUpAsync();
        _log.Entries.Clear();

        half.Calls.Transports = new[] { PhoneTransport, OtherTransport };
        half.Half.Configure(enabled: true, OtherPhoneId);
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(CallsState.Backoff, half.Half.State);
        Assert.Equal(1, half.Calls.DisconnectCount);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warn);

        half.Scheduler.Advance(Seconds(2));

        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Equal(new[] { TransportId, OtherTransportId }, half.Calls.ConnectCalls);

        // And it settles: the phone it is now registered on is the phone the settings name, so the
        // next tick finds nothing to do.
        int changed = half.ChangedCount;
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Equal(changed, half.ChangedCount);
        Assert.Equal(1, half.Calls.DisconnectCount);
    }

    [Fact]
    public async Task Reconcile_does_nothing_while_Up_and_still_registered()
    {
        Harness half = new();
        await half.ReachUpAsync();
        _log.Entries.Clear();
        int changed = half.ChangedCount;

        await half.Half.ReconcileAsync(connectPermitted: true);
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Single(half.Calls.ConnectCalls);
        Assert.Equal(0, half.Calls.DisconnectCount);
        Assert.Equal(changed, half.ChangedCount);
        Assert.Equal(0, half.Scheduler.PendingCount);

        // A tick that finds no drift writes nothing. Two of these a minute, for as long as the app
        // runs, is a log nobody can read the interesting lines out of.
        Assert.Empty(_log.Entries);
    }

    /// <summary>
    /// <b>The deliberate asymmetry with <c>LinkMachine</c>.</b> There,
    /// <c>BluetoothLinkStatus.Unknown</c> counts as disconnected, because being wrong that way means
    /// "keep looking for the phone", which costs a rediscovery. Here, being wrong that way means
    /// unregister-and-re-register, which flaps the phone's call-audio-device option - the exact harm
    /// the missing <c>OnLinkAbsent</c> exists to prevent. So a read that could not answer changes
    /// nothing at all and the next tick asks again.
    /// </summary>
    [Fact]
    public async Task Unknown_registration_while_Up_changes_nothing()
    {
        Harness half = new();
        await half.ReachUpAsync();
        _log.Entries.Clear();
        int changed = half.ChangedCount;

        half.Calls.Registration = RegistrationStatus.Unknown;
        await half.Half.ReconcileAsync(connectPermitted: true);
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Equal(0, half.Calls.DisconnectCount);
        Assert.Single(half.Calls.ConnectCalls);
        Assert.Equal(changed, half.ChangedCount);
        Assert.Equal(0, half.Scheduler.PendingCount);
        Assert.Empty(_log.Entries);

        // And it really was asked - a half that never read the tri-state would pass everything above.
        Assert.Equal(2, half.Calls.RegistrationReads);
    }

    /// <summary>
    /// The backstop for a retry that was never delivered - a suspended machine does not run its
    /// timers, so the half can be found in <c>Backoff</c> minutes past its own deadline with a timer
    /// armed that will never fire.
    /// </summary>
    [Fact]
    public async Task Reconcile_registers_from_Backoff_when_permitted()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        // The suspend, reproduced: the 2 s retry fired, failed and armed a 4 s one, and the clock
        // then ran straight past that without the timer being given a chance.
        half.Scheduler.Advance(Seconds(30));
        Assert.Equal(2, half.Calls.ConnectCalls.Count);
        Assert.Equal(TimeSpan.Zero, half.Half.NextRetryIn);

        half.Calls.ConnectResult = CallTransportResult.Claimed(true);
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Equal(3, half.Calls.ConnectCalls.Count);
        Assert.Equal(0, half.Scheduler.PendingCount);
    }

    /// <summary>
    /// The 30 s poll is a backstop for a retry that never fired, not a second retry schedule. Without
    /// the due-ness gate every wait longer than the tick was unreachable - the sequence was really
    /// 2/4/8/16/30/30/30 - and a registration started while the tray was still counting down.
    /// </summary>
    [Fact]
    public async Task Reconcile_mid_countdown_does_not_jump_the_queue()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        half.Scheduler.Advance(Seconds(1));
        await half.Half.ReconcileAsync(connectPermitted: true);

        Assert.Single(half.Calls.ConnectCalls);
        Assert.Equal(CallsState.Backoff, half.Half.State);
        Assert.Equal(Seconds(1), half.Half.NextRetryIn);

        // And the wait it did not jump is still armed, so nothing has been lost either.
        Assert.Equal(1, half.Scheduler.PendingCount);
        half.Scheduler.Advance(Seconds(1));
        Assert.Equal(2, half.Calls.ConnectCalls.Count);
    }

    /// <summary>The auto-reconnect-off guard: the app may not start anything by itself.</summary>
    [Fact]
    public async Task Reconcile_does_not_register_when_connect_is_not_permitted()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        // Overdue, so permission is the only thing left that could refuse.
        half.Scheduler.Advance(Seconds(30));
        Assert.Equal(TimeSpan.Zero, half.Half.NextRetryIn);
        int before = half.Calls.ConnectCalls.Count;

        await half.Half.ReconcileAsync(connectPermitted: false);

        Assert.Equal(CallsState.Backoff, half.Half.State);
        Assert.Equal(before, half.Calls.ConnectCalls.Count);
    }

    /// <summary>
    /// One read per tick, and none at all from a state that could not act on the answer. The real one
    /// is a live CsWinRT ABI call across the process boundary; a state machine that asked it
    /// speculatively would be paying for an answer it has nowhere to put.
    /// </summary>
    [Fact]
    public async Task The_registration_is_read_only_while_Up()
    {
        Harness half = new();

        await half.Half.ReconcileAsync(connectPermitted: true);
        Assert.Equal(0, half.Calls.RegistrationReads);

        half.Calls.ConnectResult = Refused;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        await half.Half.ReconcileAsync(connectPermitted: false);
        Assert.Equal(0, half.Calls.RegistrationReads);

        half.Calls.ConnectResult = CallTransportResult.Claimed(true);
        half.Scheduler.Advance(Seconds(2));
        Assert.Equal(CallsState.Up, half.Half.State);

        await half.Half.ReconcileAsync(connectPermitted: true);
        Assert.Equal(1, half.Calls.RegistrationReads);
    }

    // --- NextRetryIn and Changed -------------------------------------------------------------

    [Fact]
    public async Task NextRetryIn_is_null_unless_a_retry_is_pending()
    {
        Harness half = new();
        Assert.Null(half.Half.NextRetryIn);

        await half.ReachUpAsync();
        Assert.Null(half.Half.NextRetryIn);

        half.Half.OnDisabled();
        Assert.Null(half.Half.NextRetryIn);
    }

    [Fact]
    public async Task NextRetryIn_counts_down_to_the_scheduled_retry()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(Seconds(2), half.Half.NextRetryIn);

        half.Scheduler.Advance(TimeSpan.FromMilliseconds(1500));

        Assert.Equal(TimeSpan.FromMilliseconds(500), half.Half.NextRetryIn);
    }

    /// <summary>
    /// A retry can come due with nothing there to fire it - timers do not run while the machine is
    /// suspended - and the half is then found in <c>Backoff</c> past its own deadline. "Minus four
    /// seconds" is not a countdown, and the tray would have to invent a reading for it.
    /// </summary>
    [Fact]
    public async Task NextRetryIn_never_reports_a_negative_wait()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        // The fake reproduces the suspend exactly: work scheduled from inside a callback sits out the
        // rest of the Advance that is running, so the clock ends the window past the retry's due time.
        half.Scheduler.Advance(Seconds(30));

        Assert.Equal(CallsState.Backoff, half.Half.State);
        Assert.Equal(TimeSpan.Zero, half.Half.NextRetryIn);
    }

    [Fact]
    public async Task Changed_fires_once_per_state_change()
    {
        Harness half = new();
        half.Calls.DeferConnect = true;

        Task registering = half.Half.OnLinkPresentAsync(connectPermitted: true);
        Assert.Equal(1, half.ChangedCount);

        half.Calls.CompleteConnect(CallTransportResult.Claimed(false));
        await registering;
        Assert.Equal(2, half.ChangedCount);

        // Three level-triggered reports, no change.
        half.Calls.DeferConnect = false;
        await half.Half.OnLinkPresentAsync(connectPermitted: true);
        await half.Half.ReconcileAsync(connectPermitted: true);
        half.Half.Configure(enabled: true, PhoneId);

        Assert.Equal(2, half.ChangedCount);
        Assert.Equal(CallsState.Up, half.Half.State);
    }

    /// <summary>
    /// <c>Changed</c> fires on the calling thread, so a handler may call straight back in - the
    /// tray's Disconnect item is one keystroke from doing exactly that. This is the announcement of
    /// <c>Backoff</c> being answered with a teardown, and the retry timer must already be cancellable
    /// when it is announced: armed afterwards, it would survive the cancellation and register a phone
    /// the user had just switched off.
    /// </summary>
    [Fact]
    public async Task A_teardown_from_a_Changed_handler_does_not_leave_a_retry_armed()
    {
        Harness half = new();
        half.Calls.ConnectResult = Refused;
        half.Half.Changed += (_, _) =>
        {
            if (half.Half.State == CallsState.Backoff)
            {
                half.Half.OnDisabled();
            }
        };

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(CallsState.Off, half.Half.State);
        Assert.Equal(0, half.Scheduler.PendingCount);

        half.Scheduler.Advance(Seconds(2));

        Assert.Equal(CallsState.Off, half.Half.State);
        Assert.Single(half.Calls.ConnectCalls);
    }

    /// <summary>
    /// The same re-entrancy against the stale-answer guard. The generation has to be captured before
    /// the registration announces itself, or the teardown's own bump lands first, the guard compares
    /// two values the teardown wrote - agreeing - and the half reports <c>Up</c> over a role that was
    /// released while the radio was deciding.
    /// </summary>
    [Fact]
    public async Task A_teardown_from_a_Changed_handler_discards_the_registration_it_interrupted()
    {
        Harness half = new();
        half.Half.Changed += (_, _) =>
        {
            if (half.Half.State == CallsState.Registering)
            {
                half.Half.OnDisabled();
            }
        };

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(CallsState.Off, half.Half.State);
        Assert.Equal(1, half.Calls.DisconnectCount);

        // Not even asked. The guard catches this attempt at the first await, before the role could
        // be claimed a second time behind the teardown's back.
        Assert.Empty(half.Calls.ConnectCalls);
        Assert.Equal(0, half.Scheduler.PendingCount);
    }

    /// <summary>
    /// The in-flight flag is bookkeeping too, and it is subject to the same rule as the generation
    /// counter: set it before the announce, not after. The announcement of <c>Registering</c> is one
    /// keystroke from a handler that disconnects and lets the next level-triggered report straight
    /// back in - and with the flag not yet set, that report starts the second registration this half
    /// exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_teardown_and_retry_from_a_Changed_handler_does_not_start_a_second_registration()
    {
        Harness half = new();
        half.Calls.DeferConnect = true;

        bool reentered = false;
        half.Half.Changed += (_, _) =>
        {
            if (half.Half.State != CallsState.Registering || reentered)
            {
                return;
            }

            reentered = true;
            half.Half.OnDisabled();
            _ = half.Half.OnLinkPresentAsync(connectPermitted: true);
        };

        // Held rather than awaited on the spot, for the same reason as the test above: everything
        // asserted below is already true by the time this returns, and a registration that wrongly
        // carried on would be parked on a deferred connect nothing answers - which must fail here,
        // not hang the runner.
        Task registering = half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.True(reentered);
        Assert.Empty(half.Calls.ConnectCalls);
        Assert.Equal(CallsState.Off, half.Half.State);
        Assert.True(registering.IsCompleted);
        await registering;
    }

    /// <summary>
    /// And the phone id is captured with the rest of it, before the announce. Settings are written by
    /// the tray, <c>Changed</c> fires on the calling thread, so a handler re-pointing them mid-flight
    /// is one keystroke away - and an attempt that silently switched horses would announce itself for
    /// one phone and claim the role on another. Capturing it keeps the change on the orderly route:
    /// the drift check moves the role on the next tick, having released the old one first.
    /// </summary>
    [Fact]
    public async Task A_Configure_from_a_Changed_handler_does_not_redirect_the_attempt_in_flight()
    {
        Harness half = new();
        half.Calls.Transports = new[] { PhoneTransport, OtherTransport };
        half.Half.Changed += (_, _) =>
        {
            if (half.Half.State == CallsState.Registering)
            {
                half.Half.Configure(enabled: true, OtherPhoneId);
            }
        };

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(new[] { TransportId }, half.Calls.ConnectCalls);
        Assert.Equal(CallsState.Up, half.Half.State);
    }

    /// <summary>
    /// The same rule for the phone the role was claimed on. A handler that reconciles the moment it
    /// sees <c>Up</c> - the manager's own tick is one turn away from doing it - must not find a half
    /// that is Up with no phone recorded against it, because the drift check reads that as the wrong
    /// phone and releases a role that was claimed correctly a microsecond earlier.
    /// </summary>
    [Fact]
    public async Task A_reconcile_from_a_Changed_handler_does_not_see_Up_without_its_phone()
    {
        Harness half = new();
        half.Half.Changed += (_, _) =>
        {
            if (half.Half.State == CallsState.Up)
            {
                _ = half.Half.ReconcileAsync(connectPermitted: true);
            }
        };

        await half.Half.OnLinkPresentAsync(connectPermitted: true);

        Assert.Equal(CallsState.Up, half.Half.State);
        Assert.Equal(0, half.Calls.DisconnectCount);
        Assert.Equal(0, half.Scheduler.PendingCount);
    }

    /// <summary>
    /// The half plus its two doubles, wired the way <c>ConnectionManager</c> will wire them.
    ///
    /// <see cref="CallsHalf"/> subscribes to nothing itself: every inbound event reaches it as a
    /// method call the manager has already marshalled onto the UI thread, which is what makes a
    /// component with no locks in it correct.
    /// </summary>
    private sealed class Harness
    {
        public Harness(bool enabled = true, string? phoneDeviceId = PhoneId)
        {
            Calls.Transports = new[] { PhoneTransport };

            Half = new CallsHalf(Calls, Scheduler);
            Half.Changed += (_, _) => ChangedCount++;

            Half.Configure(enabled, phoneDeviceId);
        }

        public FakeCallTransportService Calls { get; } = new();

        public FakeScheduler Scheduler { get; } =
            new(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

        public CallsHalf Half { get; }

        public int ChangedCount { get; private set; }

        public async Task ReachUpAsync()
        {
            await Half.OnLinkPresentAsync(connectPermitted: true);
            Assert.Equal(CallsState.Up, Half.State);
        }
    }
}
