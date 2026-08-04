using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests.Bluetooth;

/// <summary>
/// The ids below are real: a probe with the phone (MYSTRAPIX9, C01C6A90E174) connected returned
/// exactly these two, one per selector. The second phone is synthetic - only one phone and a game
/// controller are paired on this machine, so the collision this class exists to prevent has never
/// been reproduced against hardware. It is built by substituting a different address into the real
/// transport id, which keeps every other token of the real shape intact.
/// </summary>
public sealed class TransportMatcherTests
{
    private const string PhoneAddress = "C01C6A90E174";
    private const string OtherAddress = "D8C0A63F1B22";

    // A2DP selector, 110a, ...\SNK
    private const string RealPhoneA2dpId =
        @"\\?\BTHENUM#{0000110a-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&C01C6A90E174_C00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\SNK";

    // Phone-line selector, 111f, ...\service
    private const string RealPhoneTransportId =
        @"\\?\BTHENUM#{0000111f-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&C01C6A90E174_C00000000#{bd41df2d-addd-4fc9-a194-b9881d2a2efa}\service";

    private const string OtherPhoneTransportId =
        @"\\?\BTHENUM#{0000111f-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&D8C0A63F1B22_C00000000#{bd41df2d-addd-4fc9-a194-b9881d2a2efa}\service";

    private static TransportCandidate Phone => new(RealPhoneTransportId, "MYSTRAPIX9");

    private static TransportCandidate OtherPhone => new(OtherPhoneTransportId, "Someone else's phone");

    // The correlation this whole class rests on: two different selectors, two different id shapes,
    // one shared address. Confirmed against the live pairing, not reasoned about.
    [Fact]
    public void Match_CorrelatesTheRealSelectorIdsForOnePhone()
    {
        TransportMatchResult result = TransportMatcher.Match([Phone], RealPhoneA2dpId);

        Assert.Equal(TransportMatchOutcome.AddressMatch, result.Outcome);
        Assert.Equal(RealPhoneTransportId, result.Match?.Id);
        Assert.Contains(PhoneAddress, result.Reason);
    }

    // The reason this task exists. The old code took FirstOrDefault(), so with two phones paired the
    // call transport bound to whichever one enumerated first - a coin flip that looks like a working
    // app until the wrong phone rings.
    [Fact]
    public void Match_PicksTheAddressMatch_NotTheFirstCandidate()
    {
        TransportMatchResult result = TransportMatcher.Match([OtherPhone, Phone], RealPhoneA2dpId);

        Assert.Equal(TransportMatchOutcome.AddressMatch, result.Outcome);
        Assert.Equal(RealPhoneTransportId, result.Match?.Id);
    }

    [Fact]
    public void Match_PicksTheAddressMatch_WhateverTheOrder()
    {
        TransportMatchResult result = TransportMatcher.Match([Phone, OtherPhone], RealPhoneA2dpId);

        Assert.Equal(RealPhoneTransportId, result.Match?.Id);
    }

    // Connecting nothing is the point. Any fallback here reintroduces the coin flip in a new form,
    // and a call routed to the wrong phone is worse than calls not being routed at all.
    [Fact]
    public void Match_ConnectsNothing_WhenSeveralCandidatesAndNoneIsThePhone()
    {
        TransportMatchResult result = TransportMatcher.Match(
            [OtherPhone, OtherPhone with { Id = OtherPhoneTransportId.Replace(OtherAddress, "A1B2C3D4E5F6") }],
            RealPhoneA2dpId);

        Assert.Equal(TransportMatchOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Match);
        Assert.Contains("2 candidates", result.Reason);
    }

    // An unextractable phone id is not licence to guess either, once there is more than one answer.
    [Fact]
    public void Match_ConnectsNothing_WhenThePhoneIdCarriesNoAddress()
    {
        TransportMatchResult result = TransportMatcher.Match([Phone, OtherPhone], "not-a-device-id");

        Assert.Equal(TransportMatchOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Match);
    }

