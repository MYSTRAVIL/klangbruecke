namespace Klangbruecke.Audio;

/// <summary>
/// Is the A2DP sink capture endpoint there, and tell me when that changes.
///
/// The signal the audio route has never had. The app's own log shows
/// <c>Line (&lt;phone&gt; A2DP SNK)</c> absent for an unbounded interval after the Bluetooth connection
/// reports Opened: in 5 of 8 recorded launches the app looked for it once, immediately, found nothing,
/// and silently never routed audio for the whole session. Separately, a phone call invalidates that
/// endpoint without closing the connection, so after every call the music bridge stays dead until the
/// user re-picks the phone from the tray. Both are the same missing fact - <b>nothing told the app when
/// the endpoint arrived</b> - and this is it.
///
/// <b>The endpoint is not the connection.</b> Measured: opening and closing an
/// <c>AudioPlaybackConnection</c> does not by itself create or destroy the capture endpoint. On this
/// machine it tracks the phone's Bluetooth A2DP link, which is not the app's to control, and it was
/// already <c>Active</c> before the app opened its connection and still <c>Active</c> after the app was
/// killed. So <see cref="Start"/> must report an endpoint that is <b>already present</b> rather than
/// waiting for an arrival that has already happened - an edge-only design reproduces the very bug this
/// interface exists to remove.
///
/// <b>Nothing here marshals</b>, for the reason <c>ILinkMonitor</c> gives rather than the one
/// <c>IPowerNotifier</c> gives: nothing has marshalled at all. <see cref="EndpointsChanged"/> arrives on
/// whichever thread the OS raised it on - measured as MMDevAPI's own MTA worker threads, never the
/// registering thread, and not always the same one from one callback to the next.
/// <c>ConnectionManager</c> posts every inbound event through <c>IUiDispatcher</c> before touching
/// state, which is what makes it single-threaded by contract. Do not add a second marshalling layer in
/// an implementation.
///
/// <b>Assume duplicates.</b> Measured: one cause produced five callbacks, with two of three roles
/// reported twice, in every run. An implementation is not required to de-duplicate and
/// <see cref="EndpointMonitor"/> deliberately does not - telling a duplicate from a real second change
/// needs the endpoint lookup that a notification handler must not do. Consumers re-read
/// <see cref="SinkCaptureEndpointPresent"/>, which makes reading it twice the same as reading it once.
///
/// All of the above is measured in <c>docs/probes/2026-08-05-endpoint-notification.md</c>. One question
/// that note leaves open - <i>which</i> MMDevAPI callback the A2DP endpoint's own arrival raises - is
/// exactly why this is an interface: if the smoke test on a real phone disconnect finds that none does,
/// swapping <see cref="EndpointMonitor"/> for a 2 s poll of the same lookup is a one-class change and
/// nothing above this line moves.
/// </summary>
public interface IAudioEndpointMonitor : IDisposable
{
    /// <summary>The A2DP SNK capture endpoint exists and is active.</summary>
    bool SinkCaptureEndpointPresent { get; }

    /// <summary>Raised on whatever thread the OS uses. ConnectionManager marshals; this does not.</summary>
    event EventHandler? EndpointsChanged;

    /// <summary>
    /// Begin listening, and report an endpoint that is already there.
    ///
    /// Subscribe to <see cref="EndpointsChanged"/> before calling this: an implementation is allowed to
    /// raise it from inside <see cref="Start"/>, which is how the already-present case is reported, and
    /// a handler attached afterwards would miss it.
    /// </summary>
    void Start();
}
