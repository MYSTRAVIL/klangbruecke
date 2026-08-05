using Klangbruecke.Audio;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// The A2DP sink capture endpoint arriving, and going away again, because a test said so.
///
/// This is the whole reason <see cref="IAudioEndpointMonitor"/> exists as an interface. The real one
/// listens to MMDevAPI for an endpoint that only exists while a phone is connected over A2DP, and the
/// two behaviours <c>ConnectionManager</c> has to get right are exactly the ones a phone cannot be made
/// to perform on demand: the endpoint appearing some unbounded time <i>after</i> the connection reports
/// Opened (the app's own log shows 5 of 8 launches looking once, finding nothing and never routing
/// audio again), and a phone call invalidating the endpoint without closing the connection.
///
/// Deliberately dumb, like <see cref="FakePowerNotifier"/>. It does not check <see cref="Started"/>
/// before raising, does not refuse to raise after <see cref="Dispose"/>, and de-duplicates nothing -
/// the real one does not either, because MMDevAPI duplicates its notifications and the handler is
/// forbidden to do the lookup that would tell one from another. A double that enforced rules the real
/// one does not would make its consumers' tests certify a contract nothing implements.
///
/// Public, in Fakes, and not <c>file</c>-scoped: <c>ConnectionManager</c>'s tests consume it too.
/// </summary>
public sealed class FakeEndpointMonitor : IAudioEndpointMonitor
{
    private bool _present;
    private int _presenceReads;
    private int _lastReadThreadId;

    /// <summary>Settable directly for the "it was already there" case, which needs no event.</summary>
    public bool SinkCaptureEndpointPresent
    {
        get
        {
            // Interlocked and Volatile, not ++ and a field. A consumer that keeps its promise to
            // enumerate off the UI thread reads this from a threadpool thread, so an ordinary
            // increment could lose a read and an ordinary write could stay invisible to the test
            // thread - which would make the count that pins the cost either flaky or silently
            // generous.
            Interlocked.Increment(ref _presenceReads);
            Volatile.Write(ref _lastReadThreadId, Environment.CurrentManagedThreadId);
            return _present;
        }

        set => _present = value;
    }

    /// <summary>
    /// How many times <see cref="SinkCaptureEndpointPresent"/> has been read, ever.
    ///
    /// Counted because the read is expensive in a way its signature hides: the real one is a live
    /// full endpoint enumeration, measured at 152-282 ms on this machine, and it runs on the UI
    /// thread. A consumer that reads it three times in one pass has spent most of a second doing it,
    /// and MMDevAPI's duplicate notifications multiply that by however many callbacks one cause
    /// produced. Nothing else can tell a consumer's tests that it happened.
    /// </summary>
    public int PresenceReads => Volatile.Read(ref _presenceReads);

    /// <summary>
    /// The managed thread the last read ran on, or 0 if there has not been one.
    ///
    /// The only way a test can check the half of the cost rule that a count cannot express: the read
    /// must not happen on the UI thread. A consumer that cached correctly but still enumerated on the
    /// message-loop thread would satisfy every count in the suite and still freeze the tray.
    /// </summary>
    public int LastReadThreadId => Volatile.Read(ref _lastReadThreadId);

    public event EventHandler? EndpointsChanged;

    /// <summary><see cref="Start"/> has been called at least once.</summary>
    public bool Started { get; private set; }

    /// <summary>
    /// <see cref="Dispose"/> has been called at least once. Here so a consumer's teardown test can
    /// assert the monitor was let go of - the real one holds a COM registration whose managed client
    /// COM does not root, so a consumer that forgets is a process crash rather than a leak.
    /// </summary>
    public bool Disposed { get; private set; }

    public void Start() => Started = true;

    /// <summary>Sets presence and raises the event in one call.</summary>
    public void SetPresent(bool present)
    {
        SinkCaptureEndpointPresent = present;
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A notification that turns out to be about nothing - which is most of them. MMDevAPI reports
    /// every endpoint on the machine, so the ordinary case a consumer must survive is being told
    /// something changed and finding the level exactly as it was.
    /// </summary>
    public void RaiseEndpointsChanged() => EndpointsChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Disposed = true;
}
