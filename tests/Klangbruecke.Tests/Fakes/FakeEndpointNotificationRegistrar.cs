using Klangbruecke.Audio;
using NAudio.CoreAudioApi.Interfaces;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// <c>MMDeviceEnumerator.RegisterEndpointNotificationCallback</c> without MMDevAPI.
///
/// The registration is invisible from managed code - it lives in the audio service, keyed on the client
/// object, with no way to ask how many are outstanding - so "did Start register once or twice?" and
/// "did Dispose unregister exactly once, before the enumerator went away?" are unaskable against the
/// real thing. Both are load-bearing: the probe measured that a second unregister for the same client
/// throws <c>COMException 0x80070490</c>, and that unregistering on an already-disposed enumerator
/// throws <c>NullReferenceException</c>.
///
/// <b>The client is held weakly, on purpose, and that is not tidiness.</b> The one measurement that
/// matters most in <c>docs/probes/2026-08-05-endpoint-notification.md</c> is that the COM registration
/// does <i>not</i> root the managed <c>IMMNotificationClient</c>: held only by a
/// <see cref="WeakReference"/>, it was collected by the next GC and the following notification killed
/// the process with <c>0xC0000005</c>, twice. A double that held it strongly would root it itself and
/// make the test for that pass whether or not <c>EndpointMonitor</c> keeps its field.
/// </summary>
public sealed class FakeEndpointNotificationRegistrar : IEndpointNotificationRegistrar
{
    // Synchronised, and not as a formality. Two of these tests drive Start and Dispose at each other
    // from separate threads released a nanosecond apart, 50 times - so Register really does run
    // concurrently with Unregister and Dispose here. Unsynchronised, a concurrent List<T>.Add can throw
    // IndexOutOfRangeException from inside List.Add (a confusing red in the one test least able to
    // afford one), and a lost `++` breaks the assertion in both directions: a dropped UnregisterCount
    // is a false red, and a dropped RegisterCount silently masks the exact invariant the racing test
    // exists to check. The 40-odd single-threaded tests pay one uncontended lock each.
    private readonly object _gate = new();

    private readonly List<string> _operations = new();

    private int _registerCount;
    private int _unregisterCount;
    private int _disposeCount;
    private WeakReference? _client;
    private bool _unregisteredTheClientItRegistered;

    /// <summary>
    /// Every call in order, as lower-case verbs. Shutdown ordering is the assertion this exists for -
    /// and order, not just counts: the interleaving that leaves a live registration behind produces the
    /// same counts as the correct one and differs only in sequence.
    ///
    /// A snapshot, so an assertion cannot read a list another thread is still appending to. Use
    /// <see cref="Record"/> to interleave a marker of your own.
    /// </summary>
    public IReadOnlyList<string> Operations
    {
        get
        {
            lock (_gate)
            {
                return _operations.ToList();
            }
        }
    }

    public int RegisterCount
    {
        get
        {
            lock (_gate)
            {
                return _registerCount;
            }
        }
    }

    public int UnregisterCount
    {
        get
        {
            lock (_gate)
            {
                return _unregisterCount;
            }
        }
    }

    public int DisposeCount
    {
        get
        {
            lock (_gate)
            {
                return _disposeCount;
            }
        }
    }

    /// <summary>
    /// The client that was registered, weakly. Null until <see cref="Register"/> runs. See the note
    /// above before making this a strong reference.
    /// </summary>
    public WeakReference? Client
    {
        get
        {
            lock (_gate)
            {
                return _client;
            }
        }
    }

    /// <summary>
    /// <see cref="Unregister"/> was handed the same object <see cref="Register"/> was given. False
    /// until an unregister happens. The registration is keyed on the client, so handing back a
    /// different one reports success and leaves the real registration in place.
    /// </summary>
    public bool UnregisteredTheClientItRegistered
    {
        get
        {
            lock (_gate)
            {
                return _unregisteredTheClientItRegistered;
            }
        }
    }

    /// <summary>Appends a marker of the caller's own, in sequence with the real calls.</summary>
    public void Record(string operation)
    {
        lock (_gate)
        {
            _operations.Add(operation);
        }
    }

    /// <summary>
    /// Thrown out of <see cref="Register"/> when set. The real registrar constructs an
    /// <c>MMDeviceEnumerator</c> before it can check any HRESULT, and that construction can fail while
    /// the audio service restarts - so a throw out of this call is a reachable path, not a hypothetical
    /// one, and it lands on the UI-thread startup path where nothing else would catch it.
    /// </summary>
    public Exception? RegisterThrows { get; set; }

    /// <summary>
    /// Thrown out of <see cref="Unregister"/> and <see cref="Dispose"/> when set. Teardown runs outside
    /// the message loop's exception guard, so anything that escapes it is a WER dialog.
    /// </summary>
    public Exception? TeardownThrows { get; set; }

    public void Register(IMMNotificationClient client)
    {
        lock (_gate)
        {
            _registerCount++;
            _operations.Add("register");
            _client = new WeakReference(client);
        }

        if (RegisterThrows is not null)
        {
            throw RegisterThrows;
        }
    }

    public void Unregister(IMMNotificationClient client)
    {
        lock (_gate)
        {
            _unregisterCount++;
            _operations.Add("unregister");

            // Target is read into a local that dies with this call, so nothing here outlives the method
            // and keeps the client alive past a later collection.
            _unregisteredTheClientItRegistered = ReferenceEquals(client, _client?.Target);
        }

        if (TeardownThrows is not null)
        {
            throw TeardownThrows;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposeCount++;
            _operations.Add("dispose");
        }

        if (TeardownThrows is not null)
        {
            throw TeardownThrows;
        }
    }
}
