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
    /// <summary>
    /// Every call in order, as lower-case verbs. Shutdown ordering is the assertion this exists for;
    /// tests may append their own markers to interleave a probe call with the registration.
    /// </summary>
    public List<string> Operations { get; } = new();

    public int RegisterCount { get; private set; }

    public int UnregisterCount { get; private set; }

    public int DisposeCount { get; private set; }

    /// <summary>
    /// The client that was registered, weakly. Null until <see cref="Register"/> runs. See the note
    /// above before making this a strong reference.
    /// </summary>
    public WeakReference? Client { get; private set; }

    /// <summary>
    /// <see cref="Unregister"/> was handed the same object <see cref="Register"/> was given. False
    /// until an unregister happens. The registration is keyed on the client, so handing back a
    /// different one reports success and leaves the real registration in place.
    /// </summary>
    public bool UnregisteredTheClientItRegistered { get; private set; }

    public void Register(IMMNotificationClient client)
    {
        RegisterCount++;
        Operations.Add("register");
        Client = new WeakReference(client);
    }

    public void Unregister(IMMNotificationClient client)
    {
        UnregisterCount++;
        Operations.Add("unregister");

        // Target is read into a local that dies with this call, so nothing here outlives the method and
        // keeps the client alive past a later collection.
        UnregisteredTheClientItRegistered = ReferenceEquals(client, Client?.Target);
    }

    public void Dispose()
    {
        DisposeCount++;
        Operations.Add("dispose");
    }
}
