namespace Klangbruecke.Audio;

/// <summary>
/// The A2DP sink capture endpoint's device state, reduced to the three cases the wedge watchdog has to
/// tell apart plus the ones it must not mistake for them.
///
/// The distinction is the whole reason this exists. <see cref="IAudioEndpointMonitor.SinkCaptureEndpointPresent"/>
/// only asks "is it <see cref="Active"/>", and answers false for a live call and a wedge alike - so it
/// cannot drive an automatic restart without restarting mid-call. Measured on this machine
/// (docs/FINDINGS.md §23): connected and idle the endpoint sits <see cref="Active"/>; during a call it
/// reads <see cref="Unplugged"/> (§14); and in the stale-endpoint wedge it is <see cref="Phantom"/>
/// (device-tree <c>Present=False</c> / MMDevAPI <c>NotPresent</c>). Only the last is the wedge.
/// </summary>
public enum SinkEndpointCondition
{
    /// <summary>No endpoint by that name enumerates at all, in any state.</summary>
    Absent,

    /// <summary>Present and active. Healthy - connected, whether or not audio is flowing.</summary>
    Active,

    /// <summary>Unplugged. What a live call looks like: SCO has the radio and A2DP is suspended (§14). Not a wedge.</summary>
    Unplugged,

    /// <summary>Not present - the device-tree phantom. The stale-endpoint wedge, and the only state the watchdog acts on.</summary>
    Phantom,

    /// <summary>Disabled, or a state not otherwise named. Reported so the log can carry it; never acted on.</summary>
    Other,
}

/// <summary>
/// Reads the sink capture endpoint's current <see cref="SinkEndpointCondition"/>.
///
/// A seam for the same reason <see cref="IAudioDeviceFactory"/> is one: the production read is a live
/// WASAPI enumeration (152-282 ms, docs/FINDINGS.md §4) that needs a phone to exercise, so the watchdog
/// that consumes it is tested against a fake that simply returns a condition. The one implementation
/// that talks to a device is <see cref="WasapiDeviceFactory.GetSinkCaptureEndpointCondition"/>.
/// </summary>
public interface ISinkEndpointStateProbe
{
    SinkEndpointCondition Probe();
}