    [Fact]
    public void Match_FallsBackToTheOnlyCandidate_WhenNoAddressCouldBeExtracted()
    {
        TransportMatchResult result = TransportMatcher.Match([Phone], "not-a-device-id");

        Assert.Equal(TransportMatchOutcome.SoleCandidate, result.Outcome);
        Assert.Equal(RealPhoneTransportId, result.Match?.Id);

        // The log has to say the choice was taken blind, or the fallback is indistinguishable from
        // a match and the next reader trusts a guess.
        Assert.Contains("no address could be extracted", result.Reason);
    }

    // Both addresses known and different is a stronger signal than an extraction failure: this
    // transport belongs to another paired phone. It is still taken - one candidate is not a coin
    // flip - but the log must name both addresses so the case is recognisable if it ever appears.
    [Fact]
    public void Match_NamesBothAddresses_WhenTheOnlyCandidateBelongsToAnotherPhone()
    {
        TransportMatchResult result = TransportMatcher.Match([OtherPhone], RealPhoneA2dpId);

        Assert.Equal(TransportMatchOutcome.SoleCandidate, result.Outcome);
        Assert.Contains(OtherAddress, result.Reason);
        Assert.Contains(PhoneAddress, result.Reason);
    }

    [Fact]
    public void Match_ReturnsNoCandidates_WhenNothingEnumerated()
    {
        TransportMatchResult result = TransportMatcher.Match([], RealPhoneA2dpId);

        Assert.Equal(TransportMatchOutcome.NoCandidates, result.Outcome);
        Assert.Null(result.Match);
    }

    // The two selectors are not guaranteed to agree on case, and a case-sensitive compare would fail
    // to match a phone against itself - presenting as the ambiguous path, which connects nothing.
    [Fact]
    public void Match_IsCaseInsensitiveAcrossSelectors()
    {
        TransportMatchResult result = TransportMatcher.Match([Phone], RealPhoneA2dpId.ToLowerInvariant());

        Assert.Equal(TransportMatchOutcome.AddressMatch, result.Outcome);
    }

    // Reached from the startup reconnect before any menu has been opened, where the persisted id can
    // be anything the last run wrote - including nothing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Match_HandlesAMissingPhoneId(string? phoneDeviceId)
    {
        TransportMatchResult result = TransportMatcher.Match([Phone, OtherPhone], phoneDeviceId);

        Assert.Equal(TransportMatchOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Match);
    }

    // Reason feeds the log unconditionally, so an outcome that reached it empty would turn the one
    // artifact this stage produces into a blank line.
    [Fact]
    public void Match_AlwaysExplainsItself()
    {
        TransportCandidate[][] cases = [[], [Phone], [Phone, OtherPhone], [OtherPhone, OtherPhone]];

        foreach (TransportCandidate[] candidates in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(TransportMatcher.Match(candidates, RealPhoneA2dpId).Reason));
        }
    }

    // TransportMatchOutcome's summary says the log level follows from the outcome, and it had already
    // been contradicted: NoCandidates is documented "Not an error" and was logged as a warning.
    // SoleCandidate warns despite connecting something, because it means the correlation did not do
    // its job and a transport was taken blind - the wrong-phone bug this class exists to prevent.
    [Theory]
    [InlineData(TransportMatchOutcome.AddressMatch, LogLevel.Info)]
    [InlineData(TransportMatchOutcome.NoCandidates, LogLevel.Info)]
    [InlineData(TransportMatchOutcome.SoleCandidate, LogLevel.Warn)]
    [InlineData(TransportMatchOutcome.Ambiguous, LogLevel.Warn)]
    public void LevelFor_FollowsTheOutcome(TransportMatchOutcome outcome, LogLevel expected)
    {
        Assert.Equal(expected, TransportMatcher.LevelFor(outcome));
    }

    // An outcome added later must not default into silence: every one of them feeds a log line that
    // is the only record of why a transport was or was not connected.
    [Fact]
    public void LevelFor_WarnsAboutAnUnrecognisedOutcome()
    {
        Assert.Equal(LogLevel.Warn, TransportMatcher.LevelFor((TransportMatchOutcome)99));
    }
}
