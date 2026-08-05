using Klangbruecke.Bluetooth;
using Xunit;

namespace Klangbruecke.Tests.Bluetooth;

/// <summary>
/// The decision rule the calls half is graded on, pinned where it can actually be exercised.
///
/// Registration itself cannot run unpackaged, so nothing here touches a
/// <c>PhoneLineTransportDevice</c>. What can be pinned is the rule that reads its two answers, and
/// that rule is the whole reason this type exists: on this machine
/// <c>PhoneLineTransportDevice.ConnectAsync</c> returns False on every run - including the ones
/// where the hands-free role was claimed and real cellular calls demonstrably routed - so a
/// connect verdict taken from that bool reports failure on every success. See docs/FINDINGS.md §12.
/// </summary>
public sealed class CallTransportResultTests
{
    [Fact]
    public void Registered_with_TransportConnected_false_is_success()
    {
        // The exact shape of every working run recorded on this machine:
        //     [INF] RegisterApp returned; IsRegistered=True.
        //     [INF] PhoneLineTransportDevice.ConnectAsync returned False.
        // A state machine fed "failed" here would sit in Degraded forever and re-run
        // RequestAccessAsync/RegisterApp every 30 seconds against a role it already holds.
        CallTransportResult claimed = CallTransportResult.Claimed(transportConnected: false);

        Assert.True(claimed.Registered);

        // Recorded, not discarded - it is the fact a reader correlates against the transport logs.
        Assert.False(claimed.TransportConnected);
    }

    [Fact]
    public void Registered_with_TransportConnected_false_does_not_blame_the_pairing()
    {
        // This string used to read "Call transport refused to connect. Check the pairing first -
        // BTHUSB events 35/16/24 ... See docs/FINDINGS.md §3." It was shown on every *successful*
        // run and sent readers hunting a stale-IRK problem they did not have. The claim is what is
        // wrong with it, so the claim is what is asserted against: this line may explain, but it
        // may not accuse the pairing.
        string reason = CallTransportResult.Claimed(transportConnected: false).Reason;

        Assert.DoesNotContain("BTHUSB", reason);
        Assert.DoesNotContain("§3", reason);
        Assert.DoesNotContain("refused", reason);
    }

    [Fact]
    public void A_transport_that_did_connect_is_also_success()
    {
        // The other half of the rule, so "Registered decides it" is pinned rather than "the bool is
        // ignored in one direction".
        CallTransportResult claimed = CallTransportResult.Claimed(transportConnected: true);

        Assert.True(claimed.Registered);
        Assert.True(claimed.TransportConnected);
    }

    [Fact]
    public void A_role_that_was_not_claimed_is_failure_and_reports_no_transport_answer()
    {
        CallTransportResult failed = CallTransportResult.NotClaimed("RegisterApp did not throw but the role was not claimed.");

        Assert.False(failed.Registered);

        // Null rather than false: the transport connect was never reached, and recording "false"
        // would put a measurement in the log that nothing measured.
        Assert.Null(failed.TransportConnected);
        Assert.Equal("RegisterApp did not throw but the role was not claimed.", failed.Reason);
    }

    /// <summary>
    /// Every reason this type can produce. Strings rather than the results themselves so the theory
    /// data stays xunit-serializable and the run output stays clean.
    /// </summary>
    public static TheoryData<string> EveryReason => new()
    {
        CallTransportResult.Claimed(transportConnected: true).Reason,
        CallTransportResult.Claimed(transportConnected: false).Reason,
        CallTransportResult.NotClaimed("No phone-line transport for that device.").Reason,
    };

    [Theory]
    [MemberData(nameof(EveryReason))]
    public void Reason_is_never_null_or_empty(string reason)
    {
        // Reason is the only part of this result that reaches a human, and the success-with-False
        // case is the one most likely to be left blank precisely because it is not a failure.
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
