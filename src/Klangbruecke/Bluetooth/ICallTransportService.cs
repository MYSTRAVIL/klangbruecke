using Klangbruecke.Diagnostics;

namespace Klangbruecke.Bluetooth;

/// <summary>
/// Registered is the health signal. TransportConnected carries PhoneLineTransportDevice.ConnectAsync's
/// bool, which returns False on this machine even when registration succeeded and calls demonstrably
/// work (spec finding #1, FINDINGS.md §12). It is logged, never treated as failure.
/// </summary>
/// <param name="Registered">
/// The post-<c>RegisterApp</c> <c>IsRegistered()</c> re-check. The one fact a caller may grade on.
/// </param>
/// <param name="TransportConnected">
/// What <c>PhoneLineTransportDevice.ConnectAsync</c> returned, or null when it was not reached.
/// Null rather than false in that case, deliberately: false is a measurement, and reporting one
/// nothing measured puts a fact in the log that never happened.
/// </param>
/// <param name="Reason">
/// A sentence for the log. The three factory paths below always fill it, and the tests pin that;
/// it is deliberately NOT claimed as a type invariant, because for a struct it cannot be one -
/// <c>default(CallTransportResult)</c> is always constructible and leaves this null, and the catch
/// in <see cref="CallTransportService.ConnectAsync"/> builds one directly from an exception whose
/// <c>Message</c> nothing in this codebase controls. Read it, do not assume it says something.
/// </param>
public readonly record struct CallTransportResult(bool Registered, bool? TransportConnected, string Reason)
{
    /// <summary>
    /// The hands-free role is held. This is success, and <paramref name="transportConnected"/>
    /// cannot overturn it - that is the entire point of this type.
    ///
    /// The rule lives here, in one pure place, rather than at the call site in
    /// <see cref="CallTransportService"/>, because the call site cannot be exercised without package
    /// identity and a phone. Re-inverting the rule there would be invisible to the suite; re-inverting
    /// it here fails <c>Registered_with_TransportConnected_false_is_success</c>.
    ///
    /// One path does bypass this: the catch in <c>CallTransportService.ConnectAsync</c> constructs a
    /// result directly, because a throw can leave the role claimed with no transport answer at all
    /// and that combination is not a verdict this factory renders. So "the rule lives in Claimed"
    /// covers every path where the transport actually answered - not literally every construction.
    /// </summary>
    public static CallTransportResult Claimed(bool transportConnected) => new(
        true,
        transportConnected,
        transportConnected
            ? "Hands-free role claimed and the call transport reported connected."

            // Informational, not an accusation. This case used to report "Call transport refused to
            // connect. Check the pairing first - BTHUSB events 35/16/24 ... See docs/FINDINGS.md §3",
            // which is shown on every successful run on this machine and sends the reader after a
            // stale pairing they do not have.
            : "Hands-free role claimed. The call transport reported not connected, which is the "
              + "normal result on this machine and not a failure - see docs/FINDINGS.md §12.");

    /// <summary>
    /// The role is not held, for the reason given. The transport connect was not reached, so there
    /// is no transport answer to report.
    /// </summary>
    public static CallTransportResult NotClaimed(string reason) => new(false, null, reason);
}

/// <summary>
/// The calls half of the app, as its callers see it: find the phone-line transports, claim the
/// Bluetooth HFP hands-free role on one of them, and answer whether the role is still held.
///
/// Separate from <see cref="CallTransportService"/> for the same reason
/// <see cref="IAudioSinkService"/> is separate from its implementation: the reconnect machinery has
/// to be drivable without a phone and without package identity, neither of which a test host has.
/// </summary>
public interface ICallTransportService : IDisposable
{
    /// <summary>Live PhoneLineTransportDevice.IsRegistered(). False when no transport is held.</summary>
    bool IsRegistered { get; }

    event EventHandler<StatusMessage>? Status;

    Task<IReadOnlyList<TransportCandidate>> FindTransportsAsync();
    Task<CallTransportResult> ConnectAsync(string transportDeviceId);

    /// <summary>Unregisters the hands-free role. Deliberate intent changes only.</summary>
    void Disconnect();
}
