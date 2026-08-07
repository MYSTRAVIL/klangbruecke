using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;
using Klangbruecke.Tests.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Klangbruecke.Tests.Bluetooth;

/// <summary>
/// The part of <see cref="ICallTransportService"/> that can be exercised without package identity.
///
/// No test here calls <c>ConnectAsync</c>, and none may: registering the hands-free role needs the
/// restricted capability <c>phoneLineTransportManagement</c>, which needs MSIX package identity, and
/// the unpackaged failure arrives from inside the CsWinRT ABI shim. Enumeration is the one calls-side
/// call that is documented to work without identity (docs/FINDINGS.md §2), and it is the only one
/// touched here.
/// </summary>
public sealed class CallTransportServiceContractTests : IDisposable
{
    private readonly ILog _original = Log.Current;
    private readonly RecordingLog _log = new();
    private readonly ICallTransportService _calls = new CallTransportService();
    private readonly ITestOutputHelper _output;

    public CallTransportServiceContractTests(ITestOutputHelper output)
    {
        _output = output;
        Log.Current = _log;
    }

    public void Dispose()
    {
        Log.Current = _original;
        _calls.Dispose();
    }

    [Fact]
    public void IsRegistered_is_false_before_connecting()
    {
        // The health signal the reconcile loop reads. False here is what makes "no transport held"
        // and "the role was lost" the same answer to the loop, which is correct: both need a
        // register. It must never be a cached true left over from a device that has gone.
        Assert.False(_calls.IsRegistered);
    }

    [Fact]
    public void IsRegistered_is_still_false_after_Disconnect()
    {
        // Disconnect drops the device, so the live read has nothing to ask and must say false rather
        // than throw. Catching the role going false is the reconcile loop's entire job.
        _calls.Disconnect();

        Assert.False(_calls.IsRegistered);
    }

    /// <summary>
    /// <c>NotRegistered</c>, not <c>Unknown</c>. There is no device to ask, and that is a known
    /// answer rather than a failed one - the role cannot be held through a device this class does not
    /// have. <c>CallsHalf</c> acts on the two answers in opposite directions, so a service that
    /// hedged here would have the reconcile loop sit on its hands for a role that is provably absent.
    /// </summary>
    [Fact]
    public void ReadRegistration_is_NotRegistered_before_connecting()
    {
        Assert.Equal(RegistrationStatus.NotRegistered, _calls.ReadRegistration());
    }

    [Fact]
    public void ReadRegistration_is_still_NotRegistered_after_Disconnect()
    {
        _calls.Disconnect();

        Assert.Equal(RegistrationStatus.NotRegistered, _calls.ReadRegistration());
    }

    /// <summary>
    /// The two reads answer the same question and must not disagree. <c>IsRegistered</c> is kept
    /// unguarded and unchanged because <c>TrayContext.RebuildMenuAsync</c> depends on its current
    /// semantics and defends itself by ordering; the tri-state exists for the reconcile timer, where
    /// ordering buys nothing. Different guards, one answer.
    /// </summary>
    [Fact]
    public void ReadRegistration_agrees_with_IsRegistered_when_nothing_is_held()
    {
        Assert.False(_calls.IsRegistered);
        Assert.NotEqual(RegistrationStatus.Registered, _calls.ReadRegistration());
    }

    [Fact]
    public void Disconnect_on_a_fresh_service_does_not_throw()
    {
        // Stage 1's teardown paths call Disconnect without knowing whether anything is held - a
        // reconnect that gave up, a deliberate tray disconnect, a state machine unwinding. A throw
        // from the never-connected case would make every one of those conditional at the call site.
        Assert.Null(Record.Exception(() => _calls.Disconnect()));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var calls = new CallTransportService();
        calls.Dispose();

        Assert.Null(Record.Exception(calls.Dispose));
    }

    /// <summary>
    /// Discovery works without package identity - the one calls-side fact a development run can
    /// establish before the packaged build ever claims the role. See docs/FINDINGS.md §2.
    ///
    /// Skips cleanly rather than failing when no phone is paired: this asserts what the service does
    /// with what the machine has, and a build machine with nothing paired is not a defect in it.
    /// </summary>
    [Fact]
    public async Task FindTransportsAsync_returns_candidates_unpackaged()
    {
        // The premise, asserted rather than assumed. A packaged host would be exercising a different
        // claim than the one this test's name makes.
        Assert.False(PackageIdentity.IsPackaged);

        IReadOnlyList<TransportCandidate> candidates = await _calls.FindTransportsAsync();

        // Reaching here at all is half the assertion: enumeration returned rather than throwing or
        // taking the process with it.
        Assert.NotNull(candidates);

        if (candidates.Count == 0)
        {
            _output.WriteLine(
                "SKIPPED: no phone-line transport is paired on this machine, so there is no candidate "
                + "to assert against. Pair a phone and re-run to exercise this test for real.");
            return;
        }

        // Both fields, because both are load-bearing downstream: TransportMatcher correlates on the
        // Bluetooth address extracted from Id, and Name is what the log line is read by.
        Assert.All(candidates, candidate =>
        {
            Assert.False(string.IsNullOrWhiteSpace(candidate.Id));
            Assert.False(string.IsNullOrWhiteSpace(candidate.Name));
        });

        // The per-device log lines are the input to transport correlation - the A2DP selector and the
        // phone-line selector return different id shapes for the same phone, and these lines are what
        // a reader correlates them from. A count without the ids cannot be re-run against a regex.
        Assert.Contains(
            _log.Entries,
            e => e.Level == LogLevel.Info && e.Message == $"Phone-line selector matched {candidates.Count} device(s).");

        Assert.All(candidates, candidate => Assert.Contains(
            _log.Entries,
            e => e.Level == LogLevel.Info
                 && e.Message == $"  Transport candidate '{candidate.Name}' id={candidate.Id}"));
    }
}
