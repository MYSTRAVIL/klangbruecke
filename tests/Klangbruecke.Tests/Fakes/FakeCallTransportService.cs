using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// The hands-free role with the WinRT taken out, so the calls half can be driven without a phone,
/// without MSIX package identity, and without the restricted capability that a test host cannot
/// have (docs/FINDINGS.md §2).
///
/// Two things here are more than a stub, and both are there because the real thing behaves that way:
///
/// <see cref="Registration"/> follows what the last <see cref="ConnectAsync"/> or
/// <see cref="Disconnect"/> actually did, rather than being an independent knob. The real
/// <c>IsRegistered()</c> is a live read of the device, so a double whose registration could disagree
/// with its own history would let a test claim drift that the service cannot produce. Setting it by
/// hand is still allowed, and that is exactly how drift - the role being taken by another app - is
/// staged.
///
/// <see cref="DeferConnect"/> holds a connect open. Registering genuinely takes a while, and the
/// window between asking and being answered is the only one in which the half can be torn down
/// mid-registration; a double that always answered before it returned would make that window
/// unrepresentable.
///
/// Public, in Fakes, and not <c>file</c>-scoped: <c>ConnectionManager</c>'s tests consume it too.
/// </summary>
public sealed class FakeCallTransportService : ICallTransportService
{
    private readonly Queue<TaskCompletionSource<CallTransportResult>> _pending = new();

    /// <summary>Every transport id <see cref="ConnectAsync"/> was asked for, oldest first.</summary>
    public List<string> ConnectCalls { get; } = new();

    public int FindCount { get; private set; }

    public int DisconnectCount { get; private set; }

    /// <summary>How many times <see cref="ReadRegistration"/> was asked. The real one is an ABI call.</summary>
    public int RegistrationReads { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>What <see cref="FindTransportsAsync"/> reports.</summary>
    public IReadOnlyList<TransportCandidate> Transports { get; set; } = Array.Empty<TransportCandidate>();

    /// <summary>When set, <see cref="FindTransportsAsync"/> throws it instead of answering.</summary>
    public Exception? FindThrows { get; set; }

    /// <summary>
    /// What a connect that answers immediately reports. Defaults to the textbook success rather than
    /// to this machine's <c>Claimed(false)</c>, so that the test which asserts a false
    /// <c>TransportConnected</c> still reaches Up is visibly asserting something the default does not
    /// already cover.
    /// </summary>
    public CallTransportResult ConnectResult { get; set; } = CallTransportResult.Claimed(true);

    /// <summary>
    /// When set, <see cref="ConnectAsync"/> throws it instead of answering, synchronously and before
    /// any await. Beats <see cref="DeferConnect"/>.
    /// </summary>
    public Exception? ConnectThrows { get; set; }

    /// <summary>Holds every connect open until <see cref="CompleteConnect"/> answers it.</summary>
    public bool DeferConnect { get; set; }

    /// <summary>
    /// What the live read says. Settable, because the drift the reconcile loop exists to catch -
    /// another app claiming the role, the phone re-pairing, Windows dropping it - is precisely a
    /// change nothing told this object about.
    /// </summary>
    public RegistrationStatus Registration { get; set; } = RegistrationStatus.NotRegistered;

    /// <summary>
    /// Derived from <see cref="Registration"/> rather than stored beside it, so the bool the tray
    /// reads and the tri-state the reconcile loop reads cannot disagree in a test the way they never
    /// can in the real service - both are the same live question, asked twice.
    /// </summary>
    public bool IsRegistered => Registration == RegistrationStatus.Registered;

    /// <summary>
    /// Text for the tray and the log. Discarding accessors: this one carries no decision, and
    /// <c>CallsHalf</c> subscribes to nothing - the manager owns that wiring.
    /// </summary>
    public event EventHandler<StatusMessage>? Status { add { } remove { } }

    public RegistrationStatus ReadRegistration()
    {
        RegistrationReads++;
        return Registration;
    }

    public Task<IReadOnlyList<TransportCandidate>> FindTransportsAsync()
    {
        FindCount++;

        if (FindThrows is { } thrown)
        {
            throw thrown;
        }

        return Task.FromResult(Transports);
    }

    public Task<CallTransportResult> ConnectAsync(string transportDeviceId)
    {
        ConnectCalls.Add(transportDeviceId);

        if (ConnectThrows is { } thrown)
        {
            throw thrown;
        }

        if (DeferConnect)
        {
            var source = new TaskCompletionSource<CallTransportResult>();
            _pending.Enqueue(source);
            return source.Task;
        }

        return Task.FromResult(Settle(ConnectResult));
    }

    /// <summary>Answers the oldest connect still waiting. Throws if none is.</summary>
    public void CompleteConnect(CallTransportResult result) => _pending.Dequeue().SetResult(Settle(result));

    public void Disconnect()
    {
        DisconnectCount++;
        Registration = RegistrationStatus.NotRegistered;
    }

    public void Dispose() => Disposed = true;

    /// <summary>
    /// Registered, and only Registered, decides what the live read says next.
    /// <c>TransportConnected</c> is False on every run on this machine including the ones where real
    /// calls routed both directions (docs/FINDINGS.md §12), so a double that let it touch this would
    /// be teaching the suite the bug the whole calls half is built to avoid.
    /// </summary>
    private CallTransportResult Settle(CallTransportResult result)
    {
        Registration = result.Registered ? RegistrationStatus.Registered : RegistrationStatus.NotRegistered;
        return result;
    }
}
